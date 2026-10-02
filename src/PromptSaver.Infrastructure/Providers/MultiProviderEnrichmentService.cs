using PromptSaver.Application;
using PromptSaver.Application.Dtos;
using PromptSaver.Application.Policies;
using PromptSaver.Application.Ports;
using PromptSaver.Application.UseCases;

namespace PromptSaver.Infrastructure.Providers;

public sealed class MultiProviderEnrichmentService :
    IEnrichPendingPrompts,
    IQueryPromptIntent,
    IDisposable
{
    private readonly SemaphoreSlim _worker = new(1, 1);
    private readonly IEnrichmentWorkStore _work;
    private readonly IEnrichmentPromptSource _prompts;
    private readonly IProviderConfigurationStore _configurations;
    private readonly Dictionary<ProviderKind, IPromptEnrichmentProvider> _providers;
    private readonly IClock _clock;
    private int _processedThisSession;

    public MultiProviderEnrichmentService(
        IEnrichmentWorkStore work,
        IEnrichmentPromptSource prompts,
        IProviderConfigurationStore configurations,
        IEnumerable<IPromptEnrichmentProvider> providers,
        IClock clock)
    {
        _work = work;
        _prompts = prompts;
        _configurations = configurations;
        _providers = providers.ToDictionary(provider => provider.Kind);
        _clock = clock;
    }

    public async Task<AppResult<int>> ExecuteAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _worker.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return ProviderErrors.Cancelled<int>();
        }

        try
        {
            int remaining = EnrichmentScheduler.SessionItemLimit - _processedThisSession;
            if (remaining <= 0)
            {
                return AppResult.Success(0);
            }

            AppResult<ProviderConfigurationDto?> configured =
                await _configurations.GetEnabledAsync(cancellationToken);
            if (!configured.IsSuccess)
            {
                return AppResult.Failure<int>(configured.Error!);
            }

            if (configured.Value is null ||
                !_providers.TryGetValue(
                    configured.Value.Kind,
                    out IPromptEnrichmentProvider? provider))
            {
                return AppResult.Success(0);
            }

            AppResult<IReadOnlyList<Domain.ValueObjects.PromptId>> pending =
                await _work.ClaimPendingAsync(remaining, cancellationToken);
            if (!pending.IsSuccess)
            {
                return AppResult.Failure<int>(pending.Error!);
            }

            int completed = 0;
            foreach (Domain.ValueObjects.PromptId promptId in pending.Value)
            {
                _processedThisSession++;
                AppResult<string> body = await _prompts.GetBodyAsync(promptId, cancellationToken);
                if (!body.IsSuccess)
                {
                    AppResult markedFailed =
                        await _work.MarkFailedAsync(promptId, body.Error!, cancellationToken);
                    if (!markedFailed.IsSuccess)
                    {
                        return AppResult.Failure<int>(markedFailed.Error!);
                    }

                    continue;
                }

                AppResult<ProviderEnrichmentResponse> enriched = await provider.EnrichAsync(
                    configured.Value,
                    ProviderPayloadPolicy.CreateRequest(promptId, body.Value),
                    cancellationToken);
                if (!enriched.IsSuccess)
                {
                    AppResult markedFailed =
                        await _work.MarkFailedAsync(promptId, enriched.Error!, cancellationToken);
                    if (!markedFailed.IsSuccess)
                    {
                        return AppResult.Failure<int>(markedFailed.Error!);
                    }

                    continue;
                }

                ProviderEnrichmentResponse response = enriched.Value;
                AppResult saved = await _work.SaveProposalAsync(
                    new MetadataProposalDto(
                        promptId,
                        response.Title,
                        response.Intent,
                        response.Skills,
                        response.Entities,
                        response.ProviderName,
                        response.Model,
                        _clock.UtcNow),
                    cancellationToken);
                if (!saved.IsSuccess)
                {
                    AppResult markedFailed =
                        await _work.MarkFailedAsync(promptId, saved.Error!, cancellationToken);
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

    public async Task<AppResult<PromptIntentSuggestionDto>> ExecuteAsync(
        QueryPromptIntentCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            AppResult<ProviderConfigurationDto?> configured =
                await _configurations.GetEnabledAsync(cancellationToken);
            if (!configured.IsSuccess)
            {
                return AppResult.Failure<PromptIntentSuggestionDto>(configured.Error!);
            }

            if (configured.Value is null ||
                !_providers.TryGetValue(
                    configured.Value.Kind,
                    out IPromptEnrichmentProvider? provider))
            {
                return AppResult.Failure<PromptIntentSuggestionDto>(
                    new AppError(
                        AppErrorCode.ProviderUnavailable,
                        "provider.intent.unavailable",
                        "Enable an LLM provider in Settings before asking for an intent.",
                        true));
            }

            AppResult<string> body =
                await _prompts.GetBodyAsync(command.PromptId, cancellationToken);
            if (!body.IsSuccess)
            {
                return AppResult.Failure<PromptIntentSuggestionDto>(body.Error!);
            }

            AppResult<ProviderEnrichmentResponse> enriched = await provider.EnrichAsync(
                configured.Value,
                ProviderPayloadPolicy.CreateRequest(command.PromptId, body.Value),
                cancellationToken);
            if (!enriched.IsSuccess)
            {
                return AppResult.Failure<PromptIntentSuggestionDto>(enriched.Error!);
            }

            if (string.IsNullOrWhiteSpace(enriched.Value.Intent))
            {
                return AppResult.Failure<PromptIntentSuggestionDto>(
                    new AppError(
                        AppErrorCode.ProviderRejected,
                        "provider.intent.missing",
                        "The LLM did not return an intent. Try again or review the provider model.",
                        true));
            }

            return AppResult.Success(
                new PromptIntentSuggestionDto(
                    enriched.Value.Intent.Trim(),
                    enriched.Value.ProviderName,
                    enriched.Value.Model));
        }
        catch (OperationCanceledException)
        {
            return ProviderErrors.Cancelled<PromptIntentSuggestionDto>();
        }
    }

    public void Dispose() => _worker.Dispose();
}
