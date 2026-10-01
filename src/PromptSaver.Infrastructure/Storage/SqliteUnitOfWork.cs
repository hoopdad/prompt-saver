using System.Globalization;
using Microsoft.Data.Sqlite;
using PromptSaver.Application;
using PromptSaver.Application.Ports;
using PromptSaver.Domain.Entities;
using PromptSaver.Domain.ValueObjects;

namespace PromptSaver.Infrastructure.Storage;

public sealed class SqliteUnitOfWorkFactory : IUnitOfWorkFactory
{
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly SqliteMigrationRunner _migrationRunner;

    public SqliteUnitOfWorkFactory(
        SqliteConnectionFactory connectionFactory,
        SqliteMigrationRunner migrationRunner)
    {
        _connectionFactory = connectionFactory;
        _migrationRunner = migrationRunner;
    }

    public async Task<AppResult<IAppUnitOfWork>> BeginAsync(
        CancellationToken cancellationToken)
    {
        AppResult initialized = await _migrationRunner.InitializeAsync(cancellationToken);
        if (!initialized.IsSuccess)
        {
            return AppResult.Failure<IAppUnitOfWork>(initialized.Error!);
        }

        try
        {
            SqliteConnection connection =
                await _connectionFactory.OpenAsync(cancellationToken);
            SqliteTransaction transaction =
                (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            return AppResult.Success<IAppUnitOfWork>(
                new SqliteAppUnitOfWork(connection, transaction));
        }
        catch (Exception exception) when (exception is SqliteException or InvalidOperationException)
        {
            return AppResult.Failure<IAppUnitOfWork>(
                new AppError(
                    AppErrorCode.PersistenceUnavailable,
                    "storage.transaction_failed",
                    "A local database transaction could not be started.",
                    IsRetryable: true,
                    Details: new Dictionary<string, string>
                    {
                        ["reason"] = exception.GetType().Name,
                    }));
        }
    }
}

internal sealed class SqliteAppUnitOfWork : IAppUnitOfWork
{
    private readonly SqliteConnection _connection;
    private readonly SqliteTransaction _transaction;
    private bool _completed;

    internal SqliteAppUnitOfWork(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        _connection = connection;
        _transaction = transaction;
        SqlitePromptSearchWriter search = new(connection, transaction);
        Search = search;
        Prompts = new SqlitePromptRepository(connection, transaction, search);
        Intents = new SqliteIntentRepository(connection, transaction, search);
        Metadata = new SqliteMetadataRepository(connection, transaction, search);
        IntentCandidates = new SqliteIntentCandidateRepository(connection, transaction);
    }

    public IPromptRepository Prompts { get; }

    public IIntentRepository Intents { get; }

    public IMetadataRepository Metadata { get; }

    public IIntentCandidateRepository IntentCandidates { get; }

    public IPromptSearchWriter Search { get; }

    public async Task<AppResult> CommitAsync(CancellationToken cancellationToken)
    {
        if (_completed)
        {
            return AppResult.Failure(
                new AppError(
                    AppErrorCode.Conflict,
                    "storage.transaction_completed",
                    "The database transaction has already completed."));
        }

        try
        {
            await _transaction.CommitAsync(cancellationToken);
            _completed = true;
            return AppResult.Success();
        }
        catch (SqliteException exception)
        {
            return AppResult.Failure(
                new AppError(
                    AppErrorCode.PersistenceUnavailable,
                    "storage.commit_failed",
                    "The local database transaction could not be committed.",
                    IsRetryable: true,
                    Details: new Dictionary<string, string>
                    {
                        ["reason"] = exception.GetType().Name,
                    }));
        }
    }

    public async Task RollbackAsync(CancellationToken cancellationToken)
    {
        if (!_completed)
        {
            await _transaction.RollbackAsync(cancellationToken);
            _completed = true;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_completed)
        {
            await _transaction.RollbackAsync();
        }

        await _transaction.DisposeAsync();
        await _connection.DisposeAsync();
    }
}

internal sealed class SqlitePromptRepository : IPromptRepository
{
    private readonly SqliteConnection _connection;
    private readonly SqliteTransaction _transaction;
    private readonly SqlitePromptSearchWriter _search;

    internal SqlitePromptRepository(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SqlitePromptSearchWriter search)
    {
        _connection = connection;
        _transaction = transaction;
        _search = search;
    }

    public async Task<Prompt?> GetAsync(PromptId id, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = CreateCommand("""
            SELECT body, title, title_source, created_at_utc, updated_at_utc,
                   last_copied_at_utc, copy_count, intent_id, intent_assignment,
                   intent_review_state, intent_score_basis_points,
                   scoring_algorithm_version, metadata_status, content_hash, version
            FROM prompts
            WHERE id = $id;
            """);
        command.Parameters.AddWithValue("$id", id.ToString());
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        string body = reader.GetString(0);
        string? title = reader.IsDBNull(1) ? null : reader.GetString(1);
        TitleSource titleSource = (TitleSource)reader.GetInt32(2);
        DateTimeOffset created = ParseDate(reader.GetString(3));
        DateTimeOffset updated = ParseDate(reader.GetString(4));
        DateTimeOffset? copied = reader.IsDBNull(5) ? null : ParseDate(reader.GetString(5));
        long copyCount = reader.GetInt64(6);
        IntentId intentId = new(Guid.Parse(reader.GetString(7)));
        IntentAssignment assignment = (IntentAssignment)reader.GetInt32(8);
        IntentReviewState reviewState = (IntentReviewState)reader.GetInt32(9);
        ScoreBasisPoints? score = reader.IsDBNull(10)
            ? null
            : ScoreBasisPoints.Create(reader.GetInt32(10));
        string? algorithm = reader.IsDBNull(11) ? null : reader.GetString(11);
        MetadataStatus metadataStatus = (MetadataStatus)reader.GetInt32(12);
        byte[] contentHash = (byte[])reader[13];
        long version = reader.GetInt64(14);
        await reader.DisposeAsync();

        IReadOnlyList<PromptSkill> skills =
            await ReadSkillsAsync(id, cancellationToken);
        IReadOnlyList<PromptEntity> entities =
            await ReadEntitiesAsync(id, cancellationToken);
        Prompt prompt = DomainHydrator.Prompt(
            id,
            PromptBody.Create(body),
            PromptTitle.Create(title),
            titleSource,
            created,
            updated,
            copied,
            copyCount,
            intentId,
            assignment,
            reviewState,
            score,
            algorithm,
            metadataStatus,
            contentHash,
            version,
            skills,
            entities);

        SqliteIntentCandidateRepository candidates =
            new(_connection, _transaction);
        prompt.SetIntentCandidates(await candidates.ListAsync(id, cancellationToken));
        return prompt;
    }

    public async Task<IReadOnlyList<Prompt>> ListByIntentAsync(
        IntentId intentId,
        CancellationToken cancellationToken)
    {
        List<Prompt> prompts = [];
        await using SqliteCommand command = CreateCommand(
            "SELECT id FROM prompts WHERE intent_id = $intentId ORDER BY created_at_utc, id;");
        command.Parameters.AddWithValue("$intentId", intentId.ToString());
        List<PromptId> ids = [];
        await using (SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                ids.Add(new PromptId(Guid.Parse(reader.GetString(0))));
            }
        }

        foreach (PromptId id in ids)
        {
            Prompt? prompt = await GetAsync(id, cancellationToken);
            if (prompt is not null)
            {
                prompts.Add(prompt);
            }
        }

        return prompts;
    }

    public async Task AddAsync(Prompt prompt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        await using SqliteCommand command = CreateCommand("""
            INSERT INTO prompts(
                id, body, title, title_source, created_at_utc, updated_at_utc,
                last_copied_at_utc, copy_count, intent_id, intent_assignment,
                intent_review_state, intent_score_basis_points,
                scoring_algorithm_version, metadata_status, content_hash, version)
            VALUES(
                $id, $body, $title, $titleSource, $created, $updated,
                $lastCopied, $copyCount, $intentId, $assignment,
                $reviewState, $score, $algorithm, $metadataStatus, $hash, $version);
            """);
        BindPrompt(command, prompt);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await _search.ReplaceDocumentAsync(prompt.Id, cancellationToken);
    }

    public async Task UpdateAsync(
        Prompt prompt,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        await using SqliteCommand command = CreateCommand("""
            UPDATE prompts SET
                body = $body,
                title = $title,
                title_source = $titleSource,
                updated_at_utc = $updated,
                last_copied_at_utc = $lastCopied,
                copy_count = $copyCount,
                intent_id = $intentId,
                intent_assignment = $assignment,
                intent_review_state = $reviewState,
                intent_score_basis_points = $score,
                scoring_algorithm_version = $algorithm,
                metadata_status = $metadataStatus,
                content_hash = $hash,
                version = $version
            WHERE id = $id AND version = $expectedVersion;
            """);
        BindPrompt(command, prompt);
        command.Parameters.AddWithValue("$expectedVersion", expectedVersion);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new PersistenceConcurrencyException(
                $"Prompt {prompt.Id} changed or no longer exists.");
        }

        await _search.ReplaceDocumentAsync(prompt.Id, cancellationToken);
    }

    public async Task DeleteAsync(
        PromptId id,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        await _search.DeleteDocumentAsync(id, cancellationToken);

        await using SqliteCommand command = CreateCommand(
            "DELETE FROM prompts WHERE id = $id AND version = $expectedVersion;");
        command.Parameters.AddWithValue("$id", id.ToString());
        command.Parameters.AddWithValue("$expectedVersion", expectedVersion);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new PersistenceConcurrencyException(
                $"Prompt {id} changed or no longer exists.");
        }

        await DeleteOrphanMetadataAsync(cancellationToken);
        await MarkArchiveEligibleIntentsAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<PromptSkill>> ReadSkillsAsync(
        PromptId promptId,
        CancellationToken cancellationToken)
    {
        List<PromptSkill> skills = [];
        await using SqliteCommand command = CreateCommand("""
            SELECT skill_id, source, confidence, extractor_version
            FROM prompt_skills
            WHERE prompt_id = $promptId
            ORDER BY skill_id;
            """);
        command.Parameters.AddWithValue("$promptId", promptId.ToString());
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            skills.Add(
                PromptSkill.Create(
                    promptId,
                    new SkillId(Guid.Parse(reader.GetString(0))),
                    (MetadataSource)reader.GetInt32(1),
                    Confidence.Create(decimal.Parse(reader.GetString(2), CultureInfo.InvariantCulture)),
                    reader.GetString(3)));
        }

        return skills;
    }

    private async Task<IReadOnlyList<PromptEntity>> ReadEntitiesAsync(
        PromptId promptId,
        CancellationToken cancellationToken)
    {
        List<PromptEntity> entities = [];
        await using SqliteCommand command = CreateCommand("""
            SELECT entity_id, source, confidence, extractor_version
            FROM prompt_entities
            WHERE prompt_id = $promptId
            ORDER BY entity_id;
            """);
        command.Parameters.AddWithValue("$promptId", promptId.ToString());
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            entities.Add(
                PromptEntity.Create(
                    promptId,
                    new EntityId(Guid.Parse(reader.GetString(0))),
                    (MetadataSource)reader.GetInt32(1),
                    Confidence.Create(decimal.Parse(reader.GetString(2), CultureInfo.InvariantCulture)),
                    reader.GetString(3)));
        }

        return entities;
    }

    private async Task DeleteOrphanMetadataAsync(CancellationToken cancellationToken)
    {
        await using SqliteCommand command = CreateCommand("""
            DELETE FROM skills
            WHERE NOT EXISTS(
                SELECT 1 FROM prompt_skills WHERE prompt_skills.skill_id = skills.id
            );
            DELETE FROM proper_entities
            WHERE NOT EXISTS(
                SELECT 1 FROM prompt_entities WHERE prompt_entities.entity_id = proper_entities.id
            );
            """);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task MarkArchiveEligibleIntentsAsync(CancellationToken cancellationToken)
    {
        await using SqliteCommand command = CreateCommand("""
            UPDATE intents
            SET is_archive_eligible =
                CASE WHEN id <> $unsorted
                       AND NOT EXISTS(SELECT 1 FROM prompts WHERE prompts.intent_id = intents.id)
                       AND NOT EXISTS(SELECT 1 FROM intent_aliases WHERE intent_aliases.intent_id = intents.id)
                     THEN 1 ELSE 0 END;
            """);
        command.Parameters.AddWithValue("$unsorted", Intent.ReservedUnsortedId.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void BindPrompt(SqliteCommand command, Prompt prompt)
    {
        command.Parameters.AddWithValue("$id", prompt.Id.ToString());
        command.Parameters.AddWithValue("$body", prompt.Body.Value);
        command.Parameters.AddWithValue("$title", (object?)prompt.Title?.Value ?? DBNull.Value);
        command.Parameters.AddWithValue("$titleSource", (int)prompt.TitleSource);
        command.Parameters.AddWithValue("$created", SqliteMigrationRunner.Format(prompt.CreatedAtUtc));
        command.Parameters.AddWithValue("$updated", SqliteMigrationRunner.Format(prompt.UpdatedAtUtc));
        command.Parameters.AddWithValue(
            "$lastCopied",
            prompt.LastCopiedAtUtc is null
                ? DBNull.Value
                : SqliteMigrationRunner.Format(prompt.LastCopiedAtUtc.Value));
        command.Parameters.AddWithValue("$copyCount", prompt.CopyCount);
        command.Parameters.AddWithValue("$intentId", prompt.IntentId.ToString());
        command.Parameters.AddWithValue("$assignment", (int)prompt.IntentAssignment);
        command.Parameters.AddWithValue("$reviewState", (int)prompt.IntentReviewState);
        command.Parameters.AddWithValue(
            "$score",
            prompt.IntentScoreBasisPoints is null
                ? DBNull.Value
                : prompt.IntentScoreBasisPoints.Value.Value);
        command.Parameters.AddWithValue(
            "$algorithm",
            (object?)prompt.ScoringAlgorithmVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("$metadataStatus", (int)prompt.MetadataStatus);
        command.Parameters.Add("$hash", SqliteType.Blob).Value = prompt.ContentHash;
        command.Parameters.AddWithValue("$version", prompt.Version);
    }

    private SqliteCommand CreateCommand(string commandText)
    {
        SqliteCommand command = _connection.CreateCommand();
        command.Transaction = _transaction;
        command.CommandText = commandText;
        return command;
    }

    private static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}

internal sealed class SqliteIntentRepository : IIntentRepository
{
    private readonly SqliteConnection _connection;
    private readonly SqliteTransaction _transaction;
    private readonly SqlitePromptSearchWriter _search;

    internal SqliteIntentRepository(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SqlitePromptSearchWriter search)
    {
        _connection = connection;
        _transaction = transaction;
        _search = search;
    }

    public async Task<Intent?> GetAsync(IntentId id, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = CreateCommand("""
            SELECT canonical_name, normalized_key, description, source, is_archived,
                   created_at_utc, updated_at_utc, version, merged_into_intent_id
            FROM intents
            WHERE id = $id;
            """);
        command.Parameters.AddWithValue("$id", id.ToString());
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadIntent(id, reader)
            : null;
    }

    public async Task<Intent?> FindByNormalizedKeyAsync(
        string normalizedKey,
        bool includeArchived,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = CreateCommand("""
            SELECT id, canonical_name, normalized_key, description, source, is_archived,
                   created_at_utc, updated_at_utc, version, merged_into_intent_id
            FROM intents
            WHERE normalized_key = $key AND ($includeArchived = 1 OR is_archived = 0)
            UNION ALL
            SELECT i.id, i.canonical_name, i.normalized_key, i.description, i.source, i.is_archived,
                   i.created_at_utc, i.updated_at_utc, i.version, i.merged_into_intent_id
            FROM intent_aliases a
            JOIN intents i ON i.id = a.intent_id
            WHERE a.normalized_key = $key AND ($includeArchived = 1 OR i.is_archived = 0)
            LIMIT 1;
            """);
        command.Parameters.AddWithValue("$key", normalizedKey);
        command.Parameters.AddWithValue("$includeArchived", includeArchived ? 1 : 0);
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        IntentId id = new(Guid.Parse(reader.GetString(0)));
        return ReadIntent(id, reader, offset: 1);
    }

    public Task<IReadOnlyList<Intent>> ListActiveAsync(CancellationToken cancellationToken) =>
        ListAsync(includeArchived: false, cancellationToken);

    public async Task<IReadOnlyList<IntentAlias>> ListAliasesAsync(
        CancellationToken cancellationToken) =>
        await ListAliasesCoreAsync(null, cancellationToken);

    public async Task<IReadOnlyList<IntentAlias>> ListAliasesAsync(
        IntentId intentId,
        CancellationToken cancellationToken) =>
        await ListAliasesCoreAsync(intentId, cancellationToken);

    public async Task AddAsync(Intent intent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(intent);
        await using SqliteCommand command = CreateCommand("""
            INSERT INTO intents(
                id, canonical_name, normalized_key, description, source,
                is_archived, is_archive_eligible, created_at_utc, updated_at_utc,
                version, merged_into_intent_id)
            VALUES(
                $id, $name, $key, $description, $source,
                $archived, 0, $created, $updated, $version, $mergedInto);
            """);
        BindIntent(command, intent);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateAsync(
        Intent intent,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(intent);
        await using SqliteCommand command = CreateCommand("""
            UPDATE intents SET
                canonical_name = $name,
                normalized_key = $key,
                description = $description,
                is_archived = $archived,
                updated_at_utc = $updated,
                version = $version,
                merged_into_intent_id = $mergedInto
            WHERE id = $id AND version = $expectedVersion;
            """);
        BindIntent(command, intent);
        command.Parameters.AddWithValue("$expectedVersion", expectedVersion);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new PersistenceConcurrencyException(
                $"Intent {intent.Id} changed or no longer exists.");
        }

        await ReplaceDocumentsForIntentAsync(intent.Id, cancellationToken);
    }

    public async Task AddAliasAsync(
        IntentAlias intentAlias,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(intentAlias);
        await using SqliteCommand command = CreateCommand("""
            INSERT INTO intent_aliases(id, intent_id, value, normalized_key, created_at_utc)
            VALUES($id, $intentId, $value, $key, $created);
            """);
        command.Parameters.AddWithValue("$id", intentAlias.Id.ToString());
        command.Parameters.AddWithValue("$intentId", intentAlias.IntentId.ToString());
        command.Parameters.AddWithValue("$value", intentAlias.Value);
        command.Parameters.AddWithValue("$key", intentAlias.NormalizedKey);
        command.Parameters.AddWithValue(
            "$created",
            SqliteMigrationRunner.Format(intentAlias.CreatedAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await ReplaceDocumentsForIntentAsync(intentAlias.IntentId, cancellationToken);
    }

    private async Task<IReadOnlyList<Intent>> ListAsync(
        bool includeArchived,
        CancellationToken cancellationToken)
    {
        List<Intent> intents = [];
        await using SqliteCommand command = CreateCommand("""
            SELECT id, canonical_name, normalized_key, description, source, is_archived,
                   created_at_utc, updated_at_utc, version, merged_into_intent_id
            FROM intents
            WHERE $includeArchived = 1 OR is_archived = 0
            ORDER BY normalized_key, id;
            """);
        command.Parameters.AddWithValue("$includeArchived", includeArchived ? 1 : 0);
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            intents.Add(
                ReadIntent(
                    new IntentId(Guid.Parse(reader.GetString(0))),
                    reader,
                    offset: 1));
        }

        return intents;
    }

    private async Task<IReadOnlyList<IntentAlias>> ListAliasesCoreAsync(
        IntentId? intentId,
        CancellationToken cancellationToken)
    {
        List<IntentAlias> aliases = [];
        await using SqliteCommand command = CreateCommand("""
            SELECT id, intent_id, value, created_at_utc
            FROM intent_aliases
            WHERE $intentId IS NULL OR intent_id = $intentId
            ORDER BY normalized_key, id;
            """);
        command.Parameters.AddWithValue(
            "$intentId",
            intentId is null ? DBNull.Value : intentId.Value.ToString());
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            aliases.Add(
                IntentAlias.Create(
                    new IntentAliasId(Guid.Parse(reader.GetString(0))),
                    new IntentId(Guid.Parse(reader.GetString(1))),
                    reader.GetString(2),
                    ParseDate(reader.GetString(3))));
        }

        return aliases;
    }

    private async Task ReplaceDocumentsForIntentAsync(
        IntentId intentId,
        CancellationToken cancellationToken)
    {
        List<PromptId> promptIds = [];
        await using SqliteCommand command = CreateCommand(
            "SELECT id FROM prompts WHERE intent_id = $intentId;");
        command.Parameters.AddWithValue("$intentId", intentId.ToString());
        await using (SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                promptIds.Add(new PromptId(Guid.Parse(reader.GetString(0))));
            }
        }

        foreach (PromptId promptId in promptIds)
        {
            await _search.ReplaceDocumentAsync(promptId, cancellationToken);
        }
    }

    private static Intent ReadIntent(IntentId id, SqliteDataReader reader, int offset = 0) =>
        DomainHydrator.Intent(
            id,
            reader.GetString(offset),
            reader.GetString(offset + 1),
            reader.IsDBNull(offset + 2) ? null : reader.GetString(offset + 2),
            (IntentSource)reader.GetInt32(offset + 3),
            reader.GetBoolean(offset + 4),
            ParseDate(reader.GetString(offset + 5)),
            ParseDate(reader.GetString(offset + 6)),
            reader.GetInt64(offset + 7),
            reader.IsDBNull(offset + 8)
                ? null
                : new IntentId(Guid.Parse(reader.GetString(offset + 8))));

    private static void BindIntent(SqliteCommand command, Intent intent)
    {
        command.Parameters.AddWithValue("$id", intent.Id.ToString());
        command.Parameters.AddWithValue("$name", intent.CanonicalName);
        command.Parameters.AddWithValue("$key", intent.NormalizedKey);
        command.Parameters.AddWithValue(
            "$description",
            (object?)intent.Description ?? DBNull.Value);
        command.Parameters.AddWithValue("$source", (int)intent.Source);
        command.Parameters.AddWithValue("$archived", intent.IsArchived ? 1 : 0);
        command.Parameters.AddWithValue("$created", SqliteMigrationRunner.Format(intent.CreatedAtUtc));
        command.Parameters.AddWithValue("$updated", SqliteMigrationRunner.Format(intent.UpdatedAtUtc));
        command.Parameters.AddWithValue("$version", intent.Version);
        command.Parameters.AddWithValue(
            "$mergedInto",
            intent.MergedIntoIntentId is null
                ? DBNull.Value
                : intent.MergedIntoIntentId.Value.ToString());
    }

    private SqliteCommand CreateCommand(string commandText)
    {
        SqliteCommand command = _connection.CreateCommand();
        command.Transaction = _transaction;
        command.CommandText = commandText;
        return command;
    }

    private static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}

internal sealed class SqliteMetadataRepository : IMetadataRepository
{
    private readonly SqliteConnection _connection;
    private readonly SqliteTransaction _transaction;
    private readonly SqlitePromptSearchWriter _search;

    internal SqliteMetadataRepository(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SqlitePromptSearchWriter search)
    {
        _connection = connection;
        _transaction = transaction;
        _search = search;
    }

    public async Task<Skill?> GetSkillAsync(
        SkillId id,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = CreateCommand(
            "SELECT name FROM skills WHERE id = $id;");
        command.Parameters.AddWithValue("$id", id.ToString());
        object? name = await command.ExecuteScalarAsync(cancellationToken);
        return name is string value ? Skill.Create(id, value) : null;
    }

    public async Task<Entity?> GetEntityAsync(
        EntityId id,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = CreateCommand(
            "SELECT name, entity_type FROM proper_entities WHERE id = $id;");
        command.Parameters.AddWithValue("$id", id.ToString());
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? Entity.Create(id, reader.GetString(0), (EntityType)reader.GetInt32(1))
            : null;
    }

    public async Task<Skill?> FindSkillByNormalizedKeyAsync(
        string normalizedKey,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = CreateCommand("""
            SELECT id, name
            FROM skills
            WHERE normalized_key = $key
            UNION ALL
            SELECT s.id, s.name
            FROM skill_aliases a
            JOIN skills s ON s.id = a.skill_id
            WHERE a.normalized_key = $key
            LIMIT 1;
            """);
        command.Parameters.AddWithValue("$key", normalizedKey);
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? Skill.Create(new SkillId(Guid.Parse(reader.GetString(0))), reader.GetString(1))
            : null;
    }

    public async Task<Entity?> FindEntityByNormalizedKeyAsync(
        string normalizedKey,
        EntityType type,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = CreateCommand("""
            SELECT id, name
            FROM proper_entities
            WHERE normalized_key = $key AND entity_type = $type;
            """);
        command.Parameters.AddWithValue("$key", normalizedKey);
        command.Parameters.AddWithValue("$type", (int)type);
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? Entity.Create(
                new EntityId(Guid.Parse(reader.GetString(0))),
                reader.GetString(1),
                type)
            : null;
    }

    public async Task AddSkillAsync(
        Skill skill,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = CreateCommand("""
            INSERT INTO skills(id, name, normalized_key)
            VALUES($id, $name, $key);
            """);
        command.Parameters.AddWithValue("$id", skill.Id.ToString());
        command.Parameters.AddWithValue("$name", skill.Name);
        command.Parameters.AddWithValue("$key", skill.NormalizedKey);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task AddEntityAsync(
        Entity entity,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = CreateCommand("""
            INSERT INTO proper_entities(id, name, normalized_key, entity_type)
            VALUES($id, $name, $key, $type);
            """);
        command.Parameters.AddWithValue("$id", entity.Id.ToString());
        command.Parameters.AddWithValue("$name", entity.Name);
        command.Parameters.AddWithValue("$key", entity.NormalizedKey);
        command.Parameters.AddWithValue("$type", (int)entity.Type);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ReplacePromptMetadataAsync(
        PromptId promptId,
        IReadOnlyList<PromptSkill> skills,
        IReadOnlyList<PromptEntity> entities,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(skills);
        ArgumentNullException.ThrowIfNull(entities);

        await using (SqliteCommand delete = CreateCommand("""
            DELETE FROM prompt_skills WHERE prompt_id = $promptId;
            DELETE FROM prompt_entities WHERE prompt_id = $promptId;
            """))
        {
            delete.Parameters.AddWithValue("$promptId", promptId.ToString());
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (PromptSkill skill in skills)
        {
            await using SqliteCommand insert = CreateCommand("""
                INSERT INTO prompt_skills(
                    prompt_id, skill_id, source, confidence, extractor_version)
                VALUES($promptId, $skillId, $source, $confidence, $version);
                """);
            insert.Parameters.AddWithValue("$promptId", promptId.ToString());
            insert.Parameters.AddWithValue("$skillId", skill.SkillId.ToString());
            insert.Parameters.AddWithValue("$source", (int)skill.Source);
            insert.Parameters.AddWithValue(
                "$confidence",
                skill.Confidence.Value.ToString(CultureInfo.InvariantCulture));
            insert.Parameters.AddWithValue("$version", skill.ExtractorVersion);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (PromptEntity entity in entities)
        {
            await using SqliteCommand insert = CreateCommand("""
                INSERT INTO prompt_entities(
                    prompt_id, entity_id, source, confidence, extractor_version)
                VALUES($promptId, $entityId, $source, $confidence, $version);
                """);
            insert.Parameters.AddWithValue("$promptId", promptId.ToString());
            insert.Parameters.AddWithValue("$entityId", entity.EntityId.ToString());
            insert.Parameters.AddWithValue("$source", (int)entity.Source);
            insert.Parameters.AddWithValue(
                "$confidence",
                entity.Confidence.Value.ToString(CultureInfo.InvariantCulture));
            insert.Parameters.AddWithValue("$version", entity.ExtractorVersion);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await DeleteOrphansAsync(cancellationToken);
        await _search.ReplaceDocumentAsync(promptId, cancellationToken);
    }

    public async Task DeleteOrphansAsync(CancellationToken cancellationToken)
    {
        await using SqliteCommand command = CreateCommand("""
            DELETE FROM skills
            WHERE NOT EXISTS(
                SELECT 1 FROM prompt_skills WHERE prompt_skills.skill_id = skills.id
            );
            DELETE FROM proper_entities
            WHERE NOT EXISTS(
                SELECT 1 FROM prompt_entities WHERE prompt_entities.entity_id = proper_entities.id
            );
            """);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private SqliteCommand CreateCommand(string commandText)
    {
        SqliteCommand command = _connection.CreateCommand();
        command.Transaction = _transaction;
        command.CommandText = commandText;
        return command;
    }
}

internal sealed class SqliteIntentCandidateRepository : IIntentCandidateRepository
{
    private readonly SqliteConnection _connection;
    private readonly SqliteTransaction _transaction;

    internal SqliteIntentCandidateRepository(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        _connection = connection;
        _transaction = transaction;
    }

    public async Task ReplaceAsync(
        PromptId promptId,
        IReadOnlyList<IntentCandidateScore> candidates,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count > 5)
        {
            throw new ArgumentOutOfRangeException(
                nameof(candidates),
                "At most five intent candidates may be persisted.");
        }

        await using (SqliteCommand delete = CreateCommand(
            "DELETE FROM prompt_intent_candidates WHERE prompt_id = $promptId;"))
        {
            delete.Parameters.AddWithValue("$promptId", promptId.ToString());
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (IntentCandidateScore candidate in candidates.OrderBy(candidate => candidate.Rank))
        {
            if (candidate.Score.Value < 6000)
            {
                continue;
            }

            await using SqliteCommand insert = CreateCommand("""
                INSERT INTO prompt_intent_candidates(
                    prompt_id, candidate_intent_id, score_basis_points, rank,
                    scoring_algorithm_version, created_at_utc)
                VALUES($promptId, $intentId, $score, $rank, $algorithm, $created);
                """);
            insert.Parameters.AddWithValue("$promptId", promptId.ToString());
            insert.Parameters.AddWithValue("$intentId", candidate.IntentId.ToString());
            insert.Parameters.AddWithValue("$score", candidate.Score.Value);
            insert.Parameters.AddWithValue("$rank", candidate.Rank);
            insert.Parameters.AddWithValue("$algorithm", candidate.ScoringAlgorithmVersion);
            insert.Parameters.AddWithValue(
                "$created",
                SqliteMigrationRunner.Format(candidate.CreatedAtUtc));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task<IReadOnlyList<IntentCandidateScore>> ListAsync(
        PromptId promptId,
        CancellationToken cancellationToken)
    {
        List<IntentCandidateScore> candidates = [];
        await using SqliteCommand command = CreateCommand("""
            SELECT candidate_intent_id, score_basis_points, rank,
                   scoring_algorithm_version, created_at_utc
            FROM prompt_intent_candidates
            WHERE prompt_id = $promptId
            ORDER BY rank;
            """);
        command.Parameters.AddWithValue("$promptId", promptId.ToString());
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            candidates.Add(
                IntentCandidateScore.Create(
                    new IntentId(Guid.Parse(reader.GetString(0))),
                    ScoreBasisPoints.Create(reader.GetInt32(1)),
                    reader.GetInt32(2),
                    reader.GetString(3),
                    DateTimeOffset.Parse(
                        reader.GetString(4),
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind)));
        }

        return candidates;
    }

    private SqliteCommand CreateCommand(string commandText)
    {
        SqliteCommand command = _connection.CreateCommand();
        command.Transaction = _transaction;
        command.CommandText = commandText;
        return command;
    }
}
