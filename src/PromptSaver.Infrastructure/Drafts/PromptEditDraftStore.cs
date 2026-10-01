using Microsoft.Data.Sqlite;
using PromptSaver.Application;
using PromptSaver.Application.Dtos;
using PromptSaver.Application.Ports;
using PromptSaver.Domain.ValueObjects;
using PromptSaver.Infrastructure.Storage;

namespace PromptSaver.Infrastructure.Drafts;

public sealed class PromptEditDraftStore : IPromptEditDraftStore
{
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly SqliteMigrationRunner _migrationRunner;

    public PromptEditDraftStore(
        SqliteConnectionFactory connectionFactory,
        SqliteMigrationRunner migrationRunner)
    {
        _connectionFactory = connectionFactory;
        _migrationRunner = migrationRunner;
    }

    public async Task<AppResult> SaveAsync(
        PromptEditDraftDto draft,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (!IsSelectionValid(draft))
        {
            return AppResult.Failure(
                new AppError(
                    AppErrorCode.Validation,
                    "edit_draft.selection.out_of_range",
                    "Draft caret and selection must be within the body."));
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
                INSERT INTO prompt_edit_drafts(
                    prompt_id, body, title, metadata_edit_json, base_version,
                    caret_offset, selection_length, updated_at_utc)
                VALUES(
                    $promptId, $body, $title, $metadata, $baseVersion,
                    $caret, $selection, $updated)
                ON CONFLICT(prompt_id) DO UPDATE SET
                    body = excluded.body,
                    title = excluded.title,
                    metadata_edit_json = excluded.metadata_edit_json,
                    base_version = excluded.base_version,
                    caret_offset = excluded.caret_offset,
                    selection_length = excluded.selection_length,
                    updated_at_utc = excluded.updated_at_utc;
                """;
            command.Parameters.AddWithValue("$promptId", draft.PromptId.ToString());
            command.Parameters.AddWithValue("$body", draft.Body);
            command.Parameters.AddWithValue("$title", (object?)draft.Title ?? DBNull.Value);
            command.Parameters.AddWithValue("$metadata", (object?)draft.MetadataEditJson ?? DBNull.Value);
            command.Parameters.AddWithValue("$baseVersion", draft.BaseVersion);
            command.Parameters.AddWithValue("$caret", draft.CaretOffset);
            command.Parameters.AddWithValue("$selection", draft.SelectionLength);
            command.Parameters.AddWithValue(
                "$updated",
                SqliteMigrationRunner.Format(draft.UpdatedAtUtc));
            await command.ExecuteNonQueryAsync(cancellationToken);
            return AppResult.Success();
        }
        catch (SqliteException exception)
        {
            return Failure("edit_draft.save_failed", exception);
        }
    }

    public async Task<AppResult<PromptEditDraftDto?>> GetAsync(
        PromptId promptId,
        CancellationToken cancellationToken)
    {
        AppResult initialized = await _migrationRunner.InitializeAsync(cancellationToken);
        if (!initialized.IsSuccess)
        {
            return AppResult.Failure<PromptEditDraftDto?>(initialized.Error!);
        }

        try
        {
            await using SqliteConnection connection =
                await _connectionFactory.OpenAsync(cancellationToken);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                SELECT body, title, metadata_edit_json, base_version,
                       caret_offset, selection_length, updated_at_utc
                FROM prompt_edit_drafts
                WHERE prompt_id = $promptId;
                """;
            command.Parameters.AddWithValue("$promptId", promptId.ToString());
            await using SqliteDataReader reader =
                await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return AppResult.Failure<PromptEditDraftDto?>(
                    new AppError(
                        AppErrorCode.NotFound,
                        "edit_draft.not_found",
                        "No prompt edit draft exists."));
            }

            return AppResult.Success<PromptEditDraftDto?>(
                new PromptEditDraftDto(
                    promptId,
                    reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetInt64(3),
                    reader.GetInt32(4),
                    reader.GetInt32(5),
                    DateTimeOffset.Parse(
                        reader.GetString(6),
                        System.Globalization.CultureInfo.InvariantCulture)));
        }
        catch (SqliteException exception)
        {
            return AppResult.Failure<PromptEditDraftDto?>(
                Failure("edit_draft.read_failed", exception).Error!);
        }
    }

    public async Task<AppResult> DeleteAsync(
        PromptId promptId,
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
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "DELETE FROM prompt_edit_drafts WHERE prompt_id = $promptId;";
            command.Parameters.AddWithValue("$promptId", promptId.ToString());
            await command.ExecuteNonQueryAsync(cancellationToken);
            return AppResult.Success();
        }
        catch (SqliteException exception)
        {
            return Failure("edit_draft.delete_failed", exception);
        }
    }

    public async Task<AppResult<bool>> ExistsAsync(
        PromptId promptId,
        CancellationToken cancellationToken)
    {
        AppResult<PromptEditDraftDto?> result = await GetAsync(promptId, cancellationToken);
        return result.IsSuccess
            ? AppResult.Success(result.Value is not null)
            : AppResult.Failure<bool>(result.Error!);
    }

    private static bool IsSelectionValid(PromptEditDraftDto draft) =>
        draft.CaretOffset >= 0 &&
        draft.SelectionLength >= 0 &&
        draft.CaretOffset <= draft.Body.Length &&
        draft.SelectionLength <= draft.Body.Length - draft.CaretOffset;

    private static AppResult Failure(string key, Exception exception) =>
        AppResult.Failure(
            new AppError(
                AppErrorCode.PersistenceUnavailable,
                key,
                "The prompt edit draft could not be accessed.",
                IsRetryable: true,
                Details: new Dictionary<string, string>
                {
                    ["reason"] = exception.GetType().Name,
                }));
}
