using PromptSaver.Desktop.Services;
using PromptSaver.Desktop.ViewModels;
using PromptSaver.Application.Dtos;
using PromptSaver.Domain.Entities;
using PromptSaver.Domain.ValueObjects;

namespace PromptSaver.Desktop.Tests.ViewModels;

public sealed class ShellViewModelTests
{
    [Fact]
    public async Task StartupPublishesCaptureBeforeOptionalInitializationCompletes()
    {
        TaskCompletionSource initialization = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ShellViewModel shell = DesktopComposition.CreateShell(
            new NullDesktopUseCases(),
            _ => initialization.Task);

        Task startup = shell.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.IsType<CaptureViewModel>(shell.CurrentPage);
        Assert.Equal(FocusTarget.PromptEditor, shell.FocusRequest?.Target);
        Assert.False(startup.IsCompleted);
        initialization.SetResult();
        await startup;
        Assert.Equal("0 results · Page 1 of 1", shell.Library.StatusMessage);
    }

    [Fact]
    public async Task GlobalNavigationRequestsExpectedFocusAndClearsPriorPrompt()
    {
        ShellViewModel shell = DesktopComposition.CreateShell(new NullDesktopUseCases());
        shell.Capture.Body = "Prior prompt";

        shell.ShowLibrary(focusSearch: true);
        Assert.IsType<LibraryViewModel>(shell.CurrentPage);
        Assert.Equal(FocusTarget.LibrarySearch, shell.FocusRequest?.Target);

        shell.ShowSettings();
        Assert.IsType<SettingsViewModel>(shell.CurrentPage);
        Assert.Equal(FocusTarget.SettingsCategories, shell.FocusRequest?.Target);

        shell.CloseSettingsCommand.Execute(null);
        Assert.IsType<LibraryViewModel>(shell.CurrentPage);
        Assert.Equal(FocusTarget.LibraryResults, shell.FocusRequest?.Target);

        await shell.NewCommand.ExecuteAsync();
        Assert.IsType<CaptureViewModel>(shell.CurrentPage);
        Assert.Equal(string.Empty, shell.Capture.Body);
        Assert.Equal(CaptureState.Empty, shell.Capture.State);
        Assert.Equal(FocusTarget.PromptEditor, shell.FocusRequest?.Target);
    }

    [Fact]
    public void WindowTitleIncludesDesktopAssemblyVersion()
    {
        ShellViewModel shell = DesktopComposition.CreateShell(new NullDesktopUseCases());
        Version version = typeof(ShellViewModel).Assembly.GetName().Version!;

        Assert.Equal(
            $"Prompt Saver v{version.Major}.{version.Minor}.{version.Build}",
            shell.WindowTitle);
    }

    [Fact]
    public void CloseSettingsReturnsToPreviousPage()
    {
        ShellViewModel shell = DesktopComposition.CreateShell(new NullDesktopUseCases());
        shell.ShowLibrary(focusSearch: false);

        shell.ShowSettings();
        shell.CloseSettingsCommand.Execute(null);

        Assert.IsType<LibraryViewModel>(shell.CurrentPage);
        Assert.Equal(FocusTarget.LibraryResults, shell.FocusRequest?.Target);
    }

    [Fact]
    public void F6CyclesPageRegionsInBothDirections()
    {
        ShellViewModel shell = DesktopComposition.CreateShell(new NullDesktopUseCases());

        shell.CycleFocusRegion(reverse: false);
        Assert.Equal(FocusTarget.CaptureActions, shell.FocusRequest?.Target);

        shell.CycleFocusRegion(reverse: true);
        Assert.Equal(FocusTarget.PromptEditor, shell.FocusRequest?.Target);
    }

    [Fact]
    public void ChildFocusRequestsArePromotedToWindowFocus()
    {
        ShellViewModel shell = DesktopComposition.CreateShell(new NullDesktopUseCases());
        shell.ShowLibrary(focusSearch: false);

        shell.Library.MoreOptionsCommand.Execute(
            new PromptSummaryDto(
                new PromptId(Guid.CreateVersion7()),
                "Title",
                "Body",
                DateTimeOffset.UtcNow,
                null,
                0,
                new IntentSummaryDto(
                    new IntentId(Guid.CreateVersion7()),
                    "Intent",
                    false,
                    null),
                IntentReviewState.NeedsReview,
                MetadataStatus.LocalComplete,
                0));

        Assert.Equal(FocusTarget.LibraryMoreOptions, shell.FocusRequest?.Target);
    }

    [Fact]
    public void ThemeToggleSwitchesLabelAndState()
    {
        ShellViewModel shell = DesktopComposition.CreateShell(new NullDesktopUseCases());

        Assert.False(shell.IsDarkTheme);
        Assert.Equal("Dark mode", shell.ThemeToggleText);

        shell.ToggleThemeCommand.Execute(null);

        Assert.True(shell.IsDarkTheme);
        Assert.Equal("Light mode", shell.ThemeToggleText);
    }

    [Fact]
    public void ZoomCommandsClampAndResetPercentage()
    {
        ShellViewModel shell = DesktopComposition.CreateShell(new NullDesktopUseCases());

        shell.ZoomInCommand.Execute(null);
        Assert.Equal(110, shell.ZoomPercentage);
        Assert.Equal("110%", shell.ZoomText);

        for (int index = 0; index < 10; index++)
        {
            shell.ChangeZoom(10);
        }

        Assert.Equal(160, shell.ZoomPercentage);
        Assert.False(shell.ZoomInCommand.CanExecute(null));

        shell.ResetZoomCommand.Execute(null);
        Assert.Equal(100, shell.ZoomPercentage);
    }
}
