using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using PromptSaver.Application.Ports;
using PromptSaver.Application.UseCases;
using PromptSaver.Desktop.ViewModels;
using PromptSaver.Infrastructure.Drafts;
using PromptSaver.Infrastructure.Providers;
using PromptSaver.Infrastructure.Storage;

namespace PromptSaver.Desktop.Services;

public static class DesktopComposition
{
    public static ShellViewModel CreateShell(
        object? useCases = null,
        Func<CancellationToken, Task>? optionalInitialization = null)
    {
        useCases ??= new NullDesktopUseCases();
        return new ShellViewModel(
            new CaptureViewModel(useCases),
            new LibraryViewModel(useCases),
            new PromptDetailViewModel(useCases),
            new SettingsViewModel(useCases),
            optionalInitialization);
    }

    public static ServiceProvider CreateServiceProvider()
    {
        ServiceCollection services = new();
        services.AddSingleton<IDataRootResolver, DataRootResolver>();
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IIdGenerator, SystemIdGenerator>();
        services.AddSingleton<IClipboard, WindowsClipboard>();
        services.AddSingleton<ICredentialStore, WindowsCredentialStore>();
        services.AddSingleton<IProviderCredentialSource, ProviderCredentialSource>();
        services.AddSingleton<IBackgroundScheduler, BackgroundScheduler>();

        services.AddSingleton<SqliteConnectionFactory>();
        services.AddSingleton<SqliteMigrationRunner>();
        services.AddSingleton<IUnitOfWorkFactory, SqliteUnitOfWorkFactory>();
        services.AddSingleton<IPromptSearch, SqlitePromptSearch>();
        services.AddSingleton<IPromptPersistenceOperations, SqlitePersistenceOperations>();
        services.AddSingleton<IBackupService, SqliteBackupService>();
        services.AddSingleton<ICaptureDraftStore, CaptureDraftStore>();
        services.AddSingleton<IPromptEditDraftStore, PromptEditDraftStore>();
        services.AddSingleton<SqliteProviderStore>();
        services.AddSingleton<IProviderConfigurationStore>(
            provider => provider.GetRequiredService<SqliteProviderStore>());
        services.AddSingleton<IEnrichmentWorkStore>(
            provider => provider.GetRequiredService<SqliteProviderStore>());
        services.AddSingleton<IEnrichmentPromptSource>(
            provider => provider.GetRequiredService<SqliteProviderStore>());

        services.AddSingleton<IOllamaDiscovery, OllamaDiscovery>();
        services.AddSingleton<IPromptEnrichmentProvider, OllamaEnrichmentProvider>();
        services.AddSingleton<IPromptEnrichmentProvider>(
            provider => new OpenAiCompatibleEnrichmentProvider(
                new SocketsHttpHandler { AllowAutoRedirect = false },
                provider.GetRequiredService<IProviderCredentialSource>()));
        services.AddSingleton<IEnrichPendingPrompts, MultiProviderEnrichmentService>();

        services.AddSingleton<PromptApplicationService>();
        services.AddSingleton<ISaveCaptureDraft>(ResolvePrompts);
        services.AddSingleton<IRecoverCaptureDraft>(ResolvePrompts);
        services.AddSingleton<IDiscardCaptureDraft>(ResolvePrompts);
        services.AddSingleton<ICapturePrompt>(ResolvePrompts);
        services.AddSingleton<ISavePromptEditDraft>(ResolvePrompts);
        services.AddSingleton<ICommitPromptEdit>(ResolvePrompts);
        services.AddSingleton<IDiscardPromptEditDraft>(ResolvePrompts);
        services.AddSingleton<IGetPromptDetails>(ResolvePrompts);
        services.AddSingleton<ICopyPrompt>(ResolvePrompts);
        services.AddSingleton<IDuplicatePrompt>(ResolvePrompts);
        services.AddSingleton<IDeletePrompt>(ResolvePrompts);
        services.AddSingleton<ISearchPrompts>(ResolvePrompts);
        services.AddSingleton<IAssignIntent>(ResolvePrompts);
        services.AddSingleton<IReviewIntentAssignment>(ResolvePrompts);
        services.AddSingleton<IMergeIntents>(ResolvePrompts);
        services.AddSingleton<IListIntents>(ResolvePrompts);
        services.AddSingleton<IManageMetadata>(ResolvePrompts);
        services.AddSingleton<ICreateBackup>(ResolvePrompts);
        services.AddSingleton<IRestoreBackup>(ResolvePrompts);
        services.AddSingleton<IRebuildSearchIndex>(ResolvePrompts);
        services.AddSingleton<IGetDiagnostics>(ResolvePrompts);

        services.AddSingleton<ProviderApplicationService>();
        services.AddSingleton<IConfigureProvider>(
            provider => provider.GetRequiredService<ProviderApplicationService>());
        services.AddSingleton<IGetProviderConfiguration>(
            provider => provider.GetRequiredService<ProviderApplicationService>());
        services.AddSingleton<IDiscoverLocalOllama>(
            provider => provider.GetRequiredService<ProviderApplicationService>());
        services.AddSingleton<ICheckProviderHealth>(
            provider => provider.GetRequiredService<ProviderApplicationService>());

        services.AddSingleton<CaptureViewModel>(
            provider => new CaptureViewModel(provider.GetRequiredService<PromptApplicationService>()));
        services.AddSingleton<LibraryViewModel>(
            provider => new LibraryViewModel(provider.GetRequiredService<PromptApplicationService>()));
        services.AddSingleton<PromptDetailViewModel>(
            provider => new PromptDetailViewModel(provider.GetRequiredService<PromptApplicationService>()));
        services.AddSingleton<SettingsViewModel>(
            provider => new SettingsViewModel(new DesktopUseCaseAdapter(
                provider.GetRequiredService<PromptApplicationService>(),
                provider.GetRequiredService<ProviderApplicationService>())));
        services.AddSingleton<ShellViewModel>(
            provider => new ShellViewModel(
                provider.GetRequiredService<CaptureViewModel>(),
                provider.GetRequiredService<LibraryViewModel>(),
                provider.GetRequiredService<PromptDetailViewModel>(),
                provider.GetRequiredService<SettingsViewModel>(),
                token => InitializeOptionalServicesAsync(provider, token)));
        services.AddSingleton<MainWindow>();
        return services.BuildServiceProvider(validateScopes: true);
    }

    private static PromptApplicationService ResolvePrompts(IServiceProvider provider) =>
        provider.GetRequiredService<PromptApplicationService>();

    private static async Task InitializeOptionalServicesAsync(
        IServiceProvider provider,
        CancellationToken cancellationToken)
    {
        await provider.GetRequiredService<SqliteMigrationRunner>()
            .InitializeAsync(cancellationToken);
        await provider.GetRequiredService<IDiscoverLocalOllama>()
            .ExecuteAsync(cancellationToken);
        await provider.GetRequiredService<IEnrichPendingPrompts>()
            .ExecuteAsync(cancellationToken);
    }

    private sealed class DesktopUseCaseAdapter :
        IConfigureProvider,
        IGetProviderConfiguration,
        IDiscoverLocalOllama,
        ICheckProviderHealth,
        IListIntents,
        IMergeIntents,
        ICreateBackup,
        IRestoreBackup,
        IRebuildSearchIndex,
        IGetDiagnostics
    {
        private readonly PromptApplicationService _prompts;
        private readonly ProviderApplicationService _providers;

        internal DesktopUseCaseAdapter(
            PromptApplicationService prompts,
            ProviderApplicationService providers)
        {
            _prompts = prompts;
            _providers = providers;
        }

        Task<Application.AppResult<Application.Dtos.ProviderConfigurationDto>> IConfigureProvider.ExecuteAsync(
            Application.Dtos.ConfigureProviderCommand command,
            CancellationToken cancellationToken) =>
            _providers.ExecuteAsync(command, cancellationToken);

        Task<Application.AppResult<Application.Dtos.ProviderConfigurationDto?>> IGetProviderConfiguration.ExecuteAsync(
            CancellationToken cancellationToken) =>
            _providers.ExecuteAsync(cancellationToken);

        Task<Application.AppResult<Application.Dtos.ProviderHealthDto>> IDiscoverLocalOllama.ExecuteAsync(
            CancellationToken cancellationToken) =>
            ((IDiscoverLocalOllama)_providers).ExecuteAsync(cancellationToken);

        Task<Application.AppResult<Application.Dtos.ProviderHealthDto>> ICheckProviderHealth.ExecuteAsync(
            Domain.ValueObjects.ProviderConfigurationId providerId,
            CancellationToken cancellationToken) =>
            _providers.ExecuteAsync(providerId, cancellationToken);

        Task<Application.AppResult<IReadOnlyList<Application.Dtos.IntentSummaryDto>>> IListIntents.ExecuteAsync(
            CancellationToken cancellationToken) =>
            ((IListIntents)_prompts).ExecuteAsync(cancellationToken);

        Task<Application.AppResult<Application.Dtos.MergeIntentsResult>> IMergeIntents.ExecuteAsync(
            Application.Dtos.MergeIntentsCommand command,
            CancellationToken cancellationToken) =>
            _prompts.ExecuteAsync(command, cancellationToken);

        Task<Application.AppResult<Application.Dtos.BackupInfoDto>> ICreateBackup.ExecuteAsync(
            CancellationToken cancellationToken) =>
            ((ICreateBackup)_prompts).ExecuteAsync(cancellationToken);

        Task<Application.AppResult> IRestoreBackup.ExecuteAsync(
            Application.Dtos.RestoreBackupCommand command,
            CancellationToken cancellationToken) =>
            ((IRestoreBackup)_prompts).ExecuteAsync(command, cancellationToken);

        Task<Application.AppResult> IRebuildSearchIndex.ExecuteAsync(
            CancellationToken cancellationToken) =>
            ((IRebuildSearchIndex)_prompts).ExecuteAsync(cancellationToken);

        Task<Application.AppResult<Application.Dtos.DiagnosticsDto>> IGetDiagnostics.ExecuteAsync(
            CancellationToken cancellationToken) =>
            _prompts.ExecuteAsync(cancellationToken);
    }
}
