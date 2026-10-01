using PromptSaver.Domain.Entities;
using PromptSaver.Domain.ValueObjects;

namespace PromptSaver.Application.Dtos;

public sealed record CapturePromptCommand(string Body, string? Title);

public sealed record CapturePromptResult(
    PromptDetailsDto Prompt,
    bool CaptureDraftDeleted,
    bool EnrichmentQueued);

public sealed record PromptDetailsDto(
    PromptId Id,
    string Body,
    string? Title,
    TitleSource TitleSource,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? LastCopiedAtUtc,
    long CopyCount,
    IntentSummaryDto Intent,
    IntentAssignment IntentAssignment,
    IntentReviewState IntentReviewState,
    int? IntentScoreBasisPoints,
    string? ScoringAlgorithmVersion,
    MetadataStatus MetadataStatus,
    long Version,
    IReadOnlyList<SkillDto> Skills,
    IReadOnlyList<EntityDto> Entities,
    IReadOnlyList<IntentCandidateDto> IntentCandidates);

public sealed record PromptSummaryDto(
    PromptId Id,
    string? Title,
    string BodyPreview,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? LastCopiedAtUtc,
    long CopyCount,
    IntentSummaryDto Intent,
    IntentReviewState IntentReviewState,
    MetadataStatus MetadataStatus,
    long Version);

public sealed record PromptEditDraftDto(
    PromptId PromptId,
    string Body,
    string? Title,
    string? MetadataEditJson,
    long BaseVersion,
    int CaretOffset,
    int SelectionLength,
    DateTimeOffset UpdatedAtUtc);

public sealed record CommitPromptEditCommand(
    PromptId PromptId,
    string Body,
    string? Title,
    long BaseVersion);

public sealed record CopyPromptCommand(
    PromptId? PromptId,
    string Text,
    bool IncludeMetadata);

public sealed record DuplicatePromptCommand(PromptId PromptId);

public sealed record DeletePromptCommand(PromptId PromptId, bool IsConfirmed);

public sealed record DeletePromptResult(
    PromptId DeletedPromptId,
    PromptId? SuggestedFocusPromptId);

public sealed record AssignIntentCommand(
    PromptId PromptId,
    IntentId IntentId,
    IntentAssignment Assignment,
    long PromptVersion);

public sealed record ReviewIntentAssignmentCommand(
    PromptId PromptId,
    IntentReviewAction Action,
    IntentId? SelectedIntentId,
    string? RenamedIntent,
    long PromptVersion);

public enum IntentReviewAction
{
    KeepProvisional,
    ChooseExisting,
    RenameProvisional,
    ReviewLater,
}
