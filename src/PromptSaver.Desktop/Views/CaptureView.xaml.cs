using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using PromptSaver.Desktop.ViewModels;

namespace PromptSaver.Desktop.Views;

public partial class CaptureView : UserControl
{
    public CaptureView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        PromptEditor.SelectionChanged += OnEditorSelectionChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is CaptureViewModel viewModel)
        {
            viewModel.PropertyChanged += OnViewModelPropertyChanged;
            ApplySelection(viewModel);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is CaptureViewModel viewModel)
        {
            viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }
    }

    private void OnEditorSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is CaptureViewModel viewModel)
        {
            viewModel.CaretOffset = PromptEditor.SelectionStart;
            viewModel.SelectionLength = PromptEditor.SelectionLength;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is CaptureViewModel viewModel &&
            e.PropertyName is nameof(CaptureViewModel.CaretOffset) or nameof(CaptureViewModel.SelectionLength))
        {
            Dispatcher.BeginInvoke(() => ApplySelection(viewModel));
        }
    }

    private void ApplySelection(CaptureViewModel viewModel)
    {
        int start = Math.Clamp(viewModel.CaretOffset, 0, PromptEditor.Text.Length);
        int length = Math.Clamp(viewModel.SelectionLength, 0, PromptEditor.Text.Length - start);
        PromptEditor.Select(start, length);
    }
}
