namespace PromptSaver.Domain.Tests;

public sealed class SmokeTests
{
    [Fact]
    [Trait("Category", "Smoke")]
    public void DomainAssemblyLoads()
    {
        Assert.Equal("PromptSaver.Domain", typeof(Domain.AssemblyMarker).Assembly.GetName().Name);
    }
}
