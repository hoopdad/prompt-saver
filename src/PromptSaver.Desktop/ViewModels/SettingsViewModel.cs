using PromptSaver.Application;
using PromptSaver.Application.Dtos;
using PromptSaver.Application.UseCases;
using PromptSaver.Domain.ValueObjects;

namespace PromptSaver.Desktop.ViewModels;

public sealed class SettingsViewModel : ViewModelBase, IDisposable
{
    private readonly IConfigureProvider _configureProvider;
    private readonly IGetProviderConfiguration _getProviderConfiguration;
    private readonly IDiscoverLocalOllama _discoverOllama;
    private readonly ICheckProviderHealth _checkProviderHealth;
    private readonly IListIntents _listIntents;
    private readonly IMergeIntents _mergeIntents;
    private readonly ICreateBackup _createBackup;
    private readonly IRestoreBackup _restoreBackup;
    private readonly IRebuildSearchIndex _rebuildSearch;
    private readonly IGetDiagnostics _getDiagnostics;
    private string _selectedCategory = "General";
    private string _theme = "System";
    private string _providerEndpoint = "http://127.0.0.1:11434";
    private string _providerModel = string.Empty;
    private bool _providerEnabled;
    private bool _remoteHttpAcknowledged;
    private ProviderConfigurationId? _providerId;
    private string _providerApiKey = string.Empty;
    private bool _removeProviderApiKey;
    private bool _credentialConfigured;
    private bool _isTestingProvider;
    private CancellationTokenSource? _providerTestCancellation;
    private string _statusMessage = "Local capture and search stay on this device.";
    private string _backupPath = string.Empty;
    private bool _restoreConfirmationOpen;
    private IReadOnlyList<IntentSummaryDto> _intents = [];
    private IntentSummaryDto? _sourceIntent;
    private IntentSummaryDto? _targetIntent;
    private bool _mergeConfirmationOpen;

    public SettingsViewModel(object services)
    {
        _configureProvider = Require<IConfigureProvider>(services);
        _getProviderConfiguration = Require<IGetProviderConfiguration>(services);
        _discoverOllama = Require<IDiscoverLocalOllama>(services);
        _checkProviderHealth = Require<ICheckProviderHealth>(services);
        _listIntents = Require<IListIntents>(services);
        _mergeIntents = Require<IMergeIntents>(services);
        _createBackup = Require<ICreateBackup>(services);
        _restoreBackup = Require<IRestoreBackup>(services);
        _rebuildSearch = Require<IRebuildSearchIndex>(services);
        _getDiagnostics = Require<IGetDiagnostics>(services);
        SaveProviderCommand = new AsyncDelegateCommand(SaveProviderAsync);
        DiscoverOllamaCommand = new AsyncDelegateCommand(DiscoverOllamaAsync);
        TestProviderCommand = new AsyncDelegateCommand(TestProviderAsync, () => !IsTestingProvider);
        CancelProviderTestCommand = new DelegateCommand(
            CancelProviderTest,
            () => IsTestingProvider);
        BackupCommand = new AsyncDelegateCommand(CreateBackupAsync);
        RequestRestoreCommand = new DelegateCommand(
            () => RestoreConfirmationOpen = true,
            () => BackupPath.Length > 0);
        CancelRestoreCommand = new DelegateCommand(() => RestoreConfirmationOpen = false);
        RestoreCommand = new AsyncDelegateCommand(RestoreBackupAsync, () => RestoreConfirmationOpen);
        RebuildSearchCommand = new AsyncDelegateCommand(RebuildSearchAsync);
        RefreshDiagnosticsCommand = new AsyncDelegateCommand(RefreshDiagnosticsAsync);
        RefreshIntentsCommand = new AsyncDelegateCommand(LoadIntentsAsync);
        RequestMergeCommand = new DelegateCommand(
            () => MergeConfirmationOpen = true,
            CanMerge);
        CancelMergeCommand = new DelegateCommand(() => MergeConfirmationOpen = false);
        MergeIntentsCommand = new AsyncDelegateCommand(MergeIntentsAsync, () => MergeConfirmationOpen);
    }

    public IReadOnlyList<string> Categories { get; } =
        ["General", "AI metadata", "Data and recovery", "Keyboard and accessibility"];

    public IReadOnlyList<string> Themes { get; } = ["System", "Light", "Dark"];

    public string SelectedCategory
    {
        get => _selectedCategory;
        set => SetProperty(ref _selectedCategory, value ?? "General");
    }

    public string Theme
    {
        get => _theme;
        set => SetProperty(ref _theme, value ?? "System");
    }

    public string ProviderEndpoint
    {
        get => _providerEndpoint;
        set => SetProperty(ref _providerEndpoint, value ?? string.Empty);
    }

    public string ProviderModel
    {
        get => _providerModel;
        set => SetProperty(ref _providerModel, value ?? string.Empty);
    }

    public bool ProviderEnabled
    {
        get => _providerEnabled;
        set => SetProperty(ref _providerEnabled, value);
    }

    public bool RemoteHttpAcknowledged
    {
        get => _remoteHttpAcknowledged;
        set => SetProperty(ref _remoteHttpAcknowledged, value);
    }

    public string ProviderApiKey
    {
        get => _providerApiKey;
        set => SetProperty(ref _providerApiKey, value ?? string.Empty);
    }

    public bool RemoveProviderApiKey
    {
        get => _removeProviderApiKey;
        set => SetProperty(ref _removeProviderApiKey, value);
    }

    public bool CredentialConfigured
    {
        get => _credentialConfigured;
        private set => SetProperty(ref _credentialConfigured, value);
    }

    public bool IsTestingProvider
    {
        get => _isTestingProvider;
        private set
        {
            if (SetProperty(ref _isTestingProvider, value))
            {
                TestProviderCommand.RaiseCanExecuteChanged();
                CancelProviderTestCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string BackupPath
    {
        get => _backupPath;
        set
        {
            if (SetProperty(ref _backupPath, value ?? string.Empty))
            {
                RestoreCommand.RaiseCanExecuteChanged();
                RequestRestoreCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool RestoreConfirmationOpen
    {
        get => _restoreConfirmationOpen;
        set
        {
            if (SetProperty(ref _restoreConfirmationOpen, value))
            {
                RestoreCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string DiagnosticsText { get; private set; } = "Diagnostics not loaded";

    public IReadOnlyList<IntentSummaryDto> Intents
    {
        get => _intents;
        private set => SetProperty(ref _intents, value);
    }

    public IntentSummaryDto? SourceIntent
    {
        get => _sourceIntent;
        set
        {
            if (SetProperty(ref _sourceIntent, value))
            {
                RequestMergeCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(MergeConfirmationText));
            }
        }
    }

    public IntentSummaryDto? TargetIntent
    {
        get => _targetIntent;
        set
        {
            if (SetProperty(ref _targetIntent, value))
            {
                RequestMergeCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(MergeConfirmationText));
            }
        }
    }

    public bool MergeConfirmationOpen
    {
        get => _mergeConfirmationOpen;
        private set
        {
            if (SetProperty(ref _mergeConfirmationOpen, value))
            {
                MergeIntentsCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string MergeConfirmationText =>
        SourceIntent is null || TargetIntent is null
            ? string.Empty
            : $"Merge \"{SourceIntent.CanonicalName}\" ({SourceIntent.PromptCount} prompts) into \"{TargetIntent.CanonicalName}\"? Prompt text is retained.";

    public AsyncDelegateCommand SaveProviderCommand { get; }

    public AsyncDelegateCommand DiscoverOllamaCommand { get; }

    public AsyncDelegateCommand TestProviderCommand { get; }

    public DelegateCommand CancelProviderTestCommand { get; }

    public AsyncDelegateCommand BackupCommand { get; }

    public DelegateCommand RequestRestoreCommand { get; }

    public DelegateCommand CancelRestoreCommand { get; }

    public AsyncDelegateCommand RestoreCommand { get; }

    public AsyncDelegateCommand RebuildSearchCommand { get; }

    public AsyncDelegateCommand RefreshDiagnosticsCommand { get; }

    public AsyncDelegateCommand RefreshIntentsCommand { get; }

    public DelegateCommand RequestMergeCommand { get; }

    public DelegateCommand CancelMergeCommand { get; }

    public AsyncDelegateCommand MergeIntentsCommand { get; }

    public async Task LoadAsync()
    {
        await LoadProviderAsync();
        await LoadIntentsAsync();
    }

    public async Task LoadProviderAsync()
    {
        StatusMessage = "Loading provider settings...";
        AppResult<ProviderConfigurationDto?> result =
            await _getProviderConfiguration.ExecuteAsync(CancellationToken.None);
        if (!result.IsSuccess)
        {
            StatusMessage = $"Provider settings could not be loaded. {result.Error?.Message}";
            return;
        }

        if (result.Value is null)
        {
            StatusMessage = "No AI provider is configured. Local capture and search are ready.";
            return;
        }

        ProviderConfigurationDto configuration = result.Value;
        _providerId = configuration.Id;
        ProviderEndpoint = configuration.Endpoint.AbsoluteUri;
        ProviderModel = configuration.Model;
        ProviderEnabled = configuration.IsEnabled;
        RemoteHttpAcknowledged = configuration.RemoteHttpAcknowledged;
        CredentialConfigured = configuration.CredentialTarget is not null;
        StatusMessage =
            $"Loaded model \"{configuration.Model}\" ({(configuration.IsEnabled ? "enabled" : "disabled")}).";
    }

    public async Task LoadIntentsAsync()
    {
        AppResult<IReadOnlyList<IntentSummaryDto>> result =
            await _listIntents.ExecuteAsync(CancellationToken.None);
        if (!result.IsSuccess)
        {
            StatusMessage = $"Intents could not be loaded. {result.Error?.Message}";
            return;
        }

        Intents = result.Value;
        SourceIntent = Intents.Count > 0 ? Intents[0] : null;
        TargetIntent = Intents.Count > 1 ? Intents[1] : null;
    }

    private async Task SaveProviderAsync()
    {
        AppResult<ProviderConfigurationDto> result =
            await ConfigureProviderAsync(CancellationToken.None);
        StatusMessage = result.IsSuccess
            ? CredentialConfigured
                ? $"Saved model \"{result.Value.Model}\". The API key is stored in Windows Credential Manager."
                : $"Saved model \"{result.Value.Model}\"."
            : $"Provider configuration was not saved. {result.Error?.Message}";
    }

    private async Task<AppResult<ProviderConfigurationDto>> ConfigureProviderAsync(
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(ProviderEndpoint, UriKind.Absolute, out Uri? endpoint))
        {
            StatusMessage = "Enter a valid provider URL.";
            return AppResult.Failure<ProviderConfigurationDto>(
                new AppError(
                    AppErrorCode.Validation,
                    "provider.endpoint.invalid",
                    StatusMessage));
        }

        if (endpoint.Scheme == Uri.UriSchemeHttp &&
            !endpoint.IsLoopback &&
            !RemoteHttpAcknowledged)
        {
            StatusMessage = "Remote plain HTTP requires explicit acknowledgement.";
            return AppResult.Failure<ProviderConfigurationDto>(
                new AppError(
                    AppErrorCode.Validation,
                    "provider.endpoint.remote_http_acknowledgement_required",
                    StatusMessage));
        }

        AppResult<ProviderConfigurationDto> result = await _configureProvider.ExecuteAsync(
            new ConfigureProviderCommand(
                _providerId,
                endpoint.IsLoopback ? ProviderKind.Ollama : ProviderKind.OpenAiCompatible,
                endpoint.IsLoopback ? "Ollama" : "OpenAI-compatible",
                endpoint,
                ProviderModel,
                ProviderEnabled,
                RemoteHttpAcknowledged,
                ProviderApiKey,
                RemoveProviderApiKey),
            cancellationToken);
        if (result.IsSuccess)
        {
            _providerId = result.Value.Id;
            CredentialConfigured = result.Value.CredentialTarget is not null;
            ProviderApiKey = string.Empty;
            RemoveProviderApiKey = false;
        }

        return result;
    }

    private async Task DiscoverOllamaAsync()
    {
        AppResult<ProviderHealthDto> result = await _discoverOllama.ExecuteAsync(CancellationToken.None);
        if (result.IsSuccess && result.Value.IsHealthy)
        {
            StatusMessage = "Local Ollama detected - enable AI metadata?";
            if (string.IsNullOrWhiteSpace(ProviderModel) && result.Value.AvailableModels.Count > 0)
            {
                ProviderModel = result.Value.AvailableModels[0];
            }
        }
        else
        {
            StatusMessage = "Ollama unavailable - local capture and search are available.";
        }
    }

    private async Task TestProviderAsync()
    {
        _providerTestCancellation?.Dispose();
        CancellationTokenSource cancellation = new();
        _providerTestCancellation = cancellation;
        IsTestingProvider = true;
        StatusMessage = $"Testing model \"{ProviderModel}\" at {ProviderEndpoint}...";
        try
        {
            AppResult<ProviderConfigurationDto> configured =
                await ConfigureProviderAsync(cancellation.Token);
            if (!configured.IsSuccess)
            {
                StatusMessage = configured.Error?.Code == AppErrorCode.Cancelled
                    ? "Provider connection test cancelled."
                    : $"Provider connection test could not start. {configured.Error?.Message}";
                return;
            }

            AppResult<ProviderHealthDto> health = await _checkProviderHealth.ExecuteAsync(
                configured.Value.Id,
                cancellation.Token);
            StatusMessage = health.IsSuccess && health.Value.IsHealthy
                ? "Provider connection succeeded."
                : health.Error?.Code == AppErrorCode.Cancelled
                    ? "Provider connection test cancelled."
                    : $"Provider connection failed. {health.Error?.Message}";
        }
        finally
        {
            IsTestingProvider = false;
            if (ReferenceEquals(_providerTestCancellation, cancellation))
            {
                _providerTestCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private void CancelProviderTest() => _providerTestCancellation?.Cancel();

    public void Dispose()
    {
        _providerTestCancellation?.Cancel();
        _providerTestCancellation?.Dispose();
        _providerTestCancellation = null;
    }

    private async Task CreateBackupAsync()
    {
        AppResult<BackupInfoDto> result = await _createBackup.ExecuteAsync(CancellationToken.None);
        StatusMessage = result.IsSuccess
            ? $"Backup created: {result.Value.Path}"
            : $"Backup was not created. {result.Error?.Message}";
    }

    private async Task RestoreBackupAsync()
    {
        AppResult result = await _restoreBackup.ExecuteAsync(
            new RestoreBackupCommand(BackupPath, true),
            CancellationToken.None);
        RestoreConfirmationOpen = false;
        StatusMessage = result.IsSuccess
            ? "Backup restored"
            : $"Backup was not restored. {result.Error?.Message}";
    }

    private async Task RebuildSearchAsync()
    {
        StatusMessage = "Search index rebuilding - recent prompts may be incomplete";
        AppResult result = await _rebuildSearch.ExecuteAsync(CancellationToken.None);
        StatusMessage = result.IsSuccess
            ? "Search index rebuilt"
            : $"Search index was not rebuilt. {result.Error?.Message}";
    }

    private async Task RefreshDiagnosticsAsync()
    {
        AppResult<DiagnosticsDto> result = await _getDiagnostics.ExecuteAsync(CancellationToken.None);
        DiagnosticsText = result.IsSuccess
            ? $"Version {result.Value.ApplicationVersion}; Database writable: {result.Value.DatabaseWritable}; Search healthy: {result.Value.SearchHealthy}"
            : $"Diagnostics unavailable. {result.Error?.Message}";
        OnPropertyChanged(nameof(DiagnosticsText));
    }

    private bool CanMerge() =>
        SourceIntent is not null &&
        TargetIntent is not null &&
        SourceIntent.Id != TargetIntent.Id;

    private async Task MergeIntentsAsync()
    {
        if (!CanMerge() || SourceIntent is null || TargetIntent is null)
        {
            return;
        }

        IntentSummaryDto source = SourceIntent;
        IntentSummaryDto target = TargetIntent;
        AppResult<MergeIntentsResult> result = await _mergeIntents.ExecuteAsync(
            new MergeIntentsCommand(
                source.Id,
                target.Id,
                source.Version,
                target.Version),
            CancellationToken.None);
        MergeConfirmationOpen = false;
        if (!result.IsSuccess)
        {
            StatusMessage = $"Intents were not merged. {result.Error?.Message}";
            return;
        }

        StatusMessage =
            $"Merged \"{source.CanonicalName}\" into \"{target.CanonicalName}\" and reclassified {result.Value.ReassignedPromptCount} prompts.";
        await LoadIntentsAsync();
    }

    private static T Require<T>(object services)
        where T : class =>
        services as T ??
        throw new ArgumentException($"The service adapter must implement {typeof(T).Name}.", nameof(services));
}
