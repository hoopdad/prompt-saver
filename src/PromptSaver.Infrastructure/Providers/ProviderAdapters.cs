using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using PromptSaver.Application;
using PromptSaver.Application.Dtos;
using PromptSaver.Application.Policies;
using PromptSaver.Application.Ports;
using PromptSaver.Application.UseCases;
using PromptSaver.Domain.Entities;

namespace PromptSaver.Infrastructure.Providers;

public sealed class OllamaDiscovery : IOllamaDiscovery, IDisposable
{
    public static readonly Uri TagsEndpoint = new("http://127.0.0.1:11434/api/tags");

    private readonly HttpClient _client;

    public OllamaDiscovery()
        : this(
            new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                ConnectTimeout = TimeSpan.FromMilliseconds(500),
            })
    {
    }

    public OllamaDiscovery(HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    public async Task<AppResult<ProviderHealthDto>> ProbeLoopbackAsync(
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(1));
        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        using HttpRequestMessage request = new(HttpMethod.Get, TagsEndpoint);

        try
        {
            using HttpResponseMessage response = await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                linked.Token).ConfigureAwait(false);
            if (IsRedirect(response.StatusCode) || !response.IsSuccessStatusCode)
            {
                return ProviderErrors.Unavailable<ProviderHealthDto>(
                    "provider.ollama.discovery_failed",
                    "Local Ollama did not accept the discovery request.");
            }

            byte[] body = await ProviderHttp.ReadCappedAsync(
                response.Content,
                ProviderResponsePolicy.MaximumResponseBytes,
                linked.Token).ConfigureAwait(false);
            using JsonDocument document = JsonDocument.Parse(body);
            string[] models = document.RootElement.TryGetProperty("models", out JsonElement modelArray)
                ? modelArray
                    .EnumerateArray()
                    .Select(model => model.TryGetProperty("name", out JsonElement name) ? name.GetString() : null)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Select(name => name!)
                    .Take(100)
                    .ToArray()
                : [];
            string? version = document.RootElement.TryGetProperty("version", out JsonElement versionElement)
                ? versionElement.GetString()
                : null;
            return AppResult.Success(
                new ProviderHealthDto(true, version, models, null));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ProviderErrors.Cancelled<ProviderHealthDto>();
        }
        catch (OperationCanceledException)
        {
            return ProviderErrors.Unavailable<ProviderHealthDto>(
                "provider.ollama.discovery_timeout",
                "Local Ollama discovery timed out.");
        }
        catch (Exception exception) when (
            exception is HttpRequestException or JsonException or ProviderResponseException)
        {
            return ProviderErrors.Unavailable<ProviderHealthDto>(
                "provider.ollama.discovery_failed",
                "Local Ollama discovery failed.");
        }
    }

    public void Dispose() => _client.Dispose();

    private static bool IsRedirect(HttpStatusCode statusCode) =>
        (int)statusCode is >= 300 and <= 399;
}

public sealed class OllamaEnrichmentProvider : IPromptEnrichmentProvider, IDisposable
{
    private readonly ProviderHttp _http;

    public OllamaEnrichmentProvider()
        : this(new SocketsHttpHandler { AllowAutoRedirect = false })
    {
    }

    public OllamaEnrichmentProvider(HttpMessageHandler handler)
    {
        _http = new ProviderHttp(handler);
    }

    public ProviderKind Kind => ProviderKind.Ollama;

    public async Task<AppResult<ProviderEnrichmentResponse>> EnrichAsync(
        ProviderConfigurationDto configuration,
        ProviderEnrichmentRequest request,
        CancellationToken cancellationToken)
    {
        AppResult validation =
            ProviderEndpointPolicy.Validate(configuration.Endpoint, configuration.RemoteHttpAcknowledged);
        if (!validation.IsSuccess)
        {
            return AppResult.Failure<ProviderEnrichmentResponse>(validation.Error!);
        }

        Uri endpoint = new(configuration.Endpoint, "/api/generate");
        object payload = new
        {
            model = configuration.Model,
            stream = false,
            prompt = ProviderPrompt.Create(request),
        };
        AppResult<JsonDocument> response = await _http.PostJsonAsync(
            endpoint,
            payload,
            authorization: null,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            return AppResult.Failure<ProviderEnrichmentResponse>(response.Error!);
        }

        using JsonDocument document = response.Value;
        if (!document.RootElement.TryGetProperty("response", out JsonElement content) ||
            content.ValueKind != JsonValueKind.String)
        {
            return ProviderErrors.Rejected<ProviderEnrichmentResponse>(
                "provider.ollama.response_invalid",
                "Ollama returned an invalid response.");
        }

        return ProviderResponsePolicy.Parse(
            content.GetString()!,
            "Ollama",
            configuration.Model);
    }

    public async Task<AppResult<ProviderHealthDto>> CheckHealthAsync(
        ProviderConfigurationDto configuration,
        CancellationToken cancellationToken)
    {
        AppResult validation =
            ProviderEndpointPolicy.Validate(configuration.Endpoint, configuration.RemoteHttpAcknowledged);
        if (!validation.IsSuccess)
        {
            return AppResult.Failure<ProviderHealthDto>(validation.Error!);
        }

        AppResult<JsonDocument> response = await _http.GetJsonAsync(
            new Uri(configuration.Endpoint, "/api/tags"),
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            return AppResult.Failure<ProviderHealthDto>(response.Error!);
        }

        using JsonDocument document = response.Value;
        string[] models = document.RootElement.TryGetProperty("models", out JsonElement items)
            ? items.EnumerateArray()
                .Select(item => item.TryGetProperty("name", out JsonElement name) ? name.GetString() : null)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .Take(100)
                .ToArray()
            : [];
        return AppResult.Success(new ProviderHealthDto(true, null, models, null));
    }

    public void Dispose() => _http.Dispose();
}

public sealed class OpenAiCompatibleEnrichmentProvider : IPromptEnrichmentProvider, IDisposable
{
    private readonly ProviderHttp _http;
    private readonly IProviderCredentialSource _credentials;

    public OpenAiCompatibleEnrichmentProvider(
        HttpMessageHandler handler,
        IProviderCredentialSource credentials)
    {
        _http = new ProviderHttp(handler);
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
    }

    public ProviderKind Kind => ProviderKind.OpenAiCompatible;

    public async Task<AppResult<ProviderEnrichmentResponse>> EnrichAsync(
        ProviderConfigurationDto configuration,
        ProviderEnrichmentRequest request,
        CancellationToken cancellationToken)
    {
        AppResult validation =
            ProviderEndpointPolicy.Validate(configuration.Endpoint, configuration.RemoteHttpAcknowledged);
        if (!validation.IsSuccess)
        {
            return AppResult.Failure<ProviderEnrichmentResponse>(validation.Error!);
        }

        if (string.IsNullOrWhiteSpace(configuration.CredentialTarget))
        {
            return ProviderErrors.Credential<ProviderEnrichmentResponse>(
                "provider.credential.target_required",
                "The provider credential target is required.");
        }

        AppResult<string?> credential = await _credentials.GetApiKeyAsync(
            configuration.Id,
            configuration.CredentialTarget,
            cancellationToken).ConfigureAwait(false);
        if (!credential.IsSuccess)
        {
            return AppResult.Failure<ProviderEnrichmentResponse>(credential.Error!);
        }

        if (string.IsNullOrWhiteSpace(credential.Value))
        {
            return ProviderErrors.Credential<ProviderEnrichmentResponse>(
                "provider.credential.missing",
                "The provider credential is unavailable.");
        }

        object payload = new
        {
            model = configuration.Model,
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = ProviderPrompt.Create(request),
                },
            },
            temperature = 0,
        };
        AuthenticationHeaderValue authorization = new("Bearer", credential.Value);
        AppResult<JsonDocument> response = await _http.PostJsonAsync(
            configuration.Endpoint,
            payload,
            authorization,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            return AppResult.Failure<ProviderEnrichmentResponse>(response.Error!);
        }

        using JsonDocument document = response.Value;
        if (!TryGetOpenAiContent(document.RootElement, out string? content))
        {
            return ProviderErrors.Rejected<ProviderEnrichmentResponse>(
                "provider.openai.response_invalid",
                "The provider returned an invalid response.");
        }

        return ProviderResponsePolicy.Parse(
            content!,
            configuration.DisplayName,
            configuration.Model);
    }

    public async Task<AppResult<ProviderHealthDto>> CheckHealthAsync(
        ProviderConfigurationDto configuration,
        CancellationToken cancellationToken)
    {
        AppResult validation =
            ProviderEndpointPolicy.Validate(configuration.Endpoint, configuration.RemoteHttpAcknowledged);
        if (!validation.IsSuccess)
        {
            return AppResult.Failure<ProviderHealthDto>(validation.Error!);
        }

        if (string.IsNullOrWhiteSpace(configuration.CredentialTarget))
        {
            return ProviderErrors.Credential<ProviderHealthDto>(
                "provider.credential.target_required",
                "The provider credential target is required.");
        }

        AppResult<string?> credential = await _credentials.GetApiKeyAsync(
            configuration.Id,
            configuration.CredentialTarget,
            cancellationToken).ConfigureAwait(false);
        if (!credential.IsSuccess)
        {
            return AppResult.Failure<ProviderHealthDto>(credential.Error!);
        }

        if (string.IsNullOrWhiteSpace(credential.Value))
        {
            return ProviderErrors.Credential<ProviderHealthDto>(
                "provider.credential.missing",
                "The provider credential is unavailable.");
        }

        object payload = new
        {
            model = configuration.Model,
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = "Reply with OK.",
                },
            },
            max_tokens = 1,
            temperature = 0,
        };
        AppResult<JsonDocument> response = await _http.PostJsonAsync(
            configuration.Endpoint,
            payload,
            new AuthenticationHeaderValue("Bearer", credential.Value),
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            return AppResult.Failure<ProviderHealthDto>(response.Error!);
        }

        response.Value.Dispose();
        return AppResult.Success(
            new ProviderHealthDto(true, null, [configuration.Model], null));
    }

    public void Dispose() => _http.Dispose();

    private static bool TryGetOpenAiContent(JsonElement root, out string? content)
    {
        content = null;
        if (!root.TryGetProperty("choices", out JsonElement choices) ||
            choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() == 0)
        {
            return false;
        }

        JsonElement first = choices[0];
        if (!first.TryGetProperty("message", out JsonElement message) ||
            !message.TryGetProperty("content", out JsonElement contentElement) ||
            contentElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        content = contentElement.GetString();
        return !string.IsNullOrWhiteSpace(content);
    }
}

public static class ProviderResponsePolicy
{
    public const int MaximumResponseBytes = 64 * 1024;
    public const int MaximumSkills = 20;
    public const int MaximumEntities = 30;
    public const int MaximumValueCharacters = 160;

    public static AppResult<ProviderEnrichmentResponse> Parse(
        string json,
        string providerName,
        string model)
    {
        try
        {
            if (Encoding.UTF8.GetByteCount(json) > MaximumResponseBytes)
            {
                return Rejected("provider.response.too_large", "Provider response exceeded 64 KiB.");
            }

            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            string? title = OptionalString(root, "title", 200);
            string? intent = OptionalString(root, "intent", MaximumValueCharacters);
            ProposedSkillDto[] skills = ParseSkills(root);
            ProposedEntityDto[] entities = ParseEntities(root);
            return AppResult.Success(
                new ProviderEnrichmentResponse(
                    title,
                    intent,
                    skills,
                    entities,
                    providerName,
                    model));
        }
        catch (Exception exception) when (
            exception is JsonException or ProviderResponseException)
        {
            return Rejected("provider.response.invalid", "Provider response failed validation.");
        }
    }

    private static ProposedSkillDto[] ParseSkills(JsonElement root)
    {
        if (!root.TryGetProperty("skills", out JsonElement values))
        {
            return [];
        }

        if (values.ValueKind != JsonValueKind.Array ||
            values.GetArrayLength() > MaximumSkills)
        {
            throw new ProviderResponseException();
        }

        return values.EnumerateArray()
            .Select(
                item => new ProposedSkillDto(
                    RequiredString(item, "name"),
                    RequiredConfidence(item)))
            .ToArray();
    }

    private static ProposedEntityDto[] ParseEntities(JsonElement root)
    {
        if (!root.TryGetProperty("entities", out JsonElement values))
        {
            return [];
        }

        if (values.ValueKind != JsonValueKind.Array ||
            values.GetArrayLength() > MaximumEntities)
        {
            throw new ProviderResponseException();
        }

        return values.EnumerateArray()
            .Select(
                item =>
                {
                    string typeValue = RequiredString(item, "type");
                    if (!Enum.TryParse(typeValue, ignoreCase: true, out EntityType type) ||
                        !Enum.IsDefined(type))
                    {
                        throw new ProviderResponseException();
                    }

                    return new ProposedEntityDto(
                        RequiredString(item, "name"),
                        type,
                        RequiredConfidence(item));
                })
            .ToArray();
    }

    private static string? OptionalString(JsonElement root, string name, int maximumCharacters)
    {
        if (!root.TryGetProperty(name, out JsonElement value) ||
            value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new ProviderResponseException();
        }

        string? text = value.GetString();
        if (string.IsNullOrWhiteSpace(text) || text.Length > maximumCharacters)
        {
            throw new ProviderResponseException();
        }

        return text;
    }

    private static string RequiredString(JsonElement root, string name)
    {
        string? value = OptionalString(root, name, MaximumValueCharacters);
        return value ?? throw new ProviderResponseException();
    }

    private static decimal RequiredConfidence(JsonElement root)
    {
        if (!root.TryGetProperty("confidence", out JsonElement value) ||
            !value.TryGetDecimal(out decimal confidence) ||
            confidence is < 0 or > 1)
        {
            throw new ProviderResponseException();
        }

        return confidence;
    }

    private static AppResult<ProviderEnrichmentResponse> Rejected(string key, string message) =>
        ProviderErrors.Rejected<ProviderEnrichmentResponse>(key, message);
}

public sealed class EnrichmentScheduler : IEnrichPendingPrompts, IDisposable
{
    public const int SessionItemLimit = 20;

    private readonly IEnrichmentWorkStore _workStore;
    private readonly IEnrichmentPromptSource _promptSource;
    private readonly IProviderConfigurationStore _configurationStore;
    private readonly IPromptEnrichmentProvider _provider;
    private readonly IClock _clock;
    private readonly SemaphoreSlim _worker = new(1, 1);
    private int _processedThisSession;

    public EnrichmentScheduler(
        IEnrichmentWorkStore workStore,
        IEnrichmentPromptSource promptSource,
        IProviderConfigurationStore configurationStore,
        IPromptEnrichmentProvider provider,
        IClock clock)
    {
        _workStore = workStore ?? throw new ArgumentNullException(nameof(workStore));
        _promptSource = promptSource ?? throw new ArgumentNullException(nameof(promptSource));
        _configurationStore =
            configurationStore ?? throw new ArgumentNullException(nameof(configurationStore));
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<AppResult<int>> ExecuteAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _worker.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return ProviderErrors.Cancelled<int>();
        }

        try
        {
            int remaining = SessionItemLimit - _processedThisSession;
            if (remaining <= 0)
            {
                return AppResult.Success(0);
            }

            AppResult<ProviderConfigurationDto?> configurationResult =
                await _configurationStore.GetEnabledAsync(cancellationToken).ConfigureAwait(false);
            if (!configurationResult.IsSuccess)
            {
                return AppResult.Failure<int>(configurationResult.Error!);
            }

            ProviderConfigurationDto? configuration = configurationResult.Value;
            if (configuration is null || configuration.Kind != _provider.Kind)
            {
                return AppResult.Success(0);
            }

            AppResult<IReadOnlyList<Domain.ValueObjects.PromptId>> pendingResult =
                await _workStore.ClaimPendingAsync(remaining, cancellationToken).ConfigureAwait(false);
            if (!pendingResult.IsSuccess)
            {
                return AppResult.Failure<int>(pendingResult.Error!);
            }

            int completed = 0;
            foreach (Domain.ValueObjects.PromptId promptId in pendingResult.Value.Take(remaining))
            {
                cancellationToken.ThrowIfCancellationRequested();
                AppResult<string> body =
                    await _promptSource.GetBodyAsync(promptId, cancellationToken).ConfigureAwait(false);
                if (!body.IsSuccess)
                {
                    AppResult markedFailed = await _workStore.MarkFailedAsync(
                        promptId,
                        body.Error!,
                        cancellationToken).ConfigureAwait(false);
                    if (!markedFailed.IsSuccess)
                    {
                        return AppResult.Failure<int>(markedFailed.Error!);
                    }

                    continue;
                }

                ProviderEnrichmentRequest request =
                    ProviderPayloadPolicy.CreateRequest(promptId, body.Value);
                AppResult<ProviderEnrichmentResponse> enrichment =
                    await _provider.EnrichAsync(
                        configuration,
                        request,
                        cancellationToken).ConfigureAwait(false);
                _processedThisSession++;
                if (!enrichment.IsSuccess)
                {
                    AppResult markedFailed = await _workStore.MarkFailedAsync(
                        promptId,
                        enrichment.Error!,
                        cancellationToken).ConfigureAwait(false);
                    if (!markedFailed.IsSuccess)
                    {
                        return AppResult.Failure<int>(markedFailed.Error!);
                    }

                    continue;
                }

                ProviderEnrichmentResponse value = enrichment.Value;
                MetadataProposalDto proposal = new(
                    promptId,
                    value.Title,
                    value.Intent,
                    value.Skills,
                    value.Entities,
                    value.ProviderName,
                    value.Model,
                    _clock.UtcNow);
                AppResult saved =
                    await _workStore.SaveProposalAsync(proposal, cancellationToken).ConfigureAwait(false);
                if (!saved.IsSuccess)
                {
                    AppResult markedFailed = await _workStore.MarkFailedAsync(
                        promptId,
                        saved.Error!,
                        cancellationToken).ConfigureAwait(false);
                    return markedFailed.IsSuccess
                        ? AppResult.Failure<int>(saved.Error!)
                        : AppResult.Failure<int>(markedFailed.Error!);
                }

                completed++;
            }

            return AppResult.Success(completed);
        }
        catch (OperationCanceledException)
        {
            return ProviderErrors.Cancelled<int>();
        }
        finally
        {
            _worker.Release();
        }
    }

    public void Dispose() => _worker.Dispose();
}

public sealed class PostRenderOllamaDiscovery
{
    private readonly IBackgroundScheduler _scheduler;
    private readonly IOllamaDiscovery _discovery;

    public PostRenderOllamaDiscovery(
        IBackgroundScheduler scheduler,
        IOllamaDiscovery discovery)
    {
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
    }

    public AppResult ScheduleSuggestion(
        Func<ProviderHealthDto, CancellationToken, Task> showSuggestion)
    {
        ArgumentNullException.ThrowIfNull(showSuggestion);
        return _scheduler.TrySchedule(
            "ollama-loopback-discovery",
            async cancellationToken =>
            {
                AppResult<ProviderHealthDto> result =
                    await _discovery.ProbeLoopbackAsync(cancellationToken).ConfigureAwait(false);
                if (result.IsSuccess && result.Value.IsHealthy)
                {
                    await showSuggestion(result.Value, cancellationToken).ConfigureAwait(false);
                }
            });
    }
}

internal sealed class ProviderHttp : IDisposable
{
    private static readonly TimeSpan TotalTimeout = TimeSpan.FromSeconds(30);
    private readonly HttpClient _client;

    public ProviderHttp(HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    public Task<AppResult<JsonDocument>> GetJsonAsync(
        Uri endpoint,
        CancellationToken cancellationToken) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Get, endpoint), cancellationToken);

    public Task<AppResult<JsonDocument>> PostJsonAsync(
        Uri endpoint,
        object payload,
        AuthenticationHeaderValue? authorization,
        CancellationToken cancellationToken)
    {
        HttpRequestMessage request = new(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Authorization = authorization;
        return SendAsync(request, cancellationToken);
    }

    public void Dispose() => _client.Dispose();

    public static async Task<byte[]> ReadCappedAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        await using Stream stream =
            await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using MemoryStream buffer = new();
        byte[] chunk = new byte[8192];
        while (true)
        {
            int read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return buffer.ToArray();
            }

            if (buffer.Length + read > maximumBytes)
            {
                throw new ProviderResponseException();
            }

            buffer.Write(chunk, 0, read);
        }
    }

    private async Task<AppResult<JsonDocument>> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using (request)
        using (CancellationTokenSource timeout = new(TotalTimeout))
        using (CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token))
        {
            try
            {
                using HttpResponseMessage response = await _client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    linked.Token).ConfigureAwait(false);
                if ((int)response.StatusCode is >= 300 and <= 399)
                {
                    return ProviderErrors.Rejected<JsonDocument>(
                        "provider.http.redirect_rejected",
                        "Provider redirects are not allowed.");
                }

                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    return AppResult.Failure<JsonDocument>(
                        new AppError(
                            AppErrorCode.Unauthorized,
                            "provider.http.unauthorized",
                            "The provider rejected the configured credential."));
                }

                if (!response.IsSuccessStatusCode)
                {
                    return ProviderErrors.Unavailable<JsonDocument>(
                        "provider.http.failed",
                        "The provider request failed.");
                }

                byte[] bytes = await ReadCappedAsync(
                    response.Content,
                    ProviderResponsePolicy.MaximumResponseBytes,
                    linked.Token).ConfigureAwait(false);
                return AppResult.Success(JsonDocument.Parse(bytes));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return ProviderErrors.Cancelled<JsonDocument>();
            }
            catch (OperationCanceledException)
            {
                return ProviderErrors.Unavailable<JsonDocument>(
                    "provider.http.timeout",
                    "The provider request timed out.");
            }
            catch (ProviderResponseException)
            {
                return ProviderErrors.Rejected<JsonDocument>(
                    "provider.response.too_large",
                    "Provider response exceeded 64 KiB.");
            }
            catch (JsonException)
            {
                return ProviderErrors.Rejected<JsonDocument>(
                    "provider.response.invalid_json",
                    "The provider returned invalid JSON.");
            }
            catch (HttpRequestException)
            {
                return ProviderErrors.Unavailable<JsonDocument>(
                    "provider.http.unavailable",
                    "The provider is unavailable.");
            }
        }
    }
}

internal static class ProviderPrompt
{
    public static string Create(ProviderEnrichmentRequest request)
    {
        ProviderEnrichmentRequest capped =
            ProviderPayloadPolicy.CreateRequest(request.PromptId, request.BodyExcerpt);
        object content = new
        {
            schemaVersion = request.SchemaVersion,
            contentTruncated = request.ContentTruncated || capped.ContentTruncated,
            prompt = capped.BodyExcerpt,
            instructions =
                "Return JSON with optional title and intent, skills [{name,confidence}], " +
                "and entities [{name,type,confidence}]. Do not rewrite the prompt.",
        };
        return JsonSerializer.Serialize(content);
    }
}

internal static class ProviderErrors
{
    public static AppResult<T> Unavailable<T>(string key, string message) =>
        AppResult.Failure<T>(
            new AppError(AppErrorCode.ProviderUnavailable, key, message, IsRetryable: true));

    public static AppResult<T> Rejected<T>(string key, string message) =>
        AppResult.Failure<T>(
            new AppError(AppErrorCode.ProviderRejected, key, message));

    public static AppResult<T> Credential<T>(string key, string message) =>
        AppResult.Failure<T>(
            new AppError(AppErrorCode.CredentialUnavailable, key, message));

    public static AppResult<T> Cancelled<T>() =>
        AppResult.Failure<T>(
            new AppError(AppErrorCode.Cancelled, "operation.cancelled", "The operation was cancelled."));
}

internal sealed class ProviderResponseException : Exception;
