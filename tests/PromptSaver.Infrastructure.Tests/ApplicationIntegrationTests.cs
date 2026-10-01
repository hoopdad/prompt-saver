using Microsoft.Data.Sqlite;
using PromptSaver.Application;
using PromptSaver.Application.Dtos;
using PromptSaver.Application.Ports;
using PromptSaver.Application.UseCases;
using PromptSaver.Domain.Entities;
using PromptSaver.Domain.ValueObjects;
using PromptSaver.Infrastructure.Drafts;
using PromptSaver.Infrastructure.Storage;

namespace PromptSaver.Infrastructure.Tests;

public sealed class ApplicationIntegrationTests
{
    [Fact]
    public async Task SaveRestartSearchAndCopyUsesPersistentApplicationServices()
    {
        using TestStorage storage = new();
        RecordingClipboard clipboard = new();
        PromptApplicationService first = storage.CreateApplication(clipboard);

        AppResult<CapturePromptResult> saved = await first.ExecuteAsync(
            new CapturePromptCommand(
                "Write release notes for Azure with C# examples",
                null),
            TestContext.Current.CancellationToken);

        Assert.True(saved.IsSuccess, saved.Error?.Message);
        Assert.Equal("Write release notes for Azure with C# examples", saved.Value.Prompt.Body);
        Assert.Equal(
            "Write release notes for a customer",
            saved.Value.Prompt.Intent.CanonicalName);
        Assert.NotEmpty(saved.Value.Prompt.Skills);

        PromptApplicationService restarted = storage.CreateApplication(clipboard);
        AppResult<SearchPageDto<PromptSummaryDto>> search = await restarted.ExecuteAsync(
            new SearchPromptsQuery("Azure", 1, 20),
            TestContext.Current.CancellationToken);

        PromptSummaryDto match = Assert.Single(search.Value.Items);
        AppResult<PromptDetailsDto> details = await restarted.ExecuteAsync(
            match.Id,
            TestContext.Current.CancellationToken);
        Assert.True(details.IsSuccess, details.Error?.Message);

        AppResult copied = await restarted.ExecuteAsync(
            new CopyPromptCommand(match.Id, details.Value.Body, false),
            TestContext.Current.CancellationToken);

        Assert.True(copied.IsSuccess, copied.Error?.Message);
        Assert.Equal(details.Value.Body, clipboard.Text);
        AppResult<PromptDetailsDto> afterCopy = await restarted.ExecuteAsync(
            match.Id,
            TestContext.Current.CancellationToken);
        Assert.Equal(1, afterCopy.Value.CopyCount);
        Assert.NotNull(afterCopy.Value.LastCopiedAtUtc);
    }

    [Fact]
    public async Task TypedFailuresPreserveLocalSaveAndUserText()
    {
        using TestStorage storage = new();
        RecordingClipboard clipboard = new()
        {
            Result = AppResult.Failure(
                new AppError(
                    AppErrorCode.ClipboardUnavailable,
                    "clipboard.busy",
                    "Clipboard is busy.",
                    true)),
        };
        PromptApplicationService application = storage.CreateApplication(clipboard);

        AppResult<CapturePromptResult> invalid = await application.ExecuteAsync(
            new CapturePromptCommand("   ", null),
            TestContext.Current.CancellationToken);
        Assert.False(invalid.IsSuccess);
        Assert.Equal(AppErrorCode.Validation, invalid.Error!.Code);

        AppResult<CapturePromptResult> saved = await application.ExecuteAsync(
            new CapturePromptCommand("Analyze customer usage", null),
            TestContext.Current.CancellationToken);
        Assert.True(saved.IsSuccess, saved.Error?.Message);

        AppResult copied = await application.ExecuteAsync(
            new CopyPromptCommand(saved.Value.Prompt.Id, saved.Value.Prompt.Body, false),
            TestContext.Current.CancellationToken);
        Assert.False(copied.IsSuccess);
        Assert.Equal(AppErrorCode.ClipboardUnavailable, copied.Error!.Code);

        AppResult<SearchPageDto<PromptSummaryDto>> search = await application.ExecuteAsync(
            new SearchPromptsQuery("customer", 1, 20),
            TestContext.Current.CancellationToken);
        Assert.Single(search.Value.Items);
    }

    [Fact]
    public async Task ProviderFailureNeverChangesSuccessfulLocalSave()
    {
        using TestStorage storage = new();
        FailingEnrichment enrichment = new();
        PromptApplicationService application = storage.CreateApplication(
            new RecordingClipboard(),
            enrichment,
            new EnabledProviderConfigurations());

        AppResult<CapturePromptResult> saved = await application.ExecuteAsync(
            new CapturePromptCommand("Create architecture diagram", null),
            TestContext.Current.CancellationToken);

        Assert.True(saved.IsSuccess, saved.Error?.Message);
        Assert.True(saved.Value.EnrichmentQueued);
        Assert.True(enrichment.WasCalled);
        AppResult<SearchPageDto<PromptSummaryDto>> search = await application.ExecuteAsync(
            new SearchPromptsQuery("architecture", 1, 20),
            TestContext.Current.CancellationToken);
        Assert.Single(search.Value.Items);
    }

    [Fact]
    public async Task CommittedCaptureSucceedsWithoutPostCommitDetailsRead()
    {
        using TestStorage storage = new();
        PromptApplicationService application = storage.CreateApplication(
            new RecordingClipboard(),
            unitFactoryDecorator: inner => new TestStorage.FailAfterFirstUnitOfWorkFactory(inner));

        AppResult<CapturePromptResult> saved = await application.ExecuteAsync(
            new CapturePromptCommand("Write release notes for Azure", null),
            TestContext.Current.CancellationToken);

        Assert.True(saved.IsSuccess, saved.Error?.Message);
        Assert.Equal("Write release notes for Azure", saved.Value.Prompt.Body);
        Assert.NotEmpty(saved.Value.Prompt.Skills);
    }

    [Fact]
    public async Task CaptureRevivesArchivedExactIntentBeforeActiveCollision()
    {
        using TestStorage storage = new();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Intent active = Intent.Create(
            new IntentId(Guid.Parse("0199a59c-7c00-7000-8000-000000000070")),
            "Draw architecture diagrams",
            IntentSource.User,
            now);
        Intent archived = Intent.Create(
            new IntentId(Guid.Parse("0199a59c-7c00-7000-8000-000000000071")),
            "Draw architecture diagram",
            IntentSource.User,
            now);
        archived.ArchiveAsMerged(active.Id, now.AddMinutes(1));
        await storage.SeedIntentsAsync(active, archived);
        PromptApplicationService application = storage.CreateApplication(new RecordingClipboard());

        AppResult<CapturePromptResult> saved = await application.ExecuteAsync(
            new CapturePromptCommand("Draw architecture diagram", null),
            TestContext.Current.CancellationToken);

        Assert.True(saved.IsSuccess, saved.Error?.Message);
        Assert.Equal(archived.Id, saved.Value.Prompt.Intent.Id);
        Intent revived = Assert.IsType<Intent>(await storage.GetIntentAsync(archived.Id));
        Assert.False(revived.IsArchived);
    }

    [Fact]
    public async Task IntentReviewAndMetadataCorrectionArePersistedAndSearchable()
    {
        using TestStorage storage = new();
        PromptApplicationService application = storage.CreateApplication(new RecordingClipboard());
        AppResult<CapturePromptResult> captured = await application.ExecuteAsync(
            new CapturePromptCommand("Write release notes for Azure", null),
            TestContext.Current.CancellationToken);
        Assert.True(captured.IsSuccess, captured.Error?.Message);

        PromptDetailsDto prompt = captured.Value.Prompt;
        AppResult<PromptDetailsDto> reviewed = await application.ExecuteAsync(
            new ReviewIntentAssignmentCommand(
                prompt.Id,
                IntentReviewAction.KeepProvisional,
                null,
                null,
                prompt.Version),
            TestContext.Current.CancellationToken);
        Assert.True(reviewed.IsSuccess, reviewed.Error?.Message);
        Assert.Equal(IntentReviewState.Resolved, reviewed.Value.IntentReviewState);
        Assert.Equal(IntentAssignment.UserSelected, reviewed.Value.IntentAssignment);

        AppResult<PromptDetailsDto> metadata = await application.ExecuteAsync(
            new ManageMetadataCommand(
                prompt.Id,
                [new SkillSelectionDto(null, "release engineering")],
                [new EntitySelectionDto(null, "Contoso", EntityType.Organization)],
                reviewed.Value.Version),
            TestContext.Current.CancellationToken);
        Assert.True(metadata.IsSuccess, metadata.Error?.Message);
        Assert.Equal("release engineering", Assert.Single(metadata.Value.Skills).Name);
        Assert.Equal("Contoso", Assert.Single(metadata.Value.Entities).Name);
        Assert.Equal(MetadataSource.User, metadata.Value.Skills[0].Source);

        AppResult<SearchPageDto<PromptSummaryDto>> search = await application.ExecuteAsync(
            new SearchPromptsQuery("Contoso", 1, 20),
            TestContext.Current.CancellationToken);
        Assert.Equal(prompt.Id, Assert.Single(search.Value.Items).Id);
    }

    private sealed class TestStorage : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "PromptSaver.ApplicationIntegration",
            Guid.NewGuid().ToString("N"));
        private readonly DataRootResolver _resolver;

        internal TestStorage()
        {
            _resolver = new DataRootResolver(_root);
        }

        internal PromptApplicationService CreateApplication(
            IClipboard clipboard,
            IEnrichPendingPrompts? enrichment = null,
            IProviderConfigurationStore? configurations = null,
            Func<IUnitOfWorkFactory, IUnitOfWorkFactory>? unitFactoryDecorator = null)
        {
            SqliteConnectionFactory connections = new(_resolver);
            SqliteMigrationRunner migrations = new(connections, _resolver);
            IUnitOfWorkFactory units = new SqliteUnitOfWorkFactory(connections, migrations);
            units = unitFactoryDecorator?.Invoke(units) ?? units;
            return new PromptApplicationService(
                new CaptureDraftStore(_resolver),
                new PromptEditDraftStore(connections, migrations),
                units,
                new SqlitePromptSearch(connections, migrations),
                new SqlitePersistenceOperations(connections, migrations),
                clipboard,
                new TestClock(),
                new TestIds(),
                new SqliteBackupService(_resolver, connections, migrations),
                _resolver,
                new InlineBackgroundScheduler(),
                enrichment,
                configurations);
        }

        internal async Task SeedIntentsAsync(params Intent[] intents)
        {
            SqliteConnectionFactory connections = new(_resolver);
            SqliteMigrationRunner migrations = new(connections, _resolver);
            AppResult<IAppUnitOfWork> begin =
                await new SqliteUnitOfWorkFactory(connections, migrations)
                    .BeginAsync(TestContext.Current.CancellationToken);
            Assert.True(begin.IsSuccess, begin.Error?.Message);
            await using IAppUnitOfWork unit = begin.Value;
            foreach (Intent intent in intents)
            {
                await unit.Intents.AddAsync(intent, TestContext.Current.CancellationToken);
            }

            Assert.True(
                (await unit.CommitAsync(TestContext.Current.CancellationToken)).IsSuccess);
        }

        internal async Task<Intent?> GetIntentAsync(IntentId intentId)
        {
            SqliteConnectionFactory connections = new(_resolver);
            SqliteMigrationRunner migrations = new(connections, _resolver);
            AppResult<IAppUnitOfWork> begin =
                await new SqliteUnitOfWorkFactory(connections, migrations)
                    .BeginAsync(TestContext.Current.CancellationToken);
            Assert.True(begin.IsSuccess, begin.Error?.Message);
            await using IAppUnitOfWork unit = begin.Value;
            Intent? intent = await unit.Intents.GetAsync(
                intentId,
                TestContext.Current.CancellationToken);
            await unit.RollbackAsync(TestContext.Current.CancellationToken);
            return intent;
        }

        internal sealed class FailAfterFirstUnitOfWorkFactory(IUnitOfWorkFactory inner)
            : IUnitOfWorkFactory
        {
            private int _calls;

            public Task<AppResult<IAppUnitOfWork>> BeginAsync(CancellationToken cancellationToken)
            {
                if (Interlocked.Increment(ref _calls) == 1)
                {
                    return inner.BeginAsync(cancellationToken);
                }

                return Task.FromResult(
                    AppResult.Failure<IAppUnitOfWork>(
                        new AppError(
                            AppErrorCode.PersistenceUnavailable,
                            "test.post_commit_read_failed",
                            "A post-commit details read was attempted.")));
            }
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }

    private sealed class RecordingClipboard : IClipboard
    {
        internal string? Text { get; private set; }

        internal AppResult Result { get; init; } = AppResult.Success();

        public Task<AppResult> SetTextAsync(string text, CancellationToken cancellationToken)
        {
            Text = text;
            return Task.FromResult(Result);
        }
    }

    private sealed class TestClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }

    private sealed class TestIds : IIdGenerator
    {
        public PromptId NewPromptId() => new(Guid.CreateVersion7());

        public IntentId NewIntentId() => new(Guid.CreateVersion7());

        public IntentAliasId NewIntentAliasId() => new(Guid.CreateVersion7());

        public SkillId NewSkillId() => new(Guid.CreateVersion7());

        public EntityId NewEntityId() => new(Guid.CreateVersion7());

        public ProviderConfigurationId NewProviderConfigurationId() => new(Guid.CreateVersion7());
    }

    private sealed class InlineBackgroundScheduler : IBackgroundScheduler
    {
        public AppResult TrySchedule(
            string operationName,
            Func<CancellationToken, Task> operation)
        {
            _ = operation(CancellationToken.None);
            return AppResult.Success();
        }
    }

    private sealed class FailingEnrichment : IEnrichPendingPrompts
    {
        internal bool WasCalled { get; private set; }

        public Task<AppResult<int>> ExecuteAsync(CancellationToken cancellationToken)
        {
            WasCalled = true;
            return Task.FromResult(
                AppResult.Failure<int>(
                    new AppError(
                        AppErrorCode.ProviderUnavailable,
                        "provider.offline",
                        "The provider is offline.",
                        true)));
        }
    }

    private sealed class EnabledProviderConfigurations : IProviderConfigurationStore
    {
        private static readonly ProviderConfigurationDto Enabled = new(
            new ProviderConfigurationId(Guid.CreateVersion7()),
            ProviderKind.Ollama,
            "Ollama",
            new Uri("http://127.0.0.1:11434"),
            "local-model",
            true,
            false,
            null);

        public Task<AppResult<ProviderConfigurationDto?>> GetAsync(
            ProviderConfigurationId id,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                AppResult.Success<ProviderConfigurationDto?>(
                    id == Enabled.Id ? Enabled : null));

        public Task<AppResult<ProviderConfigurationDto?>> GetEnabledAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(AppResult.Success<ProviderConfigurationDto?>(Enabled));

        public Task<AppResult<IReadOnlyList<ProviderConfigurationDto>>> ListAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(
                AppResult.Success<IReadOnlyList<ProviderConfigurationDto>>([Enabled]));

        public Task<AppResult<ProviderConfigurationDto>> SaveAsync(
            ProviderConfigurationDto configuration,
            CancellationToken cancellationToken) =>
            Task.FromResult(AppResult.Success(configuration));
    }
}
