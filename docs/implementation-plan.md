# Prompt Saver Implementation Plan

- **Status:** Ready for implementation
- **Date:** 2026-10-01
- **Method:** Thin vertical slices with tests written before production behavior

## 1. Version 1 outcome

Version 1 is complete when a user can:

1. launch into a focused editor before optional infrastructure is ready;
2. recover a crash-safe capture or edit draft;
3. save, edit, duplicate, delete, search, and copy prompts locally;
4. receive deterministic skills/entities and a reusable intent assignment;
5. understand and correct an automatic, provisional, or `Unsorted` intent;
6. merge duplicate intents;
7. optionally enrich metadata through discovered/configured Ollama or an
   API-key OpenAI-compatible endpoint without weakening privacy;
8. back up and restore local data;
9. use the primary workflows with keyboard and Narrator;
10. install and run native x64 or ARM64 MSI packages while retaining user data.

Deferred work is listed in `docs/system-design.md`; do not pull it into version
1 without a new decision.

## 2. Project structure

Create:

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
    Storage/Migrations/
    Storage/Repositories/
    Storage/Search/
    Drafts/
    Providers/
    Credentials/
    Configuration/
    Diagnostics/
  PromptSaver.Desktop/
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
  wix/PromptSaver.Installer/
scripts/
  Generate-WixFiles.ps1
  Verify-PublishArchitecture.ps1
  Seed-SearchCorpus.ps1
```

References:

```text
Application -> Domain
Infrastructure -> Application + Domain
Desktop -> Application + Infrastructure
Domain.Tests -> Domain
Application.Tests -> Application + Domain
Infrastructure.Tests -> Infrastructure + Application + Domain
Desktop.Tests -> Desktop + Application + Domain
```

No production project references a test project. Installer code consumes
publish output, not project references.

## 3. Exact toolchain and packages

Use .NET SDK `10.0.401`, `net10.0-windows`, C# default language version for the
pinned SDK, nullable references, central package management, and lock files.

Initial exact pins:

```text
CommunityToolkit.Mvvm                  8.4.2
Microsoft.Extensions.DependencyInjection 10.0.12
Microsoft.Extensions.Http            10.0.12
Microsoft.Extensions.Logging.Abstractions 10.0.12
Microsoft.Data.Sqlite                 10.0.12
Microsoft.NET.Test.Sdk               18.10.1
xunit.v3                              4.0.1
xunit.runner.visualstudio            4.0.0
coverlet.collector                    10.0.1
FlaUI.Core                            5.0.0
FlaUI.UIA3                            5.0.0
WixToolset.Sdk                        4.0.6
```

These exact versions were found in the configured NuGet feed on 2026-10-01.
Slice 0 must prove a clean restore for every exact pin before any feature code.
If a pin is unavailable or incompatible, update both this file and
`docs/technology-decision.md` in the same change with the verified exact
replacement. Do not float versions.

Use no ORM, provider SDK, embeddings library, retry library, custom control
suite, or external JSON library in version 1.

## 4. TDD working agreement

For each slice:

1. Write a failing test at the lowest owning boundary.
2. Add the minimum Domain/Application behavior.
3. Add an on-disk SQLite or file/HTTP integration test when infrastructure is
   involved.
4. Add view-model tests for user-visible state, focus requests, enabled rules,
   announcements, and error mapping.
5. Bind the tested view model to WPF with standard controls.
6. Run the slice tests, then all fast tests.
7. Add release-only UIA/package/performance coverage when the slice crosses
   those boundaries.

Tests assert outcomes, persisted shapes, and error types. Do not mock SQL,
SQLite FTS behavior, draft file atomicity, HTTP serialization, or architecture
metadata.

Default fast suite:

```text
Domain.Tests
Application.Tests
Infrastructure.Tests
Desktop.Tests
```

Release/artifact suite:

```text
UiAutomation.Tests
Package.Tests
Performance.Tests
native install/upgrade/uninstall scripts
manual Narrator and visual accessibility checklist
```

## 5. Vertical slices

### Slice 0 - Reproducible scaffold

**Failing checks first**

- clean restore fails until all exact package pins and lock files exist;
- architecture test fails until projects explicitly support `win-x64` and
  `win-arm64`;
- dependency-direction test fails on prohibited project references.

**Implement**

- solution/projects/references;
- `global.json`, central packages, build props, analyzers, warnings-as-errors;
- test categories and artifact-dependent skip helper;
- CI skeleton for fast tests and cross-publish.

**Acceptance**

- clean clone restores and builds with the pinned SDK;
- Domain and Application have no forbidden references;
- a trivial test runs in each fast test project;
- ARM64 and x64 Desktop publish commands reach compilation.

### Slice 1 - Immediate shell and capture draft

**Tests first**

- `SaveCaptureDraft` preserves exact Unicode and line breaks;
- atomic replacement survives an interrupted temp write;
- corrupt draft file yields a typed recovery result and does not erase it;
- view model enters `DraftBackedUp` after debounce;
- failed draft write retains text and exposes Copy/Try again;
- launch focus request targets the loaded editor.

**Implement**

- `IDataRootResolver`;
- `capture-draft.json` schema and atomic store;
- small `config.json` bootstrap store;
- WPF shell, multiline editor, fixed status region, debounced draft backup;
- recovered caret/selection validation;
- emergency memory-backed mode.

**Acceptance**

- a user can type before SQLite is initialized;
- crash/relaunch restores body and valid selection;
- draft status never implies a Library save;
- no provider or database call is on the editor-ready path.

### Slice 2 - Local prompt commit and saved-state UX

**Tests first**

- whitespace-only and >256 KiB bodies are rejected without changing text;
- a successful commit creates one prompt and deletes capture draft afterward;
- a failed commit does not delete draft or clear editor;
- repeat command execution while in flight is suppressed;
- `Ctrl+Shift+C` copies draft text without creating a prompt;
- copy count increments only after clipboard success.

**Implement**

- migration runner and initial tables;
- prompt repository/unit of work;
- `CapturePrompt` and `CopyPrompt`;
- capture state transitions and shortcuts;
- local save error mapping.

**Acceptance**

- `Ctrl+Enter` persists locally and keeps focus/selection/undo context;
- save acknowledgement precedes optional enrichment;
- copy is independent from save;
- the UTF-8 limit is visible from 80% and never truncates automatically.

### Slice 3 - Deterministic metadata and intent policy

**Tests first**

- controlled verb-object extraction golden corpus;
- low-information prompts assign `Unsorted` and create no new intent;
- 100 unrelated low-information prompts create zero user intents;
- exact/alias normalization and archived exclusion;
- median lexical scoring plus shared object-token guard;
- adversarial near misses such as PPT/PDF and customer/partner do not merge;
- `8499` creates provisional/review, `8500` maps, `8501` maps;
- deterministic tie break;
- below-threshold candidates persist with rank, score, and algorithm version.

**Implement**

- intent, aliases, skills, entities, relations, candidates;
- normalization/extractor/scoring policies;
- seeded stable `Unsorted` intent;
- deterministic title and metadata persistence.

**Acceptance**

- every saved prompt has an intent assignment;
- weak extraction does not grow the taxonomy;
- below 85% creates a provisional intent only for valid verb-object candidates;
- top five alternatives >=6000 are reviewable after restart.

### Slice 4 - Repository-owned FTS and Library search

**Tests first**

- no FTS maintenance triggers exist;
- changing body, title, intent, alias, skill, entity, merge, or delete removes
  old terms and adds new terms in one transaction;
- FTS integrity-check passes after each mutation family;
- `C#`, `C++`, dotted, hyphenated, underscored, accented, ligature, and
  full-width terms round trip;
- quotes and FTS operator punctuation never throw;
- structured filters are parameterized and allowlisted;
- bare local dates convert correctly over a DST boundary;
- seeded 50,000-prompt corpus meets search target on release hardware.

**Implement**

- normalized `prompt_search_documents`;
- external-content FTS5 with the specified `unicode61` tokenizer;
- `ReplaceSearchDocument`, rebuild, and integrity operations;
- typed query parser and local-date bound conversion;
- Library view model, Filters flyout, virtualized paged results.

**Acceptance**

- search covers prompt body/title, intent/aliases, skills, and entities;
- malformed literal input cannot surface a SQLite syntax error;
- focus and result selection remain stable during incremental search;
- search announcement is separately debounced.

### Slice 5 - Prompt detail and edit draft

**Tests first**

- one edit draft exists per prompt and records base version;
- navigation choices save, keep draft, or discard;
- concurrency conflict offers reload or duplicate;
- provider proposal cannot mutate an open edit draft;
- title precedence is user > accepted provider > derived.

**Implement**

- `prompt_edit_drafts`;
- detail/edit use cases and view model;
- focus-heading-on-open and F6 region behavior;
- metadata summary/editor dialogs.

**Acceptance**

- uncommitted edits survive restart but are not searchable or enriched;
- opening detail does not unexpectedly enter edit focus;
- commit updates the search index atomically;
- background metadata appears as a suggestion.

### Slice 6 - Intent review, reclassification, and merge

**Tests first**

- review later retains provisional/`Unsorted` assignment and marks skipped;
- keep/choose/rename resolves review;
- normalized rename identity-resolves without duplicates;
- merge reassigns prompts, moves aliases, archives source, and rebuilds FTS;
- merge rejects `Unsorted`;
- user assignment cannot be overwritten by background enrichment.

**Implement**

- intent review panel;
- Library filter for Needs review/Skipped;
- reclassification and merge use cases;
- archive/unarchive intent management.

**Acceptance**

- all persisted review candidates render after restart;
- applying review changes no prompt text;
- merged source intents no longer participate in automatic matching;
- screen reader announces confidence and review state meaningfully.

### Slice 7 - Duplicate and permanent delete

**Tests first**

- duplicate copies accepted content/metadata but not IDs, copy history,
  proposals, candidates, or drafts;
- delete issues FTS delete and cascades dependent rows;
- orphan skills/entities are removed;
- orphan intents remain archive-eligible;
- delete focus moves to the correct adjacent result.

**Implement**

- duplicate/delete use cases;
- named permanent-delete dialog with Cancel default;
- result-list focus recovery.

**Acceptance**

- duplicate creates an independently editable prompt;
- deleted terms produce zero search results and FTS integrity passes;
- shared intents and metadata records still referenced by other prompts remain.

### Slice 8 - Automatic Ollama discovery and configuration

**Tests first**

- discovery starts only after first render;
- it probes only `127.0.0.1`, follows no redirects, sends no user data, and
  respects 500 ms connect/1 second total timeouts;
- success shows an affordance but never enables a provider;
- dismissal and do-not-suggest behavior persist;
- Settings model discovery is cancellable and distinguishes error categories.

**Implement**

- discovery service and post-render scheduler;
- AI metadata Settings page;
- Ollama provider configuration/model test;
- scoped provider status.

**Acceptance**

- a running local Ollama instance is discoverable without visiting Settings;
- no startup focus delay is attributable to discovery;
- default endpoint is exactly `http://127.0.0.1:11434`.

### Slice 9 - Provider enrichment and privacy boundary

**Tests first**

- request contains at most 8192 valid UTF-8 bytes and a truncation flag;
- no existing intent names, IDs, metadata, or other prompts are sent;
- response over 64 KiB or exceeding item/value caps is rejected;
- logs contain no prompt, entity, credential, authorization header, or body;
- HTTP policy accepts loopback plain HTTP, requires HTTPS remotely, and handles
  explicit remote-HTTP acknowledgement;
- open prompts receive proposals, not background writes;
- one worker and 20-item session cap are enforced.

**Implement**

- direct-HTTP Ollama and OpenAI-compatible adapters;
- API-key Credential Manager adapter;
- persisted enrichment attempts/proposals;
- bounded scheduler/retry policy;
- reviewable provider suggestions.

**Acceptance**

- provider failure leaves prompt saved and locally searchable;
- privacy text matches the serialized request exactly;
- API keys never enter SQLite/config/logs;
- Entra, embeddings, and historical backfill remain absent.

### Slice 10 - Backup, migration, and recovery

**Tests first**

- migration backup runs only when migration is pending;
- daily backup runs after first render only when data changed;
- restore preserves current database first;
- unclean shutdown schedules quick-check;
- failed integrity/migration prevents writes but preserves emergency editor;
- backup/restore retains prompts, metadata, review candidates, and FTS integrity.

**Implement**

- immutable/checksummed migration runner;
- SQLite online backup and seven-daily retention;
- Data and Recovery Settings page;
- search rebuild and recovery states.

**Acceptance**

- normal launch does not run quick-check or backup before editor readiness;
- migration progress cannot block typing/copying;
- restore is explicit and produces a recoverable prior-database copy.

### Slice 11 - Accessibility and compact-layout qualification

**Tests first**

- view-model focus contracts and deduplicated announcements;
- Automation IDs/names for editor, status, search, result list/actions,
  metadata editors, intent review, and dialogs;
- deletion/flyout/dialog focus return;
- no autosave/enrichment/result update changes focus.

**Implement**

- adaptive rail/top-row layouts;
- standard control templates with all visual states;
- UIA peers/properties where standard peers are insufficient;
- contrast/reduced-motion/text-scaling resources.

**Acceptance**

- keyboard-only launch, paste, save, copy, new prompt, search, open, review,
  duplicate, delete, provider recovery, and restore navigation;
- Narrator completes primary workflows with concise stable output;
- 560 x 420 and 200% scaling retain editor, status, and primary actions;
- light/dark/contrast themes show focus, selection, errors, and disabled states.

### Slice 12 - MSI packaging and native architecture gates

**Tests first**

- publish verifier rejects wrong PE or SQLite native architecture;
- installer metadata matches requested platform;
- package tests skip clearly without `PROMPTSAVER_ARTIFACTS`;
- upgrade retains data and downgrade is blocked;
- cross-architecture replacement displays uninstall-first guidance.

**Implement**

- ReadyToRun self-contained publish profiles;
- deterministic WiX file generation;
- canonical WiX project invoked for x64 and ARM64;
- PR package metadata workflow and protected signed release workflow.

**Acceptance**

- native x64 and ARM64 install, launch, focus, type, save, search, and uninstall
  smoke tests pass;
- data survives repair, same-architecture upgrade, and uninstall;
- native ARM64 tests cover x64-installed-on-ARM64 and transition guidance;
- release artifacts are architecture-labeled, signed, timestamped, checksummed,
  and accompanied by an SBOM.

## 6. Cross-cutting acceptance matrix

| Concern | Required proof |
| --- | --- |
| Fast entry | Native release measurement from process activation to accepted editor input; provider/database work excluded from hard path |
| Draft safety | Crash/restart and atomic-write failure tests for capture and existing-prompt drafts |
| 85% behavior | Domain boundary tests at 8499/8500/8501 plus persisted candidate/review tests |
| No intent explosion | 100 low-information prompts create zero user intents |
| FTS correctness | Mutation/removal/integrity suite for every contributing source and special token/query cases |
| Privacy | Serialized request assertions, payload byte cap, credential and log redaction tests |
| Data semantics | Duplicate/delete/cascade/orphan/merge integration tests |
| Accessibility | View-model focus tests, FlaUI UIA release suite, manual Narrator/contrast/200% scaling checklist |
| ARM64 | Native startup, SQLite, UIA, publish, MSI install/upgrade/uninstall, and performance results |
| Data root | Install/upgrade/repair/uninstall tests asserting `%LOCALAPPDATA%\PromptSaver` identity and retention |

## 7. Definition of done for each change

A slice is not done until:

- its failing tests were observed before implementation;
- all fast tests pass;
- formatting and warnings-as-errors pass;
- related design/acceptance text remains accurate;
- no deferred feature leaked into version 1;
- error and privacy behavior is tested, not only the success path;
- any artifact-dependent behavior has a release-stage test or documented manual
  gate with an owner and environment.

Version 1 is not release-ready until both native architecture gates pass.
