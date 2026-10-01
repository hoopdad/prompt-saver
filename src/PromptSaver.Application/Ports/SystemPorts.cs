using PromptSaver.Domain.ValueObjects;

namespace PromptSaver.Application.Ports;

public interface IClipboard
{
    Task<AppResult> SetTextAsync(string text, CancellationToken cancellationToken);
}

public interface ICredentialStore
{
    Task<AppResult> SaveAsync(
        string opaqueTarget,
        string credential,
        CancellationToken cancellationToken);

    Task<AppResult<string?>> GetAsync(string opaqueTarget, CancellationToken cancellationToken);

    Task<AppResult> DeleteAsync(string opaqueTarget, CancellationToken cancellationToken);
}

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public interface IIdGenerator
{
    PromptId NewPromptId();

    IntentId NewIntentId();

    IntentAliasId NewIntentAliasId();

    SkillId NewSkillId();

    EntityId NewEntityId();

    ProviderConfigurationId NewProviderConfigurationId();
}

public interface IBackgroundScheduler
{
    AppResult TrySchedule(string operationName, Func<CancellationToken, Task> operation);
}

public interface IActivationService
{
    Task<AppResult> ActivateMainWindowAsync(CancellationToken cancellationToken);
}

public interface IBackupService
{
    Task<AppResult<Dtos.BackupInfoDto>> CreateAsync(CancellationToken cancellationToken);

    Task<AppResult> RestoreAsync(
        Dtos.RestoreBackupCommand command,
        CancellationToken cancellationToken);

    Task<AppResult<IReadOnlyList<Dtos.BackupInfoDto>>> ListAsync(
        CancellationToken cancellationToken);
}
