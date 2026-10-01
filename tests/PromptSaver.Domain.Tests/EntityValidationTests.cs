using PromptSaver.Domain.Entities;
using PromptSaver.Domain.ValueObjects;

namespace PromptSaver.Domain.Tests;

public sealed class EntityValidationTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void IntentConstructionNormalizesKeyWithoutChangingCanonicalName()
    {
        Intent intent = Intent.Create(
            new IntentId(Guid.Parse("0199a59c-7c00-7000-8000-000000000101")),
            "Write Café Summary",
            IntentSource.User,
            Now);

        Assert.Equal("Write Café Summary", intent.CanonicalName);
        Assert.Equal("write café summary", intent.NormalizedKey);
    }

    [Fact]
    public void AliasRequiresAValidDisplayValue()
    {
        DomainValidationException exception =
            Assert.Throws<DomainValidationException>(
                () => IntentAlias.Create(
                    new IntentAliasId(Guid.Parse("0199a59c-7c00-7000-8000-000000000102")),
                    new IntentId(Guid.Parse("0199a59c-7c00-7000-8000-000000000103")),
                    " ",
                    Now));

        Assert.Equal("intent_alias.value.required", exception.Code);
    }

    [Fact]
    public void CandidateScoreRejectsInvalidRankAndAlgorithmVersion()
    {
        IntentId intentId =
            new(Guid.Parse("0199a59c-7c00-7000-8000-000000000104"));

        Assert.Throws<DomainValidationException>(
            () => IntentCandidateScore.Create(
                intentId,
                ScoreBasisPoints.Create(7000),
                0,
                "lexical-v1",
                Now));
        Assert.Throws<DomainValidationException>(
            () => IntentCandidateScore.Create(
                intentId,
                ScoreBasisPoints.Create(7000),
                1,
                " ",
                Now));
    }

    [Fact]
    public void MetadataRelationRequiresExtractorVersion()
    {
        DomainValidationException exception =
            Assert.Throws<DomainValidationException>(
                () => PromptSkill.Create(
                    new PromptId(Guid.Parse("0199a59c-7c00-7000-8000-000000000105")),
                    new SkillId(Guid.Parse("0199a59c-7c00-7000-8000-000000000106")),
                    MetadataSource.Deterministic,
                    Confidence.Create(0.9m),
                    " "));

        Assert.Equal("metadata.extractor_version.required", exception.Code);
    }
}
