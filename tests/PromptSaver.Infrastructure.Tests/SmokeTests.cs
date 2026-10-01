namespace PromptSaver.Infrastructure.Tests;

public sealed class SmokeTests
{
    [Fact]
    [Trait("Category", "Smoke")]
    public void InfrastructureAssemblyLoads()
    {
        Assert.Equal(
            "PromptSaver.Infrastructure",
            typeof(Infrastructure.AssemblyMarker).Assembly.GetName().Name);
    }
}
