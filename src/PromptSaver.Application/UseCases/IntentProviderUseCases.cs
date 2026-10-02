using PromptSaver.Application.Dtos;
using PromptSaver.Domain.ValueObjects;

namespace PromptSaver.Application.UseCases;

public interface IAssignIntent
{
    Task<AppResult<PromptDetailsDto>> ExecuteAsync(
        AssignIntentCommand command,
        CancellationToken cancellationToken);
}

public interface IReviewIntentAssignment
{
    Task<AppResult<PromptDetailsDto>> ExecuteAsync(
        ReviewIntentAssignmentCommand command,
        CancellationToken cancellationToken);
}

public interface IMergeIntents
{
    Task<AppResult<MergeIntentsResult>> ExecuteAsync(
        MergeIntentsCommand command,
        CancellationToken cancellationToken);
}

public interface IListIntents
{
    Task<AppResult<IReadOnlyList<IntentSummaryDto>>> ExecuteAsync(
        CancellationToken cancellationToken);
}

public interface IManageMetadata
{
    Task<AppResult<PromptDetailsDto>> ExecuteAsync(
        ManageMetadataCommand command,
        CancellationToken cancellationToken);
}

public interface IReviewEnrichmentProposal
{
    Task<AppResult<PromptDetailsDto>> ExecuteAsync(
        ReviewEnrichmentProposalCommand command,
        CancellationToken cancellationToken);
}

public interface IConfigureProvider
{
    Task<AppResult<ProviderConfigurationDto>> ExecuteAsync(
        ConfigureProviderCommand command,
        CancellationToken cancellationToken);
}

public interface IGetProviderConfiguration
{
    Task<AppResult<ProviderConfigurationDto?>> ExecuteAsync(
        CancellationToken cancellationToken);
}

public interface IDiscoverLocalOllama
{
    Task<AppResult<ProviderHealthDto>> ExecuteAsync(CancellationToken cancellationToken);
}

public interface ICheckProviderHealth
{
    Task<AppResult<ProviderHealthDto>> ExecuteAsync(
        ProviderConfigurationId providerId,
        CancellationToken cancellationToken);
}

public interface IEnrichPendingPrompts
{
    Task<AppResult<int>> ExecuteAsync(CancellationToken cancellationToken);
}

public interface IQueryPromptIntent
{
    Task<AppResult<PromptIntentSuggestionDto>> ExecuteAsync(
        QueryPromptIntentCommand command,
        CancellationToken cancellationToken);
}

public interface ICreateBackup
{
    Task<AppResult<BackupInfoDto>> ExecuteAsync(CancellationToken cancellationToken);
}

public interface IRestoreBackup
{
    Task<AppResult> ExecuteAsync(
        RestoreBackupCommand command,
        CancellationToken cancellationToken);
}

public interface IRebuildSearchIndex
{
    Task<AppResult> ExecuteAsync(CancellationToken cancellationToken);
}

public interface IGetDiagnostics
{
    Task<AppResult<DiagnosticsDto>> ExecuteAsync(CancellationToken cancellationToken);
}
