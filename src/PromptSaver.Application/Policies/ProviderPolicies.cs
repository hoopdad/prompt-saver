using System.Net;
using System.Text;
using PromptSaver.Application.Dtos;
using PromptSaver.Domain.ValueObjects;

namespace PromptSaver.Application.Policies;

public static class ProviderPayloadPolicy
{
    public const int MaximumPromptBytes = 8192;
    public const string SchemaVersion = "provider-enrichment-v1";

    public static ProviderEnrichmentRequest CreateRequest(PromptId promptId, string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (Encoding.UTF8.GetByteCount(body) <= MaximumPromptBytes)
        {
            return new ProviderEnrichmentRequest(promptId, body, false, SchemaVersion);
        }

        StringBuilder excerpt = new();
        int byteCount = 0;
        foreach (Rune rune in body.EnumerateRunes())
        {
            int runeBytes = rune.Utf8SequenceLength;
            if (byteCount + runeBytes > MaximumPromptBytes)
            {
                break;
            }

            excerpt.Append(rune);
            byteCount += runeBytes;
        }

        string value = excerpt.ToString();
        int paragraphBoundary = value.LastIndexOf('\n');
        if (paragraphBoundary >= MaximumPromptBytes / 4)
        {
            value = value[..paragraphBoundary];
        }

        return new ProviderEnrichmentRequest(promptId, value, true, SchemaVersion);
    }
}

public static class ProviderEndpointPolicy
{
    public static AppResult Validate(Uri endpoint, bool remoteHttpAcknowledged)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri)
        {
            return Failure("provider.endpoint.absolute_required", "Provider endpoint must be absolute.");
        }

        if (endpoint.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return AppResult.Success();
        }

        if (!endpoint.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            return Failure("provider.endpoint.scheme_invalid", "Provider endpoint must use HTTP or HTTPS.");
        }

        bool loopback = IPAddress.TryParse(endpoint.Host, out IPAddress? address) &&
            IPAddress.IsLoopback(address);
        if (loopback || remoteHttpAcknowledged)
        {
            return AppResult.Success();
        }

        return Failure(
            "provider.endpoint.remote_http_confirmation_required",
            "Remote plain HTTP requires explicit acknowledgement.");
    }

    private static AppResult Failure(string key, string message) =>
        AppResult.Failure(new AppError(AppErrorCode.Validation, key, message));
}
