using PromptSaver.Domain.ValueObjects;

namespace PromptSaver.Domain.Entities;

public sealed record IntentCandidateScore
{
    private IntentCandidateScore(
        IntentId intentId,
        ScoreBasisPoints score,
        int rank,
        string scoringAlgorithmVersion,
        DateTimeOffset createdAtUtc)
    {
        IntentId = intentId;
        Score = score;
        Rank = rank;
        ScoringAlgorithmVersion = scoringAlgorithmVersion;
        CreatedAtUtc = createdAtUtc;
    }

    public IntentId IntentId { get; }

    public ScoreBasisPoints Score { get; }

    public int Rank { get; }

    public string ScoringAlgorithmVersion { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public static IntentCandidateScore Create(
        IntentId intentId,
        ScoreBasisPoints score,
        int rank,
        string scoringAlgorithmVersion,
        DateTimeOffset createdAtUtc)
    {
        if (rank is < 1 or > 5)
        {
            throw new DomainValidationException(
                "intent_candidate.rank.out_of_range",
                "Candidate rank must be between 1 and 5.",
                nameof(rank));
        }

        if (string.IsNullOrWhiteSpace(scoringAlgorithmVersion))
        {
            throw new DomainValidationException(
                "intent_candidate.algorithm.required",
                "Scoring algorithm version is required.",
                nameof(scoringAlgorithmVersion));
        }

        return new IntentCandidateScore(
            intentId,
            score,
            rank,
            scoringAlgorithmVersion,
            createdAtUtc);
    }

    public IntentCandidateScore WithRank(int rank) =>
        Create(IntentId, Score, rank, ScoringAlgorithmVersion, CreatedAtUtc);
}
