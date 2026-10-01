using PromptSaver.Application.Ports;
using PromptSaver.Domain;
using PromptSaver.Domain.Entities;
using PromptSaver.Domain.Policies;
using PromptSaver.Domain.ValueObjects;

namespace PromptSaver.Application.Policies;

public sealed record IntentResolutionResult(
    DeterministicMetadata Metadata,
    IntentAssignmentDecision Decision,
    Intent SelectedIntent,
    Intent? CreatedIntent,
    bool UnarchivedExistingIdentity,
    IReadOnlyList<IntentCandidateScore> ReviewCandidates);

public sealed class IntentResolutionEngine
{
    private readonly IIdGenerator _ids;
    private readonly IClock _clock;

    public IntentResolutionEngine(IIdGenerator ids, IClock clock)
    {
        _ids = ids ?? throw new ArgumentNullException(nameof(ids));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public IntentResolutionResult Resolve(
        string promptBody,
        IReadOnlyList<Intent> activeIntents,
        IReadOnlyList<Intent> allIntents,
        IReadOnlyList<IntentAlias> aliases,
        Intent? exactIdentity = null)
    {
        ArgumentNullException.ThrowIfNull(activeIntents);
        ArgumentNullException.ThrowIfNull(allIntents);
        ArgumentNullException.ThrowIfNull(aliases);

        DeterministicMetadata metadata = DeterministicMetadataExtractor.Extract(promptBody);
        if (!metadata.HasValidIntentCandidate)
        {
            IntentAssignmentDecision unsorted =
                IntentAssignmentPolicy.Decide(false, bestCandidate: null);
            return new IntentResolutionResult(
                metadata,
                unsorted,
                Intent.ReservedUnsorted,
                null,
                false,
                []);
        }

        string candidate = metadata.IntentCandidate!;
        string normalizedCandidate = IntentTextNormalizer.NormalizeKey(candidate);
        Intent? identity = exactIdentity ?? allIntents.FirstOrDefault(
            intent =>
                IntentTextNormalizer.NormalizeKey(intent.CanonicalName) == normalizedCandidate);
        if (identity?.IsArchived == true)
        {
            identity.Unarchive(_clock.UtcNow);
            IntentCandidateScore exact = IntentCandidateScore.Create(
                identity.Id,
                ScoreBasisPoints.Create(10000),
                1,
                IntentLexicalScorer.AlgorithmVersion,
                _clock.UtcNow);
            IntentAssignmentDecision exactDecision =
                IntentAssignmentPolicy.Decide(true, exact);
            return new IntentResolutionResult(
                metadata,
                exactDecision,
                identity,
                null,
                true,
                []);
        }

        IReadOnlyList<IntentCandidateScore> ranked = IntentLexicalScorer.Rank(
            candidate,
            activeIntents,
            aliases,
            _clock.UtcNow);
        IntentCandidateScore? best = ranked.Count == 0 ? null : ranked[0];
        IntentAssignmentDecision decision = IntentAssignmentPolicy.Decide(true, best);
        if (decision.Kind == IntentDecisionKind.MapExisting)
        {
            Intent selected = activeIntents.Single(intent => intent.Id == decision.IntentId);
            return new IntentResolutionResult(
                metadata,
                decision,
                selected,
                null,
                false,
                IntentAssignmentPolicy.CreateReviewSet(ranked));
        }

        Intent provisional = identity ??
            Intent.Create(
                _ids.NewIntentId(),
                candidate,
                IntentSource.Deterministic,
                _clock.UtcNow);
        return new IntentResolutionResult(
            metadata,
            decision,
            provisional,
            identity is null ? provisional : null,
            false,
            IntentAssignmentPolicy.CreateReviewSet(ranked));
    }
}

public static class IntentMutationCoordinator
{
    public static void Reclassify(
        Prompt prompt,
        Intent target,
        IntentAssignment assignment,
        DateTimeOffset updatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(target);
        if (target.IsArchived)
        {
            throw new DomainValidationException(
                "intent.reclassify.target_archived",
                "An archived intent cannot receive a prompt.");
        }

        prompt.ResolveIntent(target.Id, assignment, updatedAtUtc);
    }

    public static int Merge(
        Intent source,
        Intent target,
        IEnumerable<Prompt> sourcePrompts,
        DateTimeOffset updatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(sourcePrompts);
        IntentMergePolicy.Validate(source, target);

        Prompt[] prompts = sourcePrompts.ToArray();
        if (prompts.Any(prompt => prompt.IntentId != source.Id))
        {
            throw new DomainValidationException(
                "intent.merge.prompt_mismatch",
                "Every merged prompt must currently belong to the source intent.");
        }

        foreach (Prompt prompt in prompts)
        {
            prompt.ApplyMergedIntent(target.Id, updatedAtUtc);
        }

        source.ArchiveAsMerged(target.Id, updatedAtUtc);
        return prompts.Length;
    }
}
