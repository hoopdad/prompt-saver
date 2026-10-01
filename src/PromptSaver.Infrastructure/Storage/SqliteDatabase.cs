using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using Microsoft.Data.Sqlite;
using PromptSaver.Application;
using PromptSaver.Application.Ports;
using PromptSaver.Domain.Entities;

namespace PromptSaver.Infrastructure.Storage;

public sealed class SqliteConnectionFactory
{
    private readonly IDataRootResolver _dataRootResolver;

    public SqliteConnectionFactory(IDataRootResolver dataRootResolver)
    {
        _dataRootResolver = dataRootResolver ??
            throw new ArgumentNullException(nameof(dataRootResolver));
    }

    public string DatabasePath => _dataRootResolver.Resolve().Database;

    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            ForeignKeys = true,
        };
        SqliteConnection connection = new(builder.ToString());
        await connection.OpenAsync(cancellationToken);

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA foreign_keys = ON;
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = FULL;
            PRAGMA busy_timeout = 5000;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }
}

public sealed class SqliteMigrationRunner
{
    private const string InitialSchema = """
        CREATE TABLE intents (
            id TEXT PRIMARY KEY,
            canonical_name TEXT NOT NULL,
            normalized_key TEXT NOT NULL UNIQUE,
            description TEXT,
            source INTEGER NOT NULL,
            is_archived INTEGER NOT NULL CHECK (is_archived IN (0, 1)),
            is_archive_eligible INTEGER NOT NULL DEFAULT 0 CHECK (is_archive_eligible IN (0, 1)),
            created_at_utc TEXT NOT NULL,
            updated_at_utc TEXT NOT NULL,
            version INTEGER NOT NULL,
            merged_into_intent_id TEXT REFERENCES intents(id)
        ) STRICT;

        CREATE TABLE intent_aliases (
            id TEXT PRIMARY KEY,
            intent_id TEXT NOT NULL REFERENCES intents(id) ON DELETE CASCADE,
            value TEXT NOT NULL,
            normalized_key TEXT NOT NULL UNIQUE,
            created_at_utc TEXT NOT NULL
        ) STRICT;

        CREATE TABLE prompts (
            id TEXT PRIMARY KEY,
            body TEXT NOT NULL,
            title TEXT,
            title_source INTEGER NOT NULL,
            created_at_utc TEXT NOT NULL,
            updated_at_utc TEXT NOT NULL,
            last_copied_at_utc TEXT,
            copy_count INTEGER NOT NULL CHECK (copy_count >= 0),
            intent_id TEXT NOT NULL REFERENCES intents(id),
            intent_assignment INTEGER NOT NULL,
            intent_review_state INTEGER NOT NULL,
            intent_score_basis_points INTEGER,
            scoring_algorithm_version TEXT,
            metadata_status INTEGER NOT NULL,
            content_hash BLOB NOT NULL,
            version INTEGER NOT NULL
        ) STRICT;
        CREATE INDEX ix_prompts_intent_id ON prompts(intent_id);
        CREATE INDEX ix_prompts_created_at_utc ON prompts(created_at_utc);

        CREATE TABLE prompt_edit_drafts (
            prompt_id TEXT PRIMARY KEY REFERENCES prompts(id) ON DELETE CASCADE,
            body TEXT NOT NULL,
            title TEXT,
            metadata_edit_json TEXT,
            base_version INTEGER NOT NULL,
            caret_offset INTEGER NOT NULL,
            selection_length INTEGER NOT NULL,
            updated_at_utc TEXT NOT NULL
        ) STRICT;

        CREATE TABLE prompt_intent_candidates (
            prompt_id TEXT NOT NULL REFERENCES prompts(id) ON DELETE CASCADE,
            candidate_intent_id TEXT NOT NULL REFERENCES intents(id),
            score_basis_points INTEGER NOT NULL,
            rank INTEGER NOT NULL,
            scoring_algorithm_version TEXT NOT NULL,
            created_at_utc TEXT NOT NULL,
            PRIMARY KEY (prompt_id, candidate_intent_id),
            UNIQUE (prompt_id, rank)
        ) STRICT;

        CREATE TABLE skills (
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            normalized_key TEXT NOT NULL UNIQUE
        ) STRICT;

        CREATE TABLE skill_aliases (
            id TEXT PRIMARY KEY,
            skill_id TEXT NOT NULL REFERENCES skills(id) ON DELETE CASCADE,
            value TEXT NOT NULL,
            normalized_key TEXT NOT NULL UNIQUE
        ) STRICT;

        CREATE TABLE proper_entities (
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            normalized_key TEXT NOT NULL,
            entity_type INTEGER NOT NULL,
            UNIQUE (normalized_key, entity_type)
        ) STRICT;

        CREATE TABLE prompt_skills (
            prompt_id TEXT NOT NULL REFERENCES prompts(id) ON DELETE CASCADE,
            skill_id TEXT NOT NULL REFERENCES skills(id),
            source INTEGER NOT NULL,
            confidence TEXT NOT NULL,
            extractor_version TEXT NOT NULL,
            PRIMARY KEY (prompt_id, skill_id)
        ) STRICT;

        CREATE TABLE prompt_entities (
            prompt_id TEXT NOT NULL REFERENCES prompts(id) ON DELETE CASCADE,
            entity_id TEXT NOT NULL REFERENCES proper_entities(id),
            source INTEGER NOT NULL,
            confidence TEXT NOT NULL,
            extractor_version TEXT NOT NULL,
            PRIMARY KEY (prompt_id, entity_id)
        ) STRICT;

        CREATE TABLE provider_configurations (
            id TEXT PRIMARY KEY,
            configuration_json TEXT NOT NULL,
            updated_at_utc TEXT NOT NULL
        ) STRICT;

        CREATE TABLE enrichment_attempts (
            id INTEGER PRIMARY KEY,
            prompt_id TEXT NOT NULL REFERENCES prompts(id) ON DELETE CASCADE,
            attempted_at_utc TEXT NOT NULL,
            status TEXT NOT NULL,
            error_key TEXT
        ) STRICT;

        CREATE TABLE enrichment_proposals (
            prompt_id TEXT PRIMARY KEY REFERENCES prompts(id) ON DELETE CASCADE,
            proposal_json TEXT NOT NULL,
            created_at_utc TEXT NOT NULL
        ) STRICT;

        CREATE TABLE app_settings (
            key TEXT PRIMARY KEY,
            value TEXT NOT NULL,
            updated_at_utc TEXT NOT NULL
        ) STRICT;

        CREATE TABLE prompt_search_documents (
            rowid INTEGER PRIMARY KEY AUTOINCREMENT,
            prompt_id TEXT NOT NULL UNIQUE REFERENCES prompts(id) ON DELETE CASCADE,
            title TEXT NOT NULL,
            body TEXT NOT NULL,
            intent_text TEXT NOT NULL,
            skill_text TEXT NOT NULL,
            entity_text TEXT NOT NULL
        ) STRICT;

        CREATE VIRTUAL TABLE prompt_search_index USING fts5(
            title,
            body,
            intent_text,
            skill_text,
            entity_text,
            content = 'prompt_search_documents',
            content_rowid = 'rowid',
            tokenize = "unicode61 remove_diacritics 2 tokenchars '#+.-_'"
        );
        """;

    private static readonly Migration[] Migrations =
    [
        new("0001-initial", InitialSchema),
    ];

    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly IDataRootResolver _dataRootResolver;
    private readonly object _initializationLock = new();
    private Task<AppResult>? _initialization;

    public SqliteMigrationRunner(
        SqliteConnectionFactory connectionFactory,
        IDataRootResolver dataRootResolver)
    {
        _connectionFactory = connectionFactory;
        _dataRootResolver = dataRootResolver;
    }

    public async Task<AppResult> InitializeAsync(CancellationToken cancellationToken)
    {
        Task<AppResult> initialization;
        lock (_initializationLock)
        {
            initialization = _initialization ??= InitializeCoreAsync();
        }

        try
        {
            AppResult result = await initialization.WaitAsync(cancellationToken);
            if (!result.IsSuccess)
            {
                lock (_initializationLock)
                {
                    if (ReferenceEquals(_initialization, initialization))
                    {
                        _initialization = null;
                    }
                }
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            return AppResult.Failure(
                new AppError(AppErrorCode.Cancelled, "operation.cancelled", "The operation was cancelled."));
        }
    }

    private async Task<AppResult> InitializeCoreAsync()
    {
        try
        {
            CancellationToken cancellationToken = CancellationToken.None;
            await using SqliteConnection connection =
                await _connectionFactory.OpenAsync(cancellationToken);
            await EnsureMigrationTableAsync(connection, cancellationToken);
            bool migrationApplied = false;

            foreach (Migration migration in Migrations)
            {
                string checksum = Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(migration.Sql)));
                string? appliedChecksum =
                    await GetAppliedChecksumAsync(connection, migration.Id, cancellationToken);
                if (appliedChecksum is not null)
                {
                    if (!string.Equals(appliedChecksum, checksum, StringComparison.Ordinal))
                    {
                        throw new PersistenceIntegrityException(
                            $"Migration checksum mismatch for {migration.Id}.");
                    }

                    continue;
                }

                if (HasUserSchema(connection))
                {
                    await CreatePreMigrationBackupAsync(connection, cancellationToken);
                }

                await using SqliteTransaction transaction =
                    (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
                await using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = migration.Sql;
                await command.ExecuteNonQueryAsync(cancellationToken);

                await using SqliteCommand record = connection.CreateCommand();
                record.Transaction = transaction;
                record.CommandText = """
                    INSERT INTO schema_migrations(id, checksum, applied_at_utc)
                    VALUES ($id, $checksum, $applied);
                    """;
                record.Parameters.AddWithValue("$id", migration.Id);
                record.Parameters.AddWithValue("$checksum", checksum);
                record.Parameters.AddWithValue("$applied", Format(DateTimeOffset.UtcNow));
                await record.ExecuteNonQueryAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                migrationApplied = true;
            }

            await SeedUnsortedAsync(connection, cancellationToken);
            if (migrationApplied)
            {
                await SqliteIntegrityOperations.CheckAsync(connection, null, cancellationToken);
            }
            return AppResult.Success();
        }
        catch (Exception exception) when (
            exception is SqliteException or IOException or UnauthorizedAccessException or
                PersistenceIntegrityException)
        {
            return AppResult.Failure(
                new AppError(
                    AppErrorCode.PersistenceUnavailable,
                    "storage.initialize_failed",
                    "The local database could not be initialized.",
                    IsRetryable: true,
                    Details: new Dictionary<string, string>
                    {
                        ["reason"] = exception.GetType().Name,
                        ["detail"] = exception.Message,
                    }));
        }
    }

    private static async Task EnsureMigrationTableAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                id TEXT PRIMARY KEY,
                checksum TEXT NOT NULL,
                applied_at_utc TEXT NOT NULL
            ) STRICT;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<string?> GetAppliedChecksumAsync(
        SqliteConnection connection,
        string id,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT checksum FROM schema_migrations WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        return (string?)await command.ExecuteScalarAsync(cancellationToken);
    }

    private static bool HasUserSchema(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1
                FROM sqlite_schema
                WHERE type = 'table'
                  AND name NOT LIKE 'sqlite_%'
                  AND name <> 'schema_migrations'
            );
            """;
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    }

    private async Task CreatePreMigrationBackupAsync(
        SqliteConnection source,
        CancellationToken cancellationToken)
    {
        string path = Path.Combine(
            _dataRootResolver.Resolve().Backups,
            $"pre-migration-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}.db");
        await using SqliteConnection destination =
            new(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Pooling = false,
            }.ToString());
        await destination.OpenAsync(cancellationToken);
        source.BackupDatabase(destination);
    }

    private static async Task SeedUnsortedAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        Intent intent = Intent.ReservedUnsorted;
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO intents(
                id, canonical_name, normalized_key, description, source,
                is_archived, is_archive_eligible, created_at_utc, updated_at_utc,
                version, merged_into_intent_id)
            VALUES(
                $id, $name, $key, $description, $source,
                0, 0, $created, $updated, $version, NULL)
            ON CONFLICT(id) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$id", intent.Id.ToString());
        command.Parameters.AddWithValue("$name", intent.CanonicalName);
        command.Parameters.AddWithValue("$key", intent.NormalizedKey);
        command.Parameters.AddWithValue("$description", intent.Description);
        command.Parameters.AddWithValue("$source", (int)intent.Source);
        command.Parameters.AddWithValue("$created", Format(intent.CreatedAtUtc));
        command.Parameters.AddWithValue("$updated", Format(intent.UpdatedAtUtc));
        command.Parameters.AddWithValue("$version", intent.Version);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("O");

    private sealed record Migration(string Id, string Sql);
}

public static class SqliteIntegrityOperations
{
    public static async Task CheckAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using (SqliteCommand foreignKeys = connection.CreateCommand())
        {
            foreignKeys.Transaction = transaction;
            foreignKeys.CommandText = "PRAGMA foreign_key_check;";
            await using SqliteDataReader reader =
                await foreignKeys.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                throw new PersistenceIntegrityException("SQLite foreign-key integrity check failed.");
            }
        }

        await using SqliteCommand fts = connection.CreateCommand();
        fts.Transaction = transaction;
        fts.CommandText =
            "INSERT INTO prompt_search_index(prompt_search_index) VALUES('integrity-check');";
        await fts.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task<string> QuickCheckAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check;";
        object? result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToString(result, CultureInfo.InvariantCulture) ?? "unknown";
    }
}
