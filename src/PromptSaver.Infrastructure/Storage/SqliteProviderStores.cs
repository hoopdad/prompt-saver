using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PromptSaver.Application;
using PromptSaver.Application.Dtos;
using PromptSaver.Application.Ports;
using PromptSaver.Domain.Entities;
using PromptSaver.Domain.ValueObjects;

namespace PromptSaver.Infrastructure.Storage;

public sealed class SqliteProviderStore :
    IProviderConfigurationStore,
    IEnrichmentWorkStore,
    IEnrichmentPromptSource
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SqliteConnectionFactory _connections;
    private readonly SqliteMigrationRunner _migrations;

    public SqliteProviderStore(
        SqliteConnectionFactory connections,
        SqliteMigrationRunner migrations)
    {
        _connections = connections;
        _migrations = migrations;
    }

    public async Task<AppResult<ProviderConfigurationDto?>> GetAsync(
        ProviderConfigurationId id,
        CancellationToken cancellationToken)
    {
        AppResult<IReadOnlyList<ProviderConfigurationDto>> listed =
            await ListAsync(cancellationToken);
        return listed.IsSuccess
            ? AppResult.Success<ProviderConfigurationDto?>(
                listed.Value.FirstOrDefault(configuration => configuration.Id == id))
            : AppResult.Failure<ProviderConfigurationDto?>(listed.Error!);
    }

    public async Task<AppResult<ProviderConfigurationDto?>> GetEnabledAsync(
        CancellationToken cancellationToken)
    {
        AppResult<IReadOnlyList<ProviderConfigurationDto>> listed =
            await ListAsync(cancellationToken);
        return listed.IsSuccess
            ? AppResult.Success<ProviderConfigurationDto?>(
                listed.Value.FirstOrDefault(configuration => configuration.IsEnabled))
            : AppResult.Failure<ProviderConfigurationDto?>(listed.Error!);
    }

    public async Task<AppResult<IReadOnlyList<ProviderConfigurationDto>>> ListAsync(
        CancellationToken cancellationToken)
    {
        AppResult initialized = await _migrations.InitializeAsync(cancellationToken);
        if (!initialized.IsSuccess)
        {
            return AppResult.Failure<IReadOnlyList<ProviderConfigurationDto>>(initialized.Error!);
        }

        try
        {
            List<ProviderConfigurationDto> results = [];
            await using SqliteConnection connection = await _connections.OpenAsync(cancellationToken);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "SELECT id, configuration_json FROM provider_configurations ORDER BY updated_at_utc DESC;";
            await using SqliteDataReader reader =
                await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                ProviderConfigurationDto? configuration =
                    JsonSerializer.Deserialize<ProviderConfigurationDto>(
                        reader.GetString(1),
                        JsonOptions);
                if (configuration is not null)
                {
                    results.Add(
                        configuration with
                        {
                            Id = new ProviderConfigurationId(Guid.Parse(reader.GetString(0))),
                        });
                }
            }

            return AppResult.Success<IReadOnlyList<ProviderConfigurationDto>>(results);
        }
        catch (Exception exception) when (exception is SqliteException or JsonException)
        {
            return Failure<IReadOnlyList<ProviderConfigurationDto>>(
                "provider.configuration.read_failed",
                exception);
        }
    }

    public async Task<AppResult<ProviderConfigurationDto>> SaveAsync(
        ProviderConfigurationDto configuration,
        CancellationToken cancellationToken)
    {
        AppResult initialized = await _migrations.InitializeAsync(cancellationToken);
        if (!initialized.IsSuccess)
        {
            return AppResult.Failure<ProviderConfigurationDto>(initialized.Error!);
        }

        try
        {
            await using SqliteConnection connection = await _connections.OpenAsync(cancellationToken);
            await using SqliteTransaction transaction =
                (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            if (configuration.IsEnabled)
            {
                await using SqliteCommand disable = connection.CreateCommand();
                disable.Transaction = transaction;
                disable.CommandText = "SELECT id, configuration_json FROM provider_configurations;";
                List<(string Id, ProviderConfigurationDto Configuration)> existing = [];
                await using (SqliteDataReader reader =
                    await disable.ExecuteReaderAsync(cancellationToken))
                {
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        ProviderConfigurationDto? item =
                            JsonSerializer.Deserialize<ProviderConfigurationDto>(
                                reader.GetString(1),
                                JsonOptions);
                        string existingId = reader.GetString(0);
                        if (item is not null)
                        {
                            item = item with
                            {
                                Id = new ProviderConfigurationId(Guid.Parse(existingId)),
                            };
                        }

                        if (item is not null && item.Id != configuration.Id && item.IsEnabled)
                        {
                            existing.Add((existingId, item with { IsEnabled = false }));
                        }
                    }
                }

                foreach ((string id, ProviderConfigurationDto item) in existing)
                {
                    await UpsertAsync(connection, transaction, id, item, cancellationToken);
                }
            }

            await UpsertAsync(
                connection,
                transaction,
                configuration.Id.ToString(),
                configuration,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return AppResult.Success(configuration);
        }
        catch (Exception exception) when (exception is SqliteException or JsonException)
        {
            return Failure<ProviderConfigurationDto>(
                "provider.configuration.save_failed",
                exception);
        }
    }

    public async Task<AppResult<IReadOnlyList<PromptId>>> ClaimPendingAsync(
        int maximumCount,
        CancellationToken cancellationToken)
    {
        AppResult initialized = await _migrations.InitializeAsync(cancellationToken);
        if (!initialized.IsSuccess)
        {
            return AppResult.Failure<IReadOnlyList<PromptId>>(initialized.Error!);
        }

        try
        {
            List<PromptId> ids = [];
            await using SqliteConnection connection = await _connections.OpenAsync(cancellationToken);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                UPDATE prompts
                SET metadata_status = $processing
                WHERE id IN (
                    SELECT id
                    FROM prompts
                    WHERE metadata_status = $pending
                      AND NOT EXISTS(
                          SELECT 1 FROM prompt_edit_drafts d WHERE d.prompt_id = prompts.id
                      )
                    ORDER BY created_at_utc
                    LIMIT $limit
                )
                  AND metadata_status = $pending
                RETURNING id;
                """;
            command.Parameters.AddWithValue("$processing", (int)MetadataStatus.EnrichmentProcessing);
            command.Parameters.AddWithValue("$pending", (int)MetadataStatus.PendingEnrichment);
            command.Parameters.AddWithValue("$limit", maximumCount);
            await using SqliteDataReader reader =
                await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                ids.Add(new PromptId(Guid.Parse(reader.GetString(0))));
            }

            return AppResult.Success<IReadOnlyList<PromptId>>(ids);
        }
        catch (SqliteException exception)
        {
            return Failure<IReadOnlyList<PromptId>>("provider.pending.read_failed", exception);
        }
    }

    public Task<AppResult<MetadataProposalDto?>> GetProposalAsync(
        PromptId promptId,
        CancellationToken cancellationToken) =>
        ReadProposalAsync(promptId, cancellationToken);

    public async Task<AppResult> SaveProposalAsync(
        MetadataProposalDto proposal,
        CancellationToken cancellationToken)
    {
        try
        {
            AppResult initialized = await _migrations.InitializeAsync(cancellationToken);
            if (!initialized.IsSuccess)
            {
                return initialized;
            }

            await using SqliteConnection connection = await _connections.OpenAsync(cancellationToken);
            await using SqliteTransaction transaction =
                (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await using (SqliteCommand status = connection.CreateCommand())
            {
                status.Transaction = transaction;
                status.CommandText = "SELECT metadata_status FROM prompts WHERE id = $promptId;";
                status.Parameters.AddWithValue("$promptId", proposal.PromptId.ToString());
                object? current = await status.ExecuteScalarAsync(cancellationToken);
                if (current is null)
                {
                    return Failure(
                        "provider.proposal.prompt_not_found",
                        new InvalidOperationException("The prompt was not found."));
                }

                MetadataStatus metadataStatus =
                    (MetadataStatus)Convert.ToInt32(current, CultureInfo.InvariantCulture);
                if (metadataStatus == MetadataStatus.ProposalReady)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return AppResult.Success();
                }

                if (metadataStatus != MetadataStatus.EnrichmentProcessing)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return AppResult.Failure(
                        new AppError(
                            AppErrorCode.Conflict,
                            "provider.proposal.not_claimed",
                            "The prompt is not claimed for enrichment.",
                            true));
                }
            }

            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO enrichment_proposals(prompt_id, proposal_json, created_at_utc)
                VALUES($promptId, $json, $created)
                ON CONFLICT(prompt_id) DO NOTHING;
                UPDATE prompts
                SET metadata_status = $status
                WHERE id = $promptId AND metadata_status = $processing;
                """;
            command.Parameters.AddWithValue("$promptId", proposal.PromptId.ToString());
            command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(proposal, JsonOptions));
            command.Parameters.AddWithValue("$created", SqliteMigrationRunner.Format(proposal.CreatedAtUtc));
            command.Parameters.AddWithValue("$status", (int)MetadataStatus.ProposalReady);
            command.Parameters.AddWithValue("$processing", (int)MetadataStatus.EnrichmentProcessing);
            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return AppResult.Success();
        }
        catch (Exception exception) when (exception is SqliteException or JsonException)
        {
            return Failure("provider.proposal.save_failed", exception);
        }
    }

    public async Task<AppResult> DeleteProposalAsync(
        PromptId promptId,
        CancellationToken cancellationToken)
    {
        try
        {
            AppResult initialized = await _migrations.InitializeAsync(cancellationToken);
            if (!initialized.IsSuccess)
            {
                return initialized;
            }

            await using SqliteConnection connection = await _connections.OpenAsync(cancellationToken);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "DELETE FROM enrichment_proposals WHERE prompt_id = $promptId;";
            command.Parameters.AddWithValue("$promptId", promptId.ToString());
            await command.ExecuteNonQueryAsync(cancellationToken);
            return AppResult.Success();
        }
        catch (SqliteException exception)
        {
            return Failure("provider.proposal.delete_failed", exception);
        }
    }

    public async Task<AppResult> MarkFailedAsync(
        PromptId promptId,
        AppError appError,
        CancellationToken cancellationToken)
    {
        try
        {
            AppResult initialized = await _migrations.InitializeAsync(cancellationToken);
            if (!initialized.IsSuccess)
            {
                return initialized;
            }

            await using SqliteConnection connection = await _connections.OpenAsync(cancellationToken);
            await using SqliteTransaction transaction =
                (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE prompts
                SET metadata_status = $status
                WHERE id = $promptId;
                INSERT INTO enrichment_attempts(
                    prompt_id, attempted_at_utc, status, error_key)
                VALUES($promptId, $attempted, 'failed', $errorKey);
                """;
            command.Parameters.AddWithValue("$promptId", promptId.ToString());
            command.Parameters.AddWithValue("$status", (int)MetadataStatus.EnrichmentFailed);
            command.Parameters.AddWithValue("$attempted", SqliteMigrationRunner.Format(DateTimeOffset.UtcNow));
            command.Parameters.AddWithValue("$errorKey", appError.Key);
            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return AppResult.Success();
        }
        catch (SqliteException exception)
        {
            return Failure("provider.failure.save_failed", exception);
        }
    }

    public async Task<AppResult<string>> GetBodyAsync(
        PromptId promptId,
        CancellationToken cancellationToken)
    {
        try
        {
            AppResult initialized = await _migrations.InitializeAsync(cancellationToken);
            if (!initialized.IsSuccess)
            {
                return AppResult.Failure<string>(initialized.Error!);
            }

            await using SqliteConnection connection = await _connections.OpenAsync(cancellationToken);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT body FROM prompts WHERE id = $promptId;";
            command.Parameters.AddWithValue("$promptId", promptId.ToString());
            object? body = await command.ExecuteScalarAsync(cancellationToken);
            return body is string value
                ? AppResult.Success(value)
                : AppResult.Failure<string>(
                    new AppError(AppErrorCode.NotFound, "prompt.not_found", "The prompt was not found."));
        }
        catch (SqliteException exception)
        {
            return Failure<string>("provider.prompt.read_failed", exception);
        }
    }

    private async Task<AppResult<MetadataProposalDto?>> ReadProposalAsync(
        PromptId promptId,
        CancellationToken cancellationToken)
    {
        try
        {
            AppResult initialized = await _migrations.InitializeAsync(cancellationToken);
            if (!initialized.IsSuccess)
            {
                return AppResult.Failure<MetadataProposalDto?>(initialized.Error!);
            }

            await using SqliteConnection connection = await _connections.OpenAsync(cancellationToken);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "SELECT proposal_json FROM enrichment_proposals WHERE prompt_id = $promptId;";
            command.Parameters.AddWithValue("$promptId", promptId.ToString());
            object? json = await command.ExecuteScalarAsync(cancellationToken);
            MetadataProposalDto? proposal = json is string value
                ? JsonSerializer.Deserialize<MetadataProposalDto>(value, JsonOptions)
                : null;
            return AppResult.Success<MetadataProposalDto?>(
                proposal is null ? null : proposal with { PromptId = promptId });
        }
        catch (Exception exception) when (exception is SqliteException or JsonException)
        {
            return Failure<MetadataProposalDto?>("provider.proposal.read_failed", exception);
        }
    }

    private static async Task UpsertAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string id,
        ProviderConfigurationDto configuration,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO provider_configurations(id, configuration_json, updated_at_utc)
            VALUES($id, $json, $updated)
            ON CONFLICT(id) DO UPDATE SET
                configuration_json = excluded.configuration_json,
                updated_at_utc = excluded.updated_at_utc;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(configuration, JsonOptions));
        command.Parameters.AddWithValue("$updated", SqliteMigrationRunner.Format(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static AppResult Failure(string key, Exception exception) =>
        AppResult.Failure(Error(key, exception));

    private static AppResult<T> Failure<T>(string key, Exception exception) =>
        AppResult.Failure<T>(Error(key, exception));

    private static AppError Error(string key, Exception exception) =>
        new(
            AppErrorCode.PersistenceUnavailable,
            key,
            "Provider data could not be accessed.",
            true,
            new Dictionary<string, string>
            {
                ["reason"] = exception.GetType().Name,
            });
}
