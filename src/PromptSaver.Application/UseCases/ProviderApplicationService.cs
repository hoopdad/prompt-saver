using PromptSaver.Application.Dtos;
using PromptSaver.Application.Policies;
using PromptSaver.Application.Ports;
using PromptSaver.Domain.ValueObjects;

namespace PromptSaver.Application.UseCases;

public sealed class ProviderApplicationService :
    IConfigureProvider,
    IGetProviderConfiguration,
    IDiscoverLocalOllama,
    ICheckProviderHealth
{
    private readonly IProviderConfigurationStore _configurations;
    private readonly ICredentialStore _credentials;
    private readonly IOllamaDiscovery _discovery;
    private readonly Dictionary<ProviderKind, IPromptEnrichmentProvider> _providers;
    private readonly IIdGenerator _ids;

    public ProviderApplicationService(
        IProviderConfigurationStore configurations,
        ICredentialStore credentials,
        IOllamaDiscovery discovery,
        IEnumerable<IPromptEnrichmentProvider> providers,
        IIdGenerator ids)
    {
        _configurations = configurations;
        _credentials = credentials;
        _discovery = discovery;
        _providers = providers.ToDictionary(provider => provider.Kind);
        _ids = ids;
    }

    public async Task<AppResult<ProviderConfigurationDto>> ExecuteAsync(
        ConfigureProviderCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ConfigureCoreAsync(command, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return AppResult.Failure<ProviderConfigurationDto>(
                new AppError(
                    AppErrorCode.Cancelled,
                    "operation.cancelled",
                    "The operation was cancelled."));
        }
        catch (Exception exception)
        {
            return UnexpectedFailure<ProviderConfigurationDto>(
                "provider.configuration.unexpected",
                "The provider configuration could not be saved.",
                exception);
        }
    }

    private async Task<AppResult<ProviderConfigurationDto>> ConfigureCoreAsync(
        ConfigureProviderCommand command,
        CancellationToken cancellationToken)
    {
        AppResult endpoint =
            ProviderEndpointPolicy.Validate(command.Endpoint, command.RemoteHttpAcknowledged);
        if (!endpoint.IsSuccess)
        {
            return AppResult.Failure<ProviderConfigurationDto>(endpoint.Error!);
        }

        if (string.IsNullOrWhiteSpace(command.DisplayName) ||
            string.IsNullOrWhiteSpace(command.Model))
        {
            return AppResult.Failure<ProviderConfigurationDto>(
                new AppError(
                    AppErrorCode.Validation,
                    "provider.configuration.required",
                    "Provider name and model are required."));
        }

        ProviderConfigurationId id = command.Id ?? _ids.NewProviderConfigurationId();
        ProviderConfigurationDto? existingConfiguration = null;
        if (command.Id is not null)
        {
            AppResult<ProviderConfigurationDto?> existing =
                await _configurations.GetAsync(id, cancellationToken);
            if (!existing.IsSuccess)
            {
                return AppResult.Failure<ProviderConfigurationDto>(existing.Error!);
            }

            existingConfiguration = existing.Value;
        }

        string? credentialTarget = existingConfiguration?.CredentialTarget;
        if (command.RemoveCredential && credentialTarget is not null)
        {
            AppResult deletedCredential =
                await _credentials.DeleteAsync(credentialTarget, cancellationToken);
            if (!deletedCredential.IsSuccess)
            {
                return AppResult.Failure<ProviderConfigurationDto>(deletedCredential.Error!);
            }

            credentialTarget = null;
        }

        if (!string.IsNullOrWhiteSpace(command.Credential))
        {
            credentialTarget ??= $"PromptSaver.Provider.{id}";
            AppResult savedCredential = await _credentials.SaveAsync(
                credentialTarget,
                command.Credential,
                cancellationToken);
            if (!savedCredential.IsSuccess)
            {
                return AppResult.Failure<ProviderConfigurationDto>(savedCredential.Error!);
            }
        }

        ProviderConfigurationDto configuration = new(
            id,
            command.Kind,
            command.DisplayName.Trim(),
            command.Endpoint,
            command.Model.Trim(),
            command.IsEnabled,
            command.RemoteHttpAcknowledged,
            credentialTarget);
        return await _configurations.SaveAsync(configuration, cancellationToken);
    }

    public async Task<AppResult<ProviderConfigurationDto?>> ExecuteAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            AppResult<IReadOnlyList<ProviderConfigurationDto>> configurations =
                await _configurations.ListAsync(cancellationToken);
            return configurations.IsSuccess
                ? AppResult.Success<ProviderConfigurationDto?>(
                    configurations.Value.Count == 0 ? null : configurations.Value[0])
                : AppResult.Failure<ProviderConfigurationDto?>(configurations.Error!);
        }
        catch (OperationCanceledException)
        {
            return AppResult.Failure<ProviderConfigurationDto?>(
                new AppError(
                    AppErrorCode.Cancelled,
                    "operation.cancelled",
                    "The operation was cancelled."));
        }
        catch (Exception exception)
        {
            return UnexpectedFailure<ProviderConfigurationDto?>(
                "provider.configuration.read_unexpected",
                "The provider configuration could not be loaded.",
                exception);
        }
    }

    Task<AppResult<ProviderHealthDto>> IDiscoverLocalOllama.ExecuteAsync(
        CancellationToken cancellationToken) =>
        _discovery.ProbeLoopbackAsync(cancellationToken);

    public async Task<AppResult<ProviderHealthDto>> ExecuteAsync(
        ProviderConfigurationId providerId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await CheckHealthCoreAsync(providerId, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return AppResult.Failure<ProviderHealthDto>(
                new AppError(
                    AppErrorCode.Cancelled,
                    "operation.cancelled",
                    "The operation was cancelled."));
        }
        catch (Exception exception)
        {
            return UnexpectedFailure<ProviderHealthDto>(
                "provider.health.unexpected",
                "The provider connection could not be checked.",
                exception);
        }
    }

    private async Task<AppResult<ProviderHealthDto>> CheckHealthCoreAsync(
        ProviderConfigurationId providerId,
        CancellationToken cancellationToken)
    {
        AppResult<ProviderConfigurationDto?> configuration =
            await _configurations.GetAsync(providerId, cancellationToken);
        if (!configuration.IsSuccess)
        {
            return AppResult.Failure<ProviderHealthDto>(configuration.Error!);
        }

        if (configuration.Value is null ||
            !_providers.TryGetValue(configuration.Value.Kind, out IPromptEnrichmentProvider? provider))
        {
            return AppResult.Failure<ProviderHealthDto>(
                new AppError(
                    AppErrorCode.NotFound,
                    "provider.not_found",
                    "The provider configuration was not found."));
        }

        return await provider.CheckHealthAsync(configuration.Value, cancellationToken);
    }

    private static AppResult<T> UnexpectedFailure<T>(
        string key,
        string message,
        Exception exception) =>
        AppResult.Failure<T>(
            new AppError(
                AppErrorCode.Unexpected,
                key,
                message,
                IsRetryable: true,
                Details: new Dictionary<string, string>
                {
                    ["reason"] = exception.GetType().Name,
                }));
}
