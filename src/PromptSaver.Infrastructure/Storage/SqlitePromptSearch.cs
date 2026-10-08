using System.Buffers;
using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using PromptSaver.Application;
using PromptSaver.Application.Dtos;
using PromptSaver.Application.Ports;
using PromptSaver.Domain.Entities;
using PromptSaver.Domain.ValueObjects;

namespace PromptSaver.Infrastructure.Storage;

internal sealed class SqlitePromptSearchWriter : IPromptSearchWriter
{
    private readonly SqliteConnection _connection;
    private readonly SqliteTransaction _transaction;

    internal SqlitePromptSearchWriter(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        _connection = connection;
        _transaction = transaction;
    }

    public async Task<AppResult> ReplaceDocumentAsync(
        PromptId promptId,
        CancellationToken cancellationToken)
    {
        SearchDocument? oldDocument =
            await ReadExistingAsync(promptId, cancellationToken);
        SearchDocument? replacement =
            await BuildAsync(promptId, cancellationToken);

        if (oldDocument is not null)
        {
            await DeleteFtsRowAsync(oldDocument, cancellationToken);
        }

        if (replacement is null)
        {
            if (oldDocument is not null)
            {
                await DeleteRelationalDocumentAsync(promptId, cancellationToken);
            }

            return AppResult.Success();
        }

        long rowId;
        if (oldDocument is null)
        {
            await using SqliteCommand insert = CreateCommand("""
                INSERT INTO prompt_search_documents(
                    prompt_id, title, body, intent_text, skill_text, entity_text)
                VALUES($promptId, $title, $body, $intent, $skills, $entities);
                SELECT last_insert_rowid();
                """);
            BindDocument(insert, replacement);
            rowId = Convert.ToInt64(
                await insert.ExecuteScalarAsync(cancellationToken),
                CultureInfo.InvariantCulture);
        }
        else
        {
            rowId = oldDocument.RowId;
            await using SqliteCommand update = CreateCommand("""
                UPDATE prompt_search_documents SET
                    title = $title,
                    body = $body,
                    intent_text = $intent,
                    skill_text = $skills,
                    entity_text = $entities
                WHERE rowid = $rowId;
                """);
            BindDocument(update, replacement);
            update.Parameters.AddWithValue("$rowId", rowId);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        SearchDocument indexed = replacement with { RowId = rowId };
        await using SqliteCommand ftsInsert = CreateCommand("""
            INSERT INTO prompt_search_index(
                rowid, title, body, intent_text, skill_text, entity_text)
            VALUES($rowId, $title, $body, $intent, $skills, $entities);
            """);
        BindDocument(ftsInsert, indexed);
        ftsInsert.Parameters.AddWithValue("$rowId", rowId);
        await ftsInsert.ExecuteNonQueryAsync(cancellationToken);
        return AppResult.Success();
    }

    public async Task<AppResult> DeleteDocumentAsync(
        PromptId promptId,
        CancellationToken cancellationToken)
    {
        SearchDocument? oldDocument =
            await ReadExistingAsync(promptId, cancellationToken);
        if (oldDocument is null)
        {
            return AppResult.Success();
        }

        await DeleteFtsRowAsync(oldDocument, cancellationToken);
        await DeleteRelationalDocumentAsync(promptId, cancellationToken);
        return AppResult.Success();
    }

    private async Task<SearchDocument?> ReadExistingAsync(
        PromptId promptId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = CreateCommand("""
            SELECT rowid, title, body, intent_text, skill_text, entity_text
            FROM prompt_search_documents
            WHERE prompt_id = $promptId;
            """);
        command.Parameters.AddWithValue("$promptId", promptId.ToString());
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new SearchDocument(
                reader.GetInt64(0),
                promptId,
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5))
            : null;
    }

    private async Task<SearchDocument?> BuildAsync(
        PromptId promptId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = CreateCommand("""
            SELECT
                COALESCE(p.title, ''),
                p.body,
                i.canonical_name || ' ' || COALESCE((
                    SELECT group_concat(a.value, ' ')
                    FROM intent_aliases a
                    WHERE a.intent_id = i.id
                ), ''),
                COALESCE((
                    SELECT group_concat(value, ' ')
                    FROM (
                        SELECT s.name AS value
                        FROM prompt_skills ps
                        JOIN skills s ON s.id = ps.skill_id
                        WHERE ps.prompt_id = p.id
                        UNION ALL
                        SELECT sa.value
                        FROM prompt_skills ps
                        JOIN skill_aliases sa ON sa.skill_id = ps.skill_id
                        WHERE ps.prompt_id = p.id
                    )
                ), ''),
                COALESCE((
                    SELECT group_concat(e.name, ' ')
                    FROM prompt_entities pe
                    JOIN proper_entities e ON e.id = pe.entity_id
                    WHERE pe.prompt_id = p.id
                ), '')
            FROM prompts p
            JOIN intents i ON i.id = p.intent_id
            WHERE p.id = $promptId;
            """);
        command.Parameters.AddWithValue("$promptId", promptId.ToString());
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new SearchDocument(
            0,
            promptId,
            Normalize(reader.GetString(0)),
            Normalize(reader.GetString(1)),
            Normalize(reader.GetString(2)),
            Normalize(reader.GetString(3)),
            Normalize(reader.GetString(4)));
    }

    private async Task DeleteFtsRowAsync(
        SearchDocument document,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = CreateCommand("""
            INSERT INTO prompt_search_index(
                prompt_search_index, rowid, title, body, intent_text, skill_text, entity_text)
            VALUES('delete', $rowId, $title, $body, $intent, $skills, $entities);
            """);
        BindDocument(command, document);
        command.Parameters.AddWithValue("$rowId", document.RowId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task DeleteRelationalDocumentAsync(
        PromptId promptId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = CreateCommand(
            "DELETE FROM prompt_search_documents WHERE prompt_id = $promptId;");
        command.Parameters.AddWithValue("$promptId", promptId.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void BindDocument(SqliteCommand command, SearchDocument document)
    {
        command.Parameters.AddWithValue("$promptId", document.PromptId.ToString());
        command.Parameters.AddWithValue("$title", document.Title);
        command.Parameters.AddWithValue("$body", document.Body);
        command.Parameters.AddWithValue("$intent", document.IntentText);
        command.Parameters.AddWithValue("$skills", document.SkillText);
        command.Parameters.AddWithValue("$entities", document.EntityText);
    }

    private SqliteCommand CreateCommand(string commandText)
    {
        SqliteCommand command = _connection.CreateCommand();
        command.Transaction = _transaction;
        command.CommandText = commandText;
        return command;
    }

    private static string Normalize(string value) =>
        value.Normalize(NormalizationForm.FormKC);

    private sealed record SearchDocument(
        long RowId,
        PromptId PromptId,
        string Title,
        string Body,
        string IntentText,
        string SkillText,
        string EntityText);
}

public sealed class SqlitePromptSearch : IPromptSearch
{
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly SqliteMigrationRunner _migrationRunner;

    public SqlitePromptSearch(
        SqliteConnectionFactory connectionFactory,
        SqliteMigrationRunner migrationRunner)
    {
        _connectionFactory = connectionFactory;
        _migrationRunner = migrationRunner;
    }

    public async Task<AppResult<SearchPageDto<PromptSummaryDto>>> SearchAsync(
        SearchPromptsQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        AppResult initialized = await _migrationRunner.InitializeAsync(cancellationToken);
        if (!initialized.IsSuccess)
        {
            return AppResult.Failure<SearchPageDto<PromptSummaryDto>>(initialized.Error!);
        }

        string? match = LiteralFtsQuery.Build(query.Text);
        if (!string.IsNullOrWhiteSpace(query.Text) && match is null)
        {
            return AppResult.Success(
                new SearchPageDto<PromptSummaryDto>(
                    [],
                    query.PageNumber,
                    query.PageSize,
                    0));
        }

        try
        {
            await using SqliteConnection connection =
                await _connectionFactory.OpenAsync(cancellationToken);
            long total = await CountAsync(connection, query, match, cancellationToken);
            IReadOnlyList<PromptSummaryDto> items =
                await ReadPageAsync(connection, query, match, cancellationToken);
            return AppResult.Success(
                new SearchPageDto<PromptSummaryDto>(
                    items,
                    query.PageNumber,
                    query.PageSize,
                    total));
        }
        catch (OperationCanceledException)
        {
            return AppResult.Failure<SearchPageDto<PromptSummaryDto>>(
                new AppError(AppErrorCode.Cancelled, "operation.cancelled", "The operation was cancelled."));
        }
        catch (SqliteException exception)
        {
            return AppResult.Failure<SearchPageDto<PromptSummaryDto>>(
                new AppError(
                    AppErrorCode.PersistenceUnavailable,
                    "search.failed",
                    "The prompt search could not be completed.",
                    IsRetryable: true,
                    Details: new Dictionary<string, string>
                    {
                        ["reason"] = exception.GetType().Name,
                    }));
        }
    }

    public async Task<AppResult> RebuildAsync(CancellationToken cancellationToken)
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
            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "INSERT INTO prompt_search_index(prompt_search_index) VALUES('rebuild');";
            await command.ExecuteNonQueryAsync(cancellationToken);
            await SqliteIntegrityOperations.CheckAsync(connection, transaction, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return AppResult.Success();
        }
        catch (Exception exception) when (
            exception is SqliteException or PersistenceIntegrityException)
        {
            return AppResult.Failure(
                new AppError(
                    AppErrorCode.PersistenceUnavailable,
                    "search.rebuild_failed",
                    "The search index could not be rebuilt.",
                    IsRetryable: true,
                    Details: new Dictionary<string, string>
                    {
                        ["reason"] = exception.GetType().Name,
                    }));
        }
    }

    public async Task<AppResult> CheckIntegrityAsync(CancellationToken cancellationToken)
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
            await SqliteIntegrityOperations.CheckAsync(connection, null, cancellationToken);
            return AppResult.Success();
        }
        catch (Exception exception) when (
            exception is SqliteException or PersistenceIntegrityException)
        {
            return AppResult.Failure(
                new AppError(
                    AppErrorCode.PersistenceUnavailable,
                    "storage.integrity_failed",
                    "The local database failed an integrity check.",
                    Details: new Dictionary<string, string>
                    {
                        ["reason"] = exception.GetType().Name,
                    }));
        }
    }

    private static async Task<long> CountAsync(
        SqliteConnection connection,
        SearchPromptsQuery query,
        string? match,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT COUNT(*)
            FROM prompts p
            JOIN prompt_search_documents d ON d.prompt_id = p.id
            {(match is null ? string.Empty : "JOIN prompt_search_index ON prompt_search_index.rowid = d.rowid")}
            WHERE ($intentId IS NULL OR p.intent_id = $intentId)
              AND ($needsReview = 0 OR p.intent_review_state IN ($needsReviewState, $skippedState))
              AND ($createdFrom IS NULL OR p.created_at_utc >= $createdFrom)
              AND ($createdTo IS NULL OR p.created_at_utc < $createdTo)
              {(match is null ? string.Empty : "AND prompt_search_index MATCH $match")};
            """;
        BindSearch(command, query, match);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);
    }

    private static async Task<IReadOnlyList<PromptSummaryDto>> ReadPageAsync(
        SqliteConnection connection,
        SearchPromptsQuery query,
        string? match,
        CancellationToken cancellationToken)
    {
        List<PromptSummaryDto> results = [];
        await using SqliteCommand command = connection.CreateCommand();
        string orderBy = GetOrderBy(query.SortColumn, query.SortDirection);
        command.CommandText = $"""
            SELECT
                p.id, p.title, substr(p.body, 1, 500), p.updated_at_utc,
                p.last_copied_at_utc, p.copy_count, p.intent_review_state,
                p.metadata_status, p.version,
                i.id, i.canonical_name, i.is_archived, i.merged_into_intent_id
            FROM prompts p
            JOIN intents i ON i.id = p.intent_id
            JOIN prompt_search_documents d ON d.prompt_id = p.id
            {(match is null ? string.Empty : "JOIN prompt_search_index ON prompt_search_index.rowid = d.rowid")}
            WHERE ($intentId IS NULL OR p.intent_id = $intentId)
              AND ($needsReview = 0 OR p.intent_review_state IN ($needsReviewState, $skippedState))
              AND ($createdFrom IS NULL OR p.created_at_utc >= $createdFrom)
              AND ($createdTo IS NULL OR p.created_at_utc < $createdTo)
              {(match is null ? string.Empty : "AND prompt_search_index MATCH $match")}
            ORDER BY {orderBy}, p.id
            LIMIT $limit OFFSET $offset;
            """;
        BindSearch(command, query, match);
        command.Parameters.AddWithValue("$limit", query.PageSize);
        command.Parameters.AddWithValue(
            "$offset",
            checked((query.PageNumber - 1) * query.PageSize));

        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(
                new PromptSummaryDto(
                    new PromptId(Guid.Parse(reader.GetString(0))),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.GetString(2),
                    ParseDate(reader.GetString(3)),
                    reader.IsDBNull(4) ? null : ParseDate(reader.GetString(4)),
                    reader.GetInt64(5),
                    new IntentSummaryDto(
                        new IntentId(Guid.Parse(reader.GetString(9))),
                        reader.GetString(10),
                        reader.GetBoolean(11),
                        reader.IsDBNull(12)
                            ? null
                            : new IntentId(Guid.Parse(reader.GetString(12)))),
                    (IntentReviewState)reader.GetInt32(6),
                    (MetadataStatus)reader.GetInt32(7),
                    reader.GetInt64(8)));
        }

        return results;
    }

    private static string GetOrderBy(
        PromptSortColumn sortColumn,
        PromptSortDirection sortDirection)
    {
        string expression = sortColumn switch
        {
            PromptSortColumn.Intent => "i.canonical_name COLLATE NOCASE",
            PromptSortColumn.Title => "COALESCE(p.title, '') COLLATE NOCASE",
            PromptSortColumn.Prompt => "p.body COLLATE NOCASE",
            PromptSortColumn.Modified => "p.updated_at_utc",
            PromptSortColumn.Copies => "p.copy_count",
            _ => throw new ArgumentOutOfRangeException(nameof(sortColumn)),
        };
        string direction = sortDirection switch
        {
            PromptSortDirection.Ascending => "ASC",
            PromptSortDirection.Descending => "DESC",
            _ => throw new ArgumentOutOfRangeException(nameof(sortDirection)),
        };
        return $"{expression} {direction}";
    }

    private static void BindSearch(
        SqliteCommand command,
        SearchPromptsQuery query,
        string? match)
    {
        command.Parameters.AddWithValue(
            "$intentId",
            query.IntentId is null ? DBNull.Value : query.IntentId.Value.ToString());
        command.Parameters.AddWithValue("$needsReview", query.NeedsReviewOnly ? 1 : 0);
        command.Parameters.AddWithValue("$needsReviewState", (int)IntentReviewState.NeedsReview);
        command.Parameters.AddWithValue("$skippedState", (int)IntentReviewState.Skipped);
        command.Parameters.AddWithValue(
            "$createdFrom",
            query.CreatedFromUtc is null
                ? DBNull.Value
                : SqliteMigrationRunner.Format(query.CreatedFromUtc.Value));
        command.Parameters.AddWithValue(
            "$createdTo",
            query.CreatedToUtc is null
                ? DBNull.Value
                : SqliteMigrationRunner.Format(query.CreatedToUtc.Value));
        if (match is not null)
        {
            command.Parameters.AddWithValue("$match", match);
        }
    }

    private static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}

internal static class LiteralFtsQuery
{
    private static readonly SearchValues<char> TokenCharacters =
        SearchValues.Create("#+.-_");

    internal static string? Build(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        string normalized = input.Normalize(NormalizationForm.FormKC);
        List<string> tokens = [];
        StringBuilder current = new();
        foreach (char character in normalized)
        {
            if (char.IsLetterOrDigit(character) || TokenCharacters.Contains(character))
            {
                current.Append(character);
            }
            else
            {
                AddCurrent(tokens, current);
            }
        }

        AddCurrent(tokens, current);
        if (tokens.Count == 0)
        {
            return null;
        }

        return string.Join(
            " AND ",
            tokens.Select(
                token =>
                {
                    string literal =
                        '"' + token.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
                    return token.Length >= 3 ? literal + "*" : literal;
                }));
    }

    private static void AddCurrent(List<string> tokens, StringBuilder current)
    {
        if (current.Length == 0)
        {
            return;
        }

        tokens.Add(current.ToString());
        current.Clear();
    }
}
