using System.Runtime.InteropServices;
using PromptSaver.Desktop.Services;

namespace PromptSaver.Desktop.Tests;

public sealed class CredentialAllocationTests
{
    [Fact]
    public void CredentialAllocationIsNullTerminatedAndTracksExactContentLength()
    {
        WindowsCredentialStore.CredentialAllocation allocation =
            WindowsCredentialStore.AllocateCredential("sëcret");
        try
        {
            Assert.Equal("sëcret".Length * sizeof(char), allocation.ContentByteCount);
            Assert.Equal(
                allocation.ContentByteCount + sizeof(char),
                allocation.AllocationByteCount);
            Assert.Equal(
                "sëcret",
                Marshal.PtrToStringUni(
                    allocation.Pointer,
                    allocation.ContentByteCount / sizeof(char)));
            Assert.Equal(
                0,
                Marshal.ReadInt16(allocation.Pointer, allocation.ContentByteCount));
        }
        finally
        {
            WindowsCredentialStore.FreeCredential(allocation);
        }
    }
}
