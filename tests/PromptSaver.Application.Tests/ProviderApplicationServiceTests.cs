using PromptSaver.Application;
using PromptSaver.Application.Dtos;
using PromptSaver.Application.Ports;
using PromptSaver.Application.UseCases;
using PromptSaver.Domain.ValueObjects;

namespace PromptSaver.Application.Tests;

public sealed class ProviderApplicationServiceTests
{
    [Fact]
    public async Task ConfigureStoresCredentialOnlyBehindOpaqueTargetAndCanRemoveIt()
    {
        FakeConfigurationStore configurations = new();
        RecordingCredentialStore credentials = new();
        ProviderApplicationService service = new(
            configurations,
            credentials,
            new FakeDiscovery(),
            [new FakeProvider()],
            new FakeIds());

        AppResult<ProviderConfigurationDto> saved = await service.ExecuteAsync(
            new ConfigureProviderCommand(
                null,
                ProviderKind.OpenAiCompatible,
                "Remote",
                new Uri("https://models.example.com/v1/chat/completions"),
                "deployment",
                true,
                false,
                "super-secret"),
            TestContext.Current.CancellationToken);

        Assert.True(saved.IsSuccess, saved.Error?.Message);
        Assert.Equal("super-secret", credentials.SavedCredential);
        Assert.StartsWith("PromptSaver.Provider.", saved.Value.CredentialTarget, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "super-secret",
            System.Text.Json.JsonSerializer.Serialize(saved.Value),
            StringComparison.Ordinal);

        AppResult<ProviderConfigurationDto> removed = await service.ExecuteAsync(
            new ConfigureProviderCommand(
                saved.Value.Id,
                saved.Value.Kind,
                saved.Value.DisplayName,
                saved.Value.Endpoint,
                saved.Value.Model,
                saved.Value.IsEnabled,
                saved.Value.RemoteHttpAcknowledged,
                null,
                RemoveCredential: true),
            TestContext.Current.CancellationToken);

        Assert.True(removed.IsSuccess, removed.Error?.Message);
        Assert.Null(removed.Value.CredentialTarget);
        Assert.Equal(saved.Value.CredentialTarget, credentials.DeletedTarget);
    }

    [Fact]
    public async Task HealthCheckForMissingConfigurationReturnsNotFound()
    {
        ProviderApplicationService service = new(
            new FakeConfigurationStore(),
            new RecordingCredentialStore(),
            new FakeDiscovery(),
            [new FakeProvider()],
            new FakeIds());

        AppResult<ProviderHealthDto> result = await service.ExecuteAsync(
            new ProviderConfigurationId(Guid.Parse("0199a59c-7c00-7000-8000-000000000041")),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(AppErrorCode.NotFound, result.Error?.Code);
        Assert.Equal("provider.not_found", result.Error?.Key);
    }

    private sealed class FakeConfigurationStore : IProviderConfigurationStore
    {
        private ProviderConfigurationDto? _configuration;

        public Task<AppResult<ProviderConfigurationDto?>> GetAsync(
            ProviderConfigurationId id,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                AppResult.Success<ProviderConfigurationDto?>(
                    _configuration?.Id == id ? _configuration : null));

        public Task<AppResult<ProviderConfigurationDto?>> GetEnabledAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(
                AppResult.Success<ProviderConfigurationDto?>(
                    _configuration?.IsEnabled == true ? _configuration : null));

        public Task<AppResult<IReadOnlyList<ProviderConfigurationDto>>> ListAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(
                AppResult.Success<IReadOnlyList<ProviderConfigurationDto>>(
                    _configuration is null ? [] : [_configuration]));

        public Task<AppResult<ProviderConfigurationDto>> SaveAsync(
            ProviderConfigurationDto configuration,
            CancellationToken cancellationToken)
        {
            _configuration = configuration;
            return Task.FromResult(AppResult.Success(configuration));
        }
    }

    private sealed class RecordingCredentialStore : ICredentialStore
    {
        public string? SavedCredential { get; private set; }

        public string? DeletedTarget { get; private set; }

        public Task<AppResult> SaveAsync(
            string opaqueTarget,
            string credential,
            CancellationToken cancellationToken)
        {
            SavedCredential = credential;
            return Task.FromResult(AppResult.Success());
        }

        public Task<AppResult<string?>> GetAsync(
            string opaqueTarget,
            CancellationToken cancellationToken) =>
            Task.FromResult(AppResult.Success<string?>(SavedCredential));

        public Task<AppResult> DeleteAsync(
            string opaqueTarget,
            CancellationToken cancellationToken)
        {
            DeletedTarget = opaqueTarget;
            SavedCredential = null;
            return Task.FromResult(AppResult.Success());
        }
    }

    private sealed class FakeDiscovery : IOllamaDiscovery
    {
        public Task<AppResult<ProviderHealthDto>> ProbeLoopbackAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(
                AppResult.Success(new ProviderHealthDto(false, null, [], null)));
    }

    private sealed class FakeProvider : IPromptEnrichmentProvider
    {
        public ProviderKind Kind => ProviderKind.OpenAiCompatible;

        public Task<AppResult<ProviderEnrichmentResponse>> EnrichAsync(
            ProviderConfigurationDto configuration,
            ProviderEnrichmentRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<AppResult<ProviderHealthDto>> CheckHealthAsync(
            ProviderConfigurationDto configuration,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                AppResult.Success(new ProviderHealthDto(true, null, [configuration.Model], null)));
    }

    private sealed class FakeIds : IIdGenerator
    {
        public PromptId NewPromptId() => new(Guid.CreateVersion7());

        public IntentId NewIntentId() => new(Guid.CreateVersion7());

        public IntentAliasId NewIntentAliasId() => new(Guid.CreateVersion7());

        public SkillId NewSkillId() => new(Guid.CreateVersion7());

        public EntityId NewEntityId() => new(Guid.CreateVersion7());

        public ProviderConfigurationId NewProviderConfigurationId() =>
            new(Guid.Parse("0199a59c-7c00-7000-8000-000000000040"));
    }
}
