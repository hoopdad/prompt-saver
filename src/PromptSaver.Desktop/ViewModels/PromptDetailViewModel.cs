using PromptSaver.Application;
using PromptSaver.Application.Dtos;
using PromptSaver.Application.UseCases;
using PromptSaver.Domain.Entities;
using PromptSaver.Domain.ValueObjects;

namespace PromptSaver.Desktop.ViewModels;

public sealed class PromptDetailViewModel : ViewModelBase
{
    private readonly IGetPromptDetails _getDetails;
    private readonly ICommitPromptEdit _commitEdit;
    private readonly ICopyPrompt _copyPrompt;
    private readonly IDuplicatePrompt _duplicatePrompt;
    private readonly IDeletePrompt _deletePrompt;
    private readonly IReviewIntentAssignment _reviewIntent;
    private readonly IManageMetadata _manageMetadata;
    private readonly IQueryPromptIntent _queryPromptIntent;
    private PromptDetailsDto? _prompt;
    private string _body = string.Empty;
    private string? _title;
    private string _statusMessage = string.Empty;
    private bool _deleteConfirmationOpen;
    private IntentCandidateDto? _selectedIntentCandidate;
    private string _skillsText = string.Empty;
    private string _entitiesText = string.Empty;
    private bool _isMetadataEditing;
    private bool _isQueryingIntent;
    private string _llmIntentSuggestion = string.Empty;
    private string _intentText = string.Empty;
    private FocusRequest? _focusRequest;
    private long _focusSequence;

    public PromptDetailViewModel(object services)
    {
        _getDetails = Require<IGetPromptDetails>(services);
        _commitEdit = Require<ICommitPromptEdit>(services);
        _copyPrompt = Require<ICopyPrompt>(services);
        _duplicatePrompt = Require<IDuplicatePrompt>(services);
        _deletePrompt = Require<IDeletePrompt>(services);
        _reviewIntent = Require<IReviewIntentAssignment>(services);
        _manageMetadata = Require<IManageMetadata>(services);
        _queryPromptIntent = Require<IQueryPromptIntent>(services);
        SaveCommand = new AsyncDelegateCommand(SaveAsync, () => HasUnsavedChanges);
        CopyCommand = new AsyncDelegateCommand(CopyAsync, () => Prompt is not null);
        DuplicateCommand = new AsyncDelegateCommand(DuplicateAsync, () => Prompt is not null);
        RequestDeleteCommand = new DelegateCommand(() => DeleteConfirmationOpen = true);
        CancelDeleteCommand = new DelegateCommand(CancelDelete);
        ConfirmDeleteCommand = new AsyncDelegateCommand(DeleteAsync, () => DeleteConfirmationOpen);
        ApplyIntentReviewCommand = new AsyncDelegateCommand(
            ApplyIntentReviewAsync,
            () => Prompt is not null && SelectedIntentCandidate is not null);
        ReviewLaterCommand = new AsyncDelegateCommand(
            ReviewLaterAsync,
            () => Prompt?.IntentReviewState == IntentReviewState.NeedsReview);
        KeepCurrentIntentCommand = new AsyncDelegateCommand(
            KeepCurrentIntentAsync,
            () => Prompt?.IntentReviewState is IntentReviewState.NeedsReview or IntentReviewState.Skipped);
        EditMetadataCommand = new DelegateCommand(BeginMetadataEdit, () => Prompt is not null);
        SaveMetadataCommand = new AsyncDelegateCommand(
            SaveMetadataAsync,
            () => Prompt is not null && IsMetadataEditing);
        CancelMetadataCommand = new DelegateCommand(CancelMetadataEdit, () => IsMetadataEditing);
        QueryIntentCommand = new AsyncDelegateCommand(
            QueryIntentAsync,
            () => Prompt is not null && !HasUnsavedChanges && !IsQueryingIntent);
        UseIntentCommand = new AsyncDelegateCommand(
            UseIntentAsync,
            () => Prompt is not null &&
                !HasUnsavedChanges &&
                !IsQueryingIntent &&
                !string.IsNullOrWhiteSpace(IntentText));
    }

    public PromptDetailsDto? Prompt
    {
        get => _prompt;
        private set
        {
            if (SetProperty(ref _prompt, value))
            {
                OnPropertyChanged(nameof(IntentFeedback));
                OnPropertyChanged(nameof(MetadataSummary));
            }
        }
    }

    public string Body
    {
        get => _body;
        set
        {
            if (SetProperty(ref _body, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(HasUnsavedChanges));
                SaveCommand.RaiseCanExecuteChanged();
                QueryIntentCommand.RaiseCanExecuteChanged();
                UseIntentCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string? Title
    {
        get => _title;
        set
        {
            if (SetProperty(ref _title, value))
            {
                OnPropertyChanged(nameof(HasUnsavedChanges));
                SaveCommand.RaiseCanExecuteChanged();
                QueryIntentCommand.RaiseCanExecuteChanged();
                UseIntentCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public bool DeleteConfirmationOpen
    {
        get => _deleteConfirmationOpen;
        set => SetProperty(ref _deleteConfirmationOpen, value);
    }

    public IntentCandidateDto? SelectedIntentCandidate
    {
        get => _selectedIntentCandidate;
        set
        {
            if (SetProperty(ref _selectedIntentCandidate, value))
            {
                ApplyIntentReviewCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string SkillsText
    {
        get => _skillsText;
        set => SetProperty(ref _skillsText, value ?? string.Empty);
    }

    public string EntitiesText
    {
        get => _entitiesText;
        set => SetProperty(ref _entitiesText, value ?? string.Empty);
    }

    public bool IsMetadataEditing
    {
        get => _isMetadataEditing;
        private set
        {
            if (SetProperty(ref _isMetadataEditing, value))
            {
                SaveMetadataCommand.RaiseCanExecuteChanged();
                CancelMetadataCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsQueryingIntent
    {
        get => _isQueryingIntent;
        private set
        {
            if (SetProperty(ref _isQueryingIntent, value))
            {
                QueryIntentCommand.RaiseCanExecuteChanged();
                UseIntentCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string LlmIntentSuggestion
    {
        get => _llmIntentSuggestion;
        private set
        {
            if (SetProperty(ref _llmIntentSuggestion, value))
            {
                OnPropertyChanged(nameof(HasLlmIntentSuggestion));
            }
        }
    }

    public bool HasLlmIntentSuggestion => !string.IsNullOrWhiteSpace(LlmIntentSuggestion);

    public string IntentText
    {
        get => _intentText;
        set
        {
            if (SetProperty(ref _intentText, value ?? string.Empty))
            {
                UseIntentCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasUnsavedChanges =>
        Prompt is not null && (Body != Prompt.Body || Title != Prompt.Title);

    public string IntentFeedback =>
        Prompt is null
            ? string.Empty
            : Prompt.IntentAssignment == IntentAssignment.Unsorted
                ? "Assigned to Unsorted - review suggested"
                : Prompt.IntentReviewState is IntentReviewState.NeedsReview or IntentReviewState.Skipped
                    ? "Provisional intent - review suggested"
                    : Prompt.IntentScoreBasisPoints is int score
                        ? $"Matched to existing intent, {score / 100}%"
                        : "Intent selected";

    public string MetadataSummary =>
        Prompt is null
            ? string.Empty
            : $"Intent: {Prompt.Intent.CanonicalName}; Skills: {Prompt.Skills.Count}; Entities: {Prompt.Entities.Count}";

    public PromptId? SuggestedFocusPromptId { get; private set; }

    public FocusRequest? FocusRequest
    {
        get => _focusRequest;
        private set => SetProperty(ref _focusRequest, value);
    }

    public event EventHandler? PromptDeleted;

    public AsyncDelegateCommand SaveCommand { get; }

    public AsyncDelegateCommand CopyCommand { get; }

    public AsyncDelegateCommand DuplicateCommand { get; }

    public DelegateCommand RequestDeleteCommand { get; }

    public DelegateCommand CancelDeleteCommand { get; }

    public AsyncDelegateCommand ConfirmDeleteCommand { get; }

    public AsyncDelegateCommand ApplyIntentReviewCommand { get; }

    public AsyncDelegateCommand ReviewLaterCommand { get; }

    public AsyncDelegateCommand KeepCurrentIntentCommand { get; }

    public DelegateCommand EditMetadataCommand { get; }

    public AsyncDelegateCommand SaveMetadataCommand { get; }

    public DelegateCommand CancelMetadataCommand { get; }

    public AsyncDelegateCommand QueryIntentCommand { get; }

    public AsyncDelegateCommand UseIntentCommand { get; }

    public async Task LoadAsync(PromptId promptId)
    {
        AppResult<PromptDetailsDto> result = await _getDetails.ExecuteAsync(promptId, CancellationToken.None);
        if (!result.IsSuccess)
        {
            StatusMessage = $"Prompt could not be opened. {result.Error?.Message}";
            return;
        }

        ApplyPrompt(result.Value);
    }

    private async Task SaveAsync()
    {
        if (Prompt is null || !HasUnsavedChanges)
        {
            return;
        }

        AppResult<PromptDetailsDto> result = await _commitEdit.ExecuteAsync(
            new CommitPromptEditCommand(Prompt.Id, Body, Title, Prompt.Version),
            CancellationToken.None);
        if (!result.IsSuccess)
        {
            StatusMessage = result.Error?.Code == AppErrorCode.Conflict
                ? "This prompt changed elsewhere. Reload it or duplicate your edits."
                : $"Changes were not saved. {result.Error?.Message}";
            return;
        }

        ApplyPrompt(result.Value);
        StatusMessage = "Changes saved";
    }

    private async Task CopyAsync()
    {
        if (Prompt is null)
        {
            return;
        }

        AppResult result = await _copyPrompt.ExecuteAsync(
            new CopyPromptCommand(Prompt.Id, Body, false),
            CancellationToken.None);
        StatusMessage = result.IsSuccess ? $"Copied \"{Title ?? Prompt.Intent.CanonicalName}\"" : "Copy failed";
    }

    private async Task DuplicateAsync()
    {
        if (Prompt is null)
        {
            return;
        }

        AppResult<PromptDetailsDto> result = await _duplicatePrompt.ExecuteAsync(
            new DuplicatePromptCommand(Prompt.Id),
            CancellationToken.None);
        if (result.IsSuccess)
        {
            ApplyPrompt(result.Value);
            StatusMessage = "Prompt duplicated";
        }
        else
        {
            StatusMessage = $"Prompt was not duplicated. {result.Error?.Message}";
        }
    }

    private async Task DeleteAsync()
    {
        if (Prompt is null)
        {
            return;
        }

        AppResult<DeletePromptResult> result = await _deletePrompt.ExecuteAsync(
            new DeletePromptCommand(Prompt.Id, true),
            CancellationToken.None);
        DeleteConfirmationOpen = false;
        if (result.IsSuccess)
        {
            SuggestedFocusPromptId = result.Value.SuggestedFocusPromptId;
            StatusMessage = "Prompt deleted";
            PromptDeleted?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            StatusMessage = $"Prompt was not deleted. {result.Error?.Message}";
        }
    }

    private void CancelDelete()
    {
        DeleteConfirmationOpen = false;
        FocusRequest = new FocusRequest(FocusTarget.PromptDetailActions, ++_focusSequence);
    }

    private async Task ApplyIntentReviewAsync()
    {
        if (Prompt is null || SelectedIntentCandidate is null)
        {
            return;
        }

        AppResult<PromptDetailsDto> result = await _reviewIntent.ExecuteAsync(
            new ReviewIntentAssignmentCommand(
                Prompt.Id,
                IntentReviewAction.ChooseExisting,
                SelectedIntentCandidate.IntentId,
                null,
                Prompt.Version),
            CancellationToken.None);
        if (result.IsSuccess)
        {
            ApplyPrompt(result.Value);
            StatusMessage = "Intent review completed";
        }
        else
        {
            StatusMessage = $"Intent review was not saved. {result.Error?.Message}";
        }
    }

    private async Task ReviewLaterAsync()
    {
        if (Prompt is null)
        {
            return;
        }

        AppResult<PromptDetailsDto> result = await _reviewIntent.ExecuteAsync(
            new ReviewIntentAssignmentCommand(
                Prompt.Id,
                IntentReviewAction.ReviewLater,
                null,
                null,
                Prompt.Version),
            CancellationToken.None);
        if (result.IsSuccess)
        {
            ApplyPrompt(result.Value);
            StatusMessage = "Intent review deferred";
        }
        else
        {
            StatusMessage = $"Intent review was not updated. {result.Error?.Message}";
        }
    }

    private async Task KeepCurrentIntentAsync()
    {
        if (Prompt is null)
        {
            return;
        }

        AppResult<PromptDetailsDto> result = await _reviewIntent.ExecuteAsync(
            new ReviewIntentAssignmentCommand(
                Prompt.Id,
                IntentReviewAction.KeepProvisional,
                null,
                null,
                Prompt.Version),
            CancellationToken.None);
        if (result.IsSuccess)
        {
            ApplyPrompt(result.Value);
            StatusMessage = "Intent review completed";
        }
        else
        {
            StatusMessage = $"Intent review was not saved. {result.Error?.Message}";
        }
    }

    private void BeginMetadataEdit()
    {
        if (Prompt is null)
        {
            return;
        }

        ResetMetadataText(Prompt);
        IsMetadataEditing = true;
        FocusRequest = new FocusRequest(FocusTarget.PromptDetailMetadata, ++_focusSequence);
    }

    private async Task SaveMetadataAsync()
    {
        if (Prompt is null || !IsMetadataEditing)
        {
            return;
        }

        IReadOnlyList<SkillSelectionDto> skills = SkillsText
            .Split([',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(name => new SkillSelectionDto(null, name))
            .ToArray();
        List<EntitySelectionDto> entities = [];
        foreach (string line in EntitiesText.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] parts = line.Split('|', 2, StringSplitOptions.TrimEntries);
            EntityType type = EntityType.Other;
            if (parts.Length == 2 &&
                !Enum.TryParse(parts[1], ignoreCase: true, out type))
            {
                StatusMessage = $"Unknown entity type \"{parts[1]}\".";
                return;
            }

            entities.Add(new EntitySelectionDto(null, parts[0], type));
        }

        AppResult<PromptDetailsDto> result = await _manageMetadata.ExecuteAsync(
            new ManageMetadataCommand(Prompt.Id, skills, entities, Prompt.Version),
            CancellationToken.None);
        if (result.IsSuccess)
        {
            ApplyPrompt(result.Value);
            IsMetadataEditing = false;
            StatusMessage = "Metadata saved";
        }
        else
        {
            StatusMessage = $"Metadata was not saved. {result.Error?.Message}";
        }
    }

    private void CancelMetadataEdit()
    {
        if (Prompt is not null)
        {
            ResetMetadataText(Prompt);
        }

        IsMetadataEditing = false;
        FocusRequest = new FocusRequest(FocusTarget.PromptDetailMetadata, ++_focusSequence);
    }

    private async Task QueryIntentAsync()
    {
        if (Prompt is null || HasUnsavedChanges)
        {
            return;
        }

        IsQueryingIntent = true;
        LlmIntentSuggestion = string.Empty;
        StatusMessage = "Asking the LLM for an intent...";
        try
        {
            AppResult<PromptIntentSuggestionDto> result = await _queryPromptIntent.ExecuteAsync(
                new QueryPromptIntentCommand(Prompt.Id),
                CancellationToken.None);
            if (result.IsSuccess)
            {
                LlmIntentSuggestion = result.Value.Intent;
                IntentText = result.Value.Intent;
                StatusMessage =
                    $"Suggestion loaded from {result.Value.ProviderName} ({result.Value.Model}). Review it, then choose Use as intent.";
            }
            else
            {
                StatusMessage = $"The LLM could not suggest an intent. {result.Error?.Message}";
            }
        }
        finally
        {
            IsQueryingIntent = false;
        }
    }

    private async Task UseIntentAsync()
    {
        if (Prompt is null || string.IsNullOrWhiteSpace(IntentText))
        {
            return;
        }

        string intent = IntentText.Trim();
        AppResult<PromptDetailsDto> result = await _reviewIntent.ExecuteAsync(
            new ReviewIntentAssignmentCommand(
                Prompt.Id,
                IntentReviewAction.RenameProvisional,
                null,
                intent,
                Prompt.Version),
            CancellationToken.None);
        if (result.IsSuccess)
        {
            ApplyPrompt(result.Value);
            LlmIntentSuggestion = string.Empty;
            StatusMessage = $"Intent set to \"{result.Value.Intent.CanonicalName}\".";
        }
        else
        {
            StatusMessage = $"Intent was not saved. {result.Error?.Message}";
        }
    }

    private void ApplyPrompt(PromptDetailsDto prompt)
    {
        Prompt = prompt;
        SelectedIntentCandidate =
            prompt.IntentCandidates.Count > 0 ? prompt.IntentCandidates[0] : null;
        _body = prompt.Body;
        _title = prompt.Title;
        OnPropertyChanged(nameof(Body));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(HasUnsavedChanges));
        QueryIntentCommand.RaiseCanExecuteChanged();
        IntentText = prompt.IntentAssignment == IntentAssignment.Unsorted
            ? string.Empty
            : prompt.Intent.CanonicalName;
        ResetMetadataText(prompt);
        KeepCurrentIntentCommand.RaiseCanExecuteChanged();
        EditMetadataCommand.RaiseCanExecuteChanged();
        UseIntentCommand.RaiseCanExecuteChanged();
    }

    private void ResetMetadataText(PromptDetailsDto prompt)
    {
        SkillsText = string.Join(", ", prompt.Skills.Select(skill => skill.Name));
        EntitiesText = string.Join(
            Environment.NewLine,
            prompt.Entities.Select(entity => $"{entity.Name} | {entity.Type}"));
    }

    private static T Require<T>(object services)
        where T : class =>
        services as T ??
        throw new ArgumentException($"The service adapter must implement {typeof(T).Name}.", nameof(services));
}
