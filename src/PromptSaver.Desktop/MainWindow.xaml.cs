using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PromptSaver.Desktop.ViewModels;

namespace PromptSaver.Desktop;

public partial class MainWindow : Window
{
    private readonly ShellViewModel _viewModel;

    public MainWindow(ShellViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = _viewModel;
        InitializeComponent();
        _viewModel.PropertyChanged += OnShellPropertyChanged;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyResponsiveLayout(ActualWidth);
        MoveFocus(_viewModel.FocusRequest?.Target ?? FocusTarget.PromptEditor);
        _ = _viewModel.InitializeAsync();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _viewModel.PropertyChanged -= OnShellPropertyChanged;
        _viewModel.Settings.Dispose();
    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e) =>
        ApplyResponsiveLayout(e.NewSize.Width);

    private void ApplyResponsiveLayout(double width)
    {
        bool compact = width < 720;
        CompactHeader.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        NavigationRail.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        NavigationColumn.Width = compact ? new GridLength(0) : new GridLength(180);
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.FocusRequest) &&
            _viewModel.FocusRequest is FocusRequest request)
        {
            Dispatcher.BeginInvoke(
                DispatcherPriority.Loaded,
                () => MoveFocus(request.Target));
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F6)
        {
            _viewModel.CycleFocusRegion(
                (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            ExecuteCurrentSave();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter &&
            Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) &&
            _viewModel.CurrentPage is CaptureViewModel capture)
        {
            capture.SaveAndCopyCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.C &&
            Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            ExecuteCurrentCopy();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.S &&
            Keyboard.Modifiers == ModifierKeys.Control &&
            _viewModel.CurrentPage is PromptDetailViewModel detail)
        {
            detail.SaveCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Left &&
            Keyboard.Modifiers == ModifierKeys.Alt &&
            _viewModel.CurrentPageKind == ShellPage.PromptDetail)
        {
            _viewModel.ShowLibrary(false);
            e.Handled = true;
        }
    }

    private void ExecuteCurrentSave()
    {
        switch (_viewModel.CurrentPage)
        {
            case CaptureViewModel capture:
                capture.SaveCommand.Execute(null);
                break;
            case PromptDetailViewModel detail:
                detail.SaveCommand.Execute(null);
                break;
        }
    }

    private void ExecuteCurrentCopy()
    {
        switch (_viewModel.CurrentPage)
        {
            case CaptureViewModel capture:
                capture.CopyCommand.Execute(null);
                break;
            case PromptDetailViewModel detail:
                detail.CopyCommand.Execute(null);
                break;
            case LibraryViewModel library when library.SelectedPrompt is not null:
                library.CopyPromptCommand.Execute(library.SelectedPrompt);
                break;
        }
    }

    private void MoveFocus(FocusTarget target)
    {
        string automationId = target switch
        {
            FocusTarget.Navigation => CompactHeader.Visibility == Visibility.Visible ? "CompactNewButton" : "NewNavigationButton",
            FocusTarget.PromptEditor => "PromptEditor",
            FocusTarget.CaptureMetadata => "CaptureMetadataButton",
            FocusTarget.CaptureActions => "SavePromptButton",
            FocusTarget.LibrarySearch => "LibrarySearch",
            FocusTarget.LibraryFilters => "LibraryFilters",
            FocusTarget.LibraryResults => "LibraryResults",
            FocusTarget.LibraryMoreOptions => "LibraryMoreOptions",
            FocusTarget.LibraryStatus => "LibraryStatus",
            FocusTarget.PromptDetailHeading => "PromptDetailHeading",
            FocusTarget.PromptDetailEditor => "PromptDetailEditor",
            FocusTarget.PromptDetailMetadata => "PromptDetailMetadata",
            FocusTarget.PromptDetailActions => "RequestDeleteButton",
            FocusTarget.PromptDetailStatus => "PromptDetailStatus",
            FocusTarget.SettingsCategories => "SettingsCategories",
            FocusTarget.SettingsContent => "ThemePicker",
            FocusTarget.SettingsActions => "SettingsActions",
            FocusTarget.SettingsStatus => "SettingsStatus",
            _ => string.Empty,
        };
        if (automationId.Length == 0)
        {
            return;
        }

        FrameworkElement? element = FindByAutomationId(this, automationId);
        if (element is not null)
        {
            element.Focus();
            Keyboard.Focus(element);
        }
    }

    private static FrameworkElement? FindByAutomationId(DependencyObject parent, string automationId)
    {
        int childCount = VisualTreeHelper.GetChildrenCount(parent);
        for (int index = 0; index < childCount; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            if (child is FrameworkElement element &&
                AutomationProperties.GetAutomationId(element) == automationId)
            {
                return element;
            }

            FrameworkElement? descendant = FindByAutomationId(child, automationId);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }
}