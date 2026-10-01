using System.Globalization;
using Microsoft.Data.Sqlite;
using PromptSaver.Application;
using PromptSaver.Application.Dtos;
using PromptSaver.Application.Ports;
using PromptSaver.Domain.Entities;
using PromptSaver.Domain.ValueObjects;

namespace PromptSaver.Infrastructure.Storage;

public sealed class SqliteCatalogWriter
{
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly SqliteMigrationRunner _migrationRunner;

    public SqliteCatalogWriter(
        SqliteConnectionFactory connectionFactory,
        SqliteMigrationRunner migrationRunner)
    {
        _connectionFactory = connectionFactory;
        _migrationRunner = migrationRunner;
    }

    public async Task<AppResult> AddSkillAsync(
        Skill skill,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(skill);
        AppResult initialized = await _migrationRunner.InitializeAsync(cancellationToken);
        if (!initialized.IsSuccess)
        {
            return initialized;
        }

        try
        {
            await using SqliteConnection connection =
                await _connectionFactory.OpenAsync(cancellationToken);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO skills(id, name, normalized_key)
                VALUES($id, $name, $key)
                ON CONFLICT(normalized_key) DO NOTHING;
                """;
            command.Parameters.AddWithValue("$id", skill.Id.ToString());
            command.Parameters.AddWithValue("$name", skill.Name);
            command.Parameters.AddWithValue("$key", skill.NormalizedKey);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return AppResult.Success();
        }
        catch (SqliteException exception)
        {
            return Failure("metadata.skill_add_failed", exception);
        }
    }

    public async Task<AppResult> AddEntityAsync(
        Entity entity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entity);
        AppResult initialized = await _migrationRunner.InitializeAsync(cancellationToken);
        if (!initialized.IsSuccess)
        {
            return initialized;
        }

        try
        {
            await using SqliteConnection connection =
                await _connectionFactory.OpenAsync(cancellationToken);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO proper_entities(id, name, normalized_key, entity_type)
                VALUES($id, $name, $key, $type)
                ON CONFLICT(normalized_key, entity_type) DO NOTHING;
                """;
            command.Parameters.AddWithValue("$id", entity.Id.ToString());
            command.Parameters.AddWithValue("$name", entity.Name);
            command.Parameters.AddWithValue("$key", entity.NormalizedKey);
            command.Parameters.AddWithValue("$type", (int)entity.Type);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return AppResult.Success();
        }
        catch (SqliteException exception)
        {
            return Failure("metadata.entity_add_failed", exception);
        }
    }

    private static AppResult Failure(string key, Exception exception) =>
        AppResult.Failure(
            new AppError(
                AppErrorCode.PersistenceUnavailable,
                key,
                "Metadata could not be persisted.",
                IsRetryable: true,
                Details: new Dictionary<string, string>
                {
                    ["reason"] = exception.GetType().Name,
                }));
}

public sealed class SqlitePersistenceOperations : IPromptPersistenceOperations
{
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly SqliteMigrationRunner _migrationRunner;

    public SqlitePersistenceOperations(
        SqliteConnectionFactory connectionFactory,
        SqliteMigrationRunner migrationRunner)
    {
        _connectionFactory = connectionFactory;
        _migrationRunner = migrationRunner;
    }

    public async Task<AppResult<PromptId>> DuplicateAsync(
        PromptId sourcePromptId,
        PromptId duplicatePromptId,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken)
    {
        if (sourcePromptId == duplicatePromptId)
        {
            return AppResult.Failure<PromptId>(
                new AppError(
                    AppErrorCode.Validation,
                    "prompt.duplicate.same_id",
                    "A duplicate must have a new prompt identifier."));
        }

        AppResult initialized = await _migrationRunner.InitializeAsync(cancellationToken);
        if (!initialized.IsSuccess)
        {
            return AppResult.Failure<PromptId>(initialized.Error!);
        }

        try
        {
            await using SqliteConnection connection =
                await _connectionFactory.OpenAsync(cancellationToken);
            await using SqliteTransaction transaction =
                (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await using SqliteCommand duplicate = connection.CreateCommand();
            duplicate.Transaction = transaction;
            duplicate.CommandText = """
                INSERT INTO prompts(
                    id, body, title, title_source, created_at_utc, updated_at_utc,
                    last_copied_at_utc, copy_count, intent_id, intent_assignment,
                    intent_review_state, intent_score_basis_points,
                    scoring_algorithm_version, metadata_status, content_hash, version)
                SELECT
                    $duplicateId, body, title, title_source, $created, $created,
                    NULL, 0, intent_id, $assignment, $reviewState, NULL,
                    NULL, $metadataStatus, content_hash, 0
                FROM prompts
                WHERE id = $sourceId;

                INSERT INTO prompt_skills(
                    prompt_id, skill_id, source, confidence, extractor_version)
                SELECT $duplicateId, skill_id, source, confidence, extractor_version
                FROM prompt_skills
                WHERE prompt_id = $sourceId;

                INSERT INTO prompt_entities(
                    prompt_id, entity_id, source, confidence, extractor_version)
                SELECT $duplicateId, entity_id, source, confidence, extractor_version
                FROM prompt_entities
                WHERE prompt_id = $sourceId;
                """;
            duplicate.Parameters.AddWithValue("$duplicateId", duplicatePromptId.ToString());
            duplicate.Parameters.AddWithValue("$sourceId", sourcePromptId.ToString());
            duplicate.Parameters.AddWithValue(
                "$created",
                SqliteMigrationRunner.Format(createdAtUtc));
            duplicate.Parameters.AddWithValue("$assignment", (int)IntentAssignment.UserSelected);
            duplicate.Parameters.AddWithValue("$reviewState", (int)IntentReviewState.Resolved);
            duplicate.Parameters.AddWithValue("$metadataStatus", (int)MetadataStatus.LocalComplete);
            int rows = await duplicate.ExecuteNonQueryAsync(cancellationToken);
            if (rows == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return AppResult.Failure<PromptId>(
                    new AppError(
                        AppErrorCode.NotFound,
                        "prompt.not_found",
                        "The prompt to duplicate was not found."));
            }

            SqlitePromptSearchWriter search = new(connection, transaction);
            await search.ReplaceDocumentAsync(duplicatePromptId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return AppResult.Success(duplicatePromptId);
        }
        catch (SqliteException exception)
        {
            return AppResult.Failure<PromptId>(
                Failure("prompt.duplicate_failed", exception).Error!);
        }
    }

    public async Task<AppResult> DeleteAsync(
        PromptId promptId,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        AppResult initialized = await _migrationRunner.InitializeAsync(cancellationToken);
        if (!initialized.IsSuccess)
        {
            return initialized;
        }

        try
        {
            await using SqliteConnection connection =
                await _connectionFactory.OpenAsync(cancellationToken);
            await using SqliteTransaction transaction =
                (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            SqlitePromptRepository repository = new(
                connection,
                transaction,
                new SqlitePromptSearchWriter(connection, transaction));
            await repository.DeleteAsync(promptId, expectedVersion, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return AppResult.Success();
        }
        catch (PersistenceConcurrencyException exception)
        {
            return AppResult.Failure(
                new AppError(
                    AppErrorCode.Conflict,
                    "prompt.delete_conflict",
                    exception.Message));
        }
        catch (SqliteException exception)
        {
            return Failure("prompt.delete_failed", exception);
        }
    }

    public async Task<AppResult<MergeIntentsResult>> MergeIntentsAsync(
        MergeIntentsCommand merge,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken)
    {
        if (merge.SourceIntentId == Intent.ReservedUnsortedId ||
            merge.TargetIntentId == Intent.ReservedUnsortedId ||
            merge.SourceIntentId == merge.TargetIntentId)
        {
            return AppResult.Failure<MergeIntentsResult>(
                new AppError(
                    AppErrorCode.Validation,
                    "intent.merge.invalid",
                    "Unsorted cannot be merged and an intent cannot be merged into itself."));
        }

        AppResult initialized = await _migrationRunner.InitializeAsync(cancellationToken);
        if (!initialized.IsSuccess)
        {
            return AppResult.Failure<MergeIntentsResult>(initialized.Error!);
        }

        try
        {
            await using SqliteConnection connection =
                await _connectionFactory.OpenAsync(cancellationToken);
            await using SqliteTransaction transaction =
                (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

            (string SourceName, string SourceKey)? source =
                await ReadIntentNameAsync(
                    connection,
                    transaction,
                    merge.SourceIntentId,
                    merge.SourceVersion,
                    cancellationToken);
            bool targetExists =
                await IntentVersionExistsAsync(
                    connection,
                    transaction,
                    merge.TargetIntentId,
                    merge.TargetVersion,
                    cancellationToken);
            if (source is null || !targetExists)
            {
                await transaction.RollbackAsync(cancellationToken);
                return AppResult.Failure<MergeIntentsResult>(
                    new AppError(
                        AppErrorCode.Conflict,
                        "intent.merge_conflict",
                        "One of the intents changed or no longer exists."));
            }

            List<PromptId> affectedPromptIds =
                await ReadPromptIdsAsync(
                    connection,
                    transaction,
                    merge.SourceIntentId,
                    cancellationToken);
            List<MergeIntentConflictDto> conflicts =
                await MoveAliasesAsync(
                    connection,
                    transaction,
                    merge.SourceIntentId,
                    merge.TargetIntentId,
                    cancellationToken);
            await AddSourceNameAliasAsync(
                connection,
                transaction,
                source.Value.SourceName,
                source.Value.SourceKey,
                merge.TargetIntentId,
                updatedAtUtc,
                conflicts,
                cancellationToken);

            await using (SqliteCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    UPDATE prompts SET
                        intent_id = $targetId,
                        intent_assignment = $assignment,
                        intent_review_state = $reviewState,
                        intent_score_basis_points = NULL,
                        scoring_algorithm_version = NULL,
                        updated_at_utc = $updated,
                        version = version + 1
                    WHERE intent_id = $sourceId;

                    DELETE FROM prompt_intent_candidates
                    WHERE prompt_id IN (
                        SELECT id FROM prompts WHERE intent_id = $targetId
                    ) OR candidate_intent_id = $sourceId;

                    UPDATE intents SET
                        is_archived = 1,
                        is_archive_eligible = 0,
                        merged_into_intent_id = $targetId,
                        updated_at_utc = $updated,
                        version = version + 1
                    WHERE id = $sourceId AND version = $sourceVersion;

                    UPDATE intents SET
                        is_archive_eligible = 0,
                        updated_at_utc = $updated,
                        version = version + 1
                    WHERE id = $targetId AND version = $targetVersion;
                    """;
                command.Parameters.AddWithValue("$sourceId", merge.SourceIntentId.ToString());
                command.Parameters.AddWithValue("$targetId", merge.TargetIntentId.ToString());
                command.Parameters.AddWithValue("$assignment", (int)IntentAssignment.Merged);
                command.Parameters.AddWithValue("$reviewState", (int)IntentReviewState.Resolved);
                command.Parameters.AddWithValue(
                    "$updated",
                    SqliteMigrationRunner.Format(updatedAtUtc));
                command.Parameters.AddWithValue("$sourceVersion", merge.SourceVersion);
                command.Parameters.AddWithValue("$targetVersion", merge.TargetVersion);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            SqlitePromptSearchWriter search = new(connection, transaction);
            foreach (PromptId promptId in affectedPromptIds)
            {
                await search.ReplaceDocumentAsync(promptId, cancellationToken);
            }

            await SqliteIntegrityOperations.CheckAsync(connection, transaction, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return AppResult.Success(
                new MergeIntentsResult(
                    merge.SourceIntentId,
                    merge.TargetIntentId,
                    affectedPromptIds.Count,
                    conflicts));
        }
        catch (SqliteException exception)
        {
            return AppResult.Failure<MergeIntentsResult>(
                Failure("intent.merge_failed", exception).Error!);
        }
    }

    private static async Task<(string SourceName, string SourceKey)?> ReadIntentNameAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IntentId intentId,
        long version,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT canonical_name, normalized_key
            FROM intents
            WHERE id = $id AND version = $version AND is_archived = 0;
            """;
        command.Parameters.AddWithValue("$id", intentId.ToString());
        command.Parameters.AddWithValue("$version", version);
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? (reader.GetString(0), reader.GetString(1))
            : null;
    }

    private static async Task<bool> IntentVersionExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IntentId intentId,
        long version,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM intents
                WHERE id = $id AND version = $version AND is_archived = 0
            );
            """;
        command.Parameters.AddWithValue("$id", intentId.ToString());
        command.Parameters.AddWithValue("$version", version);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture) == 1;
    }

    private static async Task<List<PromptId>> ReadPromptIdsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IntentId intentId,
        CancellationToken cancellationToken)
    {
        List<PromptId> promptIds = [];
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id FROM prompts WHERE intent_id = $intentId;";
        command.Parameters.AddWithValue("$intentId", intentId.ToString());
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            promptIds.Add(new PromptId(Guid.Parse(reader.GetString(0))));
        }

        return promptIds;
    }

    private static async Task<List<MergeIntentConflictDto>> MoveAliasesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IntentId sourceIntentId,
        IntentId targetIntentId,
        CancellationToken cancellationToken)
    {
        List<(string Id, string Value, string Key)> aliases = [];
        await using (SqliteCommand read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = """
                SELECT id, value, normalized_key
                FROM intent_aliases
                WHERE intent_id = $sourceId;
                """;
            read.Parameters.AddWithValue("$sourceId", sourceIntentId.ToString());
            await using SqliteDataReader reader =
                await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                aliases.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
        }

        List<MergeIntentConflictDto> conflicts = [];
        foreach ((string id, string value, string key) in aliases)
        {
            IntentId? owner =
                await ReadNormalizedOwnerAsync(
                    connection,
                    transaction,
                    key,
                    sourceIntentId,
                    cancellationToken);
            if (owner is not null && owner != targetIntentId)
            {
                conflicts.Add(new MergeIntentConflictDto(value, owner.Value));
                continue;
            }

            await using SqliteCommand move = connection.CreateCommand();
            move.Transaction = transaction;
            move.CommandText =
                "UPDATE intent_aliases SET intent_id = $targetId WHERE id = $id;";
            move.Parameters.AddWithValue("$targetId", targetIntentId.ToString());
            move.Parameters.AddWithValue("$id", id);
            await move.ExecuteNonQueryAsync(cancellationToken);
        }

        return conflicts;
    }

    private static async Task AddSourceNameAliasAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourceName,
        string sourceKey,
        IntentId targetIntentId,
        DateTimeOffset createdAtUtc,
        List<MergeIntentConflictDto> conflicts,
        CancellationToken cancellationToken)
    {
        IntentId? owner =
            await ReadNormalizedOwnerAsync(
                connection,
                transaction,
                sourceKey,
                targetIntentId,
                cancellationToken);
        if (owner is not null)
        {
            if (owner != targetIntentId)
            {
                conflicts.Add(new MergeIntentConflictDto(sourceName, owner.Value));
            }

            return;
        }

        await using SqliteCommand insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO intent_aliases(id, intent_id, value, normalized_key, created_at_utc)
            VALUES($id, $targetId, $value, $key, $created);
            """;
        insert.Parameters.AddWithValue("$id", Guid.CreateVersion7().ToString());
        insert.Parameters.AddWithValue("$targetId", targetIntentId.ToString());
        insert.Parameters.AddWithValue("$value", sourceName);
        insert.Parameters.AddWithValue("$key", sourceKey);
        insert.Parameters.AddWithValue("$created", SqliteMigrationRunner.Format(createdAtUtc));
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<IntentId?> ReadNormalizedOwnerAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string normalizedKey,
        IntentId excludedIntentId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id
            FROM intents
            WHERE normalized_key = $key AND id <> $excludedId
            UNION ALL
            SELECT intent_id
            FROM intent_aliases
            WHERE normalized_key = $key AND intent_id <> $excludedId
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$key", normalizedKey);
        command.Parameters.AddWithValue("$excludedId", excludedIntentId.ToString());
        object? result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null ? null : new IntentId(Guid.Parse((string)result));
    }

    private static AppResult Failure(string key, Exception exception) =>
        AppResult.Failure(
            new AppError(
                AppErrorCode.PersistenceUnavailable,
                key,
                "The local database operation failed.",
                IsRetryable: true,
                Details: new Dictionary<string, string>
                {
                    ["reason"] = exception.GetType().Name,
                }));
}

public sealed class SqliteAppSettingsStore
{
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly SqliteMigrationRunner _migrationRunner;

    public SqliteAppSettingsStore(
        SqliteConnectionFactory connectionFactory,
        SqliteMigrationRunner migrationRunner)
    {
        _connectionFactory = connectionFactory;
        _migrationRunner = migrationRunner;
    }

    public async Task<AppResult> SetAsync(
        string key,
        string value,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return AppResult.Failure(
                new AppError(AppErrorCode.Validation, "setting.key.required", "A setting key is required."));
        }

        AppResult initialized = await _migrationRunner.InitializeAsync(cancellationToken);
        if (!initialized.IsSuccess)
        {
            return initialized;
        }

        try
        {
            await using SqliteConnection connection =
                await _connectionFactory.OpenAsync(cancellationToken);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO app_settings(key, value, updated_at_utc)
                VALUES($key, $value, $updated)
                ON CONFLICT(key) DO UPDATE SET
                    value = excluded.value,
                    updated_at_utc = excluded.updated_at_utc;
                """;
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$value", value);
            command.Parameters.AddWithValue("$updated", SqliteMigrationRunner.Format(updatedAtUtc));
            await command.ExecuteNonQueryAsync(cancellationToken);
            return AppResult.Success();
        }
        catch (SqliteException exception)
        {
            return AppResult.Failure(
                new AppError(
                    AppErrorCode.PersistenceUnavailable,
                    "setting.save_failed",
                    "The setting could not be saved.",
                    IsRetryable: true,
                    Details: new Dictionary<string, string>
                    {
                        ["reason"] = exception.GetType().Name,
                    }));
        }
    }

    public async Task<AppResult<string>> GetAsync(
        string key,
        string defaultValue,
        CancellationToken cancellationToken)
    {
        AppResult initialized = await _migrationRunner.InitializeAsync(cancellationToken);
        if (!initialized.IsSuccess)
        {
            return AppResult.Failure<string>(initialized.Error!);
        }

        try
        {
            await using SqliteConnection connection =
                await _connectionFactory.OpenAsync(cancellationToken);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM app_settings WHERE key = $key;";
            command.Parameters.AddWithValue("$key", key);
            object? value = await command.ExecuteScalarAsync(cancellationToken);
            return AppResult.Success(value is null ? defaultValue : (string)value);
        }
        catch (SqliteException exception)
        {
            return AppResult.Failure<string>(
                new AppError(
                    AppErrorCode.PersistenceUnavailable,
                    "setting.read_failed",
                    "The setting could not be read.",
                    IsRetryable: true,
                    Details: new Dictionary<string, string>
                    {
                        ["reason"] = exception.GetType().Name,
                    }));
        }
    }
}
