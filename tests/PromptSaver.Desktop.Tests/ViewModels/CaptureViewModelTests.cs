using PromptSaver.Application;
using PromptSaver.Application.Dtos;
using PromptSaver.Application.UseCases;
using PromptSaver.Desktop.ViewModels;
using PromptSaver.Domain.Entities;
using PromptSaver.Domain.ValueObjects;

namespace PromptSaver.Desktop.Tests.ViewModels;

public sealed class CaptureViewModelTests
{
    [Fact]
    public async Task InitializeRecoversExactMultilineTextAndRequestsEditorFocus()
    {
        const string body = "First line\r\nSecond line\n日本語";
        CaptureDraftDto draft = CaptureDraftDto.Create(body, 12, 6, DateTimeOffset.UtcNow);
        TestCaptureServices services = new()
        {
            RecoveryResult = new(
                DraftRecoveryStatus.Recovered,
                draft,
                null,
                null),
        };
        CaptureViewModel viewModel = new(services);

        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(body, viewModel.Body);
        Assert.Equal(12, viewModel.CaretOffset);
        Assert.Equal(6, viewModel.SelectionLength);
        Assert.Equal(CaptureState.DraftBackedUp, viewModel.State);
        Assert.Equal(FocusTarget.PromptEditor, viewModel.FocusRequest?.Target);
        Assert.Equal("Recovered draft", viewModel.StatusMessage);
    }

    [Fact]
    public async Task BackupPreservesExactTextAndReportsDraftBackedUp()
    {
        TestCaptureServices services = new();
        CaptureViewModel viewModel = new(services)
        {
            Body = "alpha\r\nbeta\n",
            CaretOffset = 5,
            SelectionLength = 2,
        };

        await viewModel.BackupDraftAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(services.SavedDraft);
        Assert.Equal("alpha\r\nbeta\n", services.SavedDraft.Body);
        Assert.Equal(5, services.SavedDraft.CaretOffset);
        Assert.Equal(2, services.SavedDraft.SelectionLength);
        Assert.Equal(CaptureState.DraftBackedUp, viewModel.State);
        Assert.Equal("Draft backed up locally", viewModel.StatusMessage);
    }

    [Fact]
    public async Task BackupFailureRetainsTextAndExposesRetryAndCopy()
    {
        TestCaptureServices services = new()
        {
            SaveDraftResult = AppResult.Failure(new(
                AppErrorCode.PersistenceUnavailable,
                "draft.unavailable",
                "The draft folder is unavailable.",
                true)),
        };
        CaptureViewModel viewModel = new(services) { Body = "Never lose this" };

        await viewModel.BackupDraftAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Never lose this", viewModel.Body);
        Assert.Equal(CaptureState.DraftBackupFailed, viewModel.State);
        Assert.True(viewModel.CanRetryDraftBackup);
        Assert.True(viewModel.CanCopy);
        Assert.Contains("not backed up", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(AnnouncementKind.Assertive, viewModel.Announcement.Kind);
    }

    [Fact]
    public async Task SaveKeepsTextSelectionAndFocusAndShowsIntentFeedback()
    {
        TestCaptureServices services = new()
        {
            CaptureResult = AppResult.Success(CreateCaptureResult(
                IntentAssignment.Created,
                IntentReviewState.NeedsReview,
                null)),
        };
        CaptureViewModel viewModel = new(services)
        {
            Body = "Create a launch plan\r\nwith milestones",
            CaretOffset = 8,
            SelectionLength = 6,
        };
        viewModel.RequestFocus(FocusTarget.PromptEditor);

        await viewModel.SaveAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Create a launch plan\r\nwith milestones", viewModel.Body);
        Assert.Equal(8, viewModel.CaretOffset);
        Assert.Equal(6, viewModel.SelectionLength);
        Assert.Equal(FocusTarget.PromptEditor, viewModel.FocusRequest?.Target);
        Assert.Equal(CaptureState.PromptSaved, viewModel.State);
        Assert.Equal("New provisional intent created - review suggested", viewModel.IntentFeedback);
    }

    [Fact]
    public async Task SaveInsideDebounceWindowDrainsBackupBeforeDraftDeletion()
    {
        TestCaptureServices services = new()
        {
            CaptureResult = AppResult.Success(CreateCaptureResult(
                IntentAssignment.Created,
                IntentReviewState.NeedsReview,
                null)),
        };
        CaptureViewModel viewModel = new(services) { Body = "Save before debounce" };

        await viewModel.SaveAsync(TestContext.Current.CancellationToken);
        await Task.Delay(600, TestContext.Current.CancellationToken);

        Assert.Equal(0, services.SaveDraftCalls);
        Assert.Equal(1, services.CaptureCalls);
        Assert.Equal(CaptureState.PromptSaved, viewModel.State);
    }

    [Fact]
    public async Task SaveFailureMapsTypedErrorWithoutClearingEditor()
    {
        TestCaptureServices services = new()
        {
            CaptureResult = AppResult.Failure<CapturePromptResult>(new(
                AppErrorCode.Conflict,
                "prompt.conflict",
                "The prompt changed elsewhere.",
                true)),
        };
        CaptureViewModel viewModel = new(services) { Body = "Keep this prompt" };

        await viewModel.SaveAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Keep this prompt", viewModel.Body);
        Assert.Equal(CaptureState.SaveFailed, viewModel.State);
        Assert.True(viewModel.CanRetrySave);
        Assert.Equal("Prompt was not saved because it changed elsewhere. Your text remains in the editor.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task CopyDraftNeverInvokesCapture()
    {
        TestCaptureServices services = new();
        CaptureViewModel viewModel = new(services) { Body = "copy\r\nexactly" };

        await viewModel.CopyAsync(TestContext.Current.CancellationToken);

        Assert.Equal("copy\r\nexactly", services.CopiedText);
        Assert.Equal(0, services.CaptureCalls);
        Assert.Equal("Copied unsaved draft", viewModel.StatusMessage);
    }

    [Fact]
    public void BodyOverLimitRemainsVisibleAndCannotSave()
    {
        CaptureViewModel viewModel = new(new TestCaptureServices())
        {
            Body = new string('a', PromptBody.MaximumUtf8Bytes + 1),
        };

        Assert.True(viewModel.IsOverSizeLimit);
        Assert.True(viewModel.ShowSize);
        Assert.False(viewModel.CanSave);
        Assert.Equal(PromptBody.MaximumUtf8Bytes + 1, viewModel.Utf8ByteCount);
    }

    [Theory]
    [InlineData(IntentAssignment.AutoMapped, IntentReviewState.NotRequired, 9100, "Matched to existing intent, 91%")]
    [InlineData(IntentAssignment.Unsorted, IntentReviewState.NeedsReview, null, "Assigned to Unsorted - review suggested")]
    public async Task SaveShowsAssignmentSpecificFeedback(
        IntentAssignment assignment,
        IntentReviewState reviewState,
        int? score,
        string expected)
    {
        TestCaptureServices services = new()
        {
            CaptureResult = AppResult.Success(CreateCaptureResult(assignment, reviewState, score)),
        };
        CaptureViewModel viewModel = new(services) { Body = "Prompt body" };

        await viewModel.SaveAsync(TestContext.Current.CancellationToken);

        Assert.Equal(expected, viewModel.IntentFeedback);
    }

    [Fact]
    public async Task SavedPromptReviewAndMetadataCommandsOpenAccessibleWorkflow()
    {
        TestCaptureServices services = new()
        {
            CaptureResult = AppResult.Success(CreateCaptureResult(
                IntentAssignment.Created,
                IntentReviewState.NeedsReview,
                null)),
        };
        CaptureViewModel viewModel = new(services) { Body = "Prompt body" };
        List<PromptNavigationRequest> requests = [];
        viewModel.OpenSavedPromptRequested += (_, request) => requests.Add(request);

        await viewModel.SaveAsync(TestContext.Current.CancellationToken);
        viewModel.ReviewIntentCommand.Execute(null);
        viewModel.ReviewMetadataCommand.Execute(null);

        Assert.Equal(2, requests.Count);
        Assert.All(
            requests,
            request => Assert.Equal(FocusTarget.PromptDetailMetadata, request.FocusTarget));
        Assert.All(
            requests,
            request => Assert.Equal(services.CaptureResult.Value.Prompt.Id, request.PromptId));
    }

    private static CapturePromptResult CreateCaptureResult(
        IntentAssignment assignment,
        IntentReviewState reviewState,
        int? score)
    {
        PromptId promptId = new(Guid.NewGuid());
        IntentId intentId = assignment == IntentAssignment.Unsorted
            ? Intent.ReservedUnsortedId
            : new IntentId(Guid.NewGuid());
        PromptDetailsDto details = new(
            promptId,
            "Prompt body",
            "Prompt title",
            TitleSource.Derived,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            null,
            0,
            new IntentSummaryDto(intentId, assignment == IntentAssignment.Unsorted ? "Unsorted" : "Launch plan", false, null),
            assignment,
            reviewState,
            score,
            score is null ? null : "local-v1",
            MetadataStatus.LocalComplete,
            0,
            [],
            [],
            []);
        return new CapturePromptResult(details, true, false);
    }

    private sealed class TestCaptureServices :
        ISaveCaptureDraft,
        IRecoverCaptureDraft,
        IDiscardCaptureDraft,
        ICapturePrompt,
        ICopyPrompt
    {
        public CaptureDraftRecoveryResult RecoveryResult { get; set; } =
            new(DraftRecoveryStatus.Missing, null, null, null);

        public AppResult SaveDraftResult { get; set; } = AppResult.Success();

        public AppResult<CapturePromptResult> CaptureResult { get; set; } =
            AppResult.Failure<CapturePromptResult>(new(
                AppErrorCode.PersistenceUnavailable,
                "library.not_ready",
                "Local storage is still starting.",
                true));

        public CaptureDraftDto? SavedDraft { get; private set; }

        public string? CopiedText { get; private set; }

        public int CaptureCalls { get; private set; }

        public int SaveDraftCalls { get; private set; }

        public Task<AppResult> ExecuteAsync(CaptureDraftDto draft, CancellationToken cancellationToken)
        {
            SaveDraftCalls++;
            SavedDraft = draft;
            return Task.FromResult(SaveDraftResult);
        }

        Task<CaptureDraftRecoveryResult> IRecoverCaptureDraft.ExecuteAsync(CancellationToken cancellationToken) =>
            Task.FromResult(RecoveryResult);

        Task<AppResult> IDiscardCaptureDraft.ExecuteAsync(CancellationToken cancellationToken) =>
            Task.FromResult(AppResult.Success());

        Task<AppResult<CapturePromptResult>> ICapturePrompt.ExecuteAsync(
            CapturePromptCommand command,
            CancellationToken cancellationToken)
        {
            CaptureCalls++;
            return Task.FromResult(CaptureResult);
        }

        Task<AppResult> ICopyPrompt.ExecuteAsync(
            CopyPromptCommand command,
            CancellationToken cancellationToken)
        {
            CopiedText = command.Text;
            return Task.FromResult(AppResult.Success());
        }
    }
}
