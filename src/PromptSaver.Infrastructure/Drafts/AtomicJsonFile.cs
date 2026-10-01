using System.Text.Json;

namespace PromptSaver.Infrastructure.Drafts;

internal static class AtomicJsonFile
{
    internal const int MaximumBootstrapFileBytes = 1024 * 1024;

    internal static async Task WriteAsync<T>(
        string path,
        T value,
        JsonSerializerOptions options,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            await using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, value, options, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
            {
                File.Replace(temporaryPath, path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporaryPath, path);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    internal static async Task<T?> ReadAsync<T>(
        string path,
        JsonSerializerOptions options,
        CancellationToken cancellationToken)
    {
        FileInfo file = new(path);
        if (file.Length > MaximumBootstrapFileBytes)
        {
            throw new InvalidDataException(
                $"Bootstrap file exceeds {MaximumBootstrapFileBytes} bytes.");
        }

        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<T>(stream, options, cancellationToken);
    }
}
