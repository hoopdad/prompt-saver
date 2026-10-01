using PromptSaver.Domain.ValueObjects;

namespace PromptSaver.Application.Dtos;

public enum ProviderKind
{
    Ollama,
    OpenAiCompatible,
}

public sealed record ProviderConfigurationDto(
    ProviderConfigurationId Id,
    ProviderKind Kind,
    string DisplayName,
    Uri Endpoint,
    string Model,
    bool IsEnabled,
    bool RemoteHttpAcknowledged,
    string? CredentialTarget);

public sealed record ConfigureProviderCommand(
    ProviderConfigurationId? Id,
    ProviderKind Kind,
    string DisplayName,
    Uri Endpoint,
    string Model,
    bool IsEnabled,
    bool RemoteHttpAcknowledged,
    string? Credential,
    bool RemoveCredential = false);

public sealed record ProviderHealthDto(
    bool IsHealthy,
    string? ProviderVersion,
    IReadOnlyList<string> AvailableModels,
    AppError? Error);

public sealed record ProviderEnrichmentRequest(
    PromptId PromptId,
    string BodyExcerpt,
    bool ContentTruncated,
    string SchemaVersion);

public sealed record ProviderEnrichmentResponse(
    string? Title,
    string? Intent,
    IReadOnlyList<ProposedSkillDto> Skills,
    IReadOnlyList<ProposedEntityDto> Entities,
    string ProviderName,
    string Model);

public enum EnrichmentProposalReviewAction
{
    Accept,
    Reject,
}

public sealed record ReviewEnrichmentProposalCommand(
    PromptId PromptId,
    EnrichmentProposalReviewAction Action,
    bool AcceptTitle,
    bool AcceptIntent,
    IReadOnlyList<string> AcceptedSkills,
    IReadOnlyList<string> AcceptedEntities,
    long PromptVersion);

public sealed record BackupInfoDto(
    string Path,
    DateTimeOffset CreatedAtUtc,
    long SizeBytes);

public sealed record RestoreBackupCommand(string BackupPath, bool IsConfirmed);

public sealed record DiagnosticsDto(
    string ApplicationVersion,
    string RuntimeIdentifier,
    string DataRoot,
    bool DatabaseWritable,
    bool SearchHealthy,
    DateTimeOffset? LastBackupAtUtc,
    IReadOnlyList<AppError> ActiveErrors);
