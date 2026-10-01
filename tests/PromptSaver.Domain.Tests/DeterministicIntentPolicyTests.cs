using PromptSaver.Domain.Entities;
using PromptSaver.Domain.Policies;
using PromptSaver.Domain.ValueObjects;

namespace PromptSaver.Domain.Tests;

public sealed class DeterministicIntentPolicyTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("Draw an Azure architecture diagram for Contoso.", "Draw Azure architecture diagram for a customer")]
    [InlineData("WRITE   a release-note summary", "Write release note summary")]
    [InlineData("Please summarize the quarterly results.", "Summarize quarterly results")]
    [InlineData("Convert this PDF to Markdown.", "Convert PDF to Markdown")]
    public void ExtractsControlledVerbObjectIntent(string body, string expected)
    {
        DeterministicMetadata metadata = DeterministicMetadataExtractor.Extract(body);

        Assert.True(metadata.HasValidIntentCandidate);
        Assert.Equal(expected, metadata.IntentCandidate);
        Assert.False(string.IsNullOrWhiteSpace(metadata.Title));
    }

    [Theory]
    [InlineData("Remember this for later")]
    [InlineData("Customer notes")]
    [InlineData("please help")]
    [InlineData("A thought about architecture")]
    public void LowInformationTextDoesNotCreateIntent(string body)
    {
        DeterministicMetadata metadata = DeterministicMetadataExtractor.Extract(body);

        Assert.False(metadata.HasValidIntentCandidate);
        Assert.Null(metadata.IntentCandidate);
    }

    [Fact]
    public void OneHundredLowInformationPromptsRemainUnsorted()
    {
        int created = Enumerable.Range(1, 100)
            .Select(index => DeterministicMetadataExtractor.Extract($"note {index}"))
            .Count(result => result.HasValidIntentCandidate);

        Assert.Equal(0, created);
    }

    [Fact]
    public void ExtractsStableSkillsEntitiesAndTitle()
    {
        DeterministicMetadata metadata =
            DeterministicMetadataExtractor.Extract(
                "Create a PowerPoint architecture presentation for Contoso using Azure and C#.\r\nMore details.");

        Assert.Equal(
            ["architecture", "presentation", "software development"],
            metadata.Skills.Select(skill => skill.Name));
        Assert.Contains(metadata.Entities, entity => entity.Name == "Contoso");
        Assert.Contains(metadata.Entities, entity => entity.Name == "Azure");
        Assert.Contains(metadata.Entities, entity => entity.Name == "C#");
        Assert.Equal(
            "Create PowerPoint architecture presentation for a customer",
            metadata.Title);
    }

    [Fact]
    public void LongRecoveredPromptCreatesBoundedIntentFromFirstSentence()
    {
        DeterministicMetadata metadata = DeterministicMetadataExtractor.Extract(
            "Create skills and update an automation from my request below. " +
            "Analyze the full request and keep processing all remaining details asynchronously.");

        Assert.Equal(
            "Create skills and update automation from request below",
            metadata.IntentCandidate);
        Assert.InRange(metadata.IntentCandidate!.Length, 3, 160);
    }

    [Fact]
    public void UnpunctuatedIntentCandidateIsTruncatedAtWordBoundary()
    {
        DeterministicMetadata metadata = DeterministicMetadataExtractor.Extract(
            $"Create {string.Join(' ', Enumerable.Repeat("workflow", 50))}");

        Assert.NotNull(metadata.IntentCandidate);
        Assert.InRange(metadata.IntentCandidate.Length, 3, 160);
        Assert.False(metadata.IntentCandidate.EndsWith(' '));
    }

    [Fact]
    public void NormalizationUsesNfkcAndStableWhitespace()
    {
        Assert.Equal(
            "create ppt for customer",
            IntentTextNormalizer.NormalizeKey("  ＣＲＥＡＴＥ\tPPT  for customer. "));
        Assert.Equal(
            "create ppt for customer",
            Intent.Create(
                new IntentId(GuidFromSeed(9)),
                "  ＣＲＥＡＴＥ\tPPT-for customer. ",
                IntentSource.User,
                Now).NormalizedKey);
    }

    [Fact]
    public void ScoresExactCanonicalAndAliasMatches()
    {
        Intent intent = CreateIntent(1, "Create a presentation");
        IntentAlias alias = IntentAlias.Create(
            new IntentAliasId(GuidFromSeed(101)),
            intent.Id,
            "Create a PPT",
            Now);

        IntentCandidateScore canonical =
            Assert.IsType<IntentCandidateScore>(
                IntentLexicalScorer.Score("Create a presentation", intent, [alias]));
        IntentCandidateScore aliasScore =
            Assert.IsType<IntentCandidateScore>(
                IntentLexicalScorer.Score("Create a PPT", intent, [alias]));

        Assert.Equal(10000, canonical.Score.Value);
        Assert.Equal(9900, aliasScore.Score.Value);
    }

    [Fact]
    public void SharedObjectGuardPreventsVerbOnlyFalsePositive()
    {
        Intent intent = CreateIntent(1, "Create PDF");

        IntentCandidateScore? score =
            IntentLexicalScorer.Score("Create PPT", intent, []);

        Assert.Null(score);
    }

    [Fact]
    public void RankingExcludesArchivedAndUsesDeterministicTies()
    {
        Intent first = CreateIntent(1, "Write customer summary");
        Intent second = CreateIntent(2, "Write summary for customer");
        Intent archived = CreateIntent(3, "Write customer summary");
        archived.ArchiveAsMerged(first.Id, Now.AddMinutes(1));

        IReadOnlyList<IntentCandidateScore> ranked = IntentLexicalScorer.Rank(
            "Write a customer summary",
            [second, archived, first],
            [],
            Now);

        Assert.DoesNotContain(ranked, score => score.IntentId == archived.Id);
        Assert.Equal(
            ranked.OrderByDescending(item => item.Score.Value).ThenBy(item => item.IntentId.Value),
            ranked);
    }

    private static Intent CreateIntent(int seed, string name) =>
        Intent.Create(
            new IntentId(GuidFromSeed(seed)),
            name,
            IntentSource.User,
            Now);

    private static Guid GuidFromSeed(int seed) =>
        new(seed, 0, 0, new byte[8]);
}
