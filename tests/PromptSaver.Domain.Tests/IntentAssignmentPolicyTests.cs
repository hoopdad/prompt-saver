using PromptSaver.Domain.Entities;
using PromptSaver.Domain.Policies;
using PromptSaver.Domain.ValueObjects;

namespace PromptSaver.Domain.Tests;

public sealed class IntentAssignmentPolicyTests
{
    public static TheoryData<int, IntentDecisionKind, IntentAssignment, IntentReviewState> BoundaryCases =>
        new()
        {
            { 8499, IntentDecisionKind.CreateProvisional, IntentAssignment.Created, IntentReviewState.NeedsReview },
            { 8500, IntentDecisionKind.MapExisting, IntentAssignment.AutoMapped, IntentReviewState.NotRequired },
            { 8501, IntentDecisionKind.MapExisting, IntentAssignment.AutoMapped, IntentReviewState.NotRequired },
        };

    [Theory]
    [MemberData(nameof(BoundaryCases))]
    public void DecideAppliesExactAutoMapBoundary(
        int score,
        IntentDecisionKind expectedKind,
        IntentAssignment expectedAssignment,
        IntentReviewState expectedReviewState)
    {
        IntentCandidateScore candidate = CreateCandidate(score);

        IntentAssignmentDecision decision =
            IntentAssignmentPolicy.Decide(hasValidControlledCandidate: true, candidate);

        Assert.Equal(expectedKind, decision.Kind);
        Assert.Equal(expectedAssignment, decision.Assignment);
        Assert.Equal(expectedReviewState, decision.ReviewState);
        Assert.Equal(candidate, decision.BestCandidate);
    }

    [Fact]
    public void DecideUsesUnsortedWithoutCreatingIntentForLowInformationText()
    {
        IntentAssignmentDecision decision =
            IntentAssignmentPolicy.Decide(hasValidControlledCandidate: false, bestCandidate: null);

        Assert.Equal(IntentDecisionKind.AssignUnsorted, decision.Kind);
        Assert.Equal(IntentAssignment.Unsorted, decision.Assignment);
        Assert.Equal(IntentReviewState.NeedsReview, decision.ReviewState);
        Assert.Equal(Intent.ReservedUnsortedId, decision.IntentId);
    }

    [Fact]
    public void ReviewCandidatesAreRankedCappedAndFiltered()
    {
        IntentCandidateScore[] candidates =
        [
            CreateCandidate(7000, 7),
            CreateCandidate(9500, 2),
            CreateCandidate(5900, 3),
            CreateCandidate(8500, 5),
            CreateCandidate(7000, 1),
            CreateCandidate(6500, 4),
            CreateCandidate(6200, 6),
            CreateCandidate(6100, 8),
        ];

        IReadOnlyList<IntentCandidateScore> reviewSet =
            IntentAssignmentPolicy.CreateReviewSet(candidates);

        Assert.Equal(5, reviewSet.Count);
        Assert.Equal([9500, 8500, 7000, 7000, 6500], reviewSet.Select(candidate => candidate.Score.Value));
        Assert.Equal([1, 2, 3, 4, 5], reviewSet.Select(candidate => candidate.Rank));
        Assert.True(reviewSet[2].IntentId.Value.CompareTo(reviewSet[3].IntentId.Value) < 0);
    }

    private static IntentCandidateScore CreateCandidate(int score, int seed = 1)
    {
        return IntentCandidateScore.Create(
            new IntentId(new Guid(seed, 0, 0, new byte[8])),
            ScoreBasisPoints.Create(score),
            rank: 1,
            "lexical-v1",
            new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
    }
}
