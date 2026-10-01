# Technology Decision: Windows Desktop Stack and Distribution

- **Status:** Accepted and reconciled
- **Date:** 2026-10-01
- **Decision:** .NET 10 WPF, SQLite/FTS5, direct HTTP providers, and
  self-contained WiX MSI packages for x64 and ARM64

## 1. Decision summary

Prompt Saver will be a WPF modular monolith targeting `net10.0-windows`.
Version 1 uses:

- WPF with MVVM for native Windows input, focus, accessibility, and startup;
- SQLite with FTS5 through `Microsoft.Data.Sqlite`;
- repository-owned direct parameterized SQL, with no ORM and no FTS triggers;
- direct `HttpClient` adapters for Ollama and OpenAI-compatible endpoints;
- Windows Credential Manager for API keys;
- separate self-contained, ReadyToRun, multi-file `win-x64` and `win-arm64`
  publishes;
- one WiX installer source project built once per architecture into two MSIs.

MSIX, `.msixbundle`, framework-dependent deployment, embeddings,
`Azure.Identity`, and provider SDKs are not version 1 technologies.

This document is normative when older critique text describes alternatives.

## 2. Why WPF

The product is a Windows-only text-entry and search utility. WPF gives:

- the smallest number of runtime/toolchain layers among the evaluated options;
- mature keyboard, focus, clipboard, text editing, UI Automation, and binding;
- native x64 and ARM64 .NET deployment;
- view-model testing without launching the desktop;
- a stable boundary that leaves Domain, Application, and Infrastructure
  independent of WPF.

WinUI 3 adds Windows App SDK and MSIX complexity without a required feature.
Tauri adds Rust/TypeScript and WebView2 boundaries. Electron adds the largest
startup, memory, update, and ARM64 native-module burden. Reconsider only if
cross-platform delivery or a required Windows App SDK feature becomes more
important than fast Windows capture.

## 3. Solution and project structure

```text
PromptSaver.sln
global.json
Directory.Build.props
Directory.Packages.props
src/
  PromptSaver.Domain/
    Entities/
    ValueObjects/
    Policies/
  PromptSaver.Application/
    UseCases/
    Ports/
    Dtos/
  PromptSaver.Infrastructure/
    Storage/
      Migrations/
      Repositories/
      Search/
    Drafts/
    Providers/
    Credentials/
    Configuration/
    Diagnostics/
  PromptSaver.Desktop/
    App.xaml
    App.xaml.cs
    Views/
    ViewModels/
    Services/
    Resources/
tests/
  PromptSaver.Domain.Tests/
  PromptSaver.Application.Tests/
  PromptSaver.Infrastructure.Tests/
  PromptSaver.Desktop.Tests/
  PromptSaver.UiAutomation.Tests/
  PromptSaver.Package.Tests/
  PromptSaver.Performance.Tests/
packaging/
  wix/
    PromptSaver.Installer/
      PromptSaver.Installer.wixproj
      Package.wxs
      Folders.wxs
      Files.wxs
scripts/
  Generate-WixFiles.ps1
  Verify-PublishArchitecture.ps1
  Seed-SearchCorpus.ps1
```

There is no `packaging/msix` project in version 1. A future channel decision
must include stable unvirtualized data-root and migration tests before adding
that directory.

Dependency flow:

```text
Desktop -> Application -> Domain
Desktop -> Infrastructure -> Application/Domain
Installer -> published Desktop output only
```

Package, UI Automation, and performance tests consume artifacts; they do not
participate in the default `dotnet test PromptSaver.sln` inner loop.

## 4. Exact SDK and package baseline

Pin the SDK in `global.json`:

```json
{
  "sdk": {
    "version": "10.0.401",
    "rollForward": "latestPatch",
    "allowPrerelease": false
  }
}
```

Pin packages centrally in `Directory.Packages.props`:

| Package | Version | Project/use |
| --- | --- | --- |
| `CommunityToolkit.Mvvm` | `8.4.2` | Desktop MVVM source generators and commands |
| `Microsoft.Extensions.DependencyInjection` | `10.0.12` | Desktop composition root |
| `Microsoft.Extensions.Http` | `10.0.12` | Infrastructure `HttpClient` lifetimes |
| `Microsoft.Extensions.Logging.Abstractions` | `10.0.12` | Application/infrastructure logging boundary |
| `Microsoft.Data.Sqlite` | `10.0.12` | SQLite and bundled native SQLite/FTS5 |
| `Microsoft.NET.Test.Sdk` | `18.10.1` | Test host |
| `xunit.v3` | `4.0.1` | Test framework |
| `xunit.runner.visualstudio` | `4.0.0` | Visual Studio/`dotnet test` adapter |
| `coverlet.collector` | `10.0.1` | Coverage collection |
| `FlaUI.Core` | `5.0.0` | Release UI Automation harness |
| `FlaUI.UIA3` | `5.0.0` | UIA3 automation provider |
| `WixToolset.Sdk` | `4.0.6` | SDK-style x64/ARM64 MSI builds without requiring acceptance of the WiX 7 OSMF EULA |

These exact versions were found in the configured NuGet feed on 2026-10-01.
They are the implementation baseline, not a floating "latest" instruction. The
first scaffold slice must restore every pin from a clean NuGet cache and record
a deliberate substitution in this document if compatibility testing requires
one. Do not silently change a major version.

The original refined decision named WiX 7.0.0. Implementation proved that a
normal WiX 7 build is blocked by WIX7015 until the user accepts the Open Source
Maintenance Fee EULA. The build must not accept legal terms on the user's
behalf, so the installer uses WiX 4.0.6, which successfully links both x64 and
ARM64 packages with the same MSI and data-retention semantics. Details and the
explicit opt-in path back to WiX 7 are in `docs/windows-packaging.md`.

No provider SDK, ORM, JSON library, reactive framework, logging sink package,
or general-purpose resilience package is required. Use `System.Text.Json`,
`HttpClient`, WPF controls, and a small repository-owned bounded JSON file
logger. Implement bounded retries at the provider application boundary.

## 5. SQLite and FTS5 decision

Use direct SQL because FTS5 replacement, migrations, backup, and integrity
commands are SQLite-specific. All repositories share an explicit unit of work.

FTS5 has exactly one owner: repository code. There are no maintenance triggers.
The tokenizer and replacement sequence are normative in
`docs/system-design.md`.

Tests use temporary on-disk databases rather than `:memory:` so WAL, native
assets, locking, FTS5, and backup behavior are real.

The data path is resolved only by `IDataRootResolver` and is always:

```text
%LOCALAPPDATA%\PromptSaver
```

MSI install, upgrade, repair, and uninstall never change or remove it.

## 6. Provider technology decision

Implement narrow direct-HTTP adapters:

- `OllamaPromptEnrichmentProvider`
- `OpenAiCompatiblePromptEnrichmentProvider`

Both use typed DTOs, `System.Text.Json`, fake `HttpMessageHandler` tests,
cancellation, explicit timeouts, response-size limits, and schema validation.
No test requires network access, credentials, Ollama, Azure, or a cloud
resource.

Version 1 cloud authentication is API key only. `IProviderCredentialSource`
keeps an authentication seam, but Entra/WAM and `Azure.Identity` are deferred.

Automatic discovery probes only `http://127.0.0.1:11434/api/tags` after first
render. It does not use the provider adapter's enrichment operation and sends
no user data.

## 7. Startup and publish choices

Publish multi-file and self-contained:

```powershell
dotnet publish .\src\PromptSaver.Desktop\PromptSaver.Desktop.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=false `
  -p:PublishTrimmed=false `
  -p:PublishReadyToRun=true `
  -o .\artifacts\publish\win-x64

dotnet publish .\src\PromptSaver.Desktop\PromptSaver.Desktop.csproj `
  -c Release -r win-arm64 --self-contained true `
  -p:PublishSingleFile=false `
  -p:PublishTrimmed=false `
  -p:PublishReadyToRun=true `
  -o .\artifacts\publish\win-arm64
```

ReadyToRun is enabled from the first performance baseline. Trimming,
single-file extraction, and Native AOT are excluded because they add WPF and
reflection risk without a demonstrated need.

The critical startup path reads only small bootstrap files before showing the
editor. SQLite, migrations, integrity work, backups, search warm-up, and
provider discovery run after first render as specified in the system design.

## 8. Canonical WiX MSI strategy

`packaging\wix\PromptSaver.Installer\PromptSaver.Installer.wixproj` is the only
installer project. CI invokes it twice:

```powershell
dotnet build .\packaging\wix\PromptSaver.Installer\PromptSaver.Installer.wixproj `
  -c Release `
  -p:InstallerPlatform=x64 `
  -p:PublishDir="$PWD\artifacts\publish\win-x64" `
  -p:OutputPath="$PWD\artifacts\installers\win-x64"

dotnet build .\packaging\wix\PromptSaver.Installer\PromptSaver.Installer.wixproj `
  -c Release `
  -p:InstallerPlatform=arm64 `
  -p:PublishDir="$PWD\artifacts\publish\win-arm64" `
  -p:OutputPath="$PWD\artifacts\installers\win-arm64"
```

Rules:

- Package scope is per-machine and binaries install under
  `ProgramFiles64Folder`.
- x64 and ARM64 have separate product/package identities and artifact names.
- Both embed one stable release-family identifier for tracking. They use
  separate architecture UpgradeCodes so same-architecture major upgrades work
  while cross-architecture replacement is explicitly blocked.
- On ARM64, installing x64 is allowed by Windows but Prompt Saver's ARM64 MSI
  detects an installed x64 product and instructs the user to uninstall it
  first. The x64 MSI similarly does not replace ARM64.
- Native ARM64 release qualification tests x64-installed-on-ARM64 detection and
  the required uninstall-then-ARM64-install transition.
- Major upgrades within one architecture are automatic; downgrades are blocked.
- Side-by-side architecture installs are blocked.
- Start menu shortcut is installed. Desktop shortcut is an installer option.
- No start-at-login entry is installed.
- Upgrade, repair, and uninstall retain `%LOCALAPPDATA%\PromptSaver`.
- The installer consumes only the matching RID directory.

`Generate-WixFiles.ps1` deterministically emits `Files.wxs` from the publish
directory. `Verify-PublishArchitecture.ps1` fails when managed host, PE machine
type, SQLite native library, or WiX platform does not match the requested RID.

Pull-request MSIs are unsigned and clearly labeled. Protected release jobs sign
executables and MSIs, timestamp signatures, generate checksums and an SBOM, and
publish:

```text
PromptSaver-<version>-win-x64.msi
PromptSaver-<version>-win-arm64.msi
```

Signed-package verification is a release test only.

## 9. Build and test commands

Inner loop:

```powershell
dotnet restore .\PromptSaver.sln --locked-mode
dotnet build .\PromptSaver.sln -c Debug --no-restore
dotnet test .\tests\PromptSaver.Domain.Tests\PromptSaver.Domain.Tests.csproj -c Debug
dotnet test .\tests\PromptSaver.Application.Tests\PromptSaver.Application.Tests.csproj -c Debug
dotnet test .\tests\PromptSaver.Infrastructure.Tests\PromptSaver.Infrastructure.Tests.csproj -c Debug
dotnet test .\tests\PromptSaver.Desktop.Tests\PromptSaver.Desktop.Tests.csproj -c Debug
```

Quality gate:

```powershell
dotnet format .\PromptSaver.sln --verify-no-changes
dotnet build .\PromptSaver.sln -c Release -p:TreatWarningsAsErrors=true
dotnet test .\PromptSaver.sln -c Release --no-build `
  --collect:"XPlat Code Coverage" `
  --results-directory .\artifacts\test-results
```

The solution excludes artifact-dependent Package, UI Automation, and
Performance projects from the default test target or marks them skipped unless
their required environment variables are present.

`Directory.Build.props` enables nullable references, implicit usings,
deterministic builds, .NET analyzers, invariant globalization only where
explicitly safe, and warnings as errors in CI. Lock files are committed and CI
uses locked restore.

## 10. CI and native architecture qualification

### Pull request

1. Clean checkout and pinned SDK.
2. Locked restore.
3. Format verification.
4. Release build with warnings as errors.
5. Domain, Application, Infrastructure, and Desktop tests.
6. Publish x64 and ARM64.
7. Verify PE/native SQLite/WiX architecture metadata for both.
8. Build unsigned MSIs.
9. Run package metadata tests.
10. Report startup measurements without enforcing shared-runner timing.

### Protected release

Run on dedicated native x64 and native ARM64 Windows machines:

1. Repeat clean build and tests.
2. Install, launch, focus editor, type, save, create database, search, and
   uninstall.
3. Verify data survives repair, upgrade, and uninstall.
4. Exercise architecture mismatch and x64-to-ARM64 transition guidance on
   native ARM64.
5. Run UIA, manual Narrator, contrast theme, 200% scaling, IME, clipboard, and
   startup/search performance qualification.
6. Sign, timestamp, verify signatures, checksum, and generate SBOM.

An ARM64 package is not declared generally available until native ARM64
startup, SQLite, UIA, install, upgrade, and uninstall tests pass. Cross-publish
success alone is insufficient.

## 11. Accepted tradeoffs and revisit triggers

- Self-contained MSI is larger, but removes a runtime prerequisite and provides
  one reproducible release channel.
- Separate x64/ARM64 MSIs require clear download labels and architecture
  guidance.
- WPF requires deliberate styling but avoids additional activation/runtime
  layers.
- Direct SQL requires disciplined tests but gives correct FTS ownership.
- ReadyToRun increases publish size for improved startup.

Revisit when:

- macOS/Linux support becomes required;
- a Windows App SDK-only feature is essential;
- native ARM64 WPF/WiX has a release-blocking defect;
- measured startup misses targets after profiling;
- a signed MSIX channel has a proven data-root and migration strategy.
