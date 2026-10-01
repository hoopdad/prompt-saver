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
        TopNavigation.Margin = width < 720
            ? new Thickness(0)
            : new Thickness(2, 0, 0, 0);
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

        if (e.PropertyName == nameof(ShellViewModel.IsDarkTheme))
        {
            ApplyTheme(_viewModel.IsDarkTheme);
        }

        if (e.PropertyName == nameof(ShellViewModel.ZoomPercentage))
        {
            ApplyZoom(_viewModel.ZoomPercentage);
        }
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control)
        {
            return;
        }

        _viewModel.ChangeZoom(e.Delta > 0 ? 10 : -10);
        e.Handled = true;
    }

    private static void ApplyTheme(bool dark)
    {
        SetBrush("AppBackgroundBrush", dark ? "#0F172A" : "#F4F6FA");
        SetBrush("SurfaceBrush", dark ? "#111827" : "#FFFFFF");
        SetBrush("SurfaceMutedBrush", dark ? "#1E293B" : "#EEF2F7");
        SetBrush("TextBrush", dark ? "#F8FAFC" : "#172033");
        SetBrush("MutedTextBrush", dark ? "#AAB5C5" : "#667085");
        SetBrush("BorderBrush", dark ? "#334155" : "#D8DEE9");
        SetBrush("AccentBrush", dark ? "#60A5FA" : "#2563EB");
        SetBrush("AccentHoverBrush", dark ? "#3B82F6" : "#1D4ED8");
        SetBrush("AccentPressedBrush", dark ? "#93C5FD" : "#1E40AF");
        SetBrush("SelectionBrush", dark ? "#243B67" : "#E8EFFF");
    }

    private static void SetBrush(string key, string color) =>
        System.Windows.Application.Current.Resources[key] =
            new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));

    private static void ApplyZoom(int percentage)
    {
        double scale = percentage / 100d;
        System.Windows.Application.Current.Resources["BaseFontSize"] = 14d * scale;
        System.Windows.Application.Current.Resources["PageHeadingFontSize"] = 22d * scale;
        System.Windows.Application.Current.Resources["SectionHeadingFontSize"] = 15d * scale;
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
            FocusTarget.Navigation => "NewNavigationButton",
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
            FocusTarget.SettingsContent => "GeneralSpellCheck",
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