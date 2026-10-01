using PromptSaver.Application.Ports;

namespace PromptSaver.Infrastructure.Storage;

public sealed class DataRootResolver : IDataRootResolver
{
    private readonly string? _rootOverride;

    public DataRootResolver(string? rootOverride = null)
    {
        _rootOverride = rootOverride;
    }

    public DataRootPaths Resolve()
    {
        string root = _rootOverride ??
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PromptSaver");
        root = Path.GetFullPath(root);

        Directory.CreateDirectory(root);
        string backups = Directory.CreateDirectory(Path.Combine(root, "Backups")).FullName;
        string logs = Directory.CreateDirectory(Path.Combine(root, "Logs")).FullName;

        return new DataRootPaths(
            root,
            Path.Combine(root, "prompts.db"),
            Path.Combine(root, "capture-draft.json"),
            Path.Combine(root, "config.json"),
            backups,
            logs);
    }
}
