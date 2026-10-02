# Repository Index

## Functional areas

- Product brief: `prompts/initial-prompt.md`
- System architecture and implementation decisions: `docs/system-design.md`
- Technology stack evaluation and packaging decision: `docs/technology-decision.md`
- Windows desktop UI/UX specification: `docs/ui-design.md`
- Vertical-slice/TDD delivery sequence and acceptance gates:
  `docs/implementation-plan.md`
- Architecture critique input: `docs/design-critique.md`
- Independent Windows desktop UI/UX critique: `docs/ui-critique.md`
- Shared SDK, analyzer, architecture, central-package, and lock-file settings:
  `global.json`, `Directory.Build.props`, `Directory.Packages.props`
- Production modular-monolith projects: `src/PromptSaver.Domain`,
  `src/PromptSaver.Application`, `src/PromptSaver.Infrastructure`,
  `src/PromptSaver.Desktop`
- Executable composition root, Windows clipboard/credential/system adapters,
  and post-render initialization:
  `src/PromptSaver.Desktop/App.xaml.cs`,
  `src/PromptSaver.Desktop/Services/DesktopComposition.cs`,
  `src/PromptSaver.Desktop/Services/SystemServices.cs`
- Modern WPF theme resources, compact top navigation, immediate light/dark
  switching, responsive focus routing, and page composition:
  `src/PromptSaver.Desktop/App.xaml`,
  `src/PromptSaver.Desktop/MainWindow.xaml`,
  `src/PromptSaver.Desktop/MainWindow.xaml.cs`,
  `src/PromptSaver.Desktop/Views`
- Shared domain model, value objects, and pure mutation/assignment policies:
  `src/PromptSaver.Domain/Entities`, `src/PromptSaver.Domain/ValueObjects`,
  `src/PromptSaver.Domain/Policies`
- Adapter-facing application contracts, DTOs, typed results, and use-case
  boundaries: `src/PromptSaver.Application/Ports`,
  `src/PromptSaver.Application/Dtos`, `src/PromptSaver.Application/UseCases`,
  `src/PromptSaver.Application/Policies`, `src/PromptSaver.Application/AppResult.cs`
- Integrated prompt and provider orchestration for capture, recovery,
  deterministic metadata/intent assignment, edit, duplicate, delete, copy,
  intent listing/review/reclassification/merge, user metadata correction,
  search, backup/restore, diagnostics, provider configuration, and optional
  enrichment:
  `src/PromptSaver.Application/UseCases/ApplicationServices.cs`,
  `src/PromptSaver.Application/UseCases/ProviderApplicationService.cs`
- Direct HTTP model providers, loopback-only Ollama discovery, bounded
  enrichment scheduling with atomic item claims and a shared 20-item session
  cap, payload/response validation, configured endpoint health checks, and
  suggestion-only post-render discovery:
  `src/PromptSaver.Infrastructure/Providers`
- Single-root data paths, atomic capture/config files, SQLite migrations and
  transactions, cached single-flight initialization, relational repositories,
  repository-owned FTS5, edit drafts, duplicate/delete/merge operations,
  explicit integrity checks, and backup/restore:
  `src/PromptSaver.Infrastructure/Storage`,
  `src/PromptSaver.Infrastructure/Drafts`
- SQLite provider configuration/proposal work queues and multi-provider
  enrichment dispatch, including database-key hydration for strongly typed IDs:
  `src/PromptSaver.Infrastructure/Storage/SqliteProviderStores.cs`,
  `src/PromptSaver.Infrastructure/Providers/MultiProviderEnrichmentService.cs`
- Fast test projects: `tests/PromptSaver.Domain.Tests`,
  `tests/PromptSaver.Application.Tests`,
  `tests/PromptSaver.Infrastructure.Tests`, `tests/PromptSaver.Desktop.Tests`
- Artifact test projects: `tests/PromptSaver.UiAutomation.Tests`,
  `tests/PromptSaver.Package.Tests`, `tests/PromptSaver.Performance.Tests`
- WiX x64/ARM64 installer sources, deterministic harvesting, package
  manifests/signing, and artifact validation:
  `packaging/wix/PromptSaver.Installer`,
  `scripts/Build-WindowsPackages.ps1`,
  `scripts/Generate-WixFiles.ps1`,
  `scripts/Verify-PublishArchitecture.ps1`,
  `scripts/New-WindowsPackageManifest.ps1`,
  `scripts/Sign-WindowsArtifacts.ps1`,
  `tests/PromptSaver.Package.Tests`
- Pull-request unsigned package pipeline and protected signing-ready release:
  `.github/workflows/ci.yml`, `.github/workflows/release-windows.yml`
- Windows packaging behavior and operations: `docs/windows-packaging.md`

## Technical layers

The application scaffold is a .NET 10 WPF modular monolith. Dependency flow is
`Desktop -> Application -> Domain`, with `Infrastructure` implementing
Application ports for draft files, SQLite/FTS, model providers, secrets,
logging, and backups. `PromptSaver.Desktop/App.xaml.cs` is the executable composition root. It renders
and focuses the capture editor before starting SQLite initialization, Ollama
discovery, and pending enrichment. Repository transactions exclusively own FTS updates. The build
declares `win-x64` and `win-arm64`; cross-publishes are self-contained,
ReadyToRun, and checked through PE machine headers. The installer consumes only
matching publish output, uses separate architecture upgrade families with a
shared release-family marker, never mutates `PATH`, and never owns the local
user data root.

## Navigation

- Understand the product request: `prompts/initial-prompt.md`
- Implement or review architecture, persistence, providers, intent policy,
  packaging, and test strategy: `docs/system-design.md`
- Review the stack comparison, selected WPF/.NET toolchain, exact project/test
  layout, exact package pins, commands, canonical MSI plan, native ARM64 gates,
  CI implications, and risks:
  `docs/technology-decision.md`
- Review fast capture, search, prompt editing, metadata review, provider
  settings, shortcuts, focus, responsive behavior, accessibility, and UI
  acceptance criteria:
  `docs/ui-design.md`
- Implement the application in tested vertical slices, or review version 1
  scope, test boundaries, and cross-cutting acceptance gates:
  `docs/implementation-plan.md`
- Trace the architectural blocking findings that the normative documents
  reconcile: `docs/design-critique.md`
- Review prioritized UI risks, resolved interaction guidance, revised
  wireframes, accessibility behavior, and implementation-readiness criteria:
  `docs/ui-critique.md`
- Review licensing: `LICENSE`
- Change SDK/build/package policy: `global.json`, `Directory.Build.props`,
  `Directory.Packages.props`
- Change project boundaries or references: production/test `.csproj` files and
  `tests/PromptSaver.Application.Tests/SmokeTests.cs`
- Change prompt/intent/metadata validity, score boundaries, duplication,
  deletion, or merge rules: `src/PromptSaver.Domain/Entities`,
  `src/PromptSaver.Domain/ValueObjects`, `src/PromptSaver.Domain/Policies`,
  `tests/PromptSaver.Domain.Tests`
- Change deterministic extraction, intent normalization/scoring, provisional
  resolution, intent listing, reclassification, or merge coordination:
  `src/PromptSaver.Domain/Policies/DeterministicMetadataPolicy.cs`,
  `src/PromptSaver.Application/Policies/IntentResolutionEngine.cs`,
  `src/PromptSaver.Application/UseCases/{ApplicationServices,IntentProviderUseCases}.cs`,
  `src/PromptSaver.Desktop/ViewModels/SettingsViewModel.cs`,
  `src/PromptSaver.Desktop/Views/SettingsView.xaml`,
  `tests/PromptSaver.Domain.Tests/DeterministicIntentPolicyTests.cs`,
  `tests/PromptSaver.Application.Tests/ProviderPolicyTests.cs`,
  `tests/PromptSaver.Desktop.Tests/ViewModels/SettingsViewModelTests.cs`
- Change provider endpoint/privacy policy, Ollama/OpenAI-compatible adapters,
  discovery, response limits, Credential Manager storage, health checks, or
  enrichment scheduling:
  `src/PromptSaver.Application/Policies/ProviderPolicies.cs`,
  `src/PromptSaver.Application/Ports/ProviderPorts.cs`,
  `src/PromptSaver.Infrastructure/Providers/ProviderAdapters.cs`,
  `tests/PromptSaver.Infrastructure.Tests/ProviderAdapterTests.cs`
- Change data-root resolution, capture/config/edit drafts, SQLite schema,
  repositories, search indexing/query safety, merge/delete/duplicate storage,
  integrity, settings, or backup/restore:
  `src/PromptSaver.Infrastructure/Storage`,
  `src/PromptSaver.Infrastructure/Drafts`,
  `tests/PromptSaver.Infrastructure.Tests/PersistenceIntegrationTests.cs`
- Implement persistence, draft, search, provider, clipboard, credentials,
  backup, or other external adapters: contracts in
  `src/PromptSaver.Application/Ports`
- Implement or consume application use cases and view-model DTOs:
  `src/PromptSaver.Application/UseCases`,
  `src/PromptSaver.Application/Dtos`, `src/PromptSaver.Application/AppResult.cs`
- Change WPF startup or dependency registrations:
  `src/PromptSaver.Desktop/App.xaml`,
  `src/PromptSaver.Desktop/App.xaml.cs`,
  `src/PromptSaver.Desktop/Services/DesktopComposition.cs`,
  `src/PromptSaver.Desktop/MainWindow.xaml.cs`
- Change the compact top navigation, page actions, shared visual language, or
  light/dark theme:
  `src/PromptSaver.Desktop/{App,MainWindow}.xaml`,
  `src/PromptSaver.Desktop/MainWindow.xaml.cs`,
  `src/PromptSaver.Desktop/ViewModels/ShellViewModel.cs`,
  `src/PromptSaver.Desktop/Views`,
  `tests/PromptSaver.Desktop.Tests/ViewModels/ShellViewModelTests.cs`
- Change restart-level save/search/copy behavior or typed integration failures:
  `tests/PromptSaver.Infrastructure.Tests/ApplicationIntegrationTests.cs`
- Change CI or publish validation: `.github/workflows/ci.yml`,
  `.github/workflows/release-windows.yml`,
  `scripts/Build-WindowsPackages.ps1`,
  `scripts/Verify-PublishArchitecture.ps1`
- Change installer source, harvesting, upgrade/cross-architecture behavior,
  shortcuts, PATH/data-root safety, checksums, SBOM, or signing:
  `packaging/wix/PromptSaver.Installer`,
  `scripts/Generate-WixFiles.ps1`,
  `scripts/New-WindowsPackageManifest.ps1`,
  `scripts/Sign-WindowsArtifacts.ps1`,
  `tests/PromptSaver.Package.Tests`, `docs/windows-packaging.md`

## Conventions and tests

Architecture rules and test boundaries are defined in `docs/system-design.md`;
executable dependency and RID checks are in
`tests/PromptSaver.Application.Tests/SmokeTests.cs`. Domain validation and
policy boundaries are covered by `tests/PromptSaver.Domain.Tests`; application
result/DTO construction contracts are covered by
`tests/PromptSaver.Application.Tests/ApplicationContractTests.cs`. The
executable TDD order and acceptance matrix are in
`docs/implementation-plan.md`.

Use Microsoft Testing Platform commands with the pinned .NET 10 SDK:

```powershell
dotnet restore .\PromptSaver.sln --locked-mode
dotnet build .\PromptSaver.sln -c Release --no-restore -p:TreatWarningsAsErrors=true
dotnet test --project .\tests\PromptSaver.Domain.Tests\PromptSaver.Domain.Tests.csproj -c Release
dotnet test --project .\tests\PromptSaver.Application.Tests\PromptSaver.Application.Tests.csproj -c Release
dotnet test --project .\tests\PromptSaver.Infrastructure.Tests\PromptSaver.Infrastructure.Tests.csproj -c Release
dotnet test --project .\tests\PromptSaver.Desktop.Tests\PromptSaver.Desktop.Tests.csproj -c Release
dotnet format .\PromptSaver.sln --verify-no-changes
```

Artifact tests compile with the solution; package tests skip clearly until
`PROMPTSAVER_ARTIFACTS` identifies built artifacts. WiX 7 was rejected after
WIX7015 required authorized OSMF EULA acceptance. WiX 4.0.6 builds both
architectures without accepting new terms; see `docs/windows-packaging.md`.

## Freshness

- Based on commit `b62a78f`; prompt detail now supports an explicit,
  suggestion-only LLM intent re-query for saved prompts.
- Current UI/provider reliability changes considered: compact top navigation
  with an immediate light/dark toggle, shared modern card/control resources,
  top-profile capture/provider actions, nullable application results,
  defensive provider errors, and SQLite hydration of provider/proposal IDs
  from relational keys.
- Windows packaging additions considered:
  `src/PromptSaver.Desktop/Properties/PublishProfiles`,
  `packaging/wix/PromptSaver.Installer/{Package,Folders}.wxs`,
  `scripts/{Build-WindowsPackages,Generate-WixFiles,Verify-PublishArchitecture,New-WindowsPackageManifest,Sign-WindowsArtifacts}.ps1`,
  `tests/PromptSaver.Package.Tests/ArtifactSmokeTests.cs`,
  `.github/workflows/{ci,release-windows}.yml`, and
  `docs/windows-packaging.md`
- Uncommitted Slice 0 additions considered: `PromptSaver.sln`, `global.json`,
  `Directory.Build.props`, `Directory.Packages.props`, `.gitignore`, `src`,
  `tests`, `packaging`, `scripts`, `.github/workflows/ci.yml`
- Uncommitted shared contract additions considered:
  `src/PromptSaver.Domain/{Entities,ValueObjects,Policies}`,
  `src/PromptSaver.Application/{Dtos,Ports,UseCases}`,
  `tests/PromptSaver.Domain.Tests`, and
  `tests/PromptSaver.Application.Tests/ApplicationContractTests.cs`
- Uncommitted intent/provider slice additions considered:
  `src/PromptSaver.Domain/Policies/DeterministicMetadataPolicy.cs`,
  `src/PromptSaver.Application/Policies`,
  `src/PromptSaver.Infrastructure/Providers`,
  `tests/PromptSaver.Domain.Tests/DeterministicIntentPolicyTests.cs`,
  `tests/PromptSaver.Application.Tests/ProviderPolicyTests.cs`, and
  `tests/PromptSaver.Infrastructure.Tests/ProviderAdapterTests.cs`
- Uncommitted persistence slice additions considered:
  `src/PromptSaver.Infrastructure/{Storage,Drafts}` and
  `tests/PromptSaver.Infrastructure.Tests/PersistenceIntegrationTests.cs`
- Uncommitted integration additions considered: `README.md`,
  `src/PromptSaver.Application/UseCases/{ApplicationServices,ProviderApplicationService}.cs`,
  `src/PromptSaver.Infrastructure/Storage/SqliteProviderStores.cs`,
  `src/PromptSaver.Infrastructure/Providers/MultiProviderEnrichmentService.cs`,
  `src/PromptSaver.Desktop/Services/{DesktopComposition,SystemServices}.cs`,
  WPF composition/view-model wiring, and
  `tests/PromptSaver.Infrastructure.Tests/ApplicationIntegrationTests.cs`
- Quality-remediation additions considered: committed capture result
  construction without a post-commit read, debounced draft draining, archived
  intent revival, cached SQLite initialization, atomic enrichment claims,
  remote credential/health-check settings, reachable Library/Capture review
  workflows, editable user metadata and provisional-intent acceptance,
  adjacent-result delete focus, secure credential allocation, explicit
  enrichment persistence failures, and their Application, Infrastructure, and
  Desktop regression tests.
- Final verification additions considered: Settings intent listing with prompt
  counts, confirmed source-to-target merge and prompt reclassification UI,
  updated README navigation, and refreshed full native package artifacts.
