using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using PromptSaver.Domain.ValueObjects;

namespace PromptSaver.Desktop.ViewModels;

public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class DelegateCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => execute();

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class ParameterizedCommand<T>(Action<T> execute, Func<T, bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) =>
        parameter is T value && (canExecute?.Invoke(value) ?? true);

    public void Execute(object? parameter)
    {
        if (parameter is T value)
        {
            execute(value);
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class AsyncParameterizedCommand<T>(
    Func<T, Task> execute,
    Func<T, bool>? canExecute = null) : ICommand
{
    private bool _isExecuting;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) =>
        !_isExecuting && parameter is T value && (canExecute?.Invoke(value) ?? true);

    public async void Execute(object? parameter)
    {
        if (parameter is not T value || !CanExecute(value))
        {
            return;
        }

        _isExecuting = true;
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            await execute(value);
        }
        finally
        {
            _isExecuting = false;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}

public sealed class AsyncDelegateCommand(
    Func<Task> execute,
    Func<bool>? canExecute = null) : ICommand
{
    private bool _isExecuting;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_isExecuting && (canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter) => await ExecuteAsync();

    public async Task ExecuteAsync()
    {
        if (!CanExecute(null))
        {
            return;
        }

        _isExecuting = true;
        RaiseCanExecuteChanged();
        try
        {
            await execute();
        }
        finally
        {
            _isExecuting = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public enum AnnouncementKind
{
    None,
    Polite,
    Assertive,
}

public sealed record StatusAnnouncement(string Text, AnnouncementKind Kind)
{
    public static readonly StatusAnnouncement Empty = new(string.Empty, AnnouncementKind.None);
}

public enum FocusTarget
{
    None,
    Navigation,
    PromptEditor,
    CaptureMetadata,
    CaptureActions,
    LibrarySearch,
    LibraryFilters,
    LibraryResults,
    LibraryMoreOptions,
    LibraryStatus,
    PromptDetailHeading,
    PromptDetailEditor,
    PromptDetailMetadata,
    PromptDetailActions,
    PromptDetailStatus,
    SettingsCategories,
    SettingsContent,
    SettingsActions,
    SettingsStatus,
}

public sealed record FocusRequest(FocusTarget Target, long Sequence);

public sealed record PromptNavigationRequest(PromptId PromptId, FocusTarget FocusTarget);

public enum CaptureState
{
    Empty,
    BackingUpDraft,
    DraftBackedUp,
    PromptSaved,
    UnsavedChanges,
    SavingPrompt,
    SaveFailed,
    DraftBackupFailed,
}

public enum ShellPage
{
    Capture,
    Library,
    PromptDetail,
    Settings,
}
