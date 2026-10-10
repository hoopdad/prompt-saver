# Windows packaging

Prompt Saver ships separate per-machine MSI packages for Windows x64 and
ARM64:

```text
PromptSaver-<version>-win-x64.msi
PromptSaver-<version>-win-arm64.msi
```

Both contain self-contained, multi-file, ReadyToRun .NET publishes. The
installer never adds Prompt Saver to `PATH`, never creates a start-at-login
entry, and never owns or removes `%LOCALAPPDATA%\PromptSaver`. Upgrade, repair,
and uninstall therefore retain prompts, drafts, configuration, backups, and
logs. The MSI owns only Program Files binaries, installer registration, and
shortcuts.

## Build unsigned local packages

From a Windows x64 or ARM64 development machine with the pinned .NET SDK:

```powershell
.\scripts\Build-WindowsPackages.ps1 -RuntimeIdentifier all -Version 1.0.0
```

Outputs:

```text
artifacts\publish\win-x64\
artifacts\publish\win-arm64\
artifacts\installers\win-x64\PromptSaver-1.0.0-win-x64.msi
artifacts\installers\win-arm64\PromptSaver-1.0.0-win-arm64.msi
artifacts\manifests\SHA256SUMS.txt
artifacts\manifests\PromptSaver-1.0.0-win-x64.spdx.json
artifacts\manifests\PromptSaver-1.0.0-win-arm64.spdx.json
```

Without `-Version`, the package script uses the desktop project's
`VersionPrefix`. An explicit version must fit Windows Installer's
three-part version limits (255.255.65535).

If a developer is running Prompt Saver directly from a publish directory, the
script does not stop that process or overwrite its loaded files. It writes the
new publish to a version-suffixed sibling and uses that clean directory for the
MSI and tests.

The build verifies `PromptSaver.Desktop.exe` and `e_sqlite3.dll` PE machine
headers before linking. Package tests then inspect MSI properties, summary
architecture, upgrade and launch-condition tables, shortcuts, absence of
`Environment`/data files, and perform an administrative extraction with
`msiexec /a`. Administrative extraction does not register or install the
product machine-wide.

## Install behavior

Download the matching MSI from the
[1.0.0 release](https://github.com/hoopdad/prompt-saver/releases/tag/v1.0.0).
The 1.0.0 assets are unsigned; verify the release's `SHA256SUMS.txt` and follow
your organization's policy for unknown publishers. See the README for
interactive installation and architecture selection.

For a managed installation from an elevated PowerShell session:

```powershell
msiexec.exe /i .\PromptSaver-1.0.0-win-arm64.msi /qn /norestart /L*v .\PromptSaver-install.log
```

Use the x64 filename on Intel/AMD Windows. Windows Installer exit code `0`
means success; `3010` means success with a restart required. This command
actually installs machine-wide, unlike the administrative extraction tests.

- The packages are per-machine and require elevation when actually installed.
- Same-architecture major upgrades are automatic.
- Downgrades are blocked with an explicit newer-version message.
- x64 and ARM64 use separate ProductCode and UpgradeCode families.
- Each package detects the other architecture's UpgradeCode and directs the
  user to uninstall it first. Uninstalling does not remove local user data.
- A Start menu shortcut is installed by default.
- The desktop shortcut is an optional MSI feature at level 2. Install it with
  `ADDLOCAL=MainFeature,DesktopShortcutFeature`.
- Repair restores MSI-owned binaries, registration, and selected shortcuts.
- `LICENSE.txt` is included with the installed binaries.

`PROMPTSAVER_FAMILY_ID=C97D46A3-944B-4BD9-A88E-2C49ECA831F7` is embedded in
both packages as the shared release-family identifier. Separate architecture
UpgradeCodes are necessary so Windows Installer can upgrade one architecture
while treating the other as a blocking related product.

Machine-wide install, repair, upgrade, uninstall, and x64-on-native-ARM64
transition tests remain protected release qualification because running them
locally would modify machine state. The release checklist must verify that
`%LOCALAPPDATA%\PromptSaver` has the same identity and contents before and
after each operation.

## WiX license/toolchain decision

The accepted design originally pinned `WixToolset.Sdk` 7.0.0. A normal build
without any acceptance property fails with:

```text
WIX7015: You must accept the Open Source Maintenance Fee (OSMF) EULA to use
WiX Toolset v7.
```

The build does not accept that agreement on the user's behalf. Packaging uses
`WixToolset.Sdk` 4.0.6 instead, which is available from the configured feed,
builds both x64 and ARM64 MSI packages, and preserves the selected MSI/data
semantics without a new EULA. Moving back to WiX 7 requires the repository
owner to review and accept the WiX OSMF terms outside this build, then
deliberately update the pinned SDK.

WiX may emit WIX1105 on a non-elevated development machine when Windows policy
prevents ICE validation. Linking and the repository's read-only MSI database
tests still run. Protected release infrastructure should run ICE validation
where policy permits.

## Signed releases

`.github/workflows/release-windows.yml` uses the protected
`release-signing` environment. Repository administrators must configure:

- `WINDOWS_SIGNING_CERTIFICATE_BASE64`
- `WINDOWS_SIGNING_CERTIFICATE_PASSWORD`

The workflow signs published PE files, rebuilds the MSIs from signed inputs,
signs and verifies each MSI, regenerates checksums/SBOMs, runs package tests,
and publishes tag assets. No certificate material is written to the
repository.

## Release qualification

Run the complete automated local gate before publishing:

```powershell
.\scripts\Invoke-LocalCi.ps1 -IncludePackages -RuntimeIdentifier all -Version 1.0.0
```

This runs locked restore, formatting verification, a warnings-as-errors
Release build, Domain/Application/Infrastructure/Desktop tests, both native
publishes, and MSI metadata/extraction tests. Package tests also check that
the application file version matches the installer and the .NET runtime is
included. SHA-256 checksums must be checked again after downloading release
assets.

The UI automation and dedicated-hardware performance projects currently
contain skipped placeholders, not completed qualification. Automated package
inspection does not prove interactive install/repair/upgrade/uninstall,
screen-reader behavior, or startup performance. Record those untested areas
in release notes rather than claiming that they passed. Do not install or
uninstall machine-wide as part of routine local CI.
