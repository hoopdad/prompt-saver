using PromptSaver.Application.Dtos;
using PromptSaver.Domain.ValueObjects;

namespace PromptSaver.Application.Ports;

public interface IPromptEnrichmentProvider
{
    ProviderKind Kind { get; }

    Task<AppResult<ProviderEnrichmentResponse>> EnrichAsync(
        ProviderConfigurationDto configuration,
        ProviderEnrichmentRequest request,
        CancellationToken cancellationToken);

    Task<AppResult<ProviderHealthDto>> CheckHealthAsync(
        ProviderConfigurationDto configuration,
        CancellationToken cancellationToken);
}

public interface IProviderConfigurationStore
{
    Task<AppResult<ProviderConfigurationDto?>> GetAsync(
        ProviderConfigurationId id,
        CancellationToken cancellationToken);

    Task<AppResult<ProviderConfigurationDto?>> GetEnabledAsync(
        CancellationToken cancellationToken);

    Task<AppResult<IReadOnlyList<ProviderConfigurationDto>>> ListAsync(
        CancellationToken cancellationToken);

    Task<AppResult<ProviderConfigurationDto>> SaveAsync(
        ProviderConfigurationDto configuration,
        CancellationToken cancellationToken);
}

public interface IProviderCredentialSource
{
    Task<AppResult<string?>> GetApiKeyAsync(
        ProviderConfigurationId providerId,
        string opaqueTarget,
        CancellationToken cancellationToken);
}

public interface IEnrichmentWorkStore
{
    Task<AppResult<IReadOnlyList<PromptId>>> ClaimPendingAsync(
        int maximumCount,
        CancellationToken cancellationToken);

    Task<AppResult> SaveProposalAsync(
        MetadataProposalDto proposal,
        CancellationToken cancellationToken);

    Task<AppResult<MetadataProposalDto?>> GetProposalAsync(
        PromptId promptId,
        CancellationToken cancellationToken);

    Task<AppResult> DeleteProposalAsync(
        PromptId promptId,
        CancellationToken cancellationToken);

    Task<AppResult> MarkFailedAsync(
        PromptId promptId,
        AppError appError,
        CancellationToken cancellationToken);
}

public interface IEnrichmentPromptSource
{
    Task<AppResult<string>> GetBodyAsync(
        PromptId promptId,
        CancellationToken cancellationToken);
}

public interface IOllamaDiscovery
{
    Task<AppResult<ProviderHealthDto>> ProbeLoopbackAsync(CancellationToken cancellationToken);
}
