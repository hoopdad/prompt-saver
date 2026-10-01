using PromptSaver.Domain.Entities;
using PromptSaver.Domain.ValueObjects;

namespace PromptSaver.Domain.Policies;

public enum IntentDecisionKind
{
    AssignUnsorted,
    CreateProvisional,
    MapExisting,
}

public sealed record IntentAssignmentDecision(
    IntentDecisionKind Kind,
    IntentId? IntentId,
    IntentAssignment Assignment,
    IntentReviewState ReviewState,
    IntentCandidateScore? BestCandidate);

public static class IntentAssignmentPolicy
{
    public const int AutoMapThresholdBasisPoints = 8500;
    public const int ReviewCandidateMinimumBasisPoints = 6000;
    public const int MaximumReviewCandidates = 5;

    public static IntentAssignmentDecision Decide(
        bool hasValidControlledCandidate,
        IntentCandidateScore? bestCandidate)
    {
        if (!hasValidControlledCandidate)
        {
            return new IntentAssignmentDecision(
                IntentDecisionKind.AssignUnsorted,
                Intent.ReservedUnsortedId,
                IntentAssignment.Unsorted,
                IntentReviewState.NeedsReview,
                bestCandidate);
        }

        if (bestCandidate is not null &&
            bestCandidate.Score.Value >= AutoMapThresholdBasisPoints)
        {
            return new IntentAssignmentDecision(
                IntentDecisionKind.MapExisting,
                bestCandidate.IntentId,
                IntentAssignment.AutoMapped,
                IntentReviewState.NotRequired,
                bestCandidate);
        }

        return new IntentAssignmentDecision(
            IntentDecisionKind.CreateProvisional,
            null,
            IntentAssignment.Created,
            IntentReviewState.NeedsReview,
            bestCandidate);
    }

    public static IReadOnlyList<IntentCandidateScore> CreateReviewSet(
        IEnumerable<IntentCandidateScore> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        return candidates
            .Where(candidate => candidate.Score.Value >= ReviewCandidateMinimumBasisPoints)
            .OrderByDescending(candidate => candidate.Score.Value)
            .ThenBy(candidate => candidate.IntentId.Value)
            .Take(MaximumReviewCandidates)
            .Select((candidate, index) => candidate.WithRank(index + 1))
            .ToArray();
    }

    public static bool CanBackgroundReplace(IntentAssignment assignment) =>
        assignment is IntentAssignment.Unsorted
            or IntentAssignment.Deterministic
            or IntentAssignment.AutoMapped
            or IntentAssignment.Created;
}
