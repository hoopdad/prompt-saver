using PromptSaver.Domain.Entities;
using PromptSaver.Domain.ValueObjects;

namespace PromptSaver.Application.Dtos;

public sealed record IntentSummaryDto(
    IntentId Id,
    string CanonicalName,
    bool IsArchived,
    IntentId? MergedIntoIntentId,
    int PromptCount = 0,
    long Version = 0);

public sealed record IntentDetailsDto(
    IntentId Id,
    string CanonicalName,
    string NormalizedKey,
    string? Description,
    IntentSource Source,
    bool IsArchived,
    IntentId? MergedIntoIntentId,
    long Version,
    IReadOnlyList<IntentAliasDto> Aliases);

public sealed record IntentAliasDto(
    IntentAliasId Id,
    IntentId IntentId,
    string Value,
    string NormalizedKey);

public sealed record IntentCandidateDto(
    IntentId IntentId,
    string CurrentDisplayName,
    int ScoreBasisPoints,
    int Rank,
    string ScoringAlgorithmVersion,
    DateTimeOffset CreatedAtUtc,
    bool IsArchived,
    IntentId? MergedIntoIntentId);

public sealed record SkillDto(
    SkillId Id,
    string Name,
    MetadataSource Source,
    decimal Confidence,
    string ExtractorVersion);

public sealed record EntityDto(
    EntityId Id,
    string Name,
    EntityType Type,
    MetadataSource Source,
    decimal Confidence,
    string ExtractorVersion);

public sealed record MergeIntentsCommand(
    IntentId SourceIntentId,
    IntentId TargetIntentId,
    long SourceVersion,
    long TargetVersion);

public sealed record MergeIntentConflictDto(
    string Alias,
    IntentId ExistingOwnerIntentId);

public sealed record MergeIntentsResult(
    IntentId SourceIntentId,
    IntentId TargetIntentId,
    int ReassignedPromptCount,
    IReadOnlyList<MergeIntentConflictDto> AliasConflicts);

public sealed record MetadataProposalDto(
    PromptId PromptId,
    string? SuggestedTitle,
    string? SuggestedIntent,
    IReadOnlyList<ProposedSkillDto> Skills,
    IReadOnlyList<ProposedEntityDto> Entities,
    string ProviderName,
    string Model,
    DateTimeOffset CreatedAtUtc);

public sealed record ProposedSkillDto(string Name, decimal Confidence);

public sealed record ProposedEntityDto(string Name, EntityType Type, decimal Confidence);

public sealed record ManageMetadataCommand(
    PromptId PromptId,
    IReadOnlyList<SkillSelectionDto> Skills,
    IReadOnlyList<EntitySelectionDto> Entities,
    long PromptVersion);

public sealed record SkillSelectionDto(SkillId? Id, string Name);

public sealed record EntitySelectionDto(EntityId? Id, string Name, EntityType Type);
