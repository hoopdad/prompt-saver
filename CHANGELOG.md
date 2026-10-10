# Changelog

## 1.0.0 - 2026-10-10

First public release of Prompt Saver, a local-first Windows prompt library.

### Included

- Capture, draft recovery, save/copy shortcuts, and editable prompt metadata.
- SQLite persistence and full-text search, a sortable paginated library,
  prompt duplication/deletion, and intent review/reclassification/merge.
- Optional Ollama and OpenAI-compatible metadata enrichment, strict response
  validation, connection diagnostics, and Windows Credential Manager keys.
- Light/dark themes, backup/restore, and separate self-contained Windows x64
  and ARM64 MSI installers. No separate .NET runtime installation is needed.
- Installation and upgrade documentation, SHA-256 checksums, and
  per-architecture SPDX file manifests.

### Installation

Download `PromptSaver-1.0.0-win-x64.msi` for Intel/AMD Windows or
`PromptSaver-1.0.0-win-arm64.msi` for ARM64 Windows from
[the release](https://github.com/hoopdad/prompt-saver/releases/tag/v1.0.0).
Close the app before installing. Installation requires Windows 10 or later
and administrator approval. Launch Prompt Saver from the Start menu.

**These installers are unsigned.** No code-signing certificate is configured.
Windows may show unknown-publisher or SmartScreen warnings; follow your
organization's security policy. Compare the downloaded MSI's SHA-256 hash
with `SHA256SUMS.txt`. Checksums verify download integrity, not publisher
identity.

Same-architecture upgrades preserve data. Uninstall the existing package
before switching architecture. Uninstalling retains
`%LOCALAPPDATA%\PromptSaver` and provider credentials; remove saved keys from
the application's Settings before uninstalling if desired.

### Verification and limitations

- Locked restore, formatting verification, and the warnings-as-errors Release
  solution build passed with zero build warnings or errors.
- All 134 executed tests passed: 39 Domain, 20 Application, 33 Infrastructure,
  31 Desktop, and 11 package tests.
- Both publishes passed native executable/SQLite architecture and ReadyToRun
  checks. MSI version, bundled runtime, upgrade/architecture rules, shortcuts,
  data/PATH isolation, and administrative extraction were checked.
- The configured NuGet feed's dependency advisory check reported no vulnerable
  direct or transitive packages.
- WiX emitted WIX1105 because machine policy prevented ICE validation; MSI
  linking and the repository's metadata/extraction tests passed.
- UI automation and dedicated-hardware performance suites each contain one
  skipped placeholder. Interactive install/repair/upgrade/uninstall,
  accessibility, startup timing, and native x64 execution were not qualified
  in this release preparation. Automated tests ran on Windows ARM64.
