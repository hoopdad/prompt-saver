using PromptSaver.Domain.ValueObjects;
using System.ComponentModel;

namespace PromptSaver.Desktop.ViewModels;

public sealed class ShellViewModel : ViewModelBase
{
    private readonly Func<CancellationToken, Task> _optionalInitialization;
    private object _currentPage;
    private ShellPage _currentPageKind;
    private object _previousPage;
    private ShellPage _previousPageKind;
    private FocusRequest? _focusRequest;
    private long _focusSequence;
    private bool _isDarkTheme;
    private int _zoomPercentage = 100;

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
        _previousPage = capture;
        _previousPageKind = ShellPage.Capture;
        _optionalInitialization = optionalInitialization ?? (_ => Task.CompletedTask);
        NewCommand = new AsyncDelegateCommand(ShowNewPromptAsync);
        LibraryCommand = new DelegateCommand(() => ShowLibrary(false));
        SearchLibraryCommand = new DelegateCommand(() => ShowLibrary(true));
        SettingsCommand = new AsyncDelegateCommand(ShowSettingsAsync);
        CloseSettingsCommand = new DelegateCommand(CloseSettings);
        ToggleThemeCommand = new DelegateCommand(ToggleTheme);
        ZoomOutCommand = new DelegateCommand(
            () => ChangeZoom(-10),
            () => ZoomPercentage > 80);
        ResetZoomCommand = new DelegateCommand(() => ZoomPercentage = 100);
        ZoomInCommand = new DelegateCommand(
            () => ChangeZoom(10),
            () => ZoomPercentage < 160);
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
        Settings.PropertyChanged += OnChildStatusChanged;
        Capture.PropertyChanged += OnChildStatusChanged;
        Library.PropertyChanged += OnChildStatusChanged;
        Detail.PropertyChanged += OnChildStatusChanged;
        RequestFocus(FocusTarget.PromptEditor);
    }

    public CaptureViewModel Capture { get; }

    public LibraryViewModel Library { get; }

    public PromptDetailViewModel Detail { get; }

    public SettingsViewModel Settings { get; }

    public object CurrentPage
    {
        get => _currentPage;
        private set
        {
            if (SetProperty(ref _currentPage, value))
            {
                OnPropertyChanged(nameof(StatusMessage));
            }
        }
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

    public bool IsDarkTheme
    {
        get => _isDarkTheme;
        private set
        {
            if (SetProperty(ref _isDarkTheme, value))
            {
                OnPropertyChanged(nameof(ThemeToggleText));
            }
        }
    }

    public string ThemeToggleText => IsDarkTheme ? "Light mode" : "Dark mode";

    public string WindowTitle { get; } = $"Prompt Saver v{GetApplicationVersion()}";

    public int ZoomPercentage
    {
        get => _zoomPercentage;
        private set
        {
            int bounded = Math.Clamp(value, 80, 160);
            if (SetProperty(ref _zoomPercentage, bounded))
            {
                OnPropertyChanged(nameof(ZoomText));
                ZoomOutCommand.RaiseCanExecuteChanged();
                ZoomInCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string ZoomText => $"{ZoomPercentage}%";

    public string StatusMessage =>
        CurrentPage switch
        {
            CaptureViewModel capture => capture.StatusMessage,
            LibraryViewModel library => library.StatusMessage,
            PromptDetailViewModel detail => detail.StatusMessage,
            SettingsViewModel settings => settings.StatusMessage,
            _ => "Ready",
        };

    public AsyncDelegateCommand NewCommand { get; }

    public DelegateCommand LibraryCommand { get; }

    public DelegateCommand SearchLibraryCommand { get; }

    public AsyncDelegateCommand SettingsCommand { get; }

    public DelegateCommand CloseSettingsCommand { get; }

    public DelegateCommand ToggleThemeCommand { get; }

    public DelegateCommand ZoomOutCommand { get; }

    public DelegateCommand ResetZoomCommand { get; }

    public DelegateCommand ZoomInCommand { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        CurrentPage = Capture;
        CurrentPageKind = ShellPage.Capture;
        RequestFocus(FocusTarget.PromptEditor);
        Task optionalWork = _optionalInitialization(cancellationToken);
        await Capture.InitializeAsync(cancellationToken);
        await optionalWork;
    }

    public async Task ShowNewPromptAsync()
    {
        CurrentPage = Capture;
        CurrentPageKind = ShellPage.Capture;
        await Capture.StartNewAsync();
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
        await Settings.LoadAsync();
    }

    public void ShowSettings()
    {
        if (CurrentPageKind != ShellPage.Settings)
        {
            _previousPage = CurrentPage;
            _previousPageKind = CurrentPageKind;
        }

        CurrentPage = Settings;
        CurrentPageKind = ShellPage.Settings;
        RequestFocus(FocusTarget.SettingsCategories);
    }

    public void CloseSettings()
    {
        CurrentPage = _previousPage;
        CurrentPageKind = _previousPageKind;
        RequestFocus(_previousPageKind switch
        {
            ShellPage.Capture => FocusTarget.PromptEditor,
            ShellPage.Library => FocusTarget.LibraryResults,
            ShellPage.PromptDetail => FocusTarget.PromptDetailHeading,
            _ => FocusTarget.Navigation,
        });
    }

    private void ToggleTheme() => IsDarkTheme = !IsDarkTheme;

    public void ChangeZoom(int percentageDelta) =>
        ZoomPercentage += percentageDelta;

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

    private void OnChildStatusChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CaptureViewModel.StatusMessage) &&
            ReferenceEquals(CurrentPage, sender))
        {
            OnPropertyChanged(nameof(StatusMessage));
        }
    }

    private static string GetApplicationVersion()
    {
        Version? version = typeof(ShellViewModel).Assembly.GetName().Version;
        return version is null
            ? "0.0.0"
            : $"{version.Major}.{version.Minor}.{version.Build}";
    }
}
