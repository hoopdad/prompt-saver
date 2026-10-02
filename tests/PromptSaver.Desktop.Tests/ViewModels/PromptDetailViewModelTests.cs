using PromptSaver.Application;
using PromptSaver.Application.Dtos;
using PromptSaver.Application.UseCases;
using PromptSaver.Desktop.ViewModels;
using PromptSaver.Domain.Entities;
using PromptSaver.Domain.ValueObjects;

namespace PromptSaver.Desktop.Tests.ViewModels;

public sealed class PromptDetailViewModelTests
{
    [Fact]
    public async Task IntentAndMetadataReviewCommandsReachApplicationWorkflows()
    {
        DetailServices services = new();
        PromptDetailViewModel viewModel = new(services);
        await viewModel.LoadAsync(services.Prompt.Id);

        await viewModel.KeepCurrentIntentCommand.ExecuteAsync();
        Assert.Equal(IntentReviewAction.KeepProvisional, services.LastReview?.Action);

        viewModel.EditMetadataCommand.Execute(null);
        Assert.True(viewModel.IsMetadataEditing);
        Assert.Equal(FocusTarget.PromptDetailMetadata, viewModel.FocusRequest?.Target);
        viewModel.SkillsText = "writing, release engineering";
        viewModel.EntitiesText = "Azure | Technology\r\nContoso | Organization";
        await viewModel.SaveMetadataCommand.ExecuteAsync();

        Assert.NotNull(services.LastMetadata);
        Assert.Equal(2, services.LastMetadata.Skills.Count);
        Assert.Equal(EntityType.Technology, services.LastMetadata.Entities[0].Type);
        Assert.Equal(EntityType.Organization, services.LastMetadata.Entities[1].Type);
        Assert.False(viewModel.IsMetadataEditing);
        Assert.Equal("Metadata saved", viewModel.StatusMessage);

        await viewModel.QueryIntentCommand.ExecuteAsync();

        Assert.Equal(services.Prompt.Id, services.LastIntentQuery?.PromptId);
        Assert.Equal("Draft release notes", viewModel.LlmIntentSuggestion);
        Assert.Equal("Draft release notes", viewModel.IntentText);
        Assert.Contains("Use as intent", viewModel.StatusMessage, StringComparison.Ordinal);

        await viewModel.UseIntentCommand.ExecuteAsync();

        Assert.Equal(IntentReviewAction.RenameProvisional, services.LastReview?.Action);
        Assert.Equal("Draft release notes", services.LastReview?.RenamedIntent);
        Assert.Equal("Intent set to \"Draft release notes\".", viewModel.StatusMessage);
    }

    private sealed class DetailServices :
        IGetPromptDetails,
        ICommitPromptEdit,
        ICopyPrompt,
        IDuplicatePrompt,
        IDeletePrompt,
        IReviewIntentAssignment,
        IManageMetadata,
        IQueryPromptIntent
    {
        public PromptDetailsDto Prompt { get; private set; } = CreatePrompt();

        public ReviewIntentAssignmentCommand? LastReview { get; private set; }

        public ManageMetadataCommand? LastMetadata { get; private set; }

        public QueryPromptIntentCommand? LastIntentQuery { get; private set; }

        public Task<AppResult<PromptDetailsDto>> ExecuteAsync(
            PromptId promptId,
            CancellationToken cancellationToken) =>
            Task.FromResult(AppResult.Success(Prompt));

        public Task<AppResult<PromptDetailsDto>> ExecuteAsync(
            CommitPromptEditCommand command,
            CancellationToken cancellationToken) =>
            Task.FromResult(AppResult.Success(Prompt));

        public Task<AppResult> ExecuteAsync(
            CopyPromptCommand command,
            CancellationToken cancellationToken) =>
            Task.FromResult(AppResult.Success());

        public Task<AppResult<PromptDetailsDto>> ExecuteAsync(
            DuplicatePromptCommand command,
            CancellationToken cancellationToken) =>
            Task.FromResult(AppResult.Success(Prompt));

        public Task<AppResult<DeletePromptResult>> ExecuteAsync(
            DeletePromptCommand command,
            CancellationToken cancellationToken) =>
            Task.FromResult(AppResult.Success(new DeletePromptResult(command.PromptId, null)));

        public Task<AppResult<PromptDetailsDto>> ExecuteAsync(
            ReviewIntentAssignmentCommand command,
            CancellationToken cancellationToken)
        {
            LastReview = command;
            Prompt = Prompt with
            {
                Intent = command.Action == IntentReviewAction.RenameProvisional
                    ? Prompt.Intent with { CanonicalName = command.RenamedIntent! }
                    : Prompt.Intent,
                IntentAssignment = IntentAssignment.UserSelected,
                IntentReviewState = IntentReviewState.Resolved,
                Version = Prompt.Version + 1,
            };
            return Task.FromResult(AppResult.Success(Prompt));
        }

        public Task<AppResult<PromptDetailsDto>> ExecuteAsync(
            ManageMetadataCommand command,
            CancellationToken cancellationToken)
        {
            LastMetadata = command;
            Prompt = Prompt with
            {
                Skills = command.Skills
                    .Select(item => new SkillDto(
                        new SkillId(Guid.CreateVersion7()),
                        item.Name,
                        MetadataSource.User,
                        1,
                        "user-v1"))
                    .ToArray(),
                Entities = command.Entities
                    .Select(item => new EntityDto(
                        new EntityId(Guid.CreateVersion7()),
                        item.Name,
                        item.Type,
                        MetadataSource.User,
                        1,
                        "user-v1"))
                    .ToArray(),
                Version = Prompt.Version + 1,
            };
            return Task.FromResult(AppResult.Success(Prompt));
        }

        public Task<AppResult<PromptIntentSuggestionDto>> ExecuteAsync(
            QueryPromptIntentCommand command,
            CancellationToken cancellationToken)
        {
            LastIntentQuery = command;
            return Task.FromResult(
                AppResult.Success(
                    new PromptIntentSuggestionDto(
                        "Draft release notes",
                        "Test provider",
                        "test-model")));
        }

        private static PromptDetailsDto CreatePrompt() =>
            new(
                new PromptId(Guid.CreateVersion7()),
                "Write release notes",
                "Release notes",
                TitleSource.Derived,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                null,
                0,
                new IntentSummaryDto(
                    new IntentId(Guid.CreateVersion7()),
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
