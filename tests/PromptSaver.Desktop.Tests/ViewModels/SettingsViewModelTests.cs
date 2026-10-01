using PromptSaver.Application;
using PromptSaver.Application.Dtos;
using PromptSaver.Application.UseCases;
using PromptSaver.Desktop.ViewModels;
using PromptSaver.Domain.ValueObjects;

namespace PromptSaver.Desktop.Tests.ViewModels;

public sealed class SettingsViewModelTests
{
    [Fact]
    public async Task SavesApiKeyWithoutRetainingItAndTestsConfiguredEndpoint()
    {
        SettingsServices services = new();
        using SettingsViewModel viewModel = new(services)
        {
            ProviderEndpoint = "https://models.example.com/v1/chat/completions",
            ProviderModel = "deployment",
            ProviderEnabled = true,
            ProviderApiKey = "secret",
        };

        await viewModel.SaveProviderCommand.ExecuteAsync();

        Assert.Equal("secret", services.LastConfiguration?.Credential);
        Assert.Equal(string.Empty, viewModel.ProviderApiKey);
        Assert.True(viewModel.CredentialConfigured);
        Assert.DoesNotContain("secret", viewModel.StatusMessage, StringComparison.Ordinal);

        await viewModel.TestProviderCommand.ExecuteAsync();

        Assert.Equal(services.ProviderId, services.CheckedProviderId);
        Assert.Equal("Provider connection succeeded.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task ProviderHealthTestCanBeCancelled()
    {
        SettingsServices services = new() { BlockHealthCheck = true };
        using SettingsViewModel viewModel = new(services)
        {
            ProviderEndpoint = "https://models.example.com/v1/chat/completions",
            ProviderModel = "deployment",
        };

        Task testing = viewModel.TestProviderCommand.ExecuteAsync();
        await services.HealthStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.True(viewModel.IsTestingProvider);

        viewModel.CancelProviderTestCommand.Execute(null);
        await testing;

        Assert.False(viewModel.IsTestingProvider);
        Assert.Equal("Provider connection test cancelled.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task LoadsAndMergesIntentsWithPromptCountConfirmation()
    {
        SettingsServices services = new();
        using SettingsViewModel viewModel = new(services);

        await viewModel.LoadIntentsAsync();
        viewModel.SourceIntent = viewModel.Intents[0];
        viewModel.TargetIntent = viewModel.Intents[1];

        viewModel.RequestMergeCommand.Execute(null);

        Assert.True(viewModel.MergeConfirmationOpen);
        Assert.Contains("3 prompts", viewModel.MergeConfirmationText, StringComparison.Ordinal);

        await viewModel.MergeIntentsCommand.ExecuteAsync();

        Assert.NotNull(services.LastMerge);
        Assert.Equal(
            new IntentId(Guid.Parse("0199a59c-7c00-7000-8000-000000000062")),
            services.LastMerge.TargetIntentId);
        Assert.Contains("reclassified 3 prompts", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    private sealed class SettingsServices :
        IConfigureProvider,
        IDiscoverLocalOllama,
        ICheckProviderHealth,
        IListIntents,
        IMergeIntents,
        ICreateBackup,
        IRestoreBackup,
        IRebuildSearchIndex,
        IGetDiagnostics
    {
        public ProviderConfigurationId ProviderId { get; } =
            new(Guid.Parse("0199a59c-7c00-7000-8000-000000000060"));

        public ConfigureProviderCommand? LastConfiguration { get; private set; }

        public ProviderConfigurationId? CheckedProviderId { get; private set; }

        public bool BlockHealthCheck { get; init; }

        public MergeIntentsCommand? LastMerge { get; private set; }

        public TaskCompletionSource HealthStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<AppResult<ProviderConfigurationDto>> ExecuteAsync(
            ConfigureProviderCommand command,
            CancellationToken cancellationToken)
        {
            LastConfiguration = command;
            return Task.FromResult(
                AppResult.Success(
                    new ProviderConfigurationDto(
                        command.Id ?? ProviderId,
                        command.Kind,
                        command.DisplayName,
                        command.Endpoint,
                        command.Model,
                        command.IsEnabled,
                        command.RemoteHttpAcknowledged,
                        command.RemoveCredential ? null : "PromptSaver.Provider.test")));
        }

        Task<AppResult<ProviderHealthDto>> IDiscoverLocalOllama.ExecuteAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(
                AppResult.Success(new ProviderHealthDto(false, null, [], null)));

        async Task<AppResult<ProviderHealthDto>> ICheckProviderHealth.ExecuteAsync(
            ProviderConfigurationId providerId,
            CancellationToken cancellationToken)
        {
            CheckedProviderId = providerId;
            HealthStarted.TrySetResult();
            if (BlockHealthCheck)
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return AppResult.Failure<ProviderHealthDto>(
                        new AppError(
                            AppErrorCode.Cancelled,
                            "operation.cancelled",
                            "The operation was cancelled."));
                }
            }

            return AppResult.Success(new ProviderHealthDto(true, null, ["deployment"], null));
        }

        Task<AppResult<BackupInfoDto>> ICreateBackup.ExecuteAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(
                AppResult.Failure<BackupInfoDto>(
                    new AppError(AppErrorCode.PersistenceUnavailable, "unused", "Unused.")));

        Task<AppResult> IRestoreBackup.ExecuteAsync(
            RestoreBackupCommand command,
            CancellationToken cancellationToken) =>
            Task.FromResult(AppResult.Success());

        Task<AppResult> IRebuildSearchIndex.ExecuteAsync(CancellationToken cancellationToken) =>
            Task.FromResult(AppResult.Success());

        Task<AppResult<DiagnosticsDto>> IGetDiagnostics.ExecuteAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(
                AppResult.Success(
                    new DiagnosticsDto(
                        "test",
                        "win-arm64",
                        "test",
                        true,
                        true,
                        null,
                        [])));

        Task<AppResult<IReadOnlyList<IntentSummaryDto>>> IListIntents.ExecuteAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(
                AppResult.Success<IReadOnlyList<IntentSummaryDto>>(
                    [
                        new IntentSummaryDto(
                            new IntentId(Guid.Parse("0199a59c-7c00-7000-8000-000000000061")),
                            "Write release notes",
                            false,
                            null,
                            3,
                            1),
                        new IntentSummaryDto(
                            new IntentId(Guid.Parse("0199a59c-7c00-7000-8000-000000000062")),
                            "Write documentation",
                            false,
                            null,
                            5,
                            2),
                    ]));

        Task<AppResult<MergeIntentsResult>> IMergeIntents.ExecuteAsync(
            MergeIntentsCommand command,
            CancellationToken cancellationToken)
        {
            LastMerge = command;
            return Task.FromResult(
                AppResult.Success(
                    new MergeIntentsResult(
                        command.SourceIntentId,
                        command.TargetIntentId,
                        3,
                        [])));
        }
    }
}
