namespace PromptSaver.Desktop.Tests;

public sealed class SmokeTests
{
    [Fact]
    [Trait("Category", "Smoke")]
    public void DesktopAssemblyLoads()
    {
        Assert.Equal("PromptSaver.Desktop", typeof(Desktop.App).Assembly.GetName().Name);
    }
}
