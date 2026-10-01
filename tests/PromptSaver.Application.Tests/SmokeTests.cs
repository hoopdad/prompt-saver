using System.Xml.Linq;

namespace PromptSaver.Application.Tests;

public sealed class SmokeTests
{
    [Fact]
    [Trait("Category", "Smoke")]
    public void ApplicationAssemblyLoads()
    {
        Assert.Equal("PromptSaver.Application", typeof(Application.AssemblyMarker).Assembly.GetName().Name);
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void RuntimeIdentifiersDeclareBothWindowsArchitectures()
    {
        XDocument props = XDocument.Load(Path.Combine(FindRepositoryRoot(), "Directory.Build.props"));
        string runtimeIdentifiers = props.Descendants("RuntimeIdentifiers").Single().Value;

        Assert.Contains("win-x64", runtimeIdentifiers.Split(';'));
        Assert.Contains("win-arm64", runtimeIdentifiers.Split(';'));
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void ProductionProjectReferencesFollowDependencyDirection()
    {
        string root = FindRepositoryRoot();

        AssertProjectReferences(root, "src/PromptSaver.Domain/PromptSaver.Domain.csproj");
        AssertProjectReferences(
            root,
            "src/PromptSaver.Application/PromptSaver.Application.csproj",
            "PromptSaver.Domain");
        AssertProjectReferences(
            root,
            "src/PromptSaver.Infrastructure/PromptSaver.Infrastructure.csproj",
            "PromptSaver.Application",
            "PromptSaver.Domain");
        AssertProjectReferences(
            root,
            "src/PromptSaver.Desktop/PromptSaver.Desktop.csproj",
            "PromptSaver.Application",
            "PromptSaver.Infrastructure");
    }

    private static void AssertProjectReferences(
        string root,
        string relativeProjectPath,
        params string[] expectedProjectNames)
    {
        string projectPath = Path.Combine(root, relativeProjectPath.Replace('/', Path.DirectorySeparatorChar));
        XDocument project = XDocument.Load(projectPath);
        string[] actualProjectNames = project
            .Descendants("ProjectReference")
            .Select(reference => Path.GetFileNameWithoutExtension(reference.Attribute("Include")!.Value))
            .Order()
            .ToArray();

        Assert.Equal(expectedProjectNames.Order(), actualProjectNames);
        Assert.DoesNotContain(actualProjectNames, name => name.EndsWith(".Tests", StringComparison.Ordinal));
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PromptSaver.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the Prompt Saver repository root.");
    }
}
