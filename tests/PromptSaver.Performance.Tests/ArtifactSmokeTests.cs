namespace PromptSaver.Performance.Tests;

public sealed class ArtifactSmokeTests
{
    [Fact(Skip = "Requires a published desktop artifact on dedicated hardware.")]
    [Trait("Category", "Artifact")]
    public void StartupBaselineIsWithinBudget()
    {
    }
}
