using System.Collections.ObjectModel;
using PromptSaver.Application;
using PromptSaver.Application.Dtos;
using PromptSaver.Application.UseCases;
using PromptSaver.Domain.ValueObjects;

namespace PromptSaver.Desktop.ViewModels;

public sealed class LibraryViewModel : ViewModelBase
{
    private readonly ISearchPrompts _searchPrompts;
    private readonly ICopyPrompt _copyPrompt;
    private readonly IGetPromptDetails _getPromptDetails;
    private readonly IDuplicatePrompt _duplicatePrompt;
    private readonly IDeletePrompt _deletePrompt;
    private string _searchText = string.Empty;
    private bool _needsReviewOnly;
    private bool _isSearching;
    private string _statusMessage = "Search your saved prompts";
    private PromptSummaryDto? _selectedPrompt;
    private int _pageNumber = 1;
    private PromptSummaryDto? _moreOptionsPrompt;
    private bool _deleteConfirmationOpen;
    private FocusRequest? _focusRequest;
    private long _focusSequence;

    public LibraryViewModel(object services)
    {
        _searchPrompts = Require<ISearchPrompts>(services);
        _copyPrompt = Require<ICopyPrompt>(services);
        _getPromptDetails = Require<IGetPromptDetails>(services);
        _duplicatePrompt = Require<IDuplicatePrompt>(services);
        _deletePrompt = Require<IDeletePrompt>(services);
        SearchCommand = new AsyncDelegateCommand(SearchAsync);
        ClearSearchCommand = new DelegateCommand(ClearSearchStage);
        ViewAllCommand = new AsyncDelegateCommand(ViewAllAsync);
        OpenPromptCommand = new ParameterizedCommand<PromptSummaryDto>(
            prompt => OpenPromptRequested?.Invoke(
                this,
                new PromptNavigationRequest(prompt.Id, FocusTarget.PromptDetailHeading)));
        CopyPromptCommand = new AsyncParameterizedCommand<PromptSummaryDto>(CopyPromptAsync);
        MoreOptionsCommand = new ParameterizedCommand<PromptSummaryDto>(OpenMoreOptions);
        ReviewIntentCommand = new DelegateCommand(
            () => OpenMoreOptionsPrompt(FocusTarget.PromptDetailMetadata),
            () => MoreOptionsPrompt is not null);
        ReviewMetadataCommand = new DelegateCommand(
            () => OpenMoreOptionsPrompt(FocusTarget.PromptDetailMetadata),
            () => MoreOptionsPrompt is not null);
        DuplicatePromptCommand = new AsyncDelegateCommand(
            DuplicateMoreOptionsPromptAsync,
            () => MoreOptionsPrompt is not null);
        RequestPermanentDeleteCommand = new DelegateCommand(
            () => DeleteConfirmationOpen = true,
            () => MoreOptionsPrompt is not null);
        CancelPermanentDeleteCommand = new DelegateCommand(CancelPermanentDelete);
        ConfirmPermanentDeleteCommand = new AsyncDelegateCommand(
            DeleteMoreOptionsPromptAsync,
            () => DeleteConfirmationOpen && MoreOptionsPrompt is not null);
    }

    public ObservableCollection<PromptSummaryDto> Results { get; } = [];

    public string SearchText
    {
        get => _searchText;
        set => SetProperty(ref _searchText, value ?? string.Empty);
    }

    public bool NeedsReviewOnly
    {
        get => _needsReviewOnly;
        set => SetProperty(ref _needsReviewOnly, value);
    }

    public bool IsSearching
    {
        get => _isSearching;
        private set => SetProperty(ref _isSearching, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public PromptSummaryDto? SelectedPrompt
    {
        get => _selectedPrompt;
        set => SetProperty(ref _selectedPrompt, value);
    }

    public long TotalCount { get; private set; }

    public PromptSummaryDto? MoreOptionsPrompt
    {
        get => _moreOptionsPrompt;
        private set
        {
            if (SetProperty(ref _moreOptionsPrompt, value))
            {
                OnPropertyChanged(nameof(HasMoreOptionsPrompt));
                RaiseMoreOptionCommands();
            }
        }
    }

    public bool HasMoreOptionsPrompt => MoreOptionsPrompt is not null;

    public bool DeleteConfirmationOpen
    {
        get => _deleteConfirmationOpen;
        private set
        {
            if (SetProperty(ref _deleteConfirmationOpen, value))
            {
                ConfirmPermanentDeleteCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public FocusRequest? FocusRequest
    {
        get => _focusRequest;
        private set => SetProperty(ref _focusRequest, value);
    }

    public event EventHandler<PromptNavigationRequest>? OpenPromptRequested;

    public AsyncDelegateCommand SearchCommand { get; }

    public DelegateCommand ClearSearchCommand { get; }

    public AsyncDelegateCommand ViewAllCommand { get; }

    public ParameterizedCommand<PromptSummaryDto> OpenPromptCommand { get; }

    public AsyncParameterizedCommand<PromptSummaryDto> CopyPromptCommand { get; }

    public ParameterizedCommand<PromptSummaryDto> MoreOptionsCommand { get; }

    public DelegateCommand ReviewIntentCommand { get; }

    public DelegateCommand ReviewMetadataCommand { get; }

    public AsyncDelegateCommand DuplicatePromptCommand { get; }

    public DelegateCommand RequestPermanentDeleteCommand { get; }

    public DelegateCommand CancelPermanentDeleteCommand { get; }

    public AsyncDelegateCommand ConfirmPermanentDeleteCommand { get; }

    public async Task SearchAsync()
    {
        IsSearching = true;
        try
        {
            var result = await _searchPrompts.ExecuteAsync(
                new SearchPromptsQuery(SearchText, _pageNumber, 50, needsReviewOnly: NeedsReviewOnly),
                CancellationToken.None);
            Results.Clear();
            if (!result.IsSuccess)
            {
                StatusMessage = result.Error?.Code == Application.AppErrorCode.SearchInvalid
                    ? "Search could not be understood. Try fewer filters or literal words."
                    : $"Search is unavailable. {result.Error?.Message}";
                return;
            }

            foreach (PromptSummaryDto item in result.Value.Items)
            {
                Results.Add(item);
            }

            TotalCount = result.Value.TotalCount;
            OnPropertyChanged(nameof(TotalCount));
            StatusMessage = TotalCount == 1 ? "1 result" : $"{TotalCount:N0} results";
        }
        finally
        {
            IsSearching = false;
        }
    }

    public void ClearSearchStage()
    {
        if (SearchText.Length > 0)
        {
            SearchText = string.Empty;
            return;
        }

        if (NeedsReviewOnly)
        {
            NeedsReviewOnly = false;
        }
    }

    private async Task ViewAllAsync()
    {
        SearchText = string.Empty;
        NeedsReviewOnly = false;
        _pageNumber = 1;
        await SearchAsync();
        RequestFocus(FocusTarget.LibraryResults);
    }

    private async Task CopyPromptAsync(PromptSummaryDto prompt)
    {
        AppResult<PromptDetailsDto> details = await _getPromptDetails.ExecuteAsync(
            prompt.Id,
            CancellationToken.None);
        if (!details.IsSuccess)
        {
            StatusMessage = $"Copy failed. {details.Error?.Message}";
            return;
        }

        AppResult result = await _copyPrompt.ExecuteAsync(
            new CopyPromptCommand(prompt.Id, details.Value.Body, false),
            CancellationToken.None);
        StatusMessage = result.IsSuccess
            ? $"Copied \"{prompt.Title ?? prompt.Intent.CanonicalName}\""
            : $"Copy failed. {result.Error?.Message}";
    }

    private void OpenMoreOptions(PromptSummaryDto prompt)
    {
        MoreOptionsPrompt = prompt;
        DeleteConfirmationOpen = false;
        RequestFocus(FocusTarget.LibraryMoreOptions);
    }

    private void OpenMoreOptionsPrompt(FocusTarget focusTarget)
    {
        if (MoreOptionsPrompt is not null)
        {
            OpenPromptRequested?.Invoke(
                this,
                new PromptNavigationRequest(MoreOptionsPrompt.Id, focusTarget));
        }
    }

    private async Task DuplicateMoreOptionsPromptAsync()
    {
        if (MoreOptionsPrompt is null)
        {
            return;
        }

        AppResult<PromptDetailsDto> result = await _duplicatePrompt.ExecuteAsync(
            new DuplicatePromptCommand(MoreOptionsPrompt.Id),
            CancellationToken.None);
        if (!result.IsSuccess)
        {
            StatusMessage = $"Prompt was not duplicated. {result.Error?.Message}";
            return;
        }

        StatusMessage = "Prompt duplicated";
        OpenPromptRequested?.Invoke(
            this,
            new PromptNavigationRequest(result.Value.Id, FocusTarget.PromptDetailHeading));
    }

    private async Task DeleteMoreOptionsPromptAsync()
    {
        if (MoreOptionsPrompt is null)
        {
            return;
        }

        int deletedIndex = Results.IndexOf(MoreOptionsPrompt);
        AppResult<DeletePromptResult> result = await _deletePrompt.ExecuteAsync(
            new DeletePromptCommand(MoreOptionsPrompt.Id, true),
            CancellationToken.None);
        DeleteConfirmationOpen = false;
        if (!result.IsSuccess)
        {
            StatusMessage = $"Prompt was not deleted. {result.Error?.Message}";
            RequestFocus(FocusTarget.LibraryMoreOptions);
            return;
        }

        MoreOptionsPrompt = null;
        await SearchAsync();
        SelectedPrompt = Results.Count == 0
            ? null
            : Results[Math.Clamp(deletedIndex, 0, Results.Count - 1)];
        StatusMessage = "Prompt permanently deleted";
        RequestFocus(FocusTarget.LibraryResults);
    }

    private void CancelPermanentDelete()
    {
        DeleteConfirmationOpen = false;
        RequestFocus(FocusTarget.LibraryMoreOptions);
    }

    private void RaiseMoreOptionCommands()
    {
        ReviewIntentCommand.RaiseCanExecuteChanged();
        ReviewMetadataCommand.RaiseCanExecuteChanged();
        DuplicatePromptCommand.RaiseCanExecuteChanged();
        RequestPermanentDeleteCommand.RaiseCanExecuteChanged();
        ConfirmPermanentDeleteCommand.RaiseCanExecuteChanged();
    }

    private void RequestFocus(FocusTarget target) =>
        FocusRequest = new FocusRequest(target, ++_focusSequence);

    private static T Require<T>(object services)
        where T : class =>
        services as T ??
        throw new ArgumentException($"The service adapter must implement {typeof(T).Name}.", nameof(services));
}
