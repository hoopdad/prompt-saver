using System.Text;
using PromptSaver.Application;
using PromptSaver.Application.Dtos;
using PromptSaver.Application.UseCases;
using PromptSaver.Domain.Entities;
using PromptSaver.Domain.ValueObjects;

namespace PromptSaver.Desktop.ViewModels;

public sealed class CaptureViewModel : ViewModelBase
{
    private readonly ISaveCaptureDraft _saveDraft;
    private readonly IRecoverCaptureDraft _recoverDraft;
    private readonly IDiscardCaptureDraft _discardDraft;
    private readonly ICapturePrompt _capturePrompt;
    private readonly ICommitPromptEdit? _commitPromptEdit;
    private readonly ICopyPrompt _copyPrompt;
    private CancellationTokenSource? _backupDebounce;
    private Task _pendingBackup = Task.CompletedTask;
    private long _draftGeneration;
    private string _body = string.Empty;
    private int _caretOffset;
    private int _selectionLength;
    private CaptureState _state;
    private string _statusMessage = string.Empty;
    private string _intentFeedback = string.Empty;
    private StatusAnnouncement _announcement = StatusAnnouncement.Empty;
    private FocusRequest? _focusRequest;
    private long _focusSequence;
    private PromptDetailsDto? _savedPrompt;

    public CaptureViewModel(object services)
    {
        _saveDraft = Require<ISaveCaptureDraft>(services);
        _recoverDraft = Require<IRecoverCaptureDraft>(services);
        _discardDraft = Require<IDiscardCaptureDraft>(services);
        _capturePrompt = Require<ICapturePrompt>(services);
        _commitPromptEdit = services as ICommitPromptEdit;
        _copyPrompt = Require<ICopyPrompt>(services);

        SaveCommand = new AsyncDelegateCommand(() => SaveAsync(), () => CanSave);
        CopyCommand = new AsyncDelegateCommand(() => CopyAsync(), () => CanCopy);
        RetryBackupCommand = new AsyncDelegateCommand(() => BackupDraftAsync(), () => CanRetryDraftBackup);
        NewPromptCommand = new AsyncDelegateCommand(StartNewAsync);
        SaveAndCopyCommand = new AsyncDelegateCommand(SaveAndCopyAsync, () => CanSave);
        ReviewIntentCommand = new DelegateCommand(
            () => OpenSavedPrompt(FocusTarget.PromptDetailMetadata),
            () => _savedPrompt is not null);
        ReviewMetadataCommand = new AsyncDelegateCommand(
            SaveAndOpenMetadataAsync,
            () => _savedPrompt is not null || CanSave);
    }

    public string Body
    {
        get => _body;
        set
        {
            value ??= string.Empty;
            if (!SetProperty(ref _body, value))
            {
                return;
            }

            NormalizeSelection();
            State = string.IsNullOrWhiteSpace(value)
                ? CaptureState.Empty
                : _savedPrompt is null
                    ? CaptureState.BackingUpDraft
                    : CaptureState.UnsavedChanges;
            IntentFeedback = string.Empty;
            RaiseBodyDependentProperties();
            ScheduleDraftBackup();
        }
    }

    public int CaretOffset
    {
        get => _caretOffset;
        set => SetProperty(ref _caretOffset, Math.Clamp(value, 0, Body.Length));
    }

    public int SelectionLength
    {
        get => _selectionLength;
        set => SetProperty(ref _selectionLength, Math.Clamp(value, 0, Body.Length - CaretOffset));
    }

    public CaptureState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(PrimaryActionText));
                OnPropertyChanged(nameof(IsBusy));
                RaiseCommandStates();
            }
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string IntentFeedback
    {
        get => _intentFeedback;
        private set
        {
            if (SetProperty(ref _intentFeedback, value))
            {
                OnPropertyChanged(nameof(HasIntentFeedback));
            }
        }
    }

    public bool HasIntentFeedback => IntentFeedback.Length > 0;

    public StatusAnnouncement Announcement
    {
        get => _announcement;
        private set => SetProperty(ref _announcement, value);
    }

    public FocusRequest? FocusRequest
    {
        get => _focusRequest;
        private set => SetProperty(ref _focusRequest, value);
    }

    public int Utf8ByteCount => Encoding.UTF8.GetByteCount(Body);

    public bool ShowSize => Utf8ByteCount >= PromptBody.MaximumUtf8Bytes * 0.8;

    public string SizeText => $"{Utf8ByteCount / 1024:N0} KiB of {PromptBody.MaximumUtf8Bytes / 1024:N0} KiB";

    public bool IsOverSizeLimit => Utf8ByteCount > PromptBody.MaximumUtf8Bytes;

    public bool CanSave =>
        !string.IsNullOrWhiteSpace(Body) &&
        !IsOverSizeLimit &&
        State is not CaptureState.SavingPrompt;

    public bool CanCopy => !string.IsNullOrWhiteSpace(Body);

    public bool CanRetrySave => State == CaptureState.SaveFailed;

    public bool CanRetryDraftBackup => State == CaptureState.DraftBackupFailed;

    public bool IsBusy => State is CaptureState.BackingUpDraft or CaptureState.SavingPrompt;

    public string PrimaryActionText => _savedPrompt is null ? "Save prompt" : "Save changes";

    public string MetadataActionText =>
        _savedPrompt is null ? "Save and edit metadata" : "Edit metadata";

    public AsyncDelegateCommand SaveCommand { get; }

    public AsyncDelegateCommand CopyCommand { get; }

    public AsyncDelegateCommand RetryBackupCommand { get; }

    public AsyncDelegateCommand NewPromptCommand { get; }

    public AsyncDelegateCommand SaveAndCopyCommand { get; }

    public DelegateCommand ReviewIntentCommand { get; }

    public AsyncDelegateCommand ReviewMetadataCommand { get; }

    public event EventHandler<PromptNavigationRequest>? OpenSavedPromptRequested;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        CaptureDraftRecoveryResult recovery = await _recoverDraft.ExecuteAsync(cancellationToken);
        if (recovery.Status == DraftRecoveryStatus.Recovered && recovery.Draft is not null)
        {
            _body = recovery.Draft.Body;
            _caretOffset = recovery.Draft.CaretOffset;
            _selectionLength = recovery.Draft.SelectionLength;
            OnPropertyChanged(nameof(Body));
            OnPropertyChanged(nameof(CaretOffset));
            OnPropertyChanged(nameof(SelectionLength));
            RaiseBodyDependentProperties();
            State = CaptureState.DraftBackedUp;
            PublishStatus("Recovered draft", AnnouncementKind.Polite);
        }
        else if (recovery.Status == DraftRecoveryStatus.Corrupt)
        {
            State = CaptureState.DraftBackupFailed;
            PublishStatus(
                "The previous draft could not be read. Its file was preserved for recovery.",
                AnnouncementKind.Assertive);
        }
        else
        {
            State = CaptureState.Empty;
        }

        RequestFocus(FocusTarget.PromptEditor);
    }

    public async Task BackupDraftAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(Body))
        {
            return;
        }

        State = CaptureState.BackingUpDraft;
        CaptureDraftDto draft = CaptureDraftDto.Create(
            Body,
            CaretOffset,
            SelectionLength,
            DateTimeOffset.UtcNow);
        AppResult result = await _saveDraft.ExecuteAsync(draft, cancellationToken);
        if (result.IsSuccess)
        {
            State = _savedPrompt is null ? CaptureState.DraftBackedUp : CaptureState.UnsavedChanges;
            PublishStatus(
                _savedPrompt is null
                    ? "Draft backed up locally"
                    : "Changes backed up locally - save to update prompt",
                AnnouncementKind.Polite);
            return;
        }

        State = CaptureState.DraftBackupFailed;
        PublishStatus(
            "Draft is not backed up. Keep this window open or copy the text.",
            AnnouncementKind.Assertive);
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        if (!CanSave)
        {
            return;
        }

        await CancelAndDrainDraftBackupAsync();
        State = CaptureState.SavingPrompt;
        PublishStatus("Saving prompt...", AnnouncementKind.Polite);
        if (_savedPrompt is not null && _commitPromptEdit is not null)
        {
            AppResult<PromptDetailsDto> editResult = await _commitPromptEdit.ExecuteAsync(
                new CommitPromptEditCommand(
                    _savedPrompt.Id,
                    Body,
                    _savedPrompt.Title,
                    _savedPrompt.Version),
                cancellationToken);
            if (!editResult.IsSuccess)
            {
                State = CaptureState.SaveFailed;
                PublishStatus(MapSaveError(editResult.Error!), AnnouncementKind.Assertive);
                OnPropertyChanged(nameof(CanRetrySave));
                return;
            }

            _savedPrompt = editResult.Value;
            await _discardDraft.ExecuteAsync(cancellationToken);
            RaiseSavedPromptCommands();
            State = CaptureState.PromptSaved;
            IntentFeedback = FormatIntentFeedback(_savedPrompt);
            PublishStatus("Changes saved", AnnouncementKind.Polite);
            RequestFocus(FocusTarget.PromptEditor);
            return;
        }

        AppResult<CapturePromptResult> captureResult = await _capturePrompt.ExecuteAsync(
            new CapturePromptCommand(Body, null),
            cancellationToken);
        if (!captureResult.IsSuccess)
        {
            State = CaptureState.SaveFailed;
            PublishStatus(MapSaveError(captureResult.Error!), AnnouncementKind.Assertive);
            OnPropertyChanged(nameof(CanRetrySave));
            return;
        }

        _savedPrompt = captureResult.Value.Prompt;
        RaiseSavedPromptCommands();
        State = CaptureState.PromptSaved;
        IntentFeedback = FormatIntentFeedback(_savedPrompt);
        PublishStatus(
            captureResult.Value.EnrichmentQueued
                ? "Prompt saved. Ollama metadata inference is running."
                : "Prompt saved",
            AnnouncementKind.Polite);
        RequestFocus(FocusTarget.PromptEditor);
    }

    public async Task CopyAsync(CancellationToken cancellationToken = default)
    {
        if (!CanCopy)
        {
            return;
        }

        bool isUnsaved = _savedPrompt is null || State != CaptureState.PromptSaved;
        AppResult result = await _copyPrompt.ExecuteAsync(
            new CopyPromptCommand(_savedPrompt?.Id, Body, false),
            cancellationToken);
        if (result.IsSuccess)
        {
            PublishStatus(isUnsaved ? "Copied unsaved draft" : "Copied prompt", AnnouncementKind.Polite);
            return;
        }

        PublishStatus("Copy failed. Your text remains in the editor.", AnnouncementKind.Assertive);
    }

    public async Task SaveAndCopyAsync()
    {
        await SaveAsync();
        if (State == CaptureState.PromptSaved)
        {
            await CopyAsync();
        }
    }

    private async Task SaveAndOpenMetadataAsync()
    {
        if (_savedPrompt is null)
        {
            await SaveAsync();
        }

        if (_savedPrompt is not null)
        {
            OpenSavedPrompt(FocusTarget.PromptDetailMetadata);
        }
    }

    public async Task StartNewAsync()
    {
        if (State == CaptureState.DraftBackupFailed && !string.IsNullOrWhiteSpace(Body))
        {
            PublishStatus(
                "The current draft is not backed up. Copy it before starting a new prompt.",
                AnnouncementKind.Assertive);
            return;
        }

        await CancelAndDrainDraftBackupAsync();
        if (!string.IsNullOrEmpty(Body))
        {
            await _discardDraft.ExecuteAsync(CancellationToken.None);
        }

        _savedPrompt = null;
        RaiseSavedPromptCommands();
        _body = string.Empty;
        _caretOffset = 0;
        _selectionLength = 0;
        IntentFeedback = string.Empty;
        StatusMessage = string.Empty;
        Announcement = StatusAnnouncement.Empty;
        State = CaptureState.Empty;
        OnPropertyChanged(nameof(Body));
        OnPropertyChanged(nameof(CaretOffset));
        OnPropertyChanged(nameof(SelectionLength));
        RaiseBodyDependentProperties();
        RequestFocus(FocusTarget.PromptEditor);
    }

    public void RequestFocus(FocusTarget target) =>
        FocusRequest = new FocusRequest(target, ++_focusSequence);

    private void ScheduleDraftBackup()
    {
        _backupDebounce?.Cancel();
        long generation = ++_draftGeneration;
        if (string.IsNullOrWhiteSpace(Body))
        {
            return;
        }

        CancellationTokenSource cancellation = new();
        _backupDebounce = cancellation;
        _pendingBackup = DebouncedBackupAsync(generation, cancellation.Token);
    }

    private async Task DebouncedBackupAsync(
        long generation,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(500, cancellationToken);
            if (generation != _draftGeneration)
            {
                return;
            }

            await BackupDraftAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task CancelAndDrainDraftBackupAsync()
    {
        ++_draftGeneration;
        CancellationTokenSource? debounce = _backupDebounce;
        _backupDebounce = null;
        debounce?.Cancel();
        try
        {
            await _pendingBackup;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            debounce?.Dispose();
            _pendingBackup = Task.CompletedTask;
        }
    }

    private void NormalizeSelection()
    {
        _caretOffset = Math.Clamp(_caretOffset, 0, Body.Length);
        _selectionLength = Math.Clamp(_selectionLength, 0, Body.Length - _caretOffset);
        OnPropertyChanged(nameof(CaretOffset));
        OnPropertyChanged(nameof(SelectionLength));
    }

    private void RaiseBodyDependentProperties()
    {
        OnPropertyChanged(nameof(Utf8ByteCount));
        OnPropertyChanged(nameof(ShowSize));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(IsOverSizeLimit));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(CanCopy));
        OnPropertyChanged(nameof(MetadataActionText));
        RaiseCommandStates();
    }

    private void RaiseCommandStates()
    {
        SaveCommand.RaiseCanExecuteChanged();
        CopyCommand.RaiseCanExecuteChanged();
        RetryBackupCommand.RaiseCanExecuteChanged();
        SaveAndCopyCommand.RaiseCanExecuteChanged();
        ReviewMetadataCommand.RaiseCanExecuteChanged();
    }

    private void RaiseSavedPromptCommands()
    {
        ReviewIntentCommand.RaiseCanExecuteChanged();
        ReviewMetadataCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(MetadataActionText));
    }

    private void OpenSavedPrompt(FocusTarget focusTarget)
    {
        if (_savedPrompt is not null)
        {
            OpenSavedPromptRequested?.Invoke(
                this,
                new PromptNavigationRequest(_savedPrompt.Id, focusTarget));
        }
    }

    private void PublishStatus(string text, AnnouncementKind kind)
    {
        StatusMessage = text;
        Announcement = new StatusAnnouncement(text, kind);
    }

    private static string FormatIntentFeedback(PromptDetailsDto prompt)
    {
        if (prompt.IntentAssignment == IntentAssignment.Unsorted)
        {
            return "Assigned to Unsorted - review suggested";
        }

        if (prompt.IntentAssignment == IntentAssignment.AutoMapped && prompt.IntentScoreBasisPoints is int score)
        {
            return $"Matched to existing intent, {score / 100}%";
        }

        if (prompt.IntentReviewState is IntentReviewState.NeedsReview or IntentReviewState.Skipped)
        {
            return "New provisional intent created - review suggested";
        }

        return $"Intent: {prompt.Intent.CanonicalName}";
    }

    private static string MapSaveError(AppError error) =>
        error.Code switch
        {
            AppErrorCode.Conflict =>
                "Prompt was not saved because it changed elsewhere. Your text remains in the editor.",
            AppErrorCode.PersistenceUnavailable =>
                "Prompt was not saved because local storage is unavailable. Your text remains in the editor.",
            AppErrorCode.Validation =>
                $"Prompt was not saved. {error.Message}",
            _ =>
                $"Prompt was not saved. {error.Message} Your text remains in the editor.",
        };

    private static T Require<T>(object services)
        where T : class =>
        services as T ??
        throw new ArgumentException($"The service adapter must implement {typeof(T).Name}.", nameof(services));
}
