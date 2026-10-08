using PromptSaver.Application;
using PromptSaver.Application.Dtos;
using PromptSaver.Application.UseCases;
using PromptSaver.Desktop.ViewModels;
using PromptSaver.Domain.Entities;
using PromptSaver.Domain.ValueObjects;

namespace PromptSaver.Desktop.Tests.ViewModels;

public sealed class LibraryViewModelTests
{
    [Fact]
    public async Task MoreOptionsReachReviewMetadataDuplicateAndPermanentDelete()
    {
        LibraryServices services = new();
        LibraryViewModel viewModel = new(services);
        PromptSummaryDto prompt = CreateSummary();
        List<PromptNavigationRequest> requests = [];
        viewModel.OpenPromptRequested += (_, request) => requests.Add(request);

        viewModel.MoreOptionsCommand.Execute(prompt);
        Assert.Equal(FocusTarget.LibraryMoreOptions, viewModel.FocusRequest?.Target);

        viewModel.ReviewIntentCommand.Execute(null);
        viewModel.ReviewMetadataCommand.Execute(null);
        await viewModel.DuplicatePromptCommand.ExecuteAsync();

        Assert.Equal(3, requests.Count);
        Assert.Equal(FocusTarget.PromptDetailMetadata, requests[0].FocusTarget);
        Assert.Equal(FocusTarget.PromptDetailMetadata, requests[1].FocusTarget);
        Assert.Equal(FocusTarget.PromptDetailHeading, requests[2].FocusTarget);
        Assert.Equal(1, services.DuplicateCalls);

        viewModel.MoreOptionsCommand.Execute(prompt);
        viewModel.RequestPermanentDeleteCommand.Execute(null);
        viewModel.CancelPermanentDeleteCommand.Execute(null);
        Assert.False(viewModel.DeleteConfirmationOpen);
        Assert.Equal(FocusTarget.LibraryMoreOptions, viewModel.FocusRequest?.Target);

        viewModel.RequestPermanentDeleteCommand.Execute(null);
        await viewModel.ConfirmPermanentDeleteCommand.ExecuteAsync();

        Assert.Equal(1, services.DeleteCalls);
        Assert.Null(viewModel.MoreOptionsPrompt);
        Assert.Null(viewModel.SelectedPrompt);
        Assert.Equal(FocusTarget.LibraryResults, viewModel.FocusRequest?.Target);
        Assert.Equal("Prompt permanently deleted", viewModel.StatusMessage);
    }

    [Fact]
    public async Task ViewAllClearsFiltersAndRunsUnfilteredSearch()
    {
        LibraryServices services = new();
        LibraryViewModel viewModel = new(services)
        {
            SearchText = "filtered",
            NeedsReviewOnly = true,
        };

        await viewModel.ViewAllCommand.ExecuteAsync();

        Assert.Equal(string.Empty, viewModel.SearchText);
        Assert.False(viewModel.NeedsReviewOnly);
        Assert.NotNull(services.LastQuery);
        Assert.Equal(string.Empty, services.LastQuery.Text);
        Assert.False(services.LastQuery.NeedsReviewOnly);
        Assert.Equal(20, services.LastQuery.PageSize);
        Assert.Equal(PromptSortColumn.Intent, services.LastQuery.SortColumn);
        Assert.Equal(PromptSortDirection.Ascending, services.LastQuery.SortDirection);
        Assert.Equal(FocusTarget.LibraryResults, viewModel.FocusRequest?.Target);
    }

    [Fact]
    public async Task SortPagingAndPageSizeAreAppliedToSearch()
    {
        LibraryServices services = new() { TotalCount = 45 };
        LibraryViewModel viewModel = new(services);

        await viewModel.SearchAsync();
        Assert.Equal("Page 1 of 3", viewModel.PageStatus);
        Assert.True(viewModel.NextPageCommand.CanExecute(null));

        await viewModel.SortAsync(PromptSortColumn.Title);
        Assert.Equal(PromptSortDirection.Ascending, services.LastQuery?.SortDirection);

        await viewModel.SortAsync(PromptSortColumn.Title);
        Assert.Equal(PromptSortDirection.Descending, services.LastQuery?.SortDirection);

        viewModel.PageSizeText = "25";
        await viewModel.ApplyPageSizeCommand.ExecuteAsync();
        Assert.Equal(25, services.LastQuery?.PageSize);
        Assert.Equal("Page 1 of 2", viewModel.PageStatus);

        await viewModel.NextPageCommand.ExecuteAsync();
        Assert.Equal(2, services.LastQuery?.PageNumber);
        Assert.False(viewModel.NextPageCommand.CanExecute(null));
        Assert.True(viewModel.PreviousPageCommand.CanExecute(null));
    }

    private static PromptSummaryDto CreateSummary()
    {
        PromptDetailsDto details = LibraryServices.CreateDetails(
            new PromptId(Guid.Parse("0199a59c-7c00-7000-8000-000000000050")));
        return new PromptSummaryDto(
            details.Id,
            details.Title,
            details.Body,
            details.UpdatedAtUtc,
            details.LastCopiedAtUtc,
            details.CopyCount,
            details.Intent,
            details.IntentReviewState,
            details.MetadataStatus,
            details.Version);
    }

    private sealed class LibraryServices :
        ISearchPrompts,
        ICopyPrompt,
        IGetPromptDetails,
        IDuplicatePrompt,
        IDeletePrompt
    {
        public int DuplicateCalls { get; private set; }

        public int DeleteCalls { get; private set; }

        public SearchPromptsQuery? LastQuery { get; private set; }

        public long TotalCount { get; init; }

        public Task<AppResult<SearchPageDto<PromptSummaryDto>>> ExecuteAsync(
            SearchPromptsQuery query,
            CancellationToken cancellationToken)
        {
            LastQuery = query;
            return Task.FromResult(
                AppResult.Success(
                    new SearchPageDto<PromptSummaryDto>(
                        [],
                        query.PageNumber,
                        query.PageSize,
                        TotalCount)));
        }

        Task<AppResult> ICopyPrompt.ExecuteAsync(
            CopyPromptCommand command,
            CancellationToken cancellationToken) =>
            Task.FromResult(AppResult.Success());

        Task<AppResult<PromptDetailsDto>> IGetPromptDetails.ExecuteAsync(
            PromptId promptId,
            CancellationToken cancellationToken) =>
            Task.FromResult(AppResult.Success(CreateDetails(promptId)));

        Task<AppResult<PromptDetailsDto>> IDuplicatePrompt.ExecuteAsync(
            DuplicatePromptCommand command,
            CancellationToken cancellationToken)
        {
            DuplicateCalls++;
            return Task.FromResult(
                AppResult.Success(
                    CreateDetails(
                        new PromptId(Guid.Parse("0199a59c-7c00-7000-8000-000000000051")))));
        }

        Task<AppResult<DeletePromptResult>> IDeletePrompt.ExecuteAsync(
            DeletePromptCommand command,
            CancellationToken cancellationToken)
        {
            DeleteCalls++;
            return Task.FromResult(
                AppResult.Success(new DeletePromptResult(command.PromptId, null)));
        }

        internal static PromptDetailsDto CreateDetails(PromptId id) =>
            new(
                id,
                "Prompt body",
                "Prompt title",
                TitleSource.User,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                null,
                0,
                new IntentSummaryDto(
                    new IntentId(Guid.Parse("0199a59c-7c00-7000-8000-000000000052")),
                    "Write release notes",
                    false,
                    null),
                IntentAssignment.Created,
                IntentReviewState.NeedsReview,
                null,
                null,
                MetadataStatus.LocalComplete,
                0,
                [],
                [],
                []);
    }
}
