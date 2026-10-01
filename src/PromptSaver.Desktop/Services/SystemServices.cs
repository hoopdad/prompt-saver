using System.Runtime.InteropServices;
using System.Globalization;
using System.Windows;
using PromptSaver.Application;
using PromptSaver.Application.Ports;
using PromptSaver.Domain.ValueObjects;

namespace PromptSaver.Desktop.Services;

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public sealed class SystemIdGenerator : IIdGenerator
{
    public PromptId NewPromptId() => new(Guid.CreateVersion7());

    public IntentId NewIntentId() => new(Guid.CreateVersion7());

    public IntentAliasId NewIntentAliasId() => new(Guid.CreateVersion7());

    public SkillId NewSkillId() => new(Guid.CreateVersion7());

    public EntityId NewEntityId() => new(Guid.CreateVersion7());

    public ProviderConfigurationId NewProviderConfigurationId() => new(Guid.CreateVersion7());
}

public sealed class WindowsClipboard : IClipboard
{
    public Task<AppResult> SetTextAsync(string text, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            Clipboard.SetText(text);
            return Task.FromResult(AppResult.Success());
        }
        catch (Exception exception) when (
            exception is COMException or InvalidOperationException)
        {
            return Task.FromResult(
                AppResult.Failure(
                    new AppError(
                        AppErrorCode.ClipboardUnavailable,
                        "clipboard.unavailable",
                        "The Windows clipboard is temporarily unavailable.",
                        true)));
        }
    }
}

public sealed class BackgroundScheduler : IBackgroundScheduler, IDisposable
{
    private readonly CancellationTokenSource _shutdown = new();

    public AppResult TrySchedule(
        string operationName,
        Func<CancellationToken, Task> operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        ArgumentNullException.ThrowIfNull(operation);
        try
        {
            _ = RunAsync(operation);
            return AppResult.Success();
        }
        catch (Exception exception)
        {
            return AppResult.Failure(
                new AppError(
                    AppErrorCode.Unexpected,
                    "background.schedule_failed",
                    $"The background operation '{operationName}' could not be scheduled.",
                    true,
                    new Dictionary<string, string>
                    {
                        ["reason"] = exception.GetType().Name,
                    }));
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _shutdown.Dispose();
    }

    private async Task RunAsync(Func<CancellationToken, Task> operation)
    {
        try
        {
            await operation(_shutdown.Token);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
    }
}

public sealed class WindowsCredentialStore : ICredentialStore
{
    public Task<AppResult> SaveAsync(
        string opaqueTarget,
        string credential,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CredentialAllocation allocation = AllocateCredential(credential);
        try
        {
            NativeCredential native = new()
            {
                Type = CredentialType.Generic,
                TargetName = opaqueTarget,
                CredentialBlobSize = checked((uint)allocation.ContentByteCount),
                CredentialBlob = allocation.Pointer,
                Persist = CredentialPersist.LocalMachine,
                UserName = Environment.UserName,
            };
            return Task.FromResult(
                CredWrite(ref native, 0)
                    ? AppResult.Success()
                    : Failure("credential.save_failed"));
        }
        finally
        {
            FreeCredential(allocation);
        }
    }

    public Task<AppResult<string?>> GetAsync(
        string opaqueTarget,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CredRead(opaqueTarget, CredentialType.Generic, 0, out IntPtr pointer))
        {
            int error = Marshal.GetLastWin32Error();
            return Task.FromResult(
                error == 1168
                    ? AppResult.Success<string?>(null)
                    : AppResult.Failure<string?>(Error("credential.read_failed", error)));
        }

        try
        {
            NativeCredential credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            string value = Marshal.PtrToStringUni(
                credential.CredentialBlob,
                checked((int)credential.CredentialBlobSize / 2)) ?? string.Empty;
            return Task.FromResult(AppResult.Success<string?>(value));
        }
        finally
        {
            CredFree(pointer);
        }
    }

    public Task<AppResult> DeleteAsync(
        string opaqueTarget,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (CredDelete(opaqueTarget, CredentialType.Generic, 0))
        {
            return Task.FromResult(AppResult.Success());
        }

        int error = Marshal.GetLastWin32Error();
        return Task.FromResult(error == 1168 ? AppResult.Success() : Failure("credential.delete_failed"));
    }

    private static AppResult Failure(string key) =>
        AppResult.Failure(Error(key, Marshal.GetLastWin32Error()));

    private static AppError Error(string key, int error) =>
        new(
            AppErrorCode.CredentialUnavailable,
            key,
            "Windows Credential Manager is unavailable.",
            true,
            new Dictionary<string, string>
            {
                ["win32Error"] = error.ToString(CultureInfo.InvariantCulture),
            });

    internal static CredentialAllocation AllocateCredential(string credential)
    {
        byte[] content = System.Text.Encoding.Unicode.GetBytes(credential);
        int allocationByteCount = checked(content.Length + sizeof(char));
        IntPtr pointer = Marshal.AllocCoTaskMem(allocationByteCount);
        try
        {
            Marshal.Copy(new byte[allocationByteCount], 0, pointer, allocationByteCount);
            Marshal.Copy(content, 0, pointer, content.Length);
            return new CredentialAllocation(pointer, content.Length, allocationByteCount);
        }
        catch
        {
            Marshal.FreeCoTaskMem(pointer);
            throw;
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(content);
        }
    }

    internal static void FreeCredential(CredentialAllocation allocation)
    {
        if (allocation.Pointer == IntPtr.Zero)
        {
            return;
        }

        Marshal.Copy(
            new byte[allocation.AllocationByteCount],
            0,
            allocation.Pointer,
            allocation.AllocationByteCount);
        Marshal.FreeCoTaskMem(allocation.Pointer);
    }

    internal readonly record struct CredentialAllocation(
        IntPtr Pointer,
        int ContentByteCount,
        int AllocationByteCount);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(
        string target,
        CredentialType type,
        uint flags,
        out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, CredentialType type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr credential);

    private enum CredentialType : uint
    {
        Generic = 1,
    }

    private enum CredentialPersist : uint
    {
        LocalMachine = 2,
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public CredentialType Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public CredentialPersist Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }
}

public sealed class ProviderCredentialSource(ICredentialStore credentials) :
    IProviderCredentialSource
{
    public Task<AppResult<string?>> GetApiKeyAsync(
        ProviderConfigurationId providerId,
        string opaqueTarget,
        CancellationToken cancellationToken) =>
        credentials.GetAsync(opaqueTarget, cancellationToken);
}
