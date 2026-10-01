using PromptSaver.Domain.Entities;
using PromptSaver.Domain.Policies;
using PromptSaver.Domain.ValueObjects;

namespace PromptSaver.Domain.Tests;

public sealed class DomainInvariantTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void DuplicateCopiesAcceptedContentButClearsTransientState()
    {
        Prompt original = Prompt.Create(
            new PromptId(Guid.Parse("0199a59c-7c00-7000-8000-000000000001")),
            PromptBody.Create("  Preserve this exact prompt.\r\n"),
            PromptTitle.Create("User title"),
            TitleSource.User,
            Now,
            new IntentId(Guid.Parse("0199a59c-7c00-7000-8000-000000000002")),
            IntentAssignment.AutoMapped,
            IntentReviewState.NotRequired,
            ScoreBasisPoints.Create(9000),
            "lexical-v1",
            MetadataStatus.ProposalReady);
        original.RecordSuccessfulCopy(Now.AddMinutes(1));
        original.SetIntentCandidates([CreateCandidate(7000)]);

        Prompt duplicate = original.Duplicate(
            new PromptId(Guid.Parse("0199a59c-7c00-7000-8000-000000000003")),
            Now.AddMinutes(2));

        Assert.Equal(original.Body, duplicate.Body);
        Assert.Equal(original.Title, duplicate.Title);
        Assert.Equal(original.IntentId, duplicate.IntentId);
        Assert.Equal(IntentAssignment.UserSelected, duplicate.IntentAssignment);
        Assert.Equal(IntentReviewState.Resolved, duplicate.IntentReviewState);
        Assert.Equal(0, duplicate.CopyCount);
        Assert.Null(duplicate.LastCopiedAtUtc);
        Assert.Empty(duplicate.IntentCandidates);
        Assert.Equal(MetadataStatus.LocalComplete, duplicate.MetadataStatus);
        Assert.NotEqual(original.Id, duplicate.Id);
    }

    [Fact]
    public void PermanentDeleteRequiresExplicitConfirmation()
    {
        DomainValidationException exception =
            Assert.Throws<DomainValidationException>(
                () => PromptDeletionPolicy.EnsurePermanentDeletionConfirmed(isConfirmed: false));

        Assert.Equal("prompt.delete.confirmation_required", exception.Code);
    }

    [Fact]
    public void MergeRejectsReservedUnsortedAsSourceOrTarget()
    {
        Intent userIntent = CreateIntent(
            new IntentId(Guid.Parse("0199a59c-7c00-7000-8000-000000000010")),
            "Write release notes");

        Assert.Throws<DomainValidationException>(
            () => IntentMergePolicy.Validate(Intent.ReservedUnsorted, userIntent));
        Assert.Throws<DomainValidationException>(
            () => IntentMergePolicy.Validate(userIntent, Intent.ReservedUnsorted));
    }

    [Fact]
    public void MergeRejectsSameIntent()
    {
        Intent intent = CreateIntent(
            new IntentId(Guid.Parse("0199a59c-7c00-7000-8000-000000000011")),
            "Write release notes");

        DomainValidationException exception =
            Assert.Throws<DomainValidationException>(() => IntentMergePolicy.Validate(intent, intent));

        Assert.Equal("intent.merge.same_intent", exception.Code);
    }

    [Theory]
    [InlineData(IntentAssignment.UserSelected)]
    [InlineData(IntentAssignment.UserCorrected)]
    [InlineData(IntentAssignment.Merged)]
    public void BackgroundEnrichmentCannotOverwriteUserOwnedAssignment(IntentAssignment assignment)
    {
        Assert.False(IntentAssignmentPolicy.CanBackgroundReplace(assignment));
    }

    private static Intent CreateIntent(IntentId id, string name)
    {
        return Intent.Create(
            id,
            name,
            IntentSource.User,
            Now);
    }

    private static IntentCandidateScore CreateCandidate(int score)
    {
        return IntentCandidateScore.Create(
            new IntentId(Guid.Parse("0199a59c-7c00-7000-8000-000000000020")),
            ScoreBasisPoints.Create(score),
            1,
            "lexical-v1",
            Now);
    }
}
