# Prompt Saver Design Critique

- **Status:** Review feedback (no source document was modified)
- **Date:** 2026-10-01
- **Scope reviewed:** `prompts/initial-prompt.md`, `docs/system-design.md`, `docs/ui-design.md`, `docs/technology-decision.md`
- **Role:** Oppositional review. Findings are ranked by severity and each one states impact and a specific implementable fix.

## Summary judgment

The architecture is coherent, privacy-conscious, and unusually well specified for a pre-code repository. The layering, local-first save path, deterministic fallback, and integer basis-point threshold are good decisions and should be kept.

However, the three design documents are **not yet mutually consistent on two decisions that drive the product's core value**: what happens below the 85% intent threshold, and which installer is the release artifact. There is also a measurable conflict between the stated startup budget and the mandated startup work, an FTS5 external-content ownership hazard that can corrupt search, and a deterministic-intent fallback that will flood the intent taxonomy in the default (no-LLM) configuration. Those should be settled before implementation begins, because each one changes domain types, schema, or CI shape.

Finding counts: **8 blocking**, **11 non-blocking**, **9 suggestions**.

---

## Blocking issues

### B1. The 85% threshold has two contradictory behaviors across documents

**Issue.** `system-design.md` §9 states the rule is unconditional: below 8500 basis points, "create a new intent from the rationalized candidate and assign it to the prompt." §4 reinforces this — `Prompt.IntentId` is non-nullable and "always assigned by the save/enrichment workflow." `ui-design.md` §7 states the opposite for the same score band: "Do not silently map," show **Needs review**, offer ranked candidates, and allow **Skip for now**, leaving the prompt under **Intent not reviewed**. `initial-prompt.md` says "a score of 85% would map automatically; lower would create a new one," which matches the system design and not the UI.

**Impact.** This is not a wording difference; it is two different domain models. The UI's "Intent not reviewed" and "Skip for now" states cannot be represented: `IntentId` is non-nullable, `IntentAssignment` has no `PendingReview`/`Skipped` member, and `MetadataStatus` has no review state. The acceptance criteria in `ui-design.md` §18 ("A score below 85% does not silently assign an existing intent") and the required test in `system-design.md` §16 ("`8499` creates") are both satisfiable only under different implementations. Every intent-related test, view model, and Library filter depends on which one wins.

**Recommendation.** Reconcile explicitly rather than letting the implementer pick. The reconciliation that satisfies all three documents:

1. Below 8500, auto-create the new intent as the brief requires, but persist `IntentAssignment = Created` **plus** a new `IntentReviewState` of `NeedsReview` when any candidate scored within a configurable review band (suggest 6000–8499).
2. Add `IntentReviewState { NotRequired, NeedsReview, Skipped, Resolved }` to `Prompt`, and persist the top N scored candidates (intent ID + score) for the review panel in a `prompt_intent_candidates` table — the UI panel in §7 cannot render ranked candidates with percentages unless those scores are stored at decision time.
3. Treat the UI's "Intent not reviewed" label as a rendering of `NeedsReview`/`Skipped`, not as "no intent assigned."
4. Record this reconciliation in both documents and add the explicit test: *below threshold creates an intent AND flags review; user skip does not clear the assignment; user confirm sets `UserSelected`/`UserCorrected` and clears review.*

### B2. The release packaging channel is specified two different ways

**Issue.** `system-design.md` §2 and §17 make **framework-dependent MSIX** (plus `.msixbundle`) the primary distribution, with WiX MSI as fallback. `technology-decision.md` (Decision, Packaging) makes **self-contained per-architecture WiX MSI** the required release path and demotes MSIX to a `packaging/msix/README.md` placeholder. The technology decision is marked **Accepted**, but the system design was not updated to match, and `system-design.md` §16 still requires package tests for "ARM64 package selected on ARM devices" (an MSIX bundle behavior that MSI does not have) and "signed-package verification."

**Impact.** Framework-dependent vs self-contained is a ~100 MB artifact-size difference, a different target-machine prerequisite, a different startup profile, and a different CI job matrix. `PromptSaver.Package.Tests` cannot be written until this is settled, and the tests currently described would fail against the accepted decision.

**Recommendation.** Amend `system-design.md` §2/§17 to defer to the accepted decision: self-contained per-RID WiX MSI is the v1 release channel; MSIX is a deferred channel. Replace the "ARM64 package selected on ARM devices" package test with an architecture-detection test on the *download/landing* path plus a PE-machine-type assertion on each MSI's payload. Keep signed-package verification only in the release workflow.

### B3. MSIX would silently break the "single data root" requirement

**Issue.** Both the brief ("save them in a single location") and `system-design.md` §5 depend on `%LOCALAPPDATA%\PromptSaver\` being the one discoverable data root, with an explicit **Open folder** action in Settings. A full-trust packaged (MSIX) desktop app has its per-user AppData writes redirected into `%LOCALAPPDATA%\Packages\<PackageFamilyName>\LocalCache\Local\`. §17 simultaneously promises MSIX as primary and that "the user database remains outside the install directory and survives upgrades/uninstall."

**Impact.** If MSIX ever ships alongside or after MSI, the same user gets two databases in two locations, the **Open folder** action points somewhere the user did not expect, and there is no stated migration path between them. MSIX uninstall also removes the package-local app data by default, which directly violates the retention promise.

**Recommendation.** If MSIX is retained as a future channel, require now that the data-root resolver be a single infrastructure service that (a) detects package identity, (b) resolves to a stable unvirtualized path, and (c) has an integration test asserting the resolved path is identical packaged and unpackaged. Add a one-way migration step with an integration test. If MSIX is dropped per B2, state that the data root is MSI-only and delete the conflicting §17 promise.

### B4. FTS5 external-content synchronization has two owners and a known corruption mode

**Issue.** `system-design.md` §5 says the FTS5 table uses `prompt_search_documents` as its external content table, that **SQLite triggers** cover prompt/title changes, that **repository code** rebuilds the row when intent/skill/entity relations change, and that the FTS table "is synchronized by triggers." `technology-decision.md` hedges further: "either through explicit repository writes or tested triggers."

**Impact.** External-content FTS5 requires the special `'delete'` command to be issued with the **pre-update column values** before the content row changes. Mixing trigger-driven maintenance with repository-driven rebuilds of the same row is the standard recipe for the index retaining stale terms or for `database disk image is malformed` on subsequent queries. Two owners also means a trigger and a repository write can both fire in one transaction and double-insert. Search correctness is a core requirement, and this failure is silent until a user notices missing or phantom results.

**Recommendation.** Pick exactly one owner and say so. Recommended: **repository-only**, no triggers.

1. Every write path that touches title, body, intent, skills, or entities calls a single `UpsertSearchDocument(promptId)` inside the same transaction.
2. That method issues `INSERT INTO prompt_search_fts(prompt_search_fts, rowid, title, body, intent_text, skill_text, entity_text) VALUES('delete', ...)` using the *old* row read in the same transaction, then updates `prompt_search_documents`, then inserts the new FTS row.
3. Expose an admin/repair `'rebuild'` path and run `INSERT INTO prompt_search_fts(prompt_search_fts) VALUES('integrity-check')` in the migration and backup integration tests.
4. Add an integration test that mutates each of the five contributing sources and asserts the index has no stale terms (search for the removed term must return zero rows).

### B5. The mandated startup work contradicts the p50 < 400 ms startup target

**Issue.** `system-design.md` §12 sets "process start to editable focused window" at p50 < 400 ms / p95 < 800 ms, and lists step 3 of the pre-render critical path as "open SQLite and run only required transactional migrations." §14 then requires that on open the app must `PRAGMA quick_check` the database and "create a consistent pre-migration backup using the SQLite backup API." Both are O(database size). Meanwhile `technology-decision.md` publishes self-contained, non-R2R, non-trimmed multi-file WPF output.

**Impact.** `quick_check` plus a full online backup on a mature database is seconds, not milliseconds, and it sits in front of the single most important product metric ("the first keystroke is the primary success metric"). Separately, cold-start WPF with JIT-only self-contained output on ARM64 realistically lands well above 400 ms before any of this work. The budget as written will be missed on the first real measurement, and there is no stated per-architecture budget even though ARM64 is a required target.

**Recommendation.**

1. Remove SQLite open from the pre-render critical path entirely. Render the editor from `config.json` plus the draft file (see B6), and open the database lazily on the first save/search or on a background thread after first render.
2. Run `quick_check` only after an unclean-shutdown marker, and only in the background.
3. Take the pre-migration backup **only when a migration is actually pending**, and show a determinate progress surface for it — it is a legitimate multi-second operation.
4. Split the budget into "process start → editable focused window" (the hard target) and "database ready" (a soft target), and publish separate x64 and ARM64 numbers.
5. Enable `PublishReadyToRun=true` from the start rather than "after measurement." It is low-risk relative to trimming/AOT and is the single largest lever on WPF cold start.

### B6. Draft autosave and recovery are required by the UI but absent from the system design

**Issue.** `ui-design.md` mandates draft autosave with debounce, a **Recovered draft** state, "Draft saved now" in the capture chrome, per-prompt "draft revision" autosave in Prompt Detail, a three-way **Save changes / Keep as draft / Discard edits** navigation prompt, and the acceptance criterion "An unsaved draft is restored after an unexpected close or restart." `system-design.md` has no draft concept anywhere: not in §4's domain model, not in §5's table list, not in §6's save flow, and not in §18's use cases.

**Impact.** A top-level acceptance criterion and a visible UI state have no backing design, no storage, no tests, and no error semantics. Drafts also interact with nearly everything already specified: Do drafts appear in search? Do they get a `ContentHash`? Does a Detail draft revision bump `Version` and trip optimistic concurrency against background enrichment? None of that is answerable today.

**Recommendation.** Add an explicit draft design before implementation:

- One `drafts` concept with a nullable `PromptId` (null = new-capture draft, non-null = uncommitted edit to an existing prompt), `Body`, `UpdatedAtUtc`.
- Store the new-capture draft as a **separate small file** under the data root, not in SQLite, so it can be read and rendered before the database opens (this is what makes B5's lazy-open workable) and so a corrupt database cannot lose in-flight text.
- State explicitly: drafts are not searchable, are not enriched, have no intent, and are deleted on successful commit.
- Add use cases `SaveDraft`, `RecoverDraft`, `DiscardDraft` to §18 and tests for crash-recovery, concurrent commit, and draft-write-failure (which `ui-design.md` already says is the only case that may block window close).

### B7. The deterministic (default) path will flood the intent taxonomy

**Issue.** Providers are disabled by default (§11, "Local-only operation is the default; providers are disabled until configured"), so the deterministic extractor in §8 is what most users will actually run. Its fallback produces `save prompt about <top keywords>`, and §9 then unconditionally creates a new intent whenever the best score is below 8500. Since keyword-derived phrases rarely resemble each other, almost every prompt will mint its own intent.

**Impact.** This defeats the brief's central idea. The brief's examples — "draw a diagram for a customer," "write utility application," "create a PPT" — are coarse reusable buckets. A user with no LLM will instead accumulate hundreds of one-prompt intents, which makes the Library's intent filter useless and makes later LLM-enabled rationalization a large cleanup problem. There is also no `MergeIntents` use case in §18 to clean it up.

**Recommendation.**

1. Do not create an intent from the low-information fallback. When the deterministic extractor cannot derive a verb-object clause, assign a reserved, well-known `Unsorted` intent and set `NeedsReview` (per B1) rather than minting a new one.
2. Constrain deterministic intent creation to the controlled verb set in §8 step 5 plus a recognized object noun phrase; anything else goes to `Unsorted`.
3. Add `MergeIntents` (with alias absorption and prompt reassignment) to §18 — over- and under-matching are both certain, and there is currently no recovery path.
4. Add a test asserting that N unrelated prompts saved with no provider produce at most a bounded number of intents.

### B8. "If an LLM is available" is never detected, so the brief's intent feature is off by default

**Issue.** The brief says intent derivation happens "if an LLM is available such as local Ollama or a Foundry OpenAI endpoint." `system-design.md` §7 states Ollama health "is checked from the settings screen and lazily before enrichment, not during startup," and §11 states providers are disabled until configured. Nothing ever notices that Ollama is running on the machine.

**Impact.** Combined with B7, the out-of-box experience delivers neither good intents nor any prompt that a better experience exists. The headline feature of the brief is reachable only by a user who independently navigates to Settings.

**Recommendation.** Add a **post-first-render, non-blocking, local-only** probe of `http://127.0.0.1:11434` with a short timeout. On success, surface a dismissible, non-modal status affordance ("Local Ollama detected — enable AI metadata?"). Do not auto-enable, do not probe remote endpoints, and never run the probe before first render. This satisfies "if available" without violating the privacy stance, because the probe sends no prompt content and targets loopback only.

---

## Non-blocking issues

### N1. Keyboard shortcuts disagree between documents

`system-design.md` §6 defines `Ctrl+K` for search focus and `Ctrl+Shift+C` as **save and copy** on the capture surface. `ui-design.md` §10 defines `Ctrl+F` for search and `Ctrl+Shift+C` as **copy the current or selected prompt body** (with `Ctrl+Enter` as save). `Ctrl+N` and `Ctrl+S` appear only in the UI document. **Fix:** make `ui-design.md` §10 the single normative table, delete the §6 list or replace it with a pointer, and decide whether a save-and-copy accelerator exists at all (recommend yes, as `Ctrl+Shift+Enter`, since it is the brief's actual workflow: capture then paste elsewhere).

### N2. Pre-save intent confidence is shown in the UI but forbidden by the design

`ui-design.md` §3 shows the collapsed capture metadata panel rendering `Intent  Draw a diagram for a customer  [Suggested · 91%]` **before** save. Enrichment only runs after the local commit (§6), so the only metadata available at that moment is deterministic — and §8 explicitly says the deterministic result "is not presented as model-generated confidence." **Fix:** in the pre-save panel, show the deterministic intent with a provenance label (**Detected locally**) and no percentage. Percentages appear only on Prompt Detail after a scored decision exists.

### N3. "Most copied" sort has no backing field

`ui-design.md` §4 offers a **Most copied** sort "if copy count is retained." The domain has only `LastCopiedAtUtc`. **Fix:** either add `CopyCount` to `prompts` (cheap, and genuinely useful for the reuse workflow the brief describes) or delete the sort option. Do not leave it conditional — it changes the schema.

### N4. Delete and duplicate are UI actions with no use case, no cascade policy, and no GC

`ui-design.md` §5 exposes **delete** and **duplicate** in the result overflow menu, but §18 of the system design lists neither `DeletePrompt` nor `DuplicatePrompt`. Nothing states what happens to the FTS row, `prompt_skills`/`prompt_entities` relations, orphaned skills/entities, or an intent whose last prompt was deleted. **Fix:** add both use cases; specify `ON DELETE CASCADE` for relation tables plus explicit FTS `'delete'`; specify that orphaned skills/entities are garbage-collected and that orphaned intents are retained but flagged archivable. Add an integration test asserting no orphan rows and no stale FTS terms after delete.

### N5. Semantic-vs-lexical scoring makes the 85% rule configuration-dependent and the cosine mapping is undefined

§9 computes `final = 0.65*semantic + 0.35*lexical` when an embedding provider is configured, and `final = lexical` otherwise. The same prompt/intent pair can therefore map in one configuration and create in another, and enabling embeddings later silently changes behavior without reconciling historical assignments. Separately, "embedding cosine similarity mapped to 0..10000" is undefined for negative cosine — clamp-at-zero and `(c+1)/2` give materially different results at the threshold. **Fix:** define the mapping explicitly (recommend clamp to `[0,1]` then scale, and reject vectors that are not unit-normalized); persist `ScoringAlgorithmVersion` on every assignment (§4 already has the slot — use it); and state that changing scoring mode does not retroactively reassign, only flags for re-review.

### N6. `max()` of three lexical metrics will over-merge semantically distinct intents

Taking the maximum of token-set Dice, trigram cosine, and normalized edit similarity is systematically optimistic on the short strings intents use (3–160 chars). `draw a diagram for a customer` vs `draw a diagram for a partner` scores very high on trigram cosine and edit similarity while being a different reusable goal; `create a PPT` vs `create a PDF` is worse. An 85% threshold on this blend is an unvalidated guess. **Fix:** (a) require agreement — take the median, or require two of three metrics ≥ threshold; (b) add a guardrail that the object noun phrase must share at least one non-stopword token; (c) build an adversarial golden corpus of near-miss pairs that **must not** map, alongside the §16 golden cases that must. The threshold is fixed at 85% by the brief, so correctness has to come from the scoring function, not from tuning the threshold.

### N7. FTS5 tokenizer and query escaping are unspecified, and the alias catalog depends on characters the default tokenizer drops

§9 deliberately preserves `+`, `#`, and internal hyphens during normalization (so `C#` and `C++` survive), but FTS5's default `unicode61` tokenizer strips exactly those characters — so `skill:c#` will not match the indexed form. Separately, §5 says the parser "never concatenates raw user input into SQL," which is necessary but not sufficient: the *bound* MATCH expression is itself a mini-language, and unescaped `"`, `*`, `-`, `:`, `(`, or `^` in a user phrase raises a syntax error rather than returning results. **Fix:** specify `tokenize = "unicode61 remove_diacritics 2 tokenchars '#+.-_'"`, document that changing the tokenizer is an FTS rebuild migration, and require the query builder to quote every user token and double internal quotes. Add a fuzz/property test that arbitrary input never throws and never returns a SQL error.

### N8. Date filters have no stated timezone semantics

`after:2026-09-01` is typed in local time; all timestamps are UTC instants. Nothing says which is used. **Fix:** state that bare dates are interpreted in the user's local timezone and converted to UTC bounds at parse time, and test around a DST boundary.

### N9. The prompt size limit is invisible in the UI

`system-design.md` §4 caps `Body` at 256 KiB UTF-8, and §6 validates length. `ui-design.md` shows a `0 characters` counter with no maximum, disables **Save prompt** only for empty/whitespace input, and has no empty/error state for oversize content. A user pasting a large transcript hits a validation failure only at save. **Fix:** render the limit in the counter as it is approached, warn before save, and define whether oversize paste is rejected or truncated-with-confirmation. Also cap what is sent to a provider (see N10).

### N10. Provider request content is uncapped and the "intent candidates" payload expands the privacy surface

§11 says prompt content and "existing intent candidates may be sent for enrichment," but §9 says the provider "may not select an intent ID or similarity score." If the provider cannot choose, sending the user's whole intent taxonomy to a cloud endpoint buys little and leaks the user's project structure. There is also no cap on body size sent, so a 256 KiB prompt can blow context limits, cost, and the 20-second timeout. **Fix:** cap provider input (suggest first ~8 KiB plus a note that it was truncated), state in Settings exactly what is transmitted, and either drop the candidate list from the payload or cap it to a small number of canonical names with a documented justification.

### N11. `Azure.Identity` contradicts the "no provider SDK" rule

`system-design.md` §7 requires `Azure.Identity` with brokered interactive login; `technology-decision.md` says "Do not take a dependency on a provider SDK in the first release" and omits `Azure.Identity` from the pinned dependency table. `ui-design.md` hedges with "Entra ID, when supported." **Fix:** decide. Recommended: ship API-key auth only in v1 (it satisfies the brief), keep `IProviderCredentialSource` as the seam, and defer Entra + `Azure.Identity` + WAM broker to a later release with its own dependency entry. Brokered auth on a non-packaged WPF app also needs a parent window handle and is a meaningful amount of work for a v1.

---

## Suggestions

### S1. Make an explicit v1 scope cut; the current design is heavy for the brief

The brief asks for: open fast, type, save, find, copy. The design additionally mandates embeddings plus a future persisted vector index, expand-copy-verify-contract migrations, tiered backup retention with manifests, `ActivitySource` plumbing, a redacted diagnostics-export zip, correlation IDs, an `EnrichmentAttempt` audit trail, optimistic concurrency `Version` columns, `processed_commands` idempotency, dual packaging channels, and an SBOM — on a **single-user, single-instance, local** application. Several of these exist to solve problems the single-instance mutex already prevents. **Recommendation:** label §19's phases with an explicit v1 line. Suggested v1: deterministic extraction, SQLite+FTS, Ollama only, MSI per architecture, backup/restore (keep it — it is the real data-loss mitigation), basic diagnostics counters. Suggested deferral: embeddings and vector index, MSIX, diagnostics export zip, `ActivitySource`, OpenAI-compatible adapter, tiered retention. Keep `processed_commands` only if a concrete double-save path is demonstrated; otherwise a disabled Save button during the in-flight transaction is sufficient and far cheaper.

### S2. Background enrichment overwriting metadata while the user is editing is a UX hazard

§9 allows enrichment to replace a deterministic provisional assignment, while §10 raises a `ConcurrencyError` on version mismatch and `ui-design.md` §5 promises that "rerunning enrichment clearly previews proposed changes before replacing user-curated metadata." A background job can therefore produce a conflict dialog for an action the user never initiated. **Recommendation:** background enrichment must never write to a prompt that is open in an editing surface; queue the proposal and surface it as a reviewable suggestion instead. State this rule in both documents.

### S3. Bound the enrichment backlog and warn before cloud spend

§6 resumes pending enrichment from the database on launch. The first launch after a user enables a cloud provider could fire hundreds of requests. **Recommendation:** specify a single-worker (or small bounded-concurrency) scheduler with a per-session cap, show the queue depth in the status region, and require explicit confirmation before backfilling more than N prompts against a *remote* provider.

### S4. Normalize the Ollama default endpoint to one value

`system-design.md` uses `http://127.0.0.1:11434`; `ui-design.md` uses `http://localhost:11434`. On hosts where `localhost` resolves to `::1` first and Ollama binds IPv4 only, the UI default fails while the design default works. **Recommendation:** use `http://127.0.0.1:11434` everywhere, and treat loopback-literal detection (not the string "localhost") as the test for the plain-HTTP exemption in §11.

### S5. Verify the pinned dependency versions actually exist before committing them

`technology-decision.md` pins .NET SDK `10.0.401`, `Microsoft.Data.Sqlite` 10.x, `xunit.v3`, and `WixToolset.Sdk` **7.x**. The document already says "resolve and commit exact patch versions," which is right — but the WiX major version in particular should be confirmed against the current released line, because the ARM64 `InstallerPlatform` switch and SDK-style `.wixproj` behavior differ across WiX majors. **Recommendation:** make "resolve and pin verified versions, including WiX's actual current major and its ARM64 support" an explicit first task in the delivery sequence, and record the verified versions in the decision document.

### S6. Cross-architecture upgrade with one shared `UpgradeCode` needs an explicit, tested policy

`technology-decision.md` specifies "one stable `UpgradeCode` for both architectures and architecture-specific package identities," and `system-design.md` §17 requires that both architectures must not install side by side for one user. x64 MSIs install and run fine on ARM64 under emulation, so a real user can end up with the x64 build on an ARM64 device and later download the ARM64 build. Windows Installer major upgrades across differing package platforms are not reliably clean. **Recommendation:** decide and test one of (a) block cross-platform upgrade with a clear "uninstall the x64 version first" message, or (b) validate an actual x64→ARM64 major upgrade on native ARM64 hardware. Either way add it to the §16 packaging test list, and add architecture detection to whatever download surface exists.

### S7. Several test-strategy items will fight the inner loop

- `PromptSaver.Package.Tests` depends on "published output and installer metadata," which cannot be expressed as a project reference. A developer running `dotnet test .\PromptSaver.sln` will get failures unrelated to their change. **Fix:** make these tests skip with a clear message unless an artifacts path environment variable is present, and run them only in the packaging CI stage.
- `technology-decision.md` says Desktop tests "must not use pixel-based UI automation," while `system-design.md` §16 requires "Windows UI automation smoke tests on x64 and ARM64 runners." These are compatible (UIA is not pixel-based) but read as a conflict, and no tool is named. **Fix:** name the library (FlaUI or WinAppDriver), state that UIA tests are a release gate and not part of the inner loop, and acknowledge that no ARM64 runner exists yet — §17's ARM64 claims depend on hardware that CI does not currently have.
- A 15% startup-regression gate on shared GitHub Actions runners will be flaky. **Fix:** measure in PR CI as *report-only*; enforce the budget only on dedicated release hardware.
- The 50,000-prompt search corpus needs a seeded deterministic generator checked into the test project, not a committed fixture database.

### S8. Specify what the search document stores relative to NFKC normalization

§8 normalizes to NFKC "for comparison while preserving original text," and §5 indexes body and title. If the FTS document holds the original form and queries are normalized (or vice versa), accented and compatibility-form queries will miss. **Recommendation:** state that `prompt_search_documents` stores the **normalized** form for all five columns, that the query parser normalizes identically, and that the original text is served from `prompts` only. Add a round-trip test with full-width, accented, and ligature input.

### S9. Smaller consistency items worth one editing pass

- `ui-design.md` §14.2 shows a status-bar string "Search index ready," implying an index-readiness state that does not exist in the system design. Either define it or drop it.
- `ui-design.md` §8 Privacy offers **Clear metadata cache**; there is no metadata cache in the storage design (the closest thing is derived metadata, which is user-visible data, not a cache). Rename or remove.
- `ui-design.md` §4 derives a result title "from the first meaningful line," while `EnrichmentProposal` also contains a title. State the precedence: user title > provider title (only when no user title and only once) > derived first line. Otherwise the "never silently overwrite user metadata" rule is ambiguous for titles.
- `system-design.md` §9 excludes archived intents from auto-map candidates, but `NormalizedKey` uniqueness is global — so the identity-resolution path can resolve a "new" intent onto an archived one. Define the outcome (recommend: unarchive and assign, recording the reason).
- "Copy with metadata" has no specified output format. Define it, since it leaves the application's trust boundary.
- `ui-design.md` offers "Start with Windows" while the MSI installs per-machine; state that the setting writes a per-user `Run` entry and is not an installer feature.

---

## What is working well and should not be re-litigated

- Local-first save with provider work strictly off the commit path, resumed from the database rather than an in-memory queue.
- Integer basis points for scoring, with the `8499` / `8500` / `8501` tests called out explicitly. This removes the single most likely source of threshold bugs.
- Layering rules, particularly "provider implementations never write to storage" and "the provider may not select an intent ID or score."
- Secrets in Windows Credential Manager with a documented, justified exception to the single-data-root rule.
- Real on-disk SQLite in integration tests rather than in-memory mode, so WAL, FTS5, locking, and backup behavior are exercised honestly.
- The WPF-over-Electron/Tauri analysis, including the stated revisit triggers.
- The accessibility section, which is more concrete and more testable than is typical at this stage.
