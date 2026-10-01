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
    }

    [Fact]
    public void GlobalNavigationRequestsExpectedFocus()
    {
        ShellViewModel shell = DesktopComposition.CreateShell(new NullDesktopUseCases());

        shell.ShowLibrary(focusSearch: true);
        Assert.IsType<LibraryViewModel>(shell.CurrentPage);
        Assert.Equal(FocusTarget.LibrarySearch, shell.FocusRequest?.Target);

        shell.ShowSettings();
        Assert.IsType<SettingsViewModel>(shell.CurrentPage);
        Assert.Equal(FocusTarget.SettingsCategories, shell.FocusRequest?.Target);

        shell.ShowNewPrompt();
        Assert.IsType<CaptureViewModel>(shell.CurrentPage);
        Assert.Equal(FocusTarget.PromptEditor, shell.FocusRequest?.Target);
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
}
