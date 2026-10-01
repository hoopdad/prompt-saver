using PromptSaver.Domain.ValueObjects;
using System.ComponentModel;

namespace PromptSaver.Desktop.ViewModels;

public sealed class ShellViewModel : ViewModelBase
{
    private readonly Func<CancellationToken, Task> _optionalInitialization;
    private object _currentPage;
    private ShellPage _currentPageKind;
    private FocusRequest? _focusRequest;
    private long _focusSequence;

    public ShellViewModel(
        CaptureViewModel capture,
        LibraryViewModel library,
        PromptDetailViewModel detail,
        SettingsViewModel settings,
        Func<CancellationToken, Task>? optionalInitialization = null)
    {
        Capture = capture;
        Library = library;
        Detail = detail;
        Settings = settings;
        _currentPage = capture;
        _currentPageKind = ShellPage.Capture;
        _optionalInitialization = optionalInitialization ?? (_ => Task.CompletedTask);
        NewCommand = new DelegateCommand(ShowNewPrompt);
        LibraryCommand = new DelegateCommand(() => ShowLibrary(false));
        SearchLibraryCommand = new DelegateCommand(() => ShowLibrary(true));
        SettingsCommand = new AsyncDelegateCommand(ShowSettingsAsync);
        Library.OpenPromptRequested += async (_, request) =>
            await ShowPromptDetailAsync(request.PromptId, request.FocusTarget);
        Capture.OpenSavedPromptRequested += async (_, request) =>
            await ShowPromptDetailAsync(request.PromptId, request.FocusTarget);
        Detail.PromptDeleted += async (_, _) =>
        {
            await Library.SearchAsync();
            ShowLibrary(false);
        };
        Capture.PropertyChanged += OnChildFocusRequested;
        Library.PropertyChanged += OnChildFocusRequested;
        Detail.PropertyChanged += OnChildFocusRequested;
        RequestFocus(FocusTarget.PromptEditor);
    }

    public CaptureViewModel Capture { get; }

    public LibraryViewModel Library { get; }

    public PromptDetailViewModel Detail { get; }

    public SettingsViewModel Settings { get; }

    public object CurrentPage
    {
        get => _currentPage;
        private set => SetProperty(ref _currentPage, value);
    }

    public ShellPage CurrentPageKind
    {
        get => _currentPageKind;
        private set => SetProperty(ref _currentPageKind, value);
    }

    public FocusRequest? FocusRequest
    {
        get => _focusRequest;
        private set => SetProperty(ref _focusRequest, value);
    }

    public DelegateCommand NewCommand { get; }

    public DelegateCommand LibraryCommand { get; }

    public DelegateCommand SearchLibraryCommand { get; }

    public AsyncDelegateCommand SettingsCommand { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        CurrentPage = Capture;
        CurrentPageKind = ShellPage.Capture;
        RequestFocus(FocusTarget.PromptEditor);
        Task optionalWork = _optionalInitialization(cancellationToken);
        await Capture.InitializeAsync(cancellationToken);
        await optionalWork;
    }

    public void ShowNewPrompt()
    {
        CurrentPage = Capture;
        CurrentPageKind = ShellPage.Capture;
        RequestFocus(FocusTarget.PromptEditor);
    }

    public void ShowLibrary(bool focusSearch)
    {
        CurrentPage = Library;
        CurrentPageKind = ShellPage.Library;
        RequestFocus(focusSearch ? FocusTarget.LibrarySearch : FocusTarget.LibraryResults);
    }

    public Task ShowPromptDetailAsync(PromptId promptId) =>
        ShowPromptDetailAsync(promptId, FocusTarget.PromptDetailHeading);

    public async Task ShowPromptDetailAsync(PromptId promptId, FocusTarget focusTarget)
    {
        CurrentPage = Detail;
        CurrentPageKind = ShellPage.PromptDetail;
        await Detail.LoadAsync(promptId);
        RequestFocus(focusTarget);
    }

    public async Task ShowSettingsAsync()
    {
        ShowSettings();
        await Settings.LoadIntentsAsync();
    }

    public void ShowSettings()
    {
        CurrentPage = Settings;
        CurrentPageKind = ShellPage.Settings;
        RequestFocus(FocusTarget.SettingsCategories);
    }

    public void CycleFocusRegion(bool reverse)
    {
        FocusTarget[] regions = CurrentPageKind switch
        {
            ShellPage.Capture =>
                [FocusTarget.PromptEditor, FocusTarget.CaptureActions, FocusTarget.Navigation],
            ShellPage.Library =>
                [FocusTarget.LibrarySearch, FocusTarget.LibraryFilters, FocusTarget.LibraryResults, FocusTarget.LibraryMoreOptions, FocusTarget.LibraryStatus],
            ShellPage.PromptDetail =>
                [FocusTarget.PromptDetailHeading, FocusTarget.PromptDetailEditor, FocusTarget.PromptDetailMetadata, FocusTarget.PromptDetailActions, FocusTarget.PromptDetailStatus],
            ShellPage.Settings =>
                [FocusTarget.SettingsCategories, FocusTarget.SettingsContent, FocusTarget.SettingsActions, FocusTarget.SettingsStatus],
            _ => [FocusTarget.Navigation],
        };
        int currentIndex = Array.FindIndex(regions, target => target == FocusRequest?.Target);
        int delta = reverse ? -1 : 1;
        int nextIndex = (currentIndex + delta + regions.Length) % regions.Length;
        RequestFocus(regions[nextIndex]);
    }

    public void RequestFocus(FocusTarget target) =>
        FocusRequest = new FocusRequest(target, ++_focusSequence);

    private void OnChildFocusRequested(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CaptureViewModel.FocusRequest))
        {
            return;
        }

        FocusRequest? request = sender switch
        {
            CaptureViewModel capture when ReferenceEquals(CurrentPage, capture) =>
                capture.FocusRequest,
            LibraryViewModel library when ReferenceEquals(CurrentPage, library) =>
                library.FocusRequest,
            PromptDetailViewModel detail when ReferenceEquals(CurrentPage, detail) =>
                detail.FocusRequest,
            _ => null,
        };
        if (request is not null)
        {
            RequestFocus(request.Target);
        }
    }
}
