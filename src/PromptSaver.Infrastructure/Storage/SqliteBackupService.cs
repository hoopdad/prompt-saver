using System.Globalization;
using Microsoft.Data.Sqlite;
using PromptSaver.Application;
using PromptSaver.Application.Dtos;
using PromptSaver.Application.Ports;

namespace PromptSaver.Infrastructure.Storage;

public sealed class SqliteBackupService : IBackupService
{
    private const int RetainedDailyBackups = 7;
    private readonly IDataRootResolver _dataRootResolver;
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly SqliteMigrationRunner _migrationRunner;

    public SqliteBackupService(
        IDataRootResolver dataRootResolver,
        SqliteConnectionFactory connectionFactory,
        SqliteMigrationRunner migrationRunner)
    {
        _dataRootResolver = dataRootResolver;
        _connectionFactory = connectionFactory;
        _migrationRunner = migrationRunner;
    }

    public async Task<AppResult<BackupInfoDto>> CreateAsync(
        CancellationToken cancellationToken)
    {
        AppResult initialized = await _migrationRunner.InitializeAsync(cancellationToken);
        if (!initialized.IsSuccess)
        {
            return AppResult.Failure<BackupInfoDto>(initialized.Error!);
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        string path = Path.Combine(
            _dataRootResolver.Resolve().Backups,
            $"daily-{now.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture)}.db");
        try
        {
            await using SqliteConnection source =
                await _connectionFactory.OpenAsync(cancellationToken);
            await using SqliteConnection destination =
                new(new SqliteConnectionStringBuilder
                {
                    DataSource = path,
                    Pooling = false,
                }.ToString());
            await destination.OpenAsync(cancellationToken);
            source.BackupDatabase(destination);
            await SqliteIntegrityOperations.CheckAsync(destination, null, cancellationToken);
            await RetainNewestAsync(cancellationToken);

            FileInfo file = new(path);
            return AppResult.Success(new BackupInfoDto(path, now, file.Length));
        }
        catch (Exception exception) when (
            exception is SqliteException or IOException or UnauthorizedAccessException or
                PersistenceIntegrityException)
        {
            return AppResult.Failure<BackupInfoDto>(
                Failure("backup.create_failed", "The backup could not be created.", exception).Error!);
        }
    }

    public async Task<AppResult> RestoreAsync(
        RestoreBackupCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!command.IsConfirmed)
        {
            return AppResult.Failure(
                new AppError(
                    AppErrorCode.Validation,
                    "backup.restore.confirmation_required",
                    "Restoring a backup requires confirmation."));
        }

        string backupPath = Path.GetFullPath(command.BackupPath);
        string backupRoot = Path.GetFullPath(_dataRootResolver.Resolve().Backups);
        if (!backupPath.StartsWith(backupRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(backupPath))
        {
            return AppResult.Failure(
                new AppError(
                    AppErrorCode.Validation,
                    "backup.restore.path_invalid",
                    "The selected backup is not in the Prompt Saver backup directory."));
        }

        string databasePath = _connectionFactory.DatabasePath;
        string stagingPath = databasePath + ".restore-" + Guid.NewGuid().ToString("N");
        string recoveryPath = Path.Combine(
            backupRoot,
            $"pre-restore-{DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture)}.db");

        try
        {
            await using (SqliteConnection source =
                new(new SqliteConnectionStringBuilder
                {
                    DataSource = backupPath,
                    Mode = SqliteOpenMode.ReadWrite,
                    Pooling = false,
                }.ToString()))
            await using (SqliteConnection staging =
                new(new SqliteConnectionStringBuilder
                {
                    DataSource = stagingPath,
                    Pooling = false,
                }.ToString()))
            {
                await source.OpenAsync(cancellationToken);
                await staging.OpenAsync(cancellationToken);
                await SqliteIntegrityOperations.CheckAsync(source, null, cancellationToken);
                source.BackupDatabase(staging);
            }

            SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath))
            {
                File.Copy(databasePath, recoveryPath, overwrite: false);
                File.Replace(stagingPath, databasePath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(stagingPath, databasePath);
            }

            DeleteSidecar(databasePath + "-wal");
            DeleteSidecar(databasePath + "-shm");
            AppResult initialized = await _migrationRunner.InitializeAsync(cancellationToken);
            return initialized;
        }
        catch (Exception exception) when (
            exception is SqliteException or IOException or UnauthorizedAccessException or
                PersistenceIntegrityException)
        {
            return Failure("backup.restore_failed", "The backup could not be restored.", exception);
        }
        finally
        {
            DeleteSidecar(stagingPath);
        }
    }

    public Task<AppResult<IReadOnlyList<BackupInfoDto>>> ListAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<BackupInfoDto> backups =
                new DirectoryInfo(_dataRootResolver.Resolve().Backups)
                    .EnumerateFiles("*.db", SearchOption.TopDirectoryOnly)
                    .OrderByDescending(file => file.CreationTimeUtc)
                    .Select(
                        file => new BackupInfoDto(
                            file.FullName,
                            new DateTimeOffset(file.CreationTimeUtc, TimeSpan.Zero),
                            file.Length))
                    .ToArray();
            return Task.FromResult(AppResult.Success(backups));
        }
        catch (OperationCanceledException)
        {
            return Task.FromResult(
                AppResult.Failure<IReadOnlyList<BackupInfoDto>>(
                    new AppError(
                        AppErrorCode.Cancelled,
                        "operation.cancelled",
                        "The operation was cancelled.")));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(
                AppResult.Failure<IReadOnlyList<BackupInfoDto>>(
                    Failure("backup.list_failed", "Backups could not be listed.", exception).Error!));
        }
    }

    private async Task RetainNewestAsync(CancellationToken cancellationToken)
    {
        FileInfo[] expired =
            new DirectoryInfo(_dataRootResolver.Resolve().Backups)
                .EnumerateFiles("daily-*.db", SearchOption.TopDirectoryOnly)
                .OrderByDescending(file => file.Name, StringComparer.Ordinal)
                .Skip(RetainedDailyBackups)
                .ToArray();
        foreach (FileInfo file in expired)
        {
            cancellationToken.ThrowIfCancellationRequested();
            file.Delete();
        }

        await Task.CompletedTask;
    }

    private static void DeleteSidecar(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static AppResult Failure(string key, string message, Exception exception) =>
        AppResult.Failure(
            new AppError(
                AppErrorCode.PersistenceUnavailable,
                key,
                message,
                IsRetryable: true,
                Details: new Dictionary<string, string>
                {
                    ["reason"] = exception.GetType().Name,
                }));
}
