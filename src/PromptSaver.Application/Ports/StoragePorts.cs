using PromptSaver.Application.Dtos;
using PromptSaver.Domain.Entities;
using PromptSaver.Domain.ValueObjects;

namespace PromptSaver.Application.Ports;

public sealed record DataRootPaths(
    string Root,
    string Database,
    string CaptureDraft,
    string Configuration,
    string Backups,
    string Logs);

public interface IDataRootResolver
{
    DataRootPaths Resolve();
}

public interface ICaptureDraftStore
{
    Task<AppResult> SaveAsync(CaptureDraftDto draft, CancellationToken cancellationToken);

    Task<CaptureDraftRecoveryResult> RecoverAsync(CancellationToken cancellationToken);

    Task<AppResult> DeleteAsync(CancellationToken cancellationToken);
}

public interface IPromptEditDraftStore
{
    Task<AppResult> SaveAsync(PromptEditDraftDto draft, CancellationToken cancellationToken);

    Task<AppResult<PromptEditDraftDto?>> GetAsync(
        PromptId promptId,
        CancellationToken cancellationToken);

    Task<AppResult> DeleteAsync(PromptId promptId, CancellationToken cancellationToken);

    Task<AppResult<bool>> ExistsAsync(PromptId promptId, CancellationToken cancellationToken);
}

public interface IPromptRepository
{
    Task<Prompt?> GetAsync(PromptId id, CancellationToken cancellationToken);

    Task<IReadOnlyList<Prompt>> ListByIntentAsync(
        IntentId intentId,
        CancellationToken cancellationToken);

    Task AddAsync(Prompt prompt, CancellationToken cancellationToken);

    Task UpdateAsync(Prompt prompt, long expectedVersion, CancellationToken cancellationToken);

    Task DeleteAsync(PromptId id, long expectedVersion, CancellationToken cancellationToken);
}

public interface IIntentRepository
{
    Task<Intent?> GetAsync(IntentId id, CancellationToken cancellationToken);

    Task<Intent?> FindByNormalizedKeyAsync(
        string normalizedKey,
        bool includeArchived,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<Intent>> ListActiveAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<IntentAlias>> ListAliasesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<IntentAlias>> ListAliasesAsync(
        IntentId intentId,
        CancellationToken cancellationToken);

    Task AddAsync(Intent intent, CancellationToken cancellationToken);

    Task UpdateAsync(Intent intent, long expectedVersion, CancellationToken cancellationToken);

    Task AddAliasAsync(IntentAlias intentAlias, CancellationToken cancellationToken);
}

public interface IMetadataRepository
{
    Task<Skill?> GetSkillAsync(SkillId id, CancellationToken cancellationToken);

    Task<Entity?> GetEntityAsync(EntityId id, CancellationToken cancellationToken);

    Task<Skill?> FindSkillByNormalizedKeyAsync(
        string normalizedKey,
        CancellationToken cancellationToken);

    Task<Entity?> FindEntityByNormalizedKeyAsync(
        string normalizedKey,
        EntityType type,
        CancellationToken cancellationToken);

    Task AddSkillAsync(Skill skill, CancellationToken cancellationToken);

    Task AddEntityAsync(Entity entity, CancellationToken cancellationToken);

    Task ReplacePromptMetadataAsync(
        PromptId promptId,
        IReadOnlyList<PromptSkill> skills,
        IReadOnlyList<PromptEntity> entities,
        CancellationToken cancellationToken);

    Task DeleteOrphansAsync(CancellationToken cancellationToken);
}

public interface IPromptPersistenceOperations
{
    Task<AppResult<PromptId>> DuplicateAsync(
        PromptId sourcePromptId,
        PromptId duplicatePromptId,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken);

    Task<AppResult> DeleteAsync(
        PromptId promptId,
        long expectedVersion,
        CancellationToken cancellationToken);

    Task<AppResult<MergeIntentsResult>> MergeIntentsAsync(
        MergeIntentsCommand merge,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken);
}

public interface IIntentCandidateRepository
{
    Task ReplaceAsync(
        PromptId promptId,
        IReadOnlyList<IntentCandidateScore> candidates,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<IntentCandidateScore>> ListAsync(
        PromptId promptId,
        CancellationToken cancellationToken);
}

public interface IAppUnitOfWork : IAsyncDisposable
{
    IPromptRepository Prompts { get; }

    IIntentRepository Intents { get; }

    IMetadataRepository Metadata { get; }

    IIntentCandidateRepository IntentCandidates { get; }

    IPromptSearchWriter Search { get; }

    Task<AppResult> CommitAsync(CancellationToken cancellationToken);

    Task RollbackAsync(CancellationToken cancellationToken);
}

public interface IUnitOfWorkFactory
{
    Task<AppResult<IAppUnitOfWork>> BeginAsync(CancellationToken cancellationToken);
}

public interface IPromptSearch
{
    Task<AppResult<SearchPageDto<PromptSummaryDto>>> SearchAsync(
        SearchPromptsQuery query,
        CancellationToken cancellationToken);

    Task<AppResult> RebuildAsync(CancellationToken cancellationToken);
}

public interface IPromptSearchWriter
{
    Task<AppResult> ReplaceDocumentAsync(PromptId promptId, CancellationToken cancellationToken);

    Task<AppResult> DeleteDocumentAsync(PromptId promptId, CancellationToken cancellationToken);
}
