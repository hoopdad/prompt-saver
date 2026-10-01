using System.Windows;
using PromptSaver.Application;
using PromptSaver.Application.Dtos;
using PromptSaver.Application.UseCases;
using PromptSaver.Domain.ValueObjects;

namespace PromptSaver.Desktop.Services;

public class NullDesktopUseCases :
    ISaveCaptureDraft,
    IRecoverCaptureDraft,
    IDiscardCaptureDraft,
    ICapturePrompt,
    ICopyPrompt,
    ISearchPrompts,
    IGetPromptDetails,
    ICommitPromptEdit,
    IDuplicatePrompt,
    IDeletePrompt,
    IReviewIntentAssignment,
    IListIntents,
    IMergeIntents,
    IManageMetadata,
    IConfigureProvider,
    IDiscoverLocalOllama,
    ICheckProviderHealth,
    ICreateBackup,
    IRestoreBackup,
    IRebuildSearchIndex,
    IGetDiagnostics
{
    private static readonly AppError StorageNotReady = new(
        AppErrorCode.PersistenceUnavailable,
        "desktop.adapter.not_configured",
        "Local storage is still starting. You can continue typing and copy your text.",
        true);

    public virtual Task<AppResult> ExecuteAsync(
        CaptureDraftDto draft,
        CancellationToken cancellationToken) =>
        Task.FromResult(AppResult.Failure(StorageNotReady));

    Task<CaptureDraftRecoveryResult> IRecoverCaptureDraft.ExecuteAsync(
        CancellationToken cancellationToken) =>
        Task.FromResult(new CaptureDraftRecoveryResult(DraftRecoveryStatus.Missing, null, null, null));

    Task<AppResult> IDiscardCaptureDraft.ExecuteAsync(CancellationToken cancellationToken) =>
        Task.FromResult(AppResult.Success());

    Task<AppResult<CapturePromptResult>> ICapturePrompt.ExecuteAsync(
        CapturePromptCommand command,
        CancellationToken cancellationToken) =>
        Task.FromResult(AppResult.Failure<CapturePromptResult>(StorageNotReady));

    Task<AppResult> ICopyPrompt.ExecuteAsync(
        CopyPromptCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            Clipboard.SetText(command.Text);
            return Task.FromResult(AppResult.Success());
        }
        catch (Exception exception) when (
            exception is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            return Task.FromResult(
                AppResult.Failure(
                    new AppError(
                        AppErrorCode.ClipboardUnavailable,
                        "clipboard.unavailable",
                        "The Windows clipboard is temporarily unavailable.",
                        true)));
        }
    }

    Task<AppResult<SearchPageDto<PromptSummaryDto>>> ISearchPrompts.ExecuteAsync(
        SearchPromptsQuery query,
        CancellationToken cancellationToken) =>
        Task.FromResult(
            AppResult.Success(
                new SearchPageDto<PromptSummaryDto>([], query.PageNumber, query.PageSize, 0)));

    Task<AppResult<PromptDetailsDto>> IGetPromptDetails.ExecuteAsync(
        PromptId promptId,
        CancellationToken cancellationToken) =>
        Task.FromResult(NotFound());

    Task<AppResult<PromptDetailsDto>> ICommitPromptEdit.ExecuteAsync(
        CommitPromptEditCommand command,
        CancellationToken cancellationToken) =>
        Task.FromResult(AppResult.Failure<PromptDetailsDto>(StorageNotReady));

    Task<AppResult<PromptDetailsDto>> IDuplicatePrompt.ExecuteAsync(
        DuplicatePromptCommand command,
        CancellationToken cancellationToken) =>
        Task.FromResult(AppResult.Failure<PromptDetailsDto>(StorageNotReady));

    Task<AppResult<DeletePromptResult>> IDeletePrompt.ExecuteAsync(
        DeletePromptCommand command,
        CancellationToken cancellationToken) =>
        Task.FromResult(AppResult.Failure<DeletePromptResult>(StorageNotReady));

    Task<AppResult<PromptDetailsDto>> IReviewIntentAssignment.ExecuteAsync(
        ReviewIntentAssignmentCommand command,
        CancellationToken cancellationToken) =>
        Task.FromResult(AppResult.Failure<PromptDetailsDto>(StorageNotReady));

    Task<AppResult<IReadOnlyList<IntentSummaryDto>>> IListIntents.ExecuteAsync(
        CancellationToken cancellationToken) =>
        Task.FromResult(
            AppResult.Success<IReadOnlyList<IntentSummaryDto>>([]));

    Task<AppResult<MergeIntentsResult>> IMergeIntents.ExecuteAsync(
        MergeIntentsCommand command,
        CancellationToken cancellationToken) =>
        Task.FromResult(AppResult.Failure<MergeIntentsResult>(StorageNotReady));

    Task<AppResult<PromptDetailsDto>> IManageMetadata.ExecuteAsync(
        ManageMetadataCommand command,
        CancellationToken cancellationToken) =>
        Task.FromResult(AppResult.Failure<PromptDetailsDto>(StorageNotReady));

    Task<AppResult<ProviderConfigurationDto>> IConfigureProvider.ExecuteAsync(
        ConfigureProviderCommand command,
        CancellationToken cancellationToken) =>
        Task.FromResult(
            AppResult.Success(
                new ProviderConfigurationDto(
                    command.Id ?? new ProviderConfigurationId(Guid.CreateVersion7()),
                    command.Kind,
                    command.DisplayName,
                    command.Endpoint,
                    command.Model,
                    command.IsEnabled,
                    command.RemoteHttpAcknowledged,
                    null)));

    Task<AppResult<ProviderHealthDto>> IDiscoverLocalOllama.ExecuteAsync(
        CancellationToken cancellationToken) =>
        Task.FromResult(
            AppResult.Success(
                new ProviderHealthDto(
                    false,
                    null,
                    [],
                    new AppError(
                        AppErrorCode.ProviderUnavailable,
                        "ollama.unavailable",
                        "Ollama was not detected.",
                        true))));

    Task<AppResult<ProviderHealthDto>> ICheckProviderHealth.ExecuteAsync(
        ProviderConfigurationId providerId,
        CancellationToken cancellationToken) =>
        Task.FromResult(
            AppResult.Failure<ProviderHealthDto>(
                new AppError(
                    AppErrorCode.ProviderUnavailable,
                    "provider.unavailable",
                    "The provider is unavailable.",
                    true)));

    Task<AppResult<BackupInfoDto>> ICreateBackup.ExecuteAsync(
        CancellationToken cancellationToken) =>
        Task.FromResult(AppResult.Failure<BackupInfoDto>(StorageNotReady));

    Task<AppResult> IRestoreBackup.ExecuteAsync(
        RestoreBackupCommand command,
        CancellationToken cancellationToken) =>
        Task.FromResult(AppResult.Failure(StorageNotReady));

    Task<AppResult> IRebuildSearchIndex.ExecuteAsync(CancellationToken cancellationToken) =>
        Task.FromResult(AppResult.Failure(StorageNotReady));

    Task<AppResult<DiagnosticsDto>> IGetDiagnostics.ExecuteAsync(
        CancellationToken cancellationToken) =>
        Task.FromResult(
            AppResult.Success(
                new DiagnosticsDto(
                    typeof(App).Assembly.GetName().Version?.ToString() ?? "development",
                    System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier,
                    "Not connected",
                    false,
                    false,
                    null,
                    [StorageNotReady])));

    private static AppResult<PromptDetailsDto> NotFound() =>
        AppResult.Failure<PromptDetailsDto>(
            new AppError(AppErrorCode.NotFound, "prompt.not_found", "The prompt is not available."));
}
