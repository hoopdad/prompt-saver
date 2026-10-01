using System.Globalization;
using System.Text.Json;
using PromptSaver.Application;
using PromptSaver.Application.Dtos;
using PromptSaver.Application.Ports;

namespace PromptSaver.Infrastructure.Drafts;

public sealed class CaptureDraftStore : ICaptureDraftStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string _path;

    public CaptureDraftStore(IDataRootResolver dataRootResolver)
    {
        ArgumentNullException.ThrowIfNull(dataRootResolver);
        _path = dataRootResolver.Resolve().CaptureDraft;
    }

    public async Task<AppResult> SaveAsync(
        CaptureDraftDto draft,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);

        try
        {
            CaptureDraftDocument document = new(
                draft.SchemaVersion,
                draft.Body,
                draft.CaretOffset,
                draft.SelectionLength,
                draft.UpdatedAtUtc);
            await AtomicJsonFile.WriteAsync(_path, document, SerializerOptions, cancellationToken);
            return AppResult.Success();
        }
        catch (OperationCanceledException)
        {
            return AppResult.Failure(Cancelled());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return AppResult.Failure(PersistenceFailure("draft.save_failed", exception));
        }
    }

    public async Task<CaptureDraftRecoveryResult> RecoverAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return new CaptureDraftRecoveryResult(DraftRecoveryStatus.Missing, null, null, null);
        }

        try
        {
            CaptureDraftDocument? draft =
                await AtomicJsonFile.ReadAsync<CaptureDraftDocument>(
                    _path,
                    SerializerOptions,
                    cancellationToken);
            if (draft is null || draft.SchemaVersion != CaptureDraftDto.CurrentSchemaVersion)
            {
                throw new JsonException("The capture draft schema is unsupported.");
            }

            CaptureDraftDto validated = CaptureDraftDto.Create(
                draft.Body,
                draft.CaretOffset,
                draft.SelectionLength,
                draft.UpdatedAtUtc);
            return new CaptureDraftRecoveryResult(
                DraftRecoveryStatus.Recovered,
                validated,
                null,
                null);
        }
        catch (OperationCanceledException)
        {
            return new CaptureDraftRecoveryResult(
                DraftRecoveryStatus.Corrupt,
                null,
                null,
                Cancelled());
        }
        catch (Exception exception) when (
            exception is JsonException or InvalidDataException or AppContractException)
        {
            string preservedPath = await PreserveCorruptFileAsync(cancellationToken);
            return new CaptureDraftRecoveryResult(
                DraftRecoveryStatus.Corrupt,
                null,
                preservedPath,
                new AppError(
                    AppErrorCode.DraftCorrupt,
                    "draft.corrupt",
                    "The capture draft is corrupt and was preserved for recovery.",
                    Details: new Dictionary<string, string>
                    {
                        ["path"] = preservedPath,
                        ["reason"] = exception.GetType().Name,
                    }));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new CaptureDraftRecoveryResult(
                DraftRecoveryStatus.Corrupt,
                null,
                null,
                PersistenceFailure("draft.read_failed", exception));
        }
    }

    public Task<AppResult> DeleteAsync(CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Delete(_path);
            return Task.FromResult(AppResult.Success());
        }
        catch (OperationCanceledException)
        {
            return Task.FromResult(AppResult.Failure(Cancelled()));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(
                AppResult.Failure(PersistenceFailure("draft.delete_failed", exception)));
        }
    }

    private async Task<string> PreserveCorruptFileAsync(CancellationToken cancellationToken)
    {
        string preservedPath =
            _path + ".corrupt-" +
            DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
        await using FileStream source = new(
            _path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous);
        await using FileStream destination = new(
            preservedPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await source.CopyToAsync(destination, cancellationToken);
        await destination.FlushAsync(cancellationToken);
        destination.Flush(flushToDisk: true);
        return preservedPath;
    }

    private static AppError PersistenceFailure(string key, Exception exception) =>
        new(
            AppErrorCode.PersistenceUnavailable,
            key,
            "The capture draft could not be accessed.",
            IsRetryable: true,
            Details: new Dictionary<string, string> { ["reason"] = exception.GetType().Name });

    private static AppError Cancelled() =>
        new(AppErrorCode.Cancelled, "operation.cancelled", "The operation was cancelled.");

    private sealed record CaptureDraftDocument(
        int SchemaVersion,
        string Body,
        int CaretOffset,
        int SelectionLength,
        DateTimeOffset UpdatedAtUtc);
}
