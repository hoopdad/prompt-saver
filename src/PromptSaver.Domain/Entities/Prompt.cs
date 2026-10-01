using System.Security.Cryptography;
using System.Text;
using PromptSaver.Domain.ValueObjects;

namespace PromptSaver.Domain.Entities;

public sealed class Prompt
{
    private readonly List<IntentCandidateScore> _intentCandidates = [];
    private readonly List<PromptSkill> _skills = [];
    private readonly List<PromptEntity> _entities = [];

    private Prompt(
        PromptId id,
        PromptBody body,
        PromptTitle? title,
        TitleSource titleSource,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc,
        IntentId intentId,
        IntentAssignment intentAssignment,
        IntentReviewState intentReviewState,
        ScoreBasisPoints? intentScore,
        string? scoringAlgorithmVersion,
        MetadataStatus metadataStatus,
        long version)
    {
        Id = id;
        Body = body;
        Title = title;
        TitleSource = titleSource;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = updatedAtUtc;
        IntentId = intentId;
        IntentAssignment = intentAssignment;
        IntentReviewState = intentReviewState;
        IntentScoreBasisPoints = intentScore;
        ScoringAlgorithmVersion = scoringAlgorithmVersion;
        MetadataStatus = metadataStatus;
        Version = version;
        ContentHash = ComputeHash(body.Value);
    }

    public PromptId Id { get; }

    public PromptBody Body { get; private set; }

    public PromptTitle? Title { get; private set; }

    public TitleSource TitleSource { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public DateTimeOffset? LastCopiedAtUtc { get; private set; }

    public long CopyCount { get; private set; }

    public IntentId IntentId { get; private set; }

    public IntentAssignment IntentAssignment { get; private set; }

    public IntentReviewState IntentReviewState { get; private set; }

    public ScoreBasisPoints? IntentScoreBasisPoints { get; private set; }

    public string? ScoringAlgorithmVersion { get; private set; }

    public MetadataStatus MetadataStatus { get; private set; }

    public byte[] ContentHash { get; private set; }

    public long Version { get; private set; }

    public IReadOnlyList<IntentCandidateScore> IntentCandidates => _intentCandidates;

    public IReadOnlyList<PromptSkill> Skills => _skills;

    public IReadOnlyList<PromptEntity> Entities => _entities;

    public static Prompt Create(
        PromptId id,
        PromptBody body,
        PromptTitle? title,
        TitleSource titleSource,
        DateTimeOffset createdAtUtc,
        IntentId intentId,
        IntentAssignment intentAssignment,
        IntentReviewState intentReviewState,
        ScoreBasisPoints? intentScoreBasisPoints,
        string? scoringAlgorithmVersion,
        MetadataStatus metadataStatus)
    {
        ValidateAssignment(
            intentId,
            intentAssignment,
            intentReviewState,
            intentScoreBasisPoints,
            scoringAlgorithmVersion);

        if (title is null && titleSource == TitleSource.User)
        {
            throw new DomainValidationException(
                "prompt.title.source_invalid",
                "A user title source requires a title.");
        }

        return new Prompt(
            id,
            body,
            title,
            titleSource,
            createdAtUtc,
            createdAtUtc,
            intentId,
            intentAssignment,
            intentReviewState,
            intentScoreBasisPoints,
            scoringAlgorithmVersion,
            metadataStatus,
            0);
    }

    public Prompt Duplicate(PromptId duplicateId, DateTimeOffset createdAtUtc)
    {
        if (duplicateId == Id)
        {
            throw new DomainValidationException(
                "prompt.duplicate.same_id",
                "A duplicate must have a new prompt identifier.");
        }

        Prompt duplicate = Create(
            duplicateId,
            Body,
            Title,
            TitleSource,
            createdAtUtc,
            IntentId,
            IntentAssignment.UserSelected,
            IntentReviewState.Resolved,
            null,
            null,
            MetadataStatus.LocalComplete);

        duplicate._skills.AddRange(
            _skills.Select(skill => skill.ForPrompt(duplicateId)));
        duplicate._entities.AddRange(
            _entities.Select(entity => entity.ForPrompt(duplicateId)));
        return duplicate;
    }

    public void RecordSuccessfulCopy(DateTimeOffset copiedAtUtc)
    {
        checked
        {
            CopyCount++;
        }

        LastCopiedAtUtc = copiedAtUtc;
    }

    public void UpdateContent(
        PromptBody body,
        PromptTitle? title,
        TitleSource titleSource,
        DateTimeOffset updatedAtUtc)
    {
        if (title is null && titleSource == TitleSource.User)
        {
            throw new DomainValidationException(
                "prompt.title.source_invalid",
                "A user title source requires a title.");
        }

        Body = body;
        Title = title;
        TitleSource = titleSource;
        UpdatedAtUtc = updatedAtUtc;
        ContentHash = ComputeHash(body.Value);
        Version++;
    }

    public void SetMetadataStatus(MetadataStatus status)
    {
        MetadataStatus = status;
    }

    public void MarkMetadataEdited(DateTimeOffset updatedAtUtc)
    {
        MetadataStatus = MetadataStatus.LocalComplete;
        UpdatedAtUtc = updatedAtUtc;
        Version++;
    }

    public void SetIntentCandidates(IEnumerable<IntentCandidateScore> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        IntentCandidateScore[] candidateArray = candidates.ToArray();
        if (candidateArray.Length > 5 ||
            candidateArray.Select(candidate => candidate.IntentId).Distinct().Count() != candidateArray.Length ||
            candidateArray.Select(candidate => candidate.Rank).Distinct().Count() != candidateArray.Length)
        {
            throw new DomainValidationException(
                "prompt.intent_candidates.invalid",
                "Intent candidates must contain at most five unique intents and ranks.");
        }

        _intentCandidates.Clear();
        _intentCandidates.AddRange(candidateArray.OrderBy(candidate => candidate.Rank));
    }

    public void ReviewLater()
    {
        if (IntentReviewState is not IntentReviewState.NeedsReview)
        {
            throw new DomainValidationException(
                "prompt.intent.review_not_pending",
                "Only a prompt needing review can be deferred.");
        }

        IntentReviewState = IntentReviewState.Skipped;
        Version++;
    }

    public void ResolveIntent(
        IntentId intentId,
        IntentAssignment assignment,
        DateTimeOffset updatedAtUtc)
    {
        if (assignment is not (IntentAssignment.UserSelected or IntentAssignment.UserCorrected))
        {
            throw new DomainValidationException(
                "prompt.intent.user_assignment_required",
                "Resolving review requires a user-owned assignment.");
        }

        IntentId = intentId;
        IntentAssignment = assignment;
        IntentReviewState = IntentReviewState.Resolved;
        IntentScoreBasisPoints = null;
        ScoringAlgorithmVersion = null;
        UpdatedAtUtc = updatedAtUtc;
        Version++;
    }

    public void ApplyMergedIntent(IntentId targetIntentId, DateTimeOffset updatedAtUtc)
    {
        if (targetIntentId == Intent.ReservedUnsortedId)
        {
            throw new DomainValidationException(
                "prompt.intent.merge_unsorted_forbidden",
                "A merged assignment cannot target the reserved Unsorted intent.");
        }

        IntentId = targetIntentId;
        IntentAssignment = IntentAssignment.Merged;
        IntentReviewState = IntentReviewState.Resolved;
        IntentScoreBasisPoints = null;
        ScoringAlgorithmVersion = null;
        UpdatedAtUtc = updatedAtUtc;
        Version++;
    }

    private static void ValidateAssignment(
        IntentId intentId,
        IntentAssignment assignment,
        IntentReviewState reviewState,
        ScoreBasisPoints? score,
        string? algorithmVersion)
    {
        if (assignment == IntentAssignment.Unsorted && intentId != Intent.ReservedUnsortedId)
        {
            throw new DomainValidationException(
                "prompt.intent.unsorted_id_required",
                "An Unsorted assignment must use the reserved Unsorted intent.");
        }

        if (assignment == IntentAssignment.AutoMapped && score is null)
        {
            throw new DomainValidationException(
                "prompt.intent.score_required",
                "An automatically mapped intent requires a score.");
        }

        if (score is not null && string.IsNullOrWhiteSpace(algorithmVersion))
        {
            throw new DomainValidationException(
                "prompt.intent.algorithm_required",
                "A scored intent decision requires an algorithm version.");
        }

        if (assignment == IntentAssignment.AutoMapped && reviewState != IntentReviewState.NotRequired)
        {
            throw new DomainValidationException(
                "prompt.intent.review_state_invalid",
                "An automatically mapped intent does not require review.");
        }
    }

    private static byte[] ComputeHash(string body)
    {
        string normalized = body.Normalize(NormalizationForm.FormKC);
        return SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
    }
}
