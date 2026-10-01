using System.Globalization;
using Microsoft.Data.Sqlite;
using PromptSaver.Application;
using PromptSaver.Application.Dtos;
using PromptSaver.Application.Ports;
using PromptSaver.Domain.Entities;
using PromptSaver.Domain.ValueObjects;
using PromptSaver.Infrastructure.Drafts;
using PromptSaver.Infrastructure.Storage;

namespace PromptSaver.Infrastructure.Tests;

public sealed class PersistenceIntegrationTests
{
    [Fact]
    public void DataRootResolverCreatesSingleRootLayout()
    {
        using TemporaryStorage storage = new();

        DataRootPaths paths = storage.Resolver.Resolve();

        Assert.Equal(storage.Root, paths.Root);
        Assert.Equal(Path.Combine(storage.Root, "prompts.db"), paths.Database);
        Assert.Equal(Path.Combine(storage.Root, "capture-draft.json"), paths.CaptureDraft);
        Assert.Equal(Path.Combine(storage.Root, "config.json"), paths.Configuration);
        Assert.True(Directory.Exists(paths.Backups));
        Assert.True(Directory.Exists(paths.Logs));
    }

    [Fact]
    public async Task CaptureDraftRoundTripsUnicodeAndPreservesCorruptInput()
    {
        using TemporaryStorage storage = new();
        CaptureDraftStore store = new(storage.Resolver);
        CaptureDraftDto draft = CaptureDraftDto.Create(
            "C# and Ａzure\nこんにちは 👩🏽‍💻",
            4,
            3,
            AtNoon());

        Assert.True((await store.SaveAsync(draft, TestContext.Current.CancellationToken)).IsSuccess);
        CaptureDraftRecoveryResult recovered =
            await store.RecoverAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DraftRecoveryStatus.Recovered, recovered.Status);
        Assert.Equal(draft.Body, recovered.Draft!.Body);
        Assert.Equal(draft.CaretOffset, recovered.Draft.CaretOffset);
        Assert.Equal(draft.SelectionLength, recovered.Draft.SelectionLength);

        await File.WriteAllTextAsync(
            storage.Resolver.Resolve().CaptureDraft,
            "{not-json",
            TestContext.Current.CancellationToken);
        CaptureDraftRecoveryResult corrupt =
            await store.RecoverAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DraftRecoveryStatus.Corrupt, corrupt.Status);
        Assert.Equal("draft.corrupt", corrupt.Error!.Key);
        Assert.True(File.Exists(storage.Resolver.Resolve().CaptureDraft));
        Assert.True(File.Exists(corrupt.PreservedCorruptFilePath));
    }

    [Fact]
    public async Task MigrationSeedsUnsortedAndCreatesTriggerlessFtsSchema()
    {
        using TemporaryStorage storage = new();
        StorageServices services = storage.CreateServices();

        Assert.True(
            (await services.Migrations.InitializeAsync(TestContext.Current.CancellationToken))
            .IsSuccess);

        await using SqliteConnection connection =
            await services.Connections.OpenAsync(TestContext.Current.CancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM intents WHERE id = $unsorted),
                (SELECT COUNT(*) FROM sqlite_schema WHERE type = 'trigger'),
                (SELECT COUNT(*) FROM schema_migrations);
            """;
        command.Parameters.AddWithValue("$unsorted", Intent.ReservedUnsortedId.ToString());
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, reader.GetInt32(0));
        Assert.Equal(0, reader.GetInt32(1));
        Assert.Equal(1, reader.GetInt32(2));
    }

    [Fact]
    public async Task InitializationIsSingleFlightAndDoesNotRepeatFtsIntegrityChecks()
    {
        using TemporaryStorage storage = new();
        StorageServices services = storage.CreateServices();

        AppResult[] results = await Task.WhenAll(
            Enumerable.Range(0, 12)
                .Select(_ => services.Migrations.InitializeAsync(
                    TestContext.Current.CancellationToken)));

        Assert.All(results, result => Assert.True(result.IsSuccess, result.Error?.Message));

        await using (SqliteConnection connection =
            await services.Connections.OpenAsync(TestContext.Current.CancellationToken))
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "DROP TABLE prompt_search_index;";
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        AppResult cached =
            await services.Migrations.InitializeAsync(TestContext.Current.CancellationToken);
        AppResult diagnostic =
            await services.Search.CheckIntegrityAsync(TestContext.Current.CancellationToken);

        Assert.True(cached.IsSuccess);
        Assert.False(diagnostic.IsSuccess);
    }

    [Fact]
    public async Task ProviderConfigurationRoundTripsStronglyTypedId()
    {
        using TemporaryStorage storage = new();
        StorageServices services = storage.CreateServices();
        SqliteProviderStore store = new(services.Connections, services.Migrations);
        ProviderConfigurationDto configuration = new(
            new ProviderConfigurationId(
                Guid.Parse("0199a59c-7c00-7000-8000-000000000071")),
            ProviderKind.Ollama,
            "Ollama",
            new Uri("http://127.0.0.1:11434"),
            "llama3.2:3b",
            true,
            false,
            null);

        AppResult<ProviderConfigurationDto> saved = await store.SaveAsync(
            configuration,
            TestContext.Current.CancellationToken);
        AppResult<ProviderConfigurationDto?> loaded = await store.GetAsync(
            configuration.Id,
            TestContext.Current.CancellationToken);
        AppResult<ProviderConfigurationDto?> enabled = await store.GetEnabledAsync(
            TestContext.Current.CancellationToken);

        Assert.True(saved.IsSuccess, saved.Error?.Message);
        Assert.True(loaded.IsSuccess, loaded.Error?.Message);
        Assert.Equal(configuration, loaded.Value);
        Assert.True(enabled.IsSuccess, enabled.Error?.Message);
        Assert.Equal(configuration, enabled.Value);
    }

    [Fact]
    public async Task PromptCandidatesAndSpecialCharacterSearchPersistAfterRestart()
    {
        using TemporaryStorage storage = new();
        StorageServices services = storage.CreateServices();
        DateTimeOffset now = AtNoon();
        Intent intent = Intent.Create(
            NewIntentId(),
            "Build C++ utility",
            IntentSource.User,
            now);
        Intent candidateIntent = Intent.Create(
            NewIntentId(),
            "Review C# API",
            IntentSource.User,
            now);
        Prompt prompt = CreatePrompt(
            "Use C++, C#, foo.bar, alpha-beta, snake_case, café, and Ａzure.",
            intent.Id,
            now);
        IntentCandidateScore candidate = IntentCandidateScore.Create(
            candidateIntent.Id,
            ScoreBasisPoints.Create(7400),
            1,
            "lexical-v1",
            now);

        await using (IAppUnitOfWork unit = await BeginAsync(services))
        {
            await unit.Intents.AddAsync(intent, TestContext.Current.CancellationToken);
            await unit.Intents.AddAsync(candidateIntent, TestContext.Current.CancellationToken);
            await unit.Prompts.AddAsync(prompt, TestContext.Current.CancellationToken);
            await unit.IntentCandidates.ReplaceAsync(
                prompt.Id,
                [candidate],
                TestContext.Current.CancellationToken);
            Assert.True((await unit.CommitAsync(TestContext.Current.CancellationToken)).IsSuccess);
        }

        StorageServices restarted = storage.CreateServices();
        await using (IAppUnitOfWork unit = await BeginAsync(restarted))
        {
            Prompt loaded =
                Assert.IsType<Prompt>(
                    await unit.Prompts.GetAsync(prompt.Id, TestContext.Current.CancellationToken));
            Assert.Equal(prompt.Body.Value, loaded.Body.Value);
            Assert.Single(loaded.IntentCandidates);
            Assert.Equal(7400, loaded.IntentCandidates[0].Score.Value);
            await unit.RollbackAsync(TestContext.Current.CancellationToken);
        }

        string[] matchingQueries =
        [
            "C++",
            "C#",
            "foo.bar",
            "alpha-beta",
            "snake_case",
            "cafe",
            "Ａzure",
            "NEAR",
        ];
        foreach (string query in matchingQueries)
        {
            AppResult<SearchPageDto<PromptSummaryDto>> result =
                await restarted.Search.SearchAsync(
                    new SearchPromptsQuery(query, 1, 20),
                    TestContext.Current.CancellationToken);
            Assert.True(result.IsSuccess, result.Error?.Message);
            if (query != "NEAR")
            {
                Assert.True(
                    result.Value.Items.Count == 1,
                    $"Expected one result for '{query}', found {result.Value.Items.Count}.");
            }
        }

        string[] safeNoResultQueries = ["\"", "(", ")", "^", "*", "a:b", "-"];
        foreach (string query in safeNoResultQueries)
        {
            AppResult<SearchPageDto<PromptSummaryDto>> result =
                await restarted.Search.SearchAsync(
                    new SearchPromptsQuery(query, 1, 20),
                    TestContext.Current.CancellationToken);
            Assert.True(result.IsSuccess, result.Error?.Message);
        }

        Assert.True(
            (await restarted.Search.CheckIntegrityAsync(TestContext.Current.CancellationToken))
            .IsSuccess);
    }

    [Fact]
    public async Task EnrichmentClaimsAreAtomicAndProposalSaveIsIdempotent()
    {
        using TemporaryStorage storage = new();
        StorageServices services = storage.CreateServices();
        DateTimeOffset now = AtNoon();
        Intent intent = Intent.Create(NewIntentId(), "Write release notes", IntentSource.User, now);
        Prompt[] prompts = Enumerable.Range(0, 3)
            .Select(index => CreatePrompt(
                $"Release prompt {index}",
                intent.Id,
                now.AddSeconds(index),
                MetadataStatus.PendingEnrichment))
            .ToArray();
        await using (IAppUnitOfWork unit = await BeginAsync(services))
        {
            await unit.Intents.AddAsync(intent, TestContext.Current.CancellationToken);
            foreach (Prompt prompt in prompts)
            {
                await unit.Prompts.AddAsync(prompt, TestContext.Current.CancellationToken);
            }

            Assert.True((await unit.CommitAsync(TestContext.Current.CancellationToken)).IsSuccess);
        }

        SqliteProviderStore first = new(services.Connections, services.Migrations);
        SqliteProviderStore second = new(services.Connections, services.Migrations);
        AppResult<IReadOnlyList<PromptId>>[] claims = await Task.WhenAll(
            first.ClaimPendingAsync(3, TestContext.Current.CancellationToken),
            second.ClaimPendingAsync(3, TestContext.Current.CancellationToken));
        PromptId[] claimed = claims.SelectMany(result => result.Value).ToArray();

        Assert.Equal(3, claimed.Length);
        Assert.Equal(3, claimed.Distinct().Count());

        PromptId promptId = claimed[0];
        MetadataProposalDto original = new(
            promptId,
            "Original",
            "Write release notes",
            [],
            [],
            "provider",
            "model",
            now);
        MetadataProposalDto duplicate =
            original with { SuggestedTitle = "Duplicate overwrite" };
        Assert.True(
            (await first.SaveProposalAsync(
                original,
                TestContext.Current.CancellationToken)).IsSuccess);
        Assert.True(
            (await second.SaveProposalAsync(
                duplicate,
                TestContext.Current.CancellationToken)).IsSuccess);
        AppResult<MetadataProposalDto?> stored =
            await first.GetProposalAsync(promptId, TestContext.Current.CancellationToken);
        Assert.Equal("Original", stored.Value?.SuggestedTitle);
    }

    [Fact]
    public async Task EditDraftDuplicateDeleteAndOrphanCleanupAreTransactional()
    {
        using TemporaryStorage storage = new();
        StorageServices services = storage.CreateServices();
        DateTimeOffset now = AtNoon();
        Intent intent = Intent.Create(NewIntentId(), "Create utility", IntentSource.User, now);
        Prompt source = CreatePrompt("Unique deletion token", intent.Id, now);
        Skill skill = Skill.Create(new SkillId(Guid.CreateVersion7()), "PowerShell");
        Entity entity = Entity.Create(
            new EntityId(Guid.CreateVersion7()),
            "Contoso",
            EntityType.Organization);

        await using (IAppUnitOfWork unit = await BeginAsync(services))
        {
            await unit.Intents.AddAsync(intent, TestContext.Current.CancellationToken);
            await unit.Prompts.AddAsync(source, TestContext.Current.CancellationToken);
            Assert.True((await unit.CommitAsync(TestContext.Current.CancellationToken)).IsSuccess);
        }

        Assert.True(
            (await services.Catalog.AddSkillAsync(skill, TestContext.Current.CancellationToken))
            .IsSuccess);
        Assert.True(
            (await services.Catalog.AddEntityAsync(entity, TestContext.Current.CancellationToken))
            .IsSuccess);
        await using (IAppUnitOfWork unit = await BeginAsync(services))
        {
            await unit.Metadata.ReplacePromptMetadataAsync(
                source.Id,
                [
                    PromptSkill.Create(
                        source.Id,
                        skill.Id,
                        MetadataSource.User,
                        Confidence.Create(1),
                        "user-v1"),
                ],
                [
                    PromptEntity.Create(
                        source.Id,
                        entity.Id,
                        MetadataSource.User,
                        Confidence.Create(1),
                        "user-v1"),
                ],
                TestContext.Current.CancellationToken);
            Assert.True((await unit.CommitAsync(TestContext.Current.CancellationToken)).IsSuccess);
        }

        PromptEditDraftDto editDraft = new(
            source.Id,
            "Uncommitted body",
            "Uncommitted title",
            """{"skills":[]}""",
            source.Version,
            3,
            4,
            now);
        Assert.True(
            (await services.EditDrafts.SaveAsync(
                editDraft,
                TestContext.Current.CancellationToken)).IsSuccess);

        PromptId duplicateId = new(Guid.CreateVersion7());
        Assert.True(
            (await services.Operations.DuplicateAsync(
                source.Id,
                duplicateId,
                now.AddMinutes(1),
                TestContext.Current.CancellationToken)).IsSuccess);
        AppResult<PromptEditDraftDto?> duplicateDraft =
            await services.EditDrafts.GetAsync(
                duplicateId,
                TestContext.Current.CancellationToken);
        Assert.False(duplicateDraft.IsSuccess);
        Assert.Equal(AppErrorCode.NotFound, duplicateDraft.Error!.Code);

        Assert.True(
            (await services.Operations.DeleteAsync(
                source.Id,
                source.Version,
                TestContext.Current.CancellationToken)).IsSuccess);
        PromptSummaryDto remaining =
            Assert.Single(
                (await services.Search.SearchAsync(
                    new SearchPromptsQuery("deletion", 1, 20),
                    TestContext.Current.CancellationToken)).Value.Items);
        Assert.Equal(duplicateId, remaining.Id);

        await using SqliteConnection connection =
            await services.Connections.OpenAsync(TestContext.Current.CancellationToken);
        await using SqliteCommand counts = connection.CreateCommand();
        counts.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM prompt_edit_drafts WHERE prompt_id = $sourceId),
                (SELECT COUNT(*) FROM prompt_edit_drafts WHERE prompt_id = $duplicateId),
                (SELECT COUNT(*) FROM skills),
                (SELECT COUNT(*) FROM proper_entities),
                (SELECT COUNT(*) FROM prompt_skills WHERE prompt_id = $duplicateId),
                (SELECT COUNT(*) FROM prompt_entities WHERE prompt_id = $duplicateId);
            """;
        counts.Parameters.AddWithValue("$sourceId", source.Id.ToString());
        counts.Parameters.AddWithValue("$duplicateId", duplicateId.ToString());
        await using SqliteDataReader reader =
            await counts.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, reader.GetInt32(0));
        Assert.Equal(0, reader.GetInt32(1));
        Assert.Equal(1, reader.GetInt32(2));
        Assert.Equal(1, reader.GetInt32(3));
        Assert.Equal(1, reader.GetInt32(4));
        Assert.Equal(1, reader.GetInt32(5));
    }

    [Fact]
    public async Task MergeReassignsPromptsArchivesSourceAndRefreshesSearch()
    {
        using TemporaryStorage storage = new();
        StorageServices services = storage.CreateServices();
        DateTimeOffset now = AtNoon();
        Intent source = Intent.Create(NewIntentId(), "Draft customer note", IntentSource.User, now);
        Intent target = Intent.Create(NewIntentId(), "Write account note", IntentSource.User, now);
        Prompt prompt = CreatePrompt("A generic body", source.Id, now);

        await using (IAppUnitOfWork unit = await BeginAsync(services))
        {
            await unit.Intents.AddAsync(source, TestContext.Current.CancellationToken);
            await unit.Intents.AddAsync(target, TestContext.Current.CancellationToken);
            await unit.Prompts.AddAsync(prompt, TestContext.Current.CancellationToken);
            Assert.True((await unit.CommitAsync(TestContext.Current.CancellationToken)).IsSuccess);
        }

        AppResult<MergeIntentsResult> merged =
            await services.Operations.MergeIntentsAsync(
                new MergeIntentsCommand(source.Id, target.Id, source.Version, target.Version),
                now.AddMinutes(1),
                TestContext.Current.CancellationToken);

        Assert.True(merged.IsSuccess, merged.Error?.Message);
        Assert.Equal(1, merged.Value.ReassignedPromptCount);
        Assert.Single(
            (await services.Search.SearchAsync(
                new SearchPromptsQuery("account", 1, 20),
                TestContext.Current.CancellationToken)).Value.Items);

        await using (IAppUnitOfWork unit = await BeginAsync(services))
        {
            Prompt loaded =
                Assert.IsType<Prompt>(
                    await unit.Prompts.GetAsync(prompt.Id, TestContext.Current.CancellationToken));
            Intent archivedSource =
                Assert.IsType<Intent>(
                    await unit.Intents.GetAsync(source.Id, TestContext.Current.CancellationToken));
            Assert.Equal(target.Id, loaded.IntentId);
            Assert.Equal(IntentAssignment.Merged, loaded.IntentAssignment);
            Assert.True(archivedSource.IsArchived);
            Assert.Equal(target.Id, archivedSource.MergedIntoIntentId);
            await unit.RollbackAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task BackupRestorePreservesCommittedDatabaseAndSearch()
    {
        using TemporaryStorage storage = new();
        StorageServices services = storage.CreateServices();
        DateTimeOffset now = AtNoon();
        Intent intent = Intent.Create(NewIntentId(), "Restore database", IntentSource.User, now);
        Prompt prompt = CreatePrompt("Backup restoration token", intent.Id, now);

        await using (IAppUnitOfWork unit = await BeginAsync(services))
        {
            await unit.Intents.AddAsync(intent, TestContext.Current.CancellationToken);
            await unit.Prompts.AddAsync(prompt, TestContext.Current.CancellationToken);
            Assert.True((await unit.CommitAsync(TestContext.Current.CancellationToken)).IsSuccess);
        }

        AppResult<BackupInfoDto> backup =
            await services.Backups.CreateAsync(TestContext.Current.CancellationToken);
        Assert.True(backup.IsSuccess, backup.Error?.Message);
        Assert.True(
            (await services.Operations.DeleteAsync(
                prompt.Id,
                prompt.Version,
                TestContext.Current.CancellationToken)).IsSuccess);

        AppResult restored =
            await services.Backups.RestoreAsync(
                new RestoreBackupCommand(backup.Value.Path, true),
                TestContext.Current.CancellationToken);
        Assert.True(
            restored.IsSuccess,
            restored.Error is null
                ? null
                : restored.Error.Message + " " +
                    (restored.Error.Details is null
                        ? string.Empty
                        : string.Join(", ", restored.Error.Details)));

        StorageServices restarted = storage.CreateServices();
        AppResult<SearchPageDto<PromptSummaryDto>> search =
            await restarted.Search.SearchAsync(
                new SearchPromptsQuery("restoration", 1, 20),
                TestContext.Current.CancellationToken);
        Assert.True(search.IsSuccess, search.Error?.Message);
        Assert.Single(search.Value.Items);
        Assert.True(
            (await restarted.Search.CheckIntegrityAsync(TestContext.Current.CancellationToken))
            .IsSuccess);
    }

    private static async Task<IAppUnitOfWork> BeginAsync(StorageServices services)
    {
        AppResult<IAppUnitOfWork> result =
            await services.Units.BeginAsync(TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess, result.Error?.Message);
        return result.Value;
    }

    private static Prompt CreatePrompt(
        string body,
        IntentId intentId,
        DateTimeOffset createdAtUtc,
        MetadataStatus metadataStatus = MetadataStatus.LocalComplete) =>
        Prompt.Create(
            new PromptId(Guid.CreateVersion7()),
            PromptBody.Create(body),
            PromptTitle.Create("Test prompt"),
            TitleSource.User,
            createdAtUtc,
            intentId,
            IntentAssignment.UserSelected,
            IntentReviewState.Resolved,
            null,
            null,
            metadataStatus);

    private static IntentId NewIntentId() => new(Guid.CreateVersion7());

    private static DateTimeOffset AtNoon() =>
        DateTimeOffset.Parse(
            "2026-10-01T12:00:00Z",
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);

    private sealed class TemporaryStorage : IDisposable
    {
        internal TemporaryStorage()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "PromptSaver.Tests",
                Guid.NewGuid().ToString("N"));
            Resolver = new DataRootResolver(Root);
        }

        internal string Root { get; }

        internal DataRootResolver Resolver { get; }

        internal StorageServices CreateServices()
        {
            SqliteConnectionFactory connections = new(Resolver);
            SqliteMigrationRunner migrations = new(connections, Resolver);
            return new StorageServices(
                connections,
                migrations,
                new SqliteUnitOfWorkFactory(connections, migrations),
                new SqlitePromptSearch(connections, migrations),
                new PromptEditDraftStore(connections, migrations),
                new SqliteCatalogWriter(connections, migrations),
                new SqlitePersistenceOperations(connections, migrations),
                new SqliteBackupService(Resolver, connections, migrations));
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    private sealed record StorageServices(
        SqliteConnectionFactory Connections,
        SqliteMigrationRunner Migrations,
        SqliteUnitOfWorkFactory Units,
        SqlitePromptSearch Search,
        PromptEditDraftStore EditDrafts,
        SqliteCatalogWriter Catalog,
        SqlitePersistenceOperations Operations,
        SqliteBackupService Backups);
}
