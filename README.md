# Prompt Saver

Prompt Saver is a local-first Windows desktop application for capturing,
organizing, searching, editing, and copying reusable AI prompts. It uses WPF on
.NET 10, SQLite with transactional FTS5 indexing, deterministic metadata and
intent derivation, and optional Ollama or OpenAI-compatible metadata
enrichment.

## Install 1.0.0

Download the MSI for your processor from the
[1.0.0 release](https://github.com/hoopdad/prompt-saver/releases/tag/v1.0.0):

| Windows processor | Installer |
| --- | --- |
| Intel or AMD (x64) | [PromptSaver-1.0.0-win-x64.msi](https://github.com/hoopdad/prompt-saver/releases/download/v1.0.0/PromptSaver-1.0.0-win-x64.msi) |
| ARM64 (including Snapdragon) | [PromptSaver-1.0.0-win-arm64.msi](https://github.com/hoopdad/prompt-saver/releases/download/v1.0.0/PromptSaver-1.0.0-win-arm64.msi) |

Find your processor under **Settings > System > About > System type**.
Windows 10 or later is required; no separate .NET installation is needed.
Close Prompt Saver, double-click the matching MSI, and approve the Windows
administrator prompt if you choose to install. Launch **Prompt Saver** from
the Start menu afterward.

**The 1.0.0 installers are unsigned.** Windows may display an unknown-publisher
or SmartScreen warning. There is no verified publisher identity; do not bypass
a security warning unless your organization's policy allows installation and
you trust the release source. SHA-256 checksums and per-architecture SPDX file
manifests are attached to the release. To check a download:

```powershell
Get-FileHash .\PromptSaver-1.0.0-win-arm64.msi -Algorithm SHA256
```

Compare the hash with the corresponding entry in the downloaded
`SHA256SUMS.txt` (use the x64 filename for x64). Checksums detect changed
downloads; they are not a substitute for code signing.

See [CHANGELOG.md](CHANGELOG.md) for release contents, verification results,
and qualification limitations.

Same-architecture upgrades retain your prompts and settings. To switch
architecture, uninstall the old package first. Uninstall through Windows
**Settings > Apps**; uninstalling does not delete your local data or saved
provider credentials. Remove provider keys in the application's Settings
before uninstalling if you no longer want them stored.

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
- Settings reloads the saved provider model and shows save, connection, and
  LLM activity in the fixed status bar at the bottom of the window.
- Light and dark modes apply a shared global palette to page surfaces, text,
  inputs, lists, popup items, and Library table headers, rows, and selections.
- Provider requests use native JSON response constraints where supported and
  validate replies against a strict typed metadata contract. Response handling
  recovers valid JSON from Markdown fences or surrounding commentary; rejected
  replies show a bounded response excerpt for troubleshooting.
- Capture can save and open metadata editing in one action. LLM intent
  suggestions populate an editable intent field and can be saved directly,
  including replacing an Unsorted assignment.
- Library opens to all prompts, defaults to 20 rows per page sorted by intent,
  supports user-sized pages and reversible column sorting, and exposes
  intent/metadata review, duplication, and confirmed permanent deletion.

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
- Prompt detail can re-query the enabled LLM provider for an intent suggestion
  without automatically changing the saved intent.
- Prompt, metadata, intent candidate, and FTS changes commit in one SQLite
  transaction.
- Backups are created and restored from the application's `Backups` directory.

## Develop

The pinned SDK is declared in `global.json`. Routine CI runs locally and can be
started with:

```powershell
.\scripts\Invoke-LocalCi.ps1
```

Include both Windows packages and package validation when needed:

```powershell
.\scripts\Invoke-LocalCi.ps1 -IncludePackages -RuntimeIdentifier all
```

The routine GitHub Actions CI workflow is manual-only. The protected release
workflow remains available for signed release artifacts once signing secrets
are configured; 1.0.0 is built and published locally without signing.

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
.\scripts\Build-WindowsPackages.ps1 -RuntimeIdentifier all -Version 1.0.0
```

The installers never modify `PATH` or remove
`%LOCALAPPDATA%\PromptSaver`. See `docs/windows-packaging.md` for upgrade,
cross-architecture, signing, and WiX toolchain details.
