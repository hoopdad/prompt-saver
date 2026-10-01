# Prompt Saver

Prompt Saver is a local-first Windows desktop application for capturing,
organizing, searching, editing, and copying reusable AI prompts. It uses WPF on
.NET 10, SQLite with transactional FTS5 indexing, deterministic metadata and
intent derivation, and optional Ollama or OpenAI-compatible metadata
enrichment.

## Use the application

Requirements:

- Windows 10 or later on x64 or ARM64.
- The .NET SDK is needed only when building from source. Published builds are
  self-contained.

Run from source:

```powershell
dotnet run --project .\src\PromptSaver.Desktop\PromptSaver.Desktop.csproj
```

The application opens directly into a focused prompt editor. Draft recovery is
available before the database and optional provider discovery finish. Use:

- `Ctrl+Enter` to save.
- `Ctrl+Shift+Enter` to save and copy.
- `Ctrl+Shift+C` to copy without saving.
- `Ctrl+F` to open Library search.
- `Ctrl+,` to open settings, provider configuration, backup/restore, search
  repair, intent merge/reclassification, and diagnostics.
- Library `More options` exposes intent/metadata review, duplication, and
  confirmed permanent deletion.

All non-secret user data is stored under:

```text
%LOCALAPPDATA%\PromptSaver\
  prompts.db
  capture-draft.json
  config.json
  Backups\
  Logs\
```

Provider credentials are stored in Windows Credential Manager. Provider use is
optional; provider failures never prevent a local save. Remote provider API
keys can be saved or removed without entering the application database, and
connection tests use the configured provider endpoint and can be cancelled.

## Behavior

- Prompt bodies are limited to 256 KiB of UTF-8 and are never truncated.
- Deterministic extraction derives a title, skills, entities, and controlled
  verb-object intent when possible.
- Existing intents are automatically selected only at a score of 85% or
  higher. Lower valid matches create a provisional intent for review;
  low-information prompts are assigned to `Unsorted`.
- Prompt detail supports intent review/reclassification, and Settings can
  merge duplicate intents after showing the number of prompts that will be
  reclassified.
- Prompt, metadata, intent candidate, and FTS changes commit in one SQLite
  transaction.
- Backups are created and restored from the application's `Backups` directory.

## Develop

The pinned SDK is declared in `global.json`. Restore, format, build, and run the
fast suites with:

```powershell
dotnet restore .\PromptSaver.sln --locked-mode
dotnet format .\PromptSaver.sln --verify-no-changes --no-restore
dotnet build .\PromptSaver.sln -c Release --no-restore -p:TreatWarningsAsErrors=true
dotnet test --project .\tests\PromptSaver.Domain.Tests\PromptSaver.Domain.Tests.csproj -c Release --no-build
dotnet test --project .\tests\PromptSaver.Application.Tests\PromptSaver.Application.Tests.csproj -c Release --no-build
dotnet test --project .\tests\PromptSaver.Infrastructure.Tests\PromptSaver.Infrastructure.Tests.csproj -c Release --no-build
dotnet test --project .\tests\PromptSaver.Desktop.Tests\PromptSaver.Desktop.Tests.csproj -c Release --no-build
```

Publish native self-contained builds:

```powershell
dotnet publish .\src\PromptSaver.Desktop\PromptSaver.Desktop.csproj -c Release -r win-x64 --self-contained true --no-restore -p:PublishSingleFile=false -p:PublishTrimmed=false -p:PublishReadyToRun=true -o .\artifacts\publish\win-x64
dotnet publish .\src\PromptSaver.Desktop\PromptSaver.Desktop.csproj -c Release -r win-arm64 --self-contained true --no-restore -p:PublishSingleFile=false -p:PublishTrimmed=false -p:PublishReadyToRun=true -o .\artifacts\publish\win-arm64
.\scripts\Verify-PublishArchitecture.ps1 -RuntimeIdentifier win-x64 -PublishDirectory .\artifacts\publish\win-x64
.\scripts\Verify-PublishArchitecture.ps1 -RuntimeIdentifier win-arm64 -PublishDirectory .\artifacts\publish\win-arm64
```

Architecture and product decisions are documented in `docs/system-design.md`,
`docs/ui-design.md`, and `docs/implementation-plan.md`. Windows installer packaging is implemented for both x64 and ARM64. Build
unsigned local MSIs, checksums, SPDX manifests, and run package validation with:

```powershell
.\scripts\Build-WindowsPackages.ps1 -RuntimeIdentifier all -Version 0.1.0
```

The installers never modify `PATH` or remove
`%LOCALAPPDATA%\PromptSaver`. See `docs/windows-packaging.md` for upgrade,
cross-architecture, signing, and WiX toolchain details.
