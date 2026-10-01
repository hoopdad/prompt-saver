using System.Text.Json;
using PromptSaver.Application;
using PromptSaver.Application.Ports;

namespace PromptSaver.Infrastructure.Drafts;

public sealed record BootstrapConfig(
    int SchemaVersion,
    string Theme,
    string LaunchDestination,
    bool SpellCheckEnabled,
    bool UncleanShutdown,
    double? WindowLeft = null,
    double? WindowTop = null,
    double? WindowWidth = null,
    double? WindowHeight = null)
{
    public const int CurrentSchemaVersion = 1;

    public static BootstrapConfig Default { get; } =
        new(CurrentSchemaVersion, "System", "Capture", true, false);
}

public sealed class BootstrapConfigStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string _path;

    public BootstrapConfigStore(IDataRootResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        _path = resolver.Resolve().Configuration;
    }

    public async Task<AppResult<BootstrapConfig>> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return AppResult.Success(BootstrapConfig.Default);
        }

        try
        {
            BootstrapConfig? config =
                await AtomicJsonFile.ReadAsync<BootstrapConfig>(
                    _path,
                    SerializerOptions,
                    cancellationToken);
            if (config is null || config.SchemaVersion != BootstrapConfig.CurrentSchemaVersion)
            {
                return AppResult.Failure<BootstrapConfig>(
                    new AppError(
                        AppErrorCode.DraftCorrupt,
                        "config.corrupt",
                        "The bootstrap configuration is invalid."));
            }

            return AppResult.Success(config);
        }
        catch (OperationCanceledException)
        {
            return AppResult.Failure<BootstrapConfig>(
                new AppError(AppErrorCode.Cancelled, "operation.cancelled", "The operation was cancelled."));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return AppResult.Failure<BootstrapConfig>(
                new AppError(
                    AppErrorCode.PersistenceUnavailable,
                    "config.read_failed",
                    "The bootstrap configuration could not be read.",
                    IsRetryable: true,
                    Details: new Dictionary<string, string>
                    {
                        ["reason"] = exception.GetType().Name,
                    }));
        }
    }

    public async Task<AppResult> SaveAsync(
        BootstrapConfig config,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.SchemaVersion != BootstrapConfig.CurrentSchemaVersion)
        {
            return AppResult.Failure(
                new AppError(
                    AppErrorCode.Validation,
                    "config.schema.invalid",
                    "The bootstrap configuration schema is invalid."));
        }

        try
        {
            await AtomicJsonFile.WriteAsync(_path, config, SerializerOptions, cancellationToken);
            return AppResult.Success();
        }
        catch (OperationCanceledException)
        {
            return AppResult.Failure(
                new AppError(AppErrorCode.Cancelled, "operation.cancelled", "The operation was cancelled."));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return AppResult.Failure(
                new AppError(
                    AppErrorCode.PersistenceUnavailable,
                    "config.save_failed",
                    "The bootstrap configuration could not be saved.",
                    IsRetryable: true,
                    Details: new Dictionary<string, string>
                    {
                        ["reason"] = exception.GetType().Name,
                    }));
        }
    }
}
