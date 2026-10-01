using System.ComponentModel;
using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text;

namespace PromptSaver.Package.Tests;

public sealed class ArtifactSmokeTests
{
    public static TheoryData<string, ushort, string> Architectures =>
        new()
        {
            { "win-x64", 0x8664, "x64" },
            { "win-arm64", 0xAA64, "Arm64" },
        };

    [Theory]
    [MemberData(nameof(Architectures))]
    [Trait("Category", "Artifact")]
    public void PublishedNativeFilesMatchArchitecture(string rid, ushort expectedMachine, string _)
    {
        ArtifactSet artifacts = ArtifactSet.Require(rid);

        Assert.Equal(expectedMachine, PortableExecutable.ReadMachine(artifacts.Executable));
        Assert.Equal(expectedMachine, PortableExecutable.ReadMachine(artifacts.NativeSqlite));
    }

    [Theory]
    [MemberData(nameof(Architectures))]
    [Trait("Category", "Artifact")]
    public void PublishedDesktopAssemblyIsReadyToRun(string rid, ushort expectedMachine, string expectedTemplate)
    {
        ArtifactSet artifacts = ArtifactSet.Require(rid);
        using FileStream stream = File.OpenRead(artifacts.ManagedDesktopAssembly);
        using var reader = new PEReader(stream);

        Assert.NotEqual((ushort)0, expectedMachine);
        Assert.NotEmpty(expectedTemplate);
        Assert.NotNull(reader.PEHeaders.CorHeader);
        Assert.True(
            reader.PEHeaders.CorHeader.ManagedNativeHeaderDirectory.Size > 0,
            $"{artifacts.ManagedDesktopAssembly} is not ReadyToRun.");
    }

    [Theory]
    [MemberData(nameof(Architectures))]
    [Trait("Category", "Artifact")]
    public void InstallerMetadataMatchesArchitecture(string rid, ushort _, string expectedTemplate)
    {
        ArtifactSet artifacts = ArtifactSet.Require(rid);
        using var database = new InstallerDatabase(artifacts.Installer);

        Assert.Equal("Prompt Saver", database.Property("ProductName"));
        Assert.Equal(
            Environment.GetEnvironmentVariable("PROMPTSAVER_PACKAGE_VERSION") ?? "0.1.0",
            database.Property("ProductVersion"));
        Assert.Equal(
            rid == "win-x64"
                ? "{7990DD6A-9342-55AE-A605-83028F67A455}"
                : "{93D630EE-729C-5A12-80A5-EDAE1E74DDEA}",
            database.Property("UpgradeCode"));
        Assert.Equal(rid[4..], database.Property("PROMPTSAVER_ARCHITECTURE"));
        Assert.Equal("C97D46A3-944B-4BD9-A88E-2C49ECA831F7", database.Property("PROMPTSAVER_FAMILY_ID"));
        Assert.Contains(expectedTemplate, database.SummaryTemplate, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, database.RowCount("Environment"));
        Assert.Equal(0, database.CountContaining("File", "FileName", "prompts.db"));
        Assert.Equal(0, database.CountContaining("File", "FileName", "capture-draft.json"));
        Assert.True(database.RowCount("Shortcut") >= 2);
        Assert.True(database.RowCount("Upgrade") >= 2);
        Assert.True(database.CountContaining("LaunchCondition", "Condition", "OTHERARCHITECTUREFOUND") >= 1);
        Assert.True(database.CountContaining("LaunchCondition", "Condition", "WIX_DOWNGRADE_DETECTED") >= 1);
        Assert.True(database.CountContaining("InstallExecuteSequence", "Action", "RemoveExistingProducts") >= 1);
        Assert.Equal(0, database.CountContaining("Directory", "DefaultDir", "AppData"));
        Assert.Equal(0, database.CountContaining("Registry", "Name", "PATH"));
        Assert.True(database.CountContaining("File", "FileName", "PromptSaver.Desktop.exe") >= 1);
        Assert.True(database.CountContaining("File", "FileName", "LICENSE.txt") >= 1);
    }

    [Fact]
    [Trait("Category", "Artifact")]
    public void ArchitecturePackagesAreRelatedButCannotReplaceEachOther()
    {
        ArtifactSet x64 = ArtifactSet.Require("win-x64");
        ArtifactSet arm64 = ArtifactSet.Require("win-arm64");
        using var x64Database = new InstallerDatabase(x64.Installer);
        using var arm64Database = new InstallerDatabase(arm64.Installer);

        Assert.NotEqual(x64Database.Property("ProductCode"), arm64Database.Property("ProductCode"));
        Assert.NotEqual(x64Database.Property("UpgradeCode"), arm64Database.Property("UpgradeCode"));
        Assert.Equal(
            x64Database.Property("PROMPTSAVER_FAMILY_ID"),
            arm64Database.Property("PROMPTSAVER_FAMILY_ID"));
        Assert.Contains(arm64Database.Property("UpgradeCode"), x64Database.AllValues("Upgrade", "UpgradeCode"));
        Assert.Contains(x64Database.Property("UpgradeCode"), arm64Database.AllValues("Upgrade", "UpgradeCode"));
    }

    [Theory]
    [MemberData(nameof(Architectures))]
    [Trait("Category", "Artifact")]
    public void AdministrativeInstallExtractsWithoutRegisteringProduct(
        string rid,
        ushort expectedMachine,
        string _)
    {
        ArtifactSet artifacts = ArtifactSet.Require(rid);
        string targetDirectory = Path.Combine(Path.GetTempPath(), $"PromptSaver-PackageTest-{Guid.NewGuid():N}");

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "msiexec.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("/a");
            startInfo.ArgumentList.Add(artifacts.Installer);
            startInfo.ArgumentList.Add("/qn");
            startInfo.ArgumentList.Add($"TARGETDIR={targetDirectory}");

            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start msiexec.");
            Assert.True(process.WaitForExit(TimeSpan.FromMinutes(3)), "Administrative install timed out.");
            Assert.Equal(0, process.ExitCode);

            string extractedExecutable = Directory
                .EnumerateFiles(targetDirectory, "PromptSaver.Desktop.exe", SearchOption.AllDirectories)
                .Single();
            Assert.Equal(expectedMachine, PortableExecutable.ReadMachine(extractedExecutable));
            Assert.Empty(Directory.EnumerateFiles(targetDirectory, "prompts.db", SearchOption.AllDirectories));
        }
        finally
        {
            if (Directory.Exists(targetDirectory))
            {
                Directory.Delete(targetDirectory, recursive: true);
            }
        }
    }

    private sealed record ArtifactSet(
        string Executable,
        string ManagedDesktopAssembly,
        string NativeSqlite,
        string Installer)
    {
        public static ArtifactSet Require(string rid)
        {
            string? root = Environment.GetEnvironmentVariable("PROMPTSAVER_ARTIFACTS");
            if (string.IsNullOrWhiteSpace(root))
            {
                Assert.Skip("Set PROMPTSAVER_ARTIFACTS to the artifacts directory to run package tests.");
            }

            string overrideName = $"PROMPTSAVER_PUBLISH_{rid.Replace('-', '_').ToUpperInvariant()}";
            string publishDirectory = Environment.GetEnvironmentVariable(overrideName)
                ?? Path.Combine(root!, "publish", rid);
            string installerDirectory = Path.Combine(root, "installers", rid);
            string installer = Directory.Exists(installerDirectory)
                ? Directory.EnumerateFiles(installerDirectory, $"PromptSaver-*-{rid}.msi").SingleOrDefault() ?? string.Empty
                : string.Empty;
            var artifacts = new ArtifactSet(
                Path.Combine(publishDirectory, "PromptSaver.Desktop.exe"),
                Path.Combine(publishDirectory, "PromptSaver.Desktop.dll"),
                Path.Combine(publishDirectory, "e_sqlite3.dll"),
                installer);

            foreach (string path in new[]
                     {
                         artifacts.Executable,
                         artifacts.ManagedDesktopAssembly,
                         artifacts.NativeSqlite,
                         artifacts.Installer,
                     })
            {
                Assert.True(File.Exists(path), $"Required package artifact does not exist: {path}");
            }

            return artifacts;
        }
    }

    private static class PortableExecutable
    {
        public static ushort ReadMachine(string path)
        {
            using FileStream stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);
            Assert.Equal(0x5A4D, reader.ReadUInt16());
            stream.Position = 0x3C;
            int peHeaderOffset = reader.ReadInt32();
            stream.Position = peHeaderOffset;
            Assert.Equal(0x00004550u, reader.ReadUInt32());
            return reader.ReadUInt16();
        }
    }

    private sealed class InstallerDatabase : IDisposable
    {
        private const int ErrorSuccess = 0;
        private readonly IntPtr handle;

        public InstallerDatabase(string path)
        {
            int result = MsiOpenDatabase(path, IntPtr.Zero, out handle);
            ThrowOnError(result, $"Opening MSI database {path}");
        }

        public string SummaryTemplate
        {
            get
            {
                int result = MsiGetSummaryInformation(handle, null, 0, out IntPtr summaryHandle);
                ThrowOnError(result, "Opening MSI summary information");
                try
                {
                    uint length = 0;
                    _ = MsiSummaryInfoGetProperty(summaryHandle, 7, out _, out _, out _, null, ref length);
                    var value = new StringBuilder(checked((int)length + 1));
                    uint capacity = checked((uint)value.Capacity);
                    result = MsiSummaryInfoGetProperty(summaryHandle, 7, out _, out _, out _, value, ref capacity);
                    ThrowOnError(result, "Reading MSI template summary property");
                    return value.ToString();
                }
                finally
                {
                    _ = MsiCloseHandle(summaryHandle);
                }
            }
        }

        public string Property(string name) =>
            Scalar($"SELECT `Value` FROM `Property` WHERE `Property` = '{Escape(name)}'");

        public int RowCount(string table)
        {
            if (ScalarOrDefault($"SELECT `Name` FROM `_Tables` WHERE `Name` = '{Escape(table)}'") is null)
            {
                return 0;
            }

            return AllValues(table, table == "Upgrade" ? "UpgradeCode" : table == "Shortcut" ? "Shortcut" : "Name").Count;
        }

        public int CountContaining(string table, string column, string value) =>
            AllValues(table, column).Count(item => item.Contains(value, StringComparison.OrdinalIgnoreCase));

        public List<string> AllValues(string table, string column) =>
            Query($"SELECT `{column}` FROM `{table}`");

        public void Dispose()
        {
            if (handle != IntPtr.Zero)
            {
                _ = MsiCloseHandle(handle);
            }
        }

        private string Scalar(string query) =>
            ScalarOrDefault(query) ?? throw new InvalidOperationException($"MSI query returned no rows: {query}");

        private string? ScalarOrDefault(string query)
        {
            List<string> values = Query(query);
            return values.Count == 0 ? null : values[0];
        }

        private List<string> Query(string query)
        {
            int result = MsiDatabaseOpenView(handle, query, out IntPtr viewHandle);
            ThrowOnError(result, $"Opening MSI view: {query}");
            try
            {
                ThrowOnError(MsiViewExecute(viewHandle, IntPtr.Zero), $"Executing MSI view: {query}");
                var values = new List<string>();
                while ((result = MsiViewFetch(viewHandle, out IntPtr recordHandle)) == ErrorSuccess)
                {
                    try
                    {
                        uint length = 0;
                        _ = MsiRecordGetString(recordHandle, 1, null, ref length);
                        var value = new StringBuilder(checked((int)length + 1));
                        uint capacity = checked((uint)value.Capacity);
                        ThrowOnError(MsiRecordGetString(recordHandle, 1, value, ref capacity), $"Reading MSI row: {query}");
                        values.Add(value.ToString());
                    }
                    finally
                    {
                        _ = MsiCloseHandle(recordHandle);
                    }
                }

                const int errorNoMoreItems = 259;
                if (result != errorNoMoreItems)
                {
                    ThrowOnError(result, $"Fetching MSI rows: {query}");
                }

                return values;
            }
            finally
            {
                _ = MsiViewClose(viewHandle);
                _ = MsiCloseHandle(viewHandle);
            }
        }

        private static string Escape(string value) => value.Replace("'", "''", StringComparison.Ordinal);

        private static void ThrowOnError(int error, string operation)
        {
            if (error != ErrorSuccess)
            {
                throw new Win32Exception(error, operation);
            }
        }

#pragma warning disable CA1838
        [DllImport("msi.dll", CharSet = CharSet.Unicode)]
        private static extern int MsiOpenDatabase(string databasePath, IntPtr persist, out IntPtr databaseHandle);

        [DllImport("msi.dll", CharSet = CharSet.Unicode)]
        private static extern int MsiDatabaseOpenView(IntPtr databaseHandle, string query, out IntPtr viewHandle);

        [DllImport("msi.dll")]
        private static extern int MsiViewExecute(IntPtr viewHandle, IntPtr recordHandle);

        [DllImport("msi.dll")]
        private static extern int MsiViewFetch(IntPtr viewHandle, out IntPtr recordHandle);

        [DllImport("msi.dll")]
        private static extern int MsiViewClose(IntPtr viewHandle);

        [DllImport("msi.dll", CharSet = CharSet.Unicode)]
        private static extern int MsiRecordGetString(IntPtr recordHandle, uint field, StringBuilder? value, ref uint valueLength);

        [DllImport("msi.dll", CharSet = CharSet.Unicode)]
        private static extern int MsiGetSummaryInformation(
            IntPtr databaseHandle,
            string? databasePath,
            uint updateCount,
            out IntPtr summaryInfoHandle);

        [DllImport("msi.dll", CharSet = CharSet.Unicode)]
        private static extern int MsiSummaryInfoGetProperty(
            IntPtr summaryInfoHandle,
            uint property,
            out uint dataType,
            out int integerValue,
            out System.Runtime.InteropServices.ComTypes.FILETIME fileTimeValue,
            StringBuilder? stringValue,
            ref uint stringValueLength);

        [DllImport("msi.dll")]
        private static extern int MsiCloseHandle(IntPtr handle);
#pragma warning restore CA1838
    }
}
