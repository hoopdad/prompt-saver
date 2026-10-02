using System.Net;
using System.Text;
using System.Text.Json;
using PromptSaver.Application;
using PromptSaver.Application.Dtos;
using PromptSaver.Application.Ports;
using PromptSaver.Domain.Entities;
using PromptSaver.Domain.ValueObjects;
using PromptSaver.Infrastructure.Providers;

namespace PromptSaver.Infrastructure.Tests;

public sealed class ProviderAdapterTests
{
    [Fact]
    public async Task OllamaDiscoveryUsesOnlyLoopbackTagsWithoutRedirectsOrUserData()
    {
        RecordingHandler handler = new(
            request =>
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal("http://127.0.0.1:11434/api/tags", request.RequestUri?.AbsoluteUri);
                Assert.Empty(request.Headers);
                return JsonResponse("""{"version":"0.4","models":[{"name":"llama3.2"}]}""");
            });
        OllamaDiscovery discovery = new(handler);

        AppResult<ProviderHealthDto> result =
            await discovery.ProbeLoopbackAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.IsHealthy);
        Assert.Equal(["llama3.2"], result.Value.AvailableModels);
    }

    [Fact]
    public async Task OllamaAdapterSendsCappedPrivatePayloadAndParsesResponse()
    {
        RecordingHandler handler = new(
            request =>
            {
                string json = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                Assert.DoesNotContain("intentCatalog", json, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("existingIntent", json, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("0199a59c", json, StringComparison.OrdinalIgnoreCase);

                using JsonDocument document = JsonDocument.Parse(json);
                Assert.Equal("llama3.2", document.RootElement.GetProperty("model").GetString());
                Assert.False(document.RootElement.GetProperty("stream").GetBoolean());
                JsonElement format = document.RootElement.GetProperty("format");
                Assert.Equal("object", format.GetProperty("type").GetString());
                Assert.False(format.GetProperty("additionalProperties").GetBoolean());
                Assert.Contains(
                    "intent",
                    format.GetProperty("required")
                        .EnumerateArray()
                        .Select(item => item.GetString()));
                string prompt = document.RootElement.GetProperty("prompt").GetString()!;
                using JsonDocument promptDocument = JsonDocument.Parse(prompt);
                string instructions =
                    promptDocument.RootElement.GetProperty("instructions").GetString()!;
                Assert.Contains("one JSON object only", instructions, StringComparison.Ordinal);
                Assert.Contains("Person|Organization|Product", instructions, StringComparison.Ordinal);
                Assert.Contains("0 through 1", instructions, StringComparison.Ordinal);

                return JsonResponse(
                    """
                    {"response":"{\"title\":\"Release notes\",\"intent\":\"Write release notes\",\"skills\":[{\"name\":\"writing\",\"confidence\":0.9}],\"entities\":[{\"name\":\"Azure\",\"type\":\"Technology\",\"confidence\":0.8}]}"}
                    """);
            });
        OllamaEnrichmentProvider provider = new(handler);
        ProviderConfigurationDto configuration = Configuration(
            ProviderKind.Ollama,
            "http://127.0.0.1:11434");
        ProviderEnrichmentRequest request = new(
            new PromptId(Guid.Parse("0199a59c-7c00-7000-8000-000000000001")),
            "Write release notes",
            false,
            "provider-enrichment-v1");

        AppResult<ProviderEnrichmentResponse> result =
            await provider.EnrichAsync(configuration, request, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("Write release notes", result.Value.Intent);
        Assert.Equal("Azure", Assert.Single(result.Value.Entities).Name);
    }

    [Fact]
    public async Task OpenAiAdapterUsesCredentialAbstractionAndBearerHeader()
    {
        ProviderConfigurationDto configuration = Configuration(
            ProviderKind.OpenAiCompatible,
            "https://models.example.com/v1/chat/completions",
            "credential-target");
        FakeCredentialSource credentials = new("secret");
        RecordingHandler handler = new(
            request =>
            {
                Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
                Assert.Equal("secret", request.Headers.Authorization?.Parameter);
                using JsonDocument payload = JsonDocument.Parse(
                    request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                Assert.Equal(
                    "json_object",
                    payload.RootElement
                        .GetProperty("response_format")
                        .GetProperty("type")
                        .GetString());
                return JsonResponse(
                    """
                    {"choices":[{"message":{"content":"{\"title\":\"Title\",\"intent\":\"Analyze logs\",\"skills\":[],\"entities\":[]}"}}]}
                    """);
            });
        OpenAiCompatibleEnrichmentProvider provider = new(handler, credentials);

        AppResult<ProviderEnrichmentResponse> result =
            await provider.EnrichAsync(
                configuration,
                Request("Analyze logs"),
                CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, credentials.CallCount);
    }

    [Fact]
    public void ResponsePolicyAcceptsJsonInsideMarkdownFence()
    {
        AppResult<ProviderEnrichmentResponse> result = ProviderResponsePolicy.Parse(
            """
            Here is the requested metadata:
            ```json
            {"title":"Release notes","intent":"Write release notes","skills":[],"entities":[]}
            ```
            """,
            "Test provider",
            "test-model");

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal("Write release notes", result.Value.Intent);
    }

    [Fact]
    public void ResponsePolicyExtractsTypedJsonObjectFromCommentary()
    {
        AppResult<ProviderEnrichmentResponse> result = ProviderResponsePolicy.Parse(
            """
            I used the requested schema:
            {"title":"Release notes","intent":"Write release notes","skills":[],"entities":[]}
            Let me know if you need anything else.
            """,
            "Test provider",
            "test-model");

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal("Write release notes", result.Value.Intent);
    }

    [Fact]
    public void ResponsePolicyRejectsUnknownContractFieldsAndShowsBoundedResponse()
    {
        string response =
            "{\"title\":\"Release notes\",\"intent\":\"Write release notes\"," +
            "\"skills\":[],\"entities\":[],\"commentary\":\"" +
            new string('x', ProviderResponsePolicy.MaximumDiagnosticCharacters) +
            "\"}";

        AppResult<ProviderEnrichmentResponse> result = ProviderResponsePolicy.Parse(
            response,
            "Test provider",
            "test-model");

        Assert.False(result.IsSuccess);
        Assert.Equal("provider.response.invalid", result.Error?.Key);
        Assert.Contains("Response excerpt:", result.Error?.Message, StringComparison.Ordinal);
        Assert.EndsWith("...", result.Error?.Message, StringComparison.Ordinal);
        Assert.True(
            result.Error!.Message.Length <
            ProviderResponsePolicy.MaximumDiagnosticCharacters + 120);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("```text\n{\"intent\":\"Write release notes\",\"skills\":[],\"entities\":[]}\n```")]
    [InlineData("```json\n{\"intent\":\"Write release notes\",\"skills\":[],\"entities\":[]}")]
    public void ResponsePolicyRejectsAmbiguousOrInvalidContainers(string response)
    {
        AppResult<ProviderEnrichmentResponse> result = ProviderResponsePolicy.Parse(
            response,
            "Test provider",
            "test-model");

        Assert.False(result.IsSuccess);
        Assert.Equal("provider.response.invalid", result.Error?.Key);
    }

    [Fact]
    public async Task AdapterRejectsOversizedOrInvalidResponses()
    {
        string oversized = new('x', ProviderResponsePolicy.MaximumResponseBytes + 1);
        RecordingHandler handler = new(
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(oversized, Encoding.UTF8, "application/json"),
            });
        OllamaEnrichmentProvider provider = new(handler);

        AppResult<ProviderEnrichmentResponse> result =
            await provider.EnrichAsync(
                Configuration(ProviderKind.Ollama, "http://127.0.0.1:11434"),
                Request("Write release notes"),
                CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(AppErrorCode.ProviderRejected, result.Error?.Code);
    }

    [Fact]
    public async Task SchedulerProcessesAtMostTwentyWithOneWorker()
    {
        TrackingProvider provider = new();
        FakeWorkStore workStore = new(25);
        using EnrichmentScheduler scheduler = new(
            workStore,
            new FakePromptSource(),
            new FakeConfigurationStore(Configuration(ProviderKind.Ollama, "http://127.0.0.1:11434")),
            provider,
            new FixedClock());

        AppResult<int> result = await scheduler.ExecuteAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(20, result.Value);
        Assert.Equal(1, provider.MaximumConcurrency);
        Assert.Equal(20, workStore.Proposals.Count);
    }

    [Fact]
    public async Task MultiProviderServiceSharesOneWorkerAndOneAtomicSessionCap()
    {
        TrackingProvider provider = new();
        FakeWorkStore workStore = new(25);
        using MultiProviderEnrichmentService service = new(
            workStore,
            new FakePromptSource(),
            new FakeConfigurationStore(
                Configuration(ProviderKind.Ollama, "http://127.0.0.1:11434")),
            [provider],
            new FixedClock());

        AppResult<int>[] results = await Task.WhenAll(
            Enumerable.Range(0, 5)
                .Select(_ => service.ExecuteAsync(CancellationToken.None)));

        Assert.All(results, result => Assert.True(result.IsSuccess, result.Error?.Message));
        Assert.Equal(20, results.Sum(result => result.Value));
        Assert.Equal(1, provider.MaximumConcurrency);
        Assert.Equal(20, workStore.Proposals.Select(item => item.PromptId).Distinct().Count());
        Assert.Equal(1, workStore.ClaimCalls);
    }

    [Fact]
    public async Task MultiProviderServiceSurfacesProposalPersistenceFailureAndFinalizesClaim()
    {
        TrackingProvider provider = new();
        FakeWorkStore workStore = new(1)
        {
            SaveProposalResult = AppResult.Failure(
                new AppError(
                    AppErrorCode.PersistenceUnavailable,
                    "provider.proposal.save_failed",
                    "Proposal could not be saved.")),
        };
        using MultiProviderEnrichmentService service = new(
            workStore,
            new FakePromptSource(),
            new FakeConfigurationStore(
                Configuration(ProviderKind.Ollama, "http://127.0.0.1:11434")),
            [provider],
            new FixedClock());

        AppResult<int> result = await service.ExecuteAsync(CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("provider.proposal.save_failed", result.Error?.Key);
        Assert.Single(workStore.Failed);
    }

    [Fact]
    public async Task OpenAiHealthCheckUsesCredentialConfiguredEndpointAndCancellation()
    {
        ProviderConfigurationDto configuration = Configuration(
            ProviderKind.OpenAiCompatible,
            "https://models.example.com/v1/chat/completions",
            "credential-target");
        FakeCredentialSource credentials = new("secret");
        RecordingHandler handler = new(
            request =>
            {
                Assert.Equal(configuration.Endpoint, request.RequestUri);
                Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
                Assert.Equal("secret", request.Headers.Authorization?.Parameter);
                return JsonResponse("""{"choices":[{"message":{"content":"OK"}}]}""");
            });
        using OpenAiCompatibleEnrichmentProvider provider = new(handler, credentials);

        AppResult<ProviderHealthDto> healthy =
            await provider.CheckHealthAsync(configuration, CancellationToken.None);

        Assert.True(healthy.IsSuccess, healthy.Error?.Message);

        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        AppResult<ProviderHealthDto> cancellation =
            await provider.CheckHealthAsync(configuration, cancelled.Token);
        Assert.False(cancellation.IsSuccess);
        Assert.Equal(AppErrorCode.Cancelled, cancellation.Error?.Code);
    }

    private static ProviderConfigurationDto Configuration(
        ProviderKind kind,
        string endpoint,
        string? credentialTarget = null) =>
        new(
            new ProviderConfigurationId(Guid.Parse("0199a59c-7c00-7000-8000-000000000010")),
            kind,
            "Provider",
            new Uri(endpoint),
            "llama3.2",
            true,
            false,
            credentialTarget);

    private static ProviderEnrichmentRequest Request(string body) =>
        new(
            new PromptId(Guid.Parse("0199a59c-7c00-7000-8000-000000000001")),
            body,
            false,
            "provider-enrichment-v1");

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }

    private sealed class FakeCredentialSource(string credential) : IProviderCredentialSource
    {
        public int CallCount { get; private set; }

        public Task<AppResult<string?>> GetApiKeyAsync(
            ProviderConfigurationId providerId,
            string opaqueTarget,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(AppResult.Success<string?>(credential));
        }
    }

    private sealed class TrackingProvider : IPromptEnrichmentProvider
    {
        private int _concurrency;

        public ProviderKind Kind => ProviderKind.Ollama;

        public int MaximumConcurrency { get; private set; }

        public async Task<AppResult<ProviderEnrichmentResponse>> EnrichAsync(
            ProviderConfigurationDto configuration,
            ProviderEnrichmentRequest request,
            CancellationToken cancellationToken)
        {
            int concurrency = Interlocked.Increment(ref _concurrency);
            MaximumConcurrency = Math.Max(MaximumConcurrency, concurrency);
            await Task.Yield();
            Interlocked.Decrement(ref _concurrency);
            return AppResult.Success(
                new ProviderEnrichmentResponse(
                    null,
                    "Write release notes",
                    [],
                    [],
                    "Ollama",
                    configuration.Model));
        }

        public Task<AppResult<ProviderHealthDto>> CheckHealthAsync(
            ProviderConfigurationDto configuration,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                AppResult.Success(new ProviderHealthDto(true, null, [], null)));
    }

    private sealed class FakeWorkStore : IEnrichmentWorkStore
    {
        private readonly Queue<PromptId> _pending;
        private readonly object _gate = new();

        public FakeWorkStore(int count)
        {
            _pending = new Queue<PromptId>(Enumerable.Range(1, count)
                .Select(index => new PromptId(new Guid(index, 0, 0, new byte[8])))
                .ToArray());
        }

        public List<MetadataProposalDto> Proposals { get; } = [];

        public List<PromptId> Failed { get; } = [];

        public AppResult SaveProposalResult { get; init; } = AppResult.Success();

        public int ClaimCalls { get; private set; }

        public Task<AppResult<IReadOnlyList<PromptId>>> ClaimPendingAsync(
            int maximumCount,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                ClaimCalls++;
                List<PromptId> claimed = [];
                while (claimed.Count < maximumCount && _pending.TryDequeue(out PromptId id))
                {
                    claimed.Add(id);
                }

                return Task.FromResult(
                    AppResult.Success<IReadOnlyList<PromptId>>(claimed));
            }
        }

        public Task<AppResult> SaveProposalAsync(
            MetadataProposalDto proposal,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (SaveProposalResult.IsSuccess)
                {
                    Proposals.Add(proposal);
                }
            }

            return Task.FromResult(SaveProposalResult);
        }

        public Task<AppResult<MetadataProposalDto?>> GetProposalAsync(
            PromptId promptId,
            CancellationToken cancellationToken) =>
            Task.FromResult(AppResult.Success<MetadataProposalDto?>(null));

        public Task<AppResult> DeleteProposalAsync(
            PromptId promptId,
            CancellationToken cancellationToken) =>
            Task.FromResult(AppResult.Success());

        public Task<AppResult> MarkFailedAsync(
            PromptId promptId,
            AppError appError,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                Failed.Add(promptId);
            }

            return Task.FromResult(AppResult.Success());
        }
    }

    private sealed class FakePromptSource : IEnrichmentPromptSource
    {
        public Task<AppResult<string>> GetBodyAsync(
            PromptId promptId,
            CancellationToken cancellationToken) =>
            Task.FromResult(AppResult.Success("Write release notes"));
    }

    private sealed class FakeConfigurationStore(ProviderConfigurationDto configuration)
        : IProviderConfigurationStore
    {
        public Task<AppResult<ProviderConfigurationDto?>> GetEnabledAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(AppResult.Success<ProviderConfigurationDto?>(configuration));

        public Task<AppResult<ProviderConfigurationDto?>> GetAsync(
            ProviderConfigurationId id,
            CancellationToken cancellationToken) =>
            GetEnabledAsync(cancellationToken);

        public Task<AppResult<IReadOnlyList<ProviderConfigurationDto>>> ListAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(
                AppResult.Success<IReadOnlyList<ProviderConfigurationDto>>([configuration]));

        public Task<AppResult<ProviderConfigurationDto>> SaveAsync(
            ProviderConfigurationDto value,
            CancellationToken cancellationToken) =>
            Task.FromResult(AppResult.Success(value));
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow =>
            new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    }
}
