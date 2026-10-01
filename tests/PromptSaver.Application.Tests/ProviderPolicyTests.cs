using System.Text;
using PromptSaver.Application.Dtos;
using PromptSaver.Application.Policies;
using PromptSaver.Application.Ports;
using PromptSaver.Domain.Entities;
using PromptSaver.Domain.ValueObjects;

namespace PromptSaver.Application.Tests;

public sealed class ProviderPolicyTests
{
    [Fact]
    public void PayloadCutsAtValidUtf8Boundary()
    {
        string body = string.Concat(Enumerable.Repeat("abc😀", 3000));

        ProviderEnrichmentRequest request = ProviderPayloadPolicy.CreateRequest(
            new PromptId(Guid.Parse("0199a59c-7c00-7000-8000-000000000001")),
            body);

        Assert.True(request.ContentTruncated);
        Assert.InRange(Encoding.UTF8.GetByteCount(request.BodyExcerpt), 1, 8192);
        Assert.DoesNotContain('\uFFFD', request.BodyExcerpt);
    }

    [Theory]
    [InlineData("http://127.0.0.1:11434", false, true)]
    [InlineData("http://[::1]:11434", false, true)]
    [InlineData("https://models.example.com", false, true)]
    [InlineData("http://models.example.com", false, false)]
    [InlineData("http://models.example.com", true, true)]
    public void EndpointPolicyEnforcesTransportRules(
        string endpoint,
        bool acknowledged,
        bool expected)
    {
        AppResult result =
            ProviderEndpointPolicy.Validate(new Uri(endpoint), acknowledged);

        Assert.Equal(expected, result.IsSuccess);
    }

    [Fact]
    public void IntentResolutionCreatesOnlyValidProvisionalCandidates()
    {
        IntentResolutionEngine engine = new(new FakeIds(), new FixedClock());

        IntentResolutionResult lowInformation = engine.Resolve("note for later", [], [], []);
        IntentResolutionResult valid = engine.Resolve("Write release notes", [], [], []);

        Assert.Equal(Intent.ReservedUnsortedId, lowInformation.SelectedIntent.Id);
        Assert.Null(lowInformation.CreatedIntent);
        Assert.NotNull(valid.CreatedIntent);
        Assert.Equal(IntentAssignment.Created, valid.Decision.Assignment);
    }

    [Fact]
    public void IntentResolutionAutoMapsExactAndUnarchivesIdentity()
    {
        Intent active = Intent.Create(
            new IntentId(Guid.Parse("0199a59c-7c00-7000-8000-000000000010")),
            "Write release notes",
            IntentSource.User,
            FixedClock.Now);
        Intent archived = Intent.Create(
            new IntentId(Guid.Parse("0199a59c-7c00-7000-8000-000000000011")),
            "Draw architecture diagram",
            IntentSource.User,
            FixedClock.Now);
        archived.ArchiveAsMerged(active.Id, FixedClock.Now.AddMinutes(1));
        IntentResolutionEngine engine = new(new FakeIds(), new FixedClock());

        IntentResolutionResult exact =
            engine.Resolve("Write release notes", [active], [active, archived], []);
        IntentResolutionResult revived =
            engine.Resolve("Draw architecture diagram", [active], [active, archived], []);

        Assert.Equal(IntentAssignment.AutoMapped, exact.Decision.Assignment);
        Assert.Equal(active.Id, exact.SelectedIntent.Id);
        Assert.True(revived.UnarchivedExistingIdentity);
        Assert.False(archived.IsArchived);
        Assert.Null(revived.CreatedIntent);
    }

    [Fact]
    public void IntentResolutionRevivesArchivedExactIdentityBeforeScoringActiveCollision()
    {
        Intent active = Intent.Create(
            new IntentId(Guid.Parse("0199a59c-7c00-7000-8000-000000000012")),
            "Draw architecture diagrams",
            IntentSource.User,
            FixedClock.Now);
        Intent archived = Intent.Create(
            new IntentId(Guid.Parse("0199a59c-7c00-7000-8000-000000000013")),
            "Draw architecture diagram",
            IntentSource.User,
            FixedClock.Now);
        archived.ArchiveAsMerged(active.Id, FixedClock.Now.AddMinutes(1));
        IntentResolutionEngine engine = new(new FakeIds(), new FixedClock());

        IntentResolutionResult result =
            engine.Resolve("Draw architecture diagram", [active], [active, archived], []);

        Assert.Equal(archived.Id, result.SelectedIntent.Id);
        Assert.True(result.UnarchivedExistingIdentity);
        Assert.False(archived.IsArchived);
        Assert.Null(result.CreatedIntent);
    }

    private sealed class FakeIds : IIdGenerator
    {
        public PromptId NewPromptId() => new(Guid.NewGuid());

        public IntentId NewIntentId() =>
            new(Guid.Parse("0199a59c-7c00-7000-8000-000000000099"));

        public IntentAliasId NewIntentAliasId() => new(Guid.NewGuid());

        public SkillId NewSkillId() => new(Guid.NewGuid());

        public EntityId NewEntityId() => new(Guid.NewGuid());

        public ProviderConfigurationId NewProviderConfigurationId() => new(Guid.NewGuid());
    }

    private sealed class FixedClock : IClock
    {
        public static DateTimeOffset Now =>
            new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

        public DateTimeOffset UtcNow => Now;
    }
}
