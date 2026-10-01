# Prompt Saver UI Design

- **Status:** Implementation-ready
- **Date:** 2026-10-01
- **Normative scope:** Version 1

## 1. Experience contract

Prompt Saver opens to a focused multiline editor. It does not show onboarding,
a splash screen, a provider prompt, or a database progress dialog before the
user can type.

The UI follows five rules:

1. Draft backup, Library save, and metadata enrichment are separate states.
2. Save, copy, and new-prompt actions never have hidden side effects.
3. The editor remains the dominant surface and retains focus, selection, scroll
   position, undo history, and IME composition through background work.
4. Local capture, save, edit, search, copy, and recovery work without a
   provider.
5. Every pointer workflow has a keyboard and screen-reader equivalent.

## 2. Information architecture and responsive shell

Top-level destinations:

- **New prompt**
- **Library**
- **Settings**

Prompt Detail is opened from Library and is not a persistent navigation item.

At 720 effective pixels and wider, use a labeled left navigation rail. Below
720 pixels, replace the rail with a compact top row containing labeled
**New** and **Library** actions plus a labeled **More** menu for Settings,
shortcuts, and Help. Do not use unexplained icon-only primary navigation.

- Preferred initial size: 1000 x 720 effective pixels.
- Supported compact size: 560 x 420 at 100% scaling.
- Hard minimum: no greater than 480 x 360; below supported size, scroll instead
  of clipping.
- Test 200% text scaling in a 1280 x 720 work area.

The editor label, editor, persistence state, and primary save/copy actions stay
reachable at every supported size. Metadata moves below the editor or into a
flyout before editor height is sacrificed.

There is no permanent global status bar. Each page has one fixed-height status
region associated with its primary operation. This prevents duplicate inline,
status-bar, and toast messages.

## 3. Capture state model

The capture surface has four explicit states:

| State | Meaning | Status text | Primary action |
| --- | --- | --- | --- |
| `Empty` | No non-whitespace text | None | Type or paste |
| `DraftBackedUp` | Crash-recoverable text; no Library record | `Draft backed up locally` | **Save prompt** |
| `PromptSaved` | Displayed text matches a Library record | `Prompt saved` | **Copy prompt** |
| `UnsavedChanges` | Saved prompt has a newer backed-up edit | `Changes backed up locally - save to update prompt` | **Save changes** |

Additional transient states are `BackingUpDraft`, `SavingPrompt`,
`SaveFailed`, and `DraftBackupFailed`. Status changes do not reflow the editor.

Autosave never creates a Library record or duplicate. The first explicit save
creates a record. Later explicit saves update that record until the user
chooses **New prompt**.

### Launch and recovery

On activation:

1. Show and activate the window.
2. Load an empty or recovered capture draft.
3. Focus the loaded editor after activation and show the caret.
4. Accept input before database or provider readiness.

A recovered draft is labeled `Recovered draft` and announced politely once.
Restore a valid caret and selection; otherwise place the caret at the end.
Second-instance activation brings the current window forward without replacing
text, focus, selection, or navigation.

If draft persistence is unavailable, keep a memory-backed editor and show:

```text
Draft is not backed up. Keep this window open or copy the text.
[Copy text] [Try again]
```

If SQLite is unavailable, draft backup may continue, but Library save is
disabled with recovery guidance. The emergency editor is never replaced by a
database error page.

### Capture layout

```text
┌─ Prompt Saver ───────────────────────────────────────────── _ □ × ┐
│ New prompt   Library                                      [More] │
├──────────────────────────────────────────────────────────────────┤
│ Prompt                                                           │
│ ┌──────────────────────────────────────────────────────────────┐ │
│ │ [focused multiline editor]                                   │ │
│ │                                                              │ │
│ └──────────────────────────────────────────────────────────────┘ │
│ Draft backed up locally                                          │
│                                                                  │
│ [Review metadata]                      [Copy draft] [Save prompt] │
└──────────────────────────────────────────────────────────────────┘
```

The visible label is `Prompt`; placeholder text is optional and is never the
accessible name. Character count is hidden until the UTF-8 body reaches 80% of
the 256 KiB limit. Then show `205 KiB of 256 KiB`. An oversize paste remains in
the editor, is visibly invalid, and cannot be saved until reduced; it is never
silently truncated.

### Save, copy, and new prompt

- `Ctrl+Enter`: save the current draft or commit changes.
- `Ctrl+Shift+C`: copy current editor text. It never saves.
- `Ctrl+Shift+Enter`: save and copy, only when save is available.
- **Copy draft** appears for non-whitespace unsaved text.
- After save, keep text, focus, caret, selection, and undo history unchanged.
- Successful save announces `Prompt saved` politely once.
- **New prompt** creates a blank capture surface. No modal is needed when the
  draft is backed up. If draft backup failed, offer **Keep editing**, **Copy
  and start new**, and **Start new without backup**, with the safe action
  focused.

Clipboard success updates copy count only after the OS clipboard operation
succeeds. Clipboard failure remains inline with **Try again** and does not claim
success.

## 4. Metadata during capture

Metadata is collapsed by default and never opens automatically.

Before save, locally detected metadata may be shown as `Detected locally`.
There is no percentage before a scored post-save intent decision. Manual input
is labeled `Added by you` and is authoritative.

Use ordinary labeled list editors, not dense chips as the sole interaction:

```text
Metadata
Intent     Prepare a customer workshop    Detected locally  [Change]
Skills     Azure; diagramming                                [Edit]
Entities   Contoso (Organization)                           [Edit]
```

Each **Edit** action opens one focused flyout or dialog with:

- a visible label;
- a standard list of current values;
- separately named Edit and Remove buttons;
- an Add field;
- a distinct Suggestions group with **Accept** and **Dismiss** actions.

Closing returns focus to the invoking control. Provider suggestions never
replace user-curated values without review.

## 5. Prompt Detail

Opening a result focuses the page heading, not the text editor. `F6` reaches the
editor. The editor is directly editable, but opening the page does not
unexpectedly place a caret into it.

Edits are backed up to one per-prompt draft revision. Navigation with
uncommitted edits offers:

- **Save changes**
- **Keep as draft**
- **Discard edits**

The safe default is **Keep as draft**. `Esc` closes without discarding.

Background enrichment received while the prompt is open appears as a
reviewable suggestion; it never changes the open record or creates a conflict
dialog.

Title precedence is:

1. user title;
2. first accepted provider title, only when no user title exists;
3. derived first meaningful line.

### Copy formats

**Copy prompt** copies body only.

**Copy with metadata** uses deterministic plain text:

```text
<prompt body>

---
Intent: <canonical intent>
Skills: <semicolon-separated skills or "None">
Entities: <Display Name (Type); ... or "None">
```

It includes no confidence, provider, IDs, timestamps, or hidden diagnostics.

## 6. Intent review, reclassification, and merge

The 85% policy is presented consistently:

| Outcome | UI |
| --- | --- |
| Existing score >= 85% | `Matched to existing intent, 91%` with **Review** |
| Valid new candidate below 85% | `New provisional intent created - review suggested` |
| No valid local candidate | `Assigned to Unsorted - review suggested` |
| Provider unavailable | Preserve local result and show scoped provider state |

Below 85%, saving is already complete. A provisional intent exists unless the
prompt is `Unsorted`; review does not block capture.

```text
┌─ Review intent ────────────────────────────────────────────────────┐
│ No existing intent matched at 85% or higher.                      │
│                                                                   │
│ Current assignment                                                │
│ [ Prepare a customer architecture workshop                    ]   │
│ Source: Local analysis · Needs review                             │
│                                                                   │
│ Existing alternatives                                             │
│ ( ) Draw a diagram for a customer                         78%     │
│ ( ) Prepare for a customer meeting                        64%     │
│ (•) Keep the current provisional intent                           │
│                                                                   │
│ [Review later]                              [Apply selection]     │
└───────────────────────────────────────────────────────────────────┘
```

`Review later` retains the assignment and marks it skipped. Applying a choice
clears review and updates search. Duplicate and near-duplicate warnings update
while the provisional name is edited.

Intent management in Settings/Data includes:

- rename;
- archive/unarchive;
- merge source into target;
- prompt count before merge;
- explicit confirmation naming both intents.

Merge reclassifies prompts and aliases as specified in the system design. It
does not delete prompt text.

## 7. Library and search

`Ctrl+F` is the only global search shortcut. It opens Library and focuses the
Search field. Opening Library with pointer/navigation preserves the last
Library focus; if none exists, focus the page heading.

```text
┌─ Prompt Library ───────────────────────────────────────────────────┐
│ Search prompts                                                    │
│ [                                                     ] [Filters] │
│ [Intent: Diagram x] [Skill: Azure x]       24 results · Recent v │
│                                                                  │
│ Azure architecture diagram for Contoso                            │
│ Create an Azure architecture diagram for Contoso that...          │
│ Draw a diagram for a customer · Modified 2 hours ago              │
│                            [Open prompt] [Copy prompt] [More...]  │
└──────────────────────────────────────────────────────────────────┘
```

The Filters flyout contains Intent, Skill, Entity, and Date. Filters across
groups use AND; multiple values in a group use OR. Active filters appear as
removable labeled items.

Incremental search is debounced by about 120 ms. Result-count announcements
have a separate longer debounce and do not announce sub-300 ms loading states.
Results are virtualized and paged.

`Esc` behavior while Search is focused:

1. clear text;
2. clear filters;
3. move focus to results.

Search syntax help explains quotes and structured filters. Literal punctuation
never surfaces a database syntax error.

Sorts in version 1:

- Recently modified
- Recently created
- Title A-Z
- Intent A-Z
- Most copied

### Result accessibility

Each result is a list item with:

- optional meaningful title;
- two-line body excerpt;
- intent, modified date, and at most two skills;
- explicit **Open prompt**, **Copy prompt**, and **More options** controls.

The row itself is not a nested clickable container. Selection and keyboard
focus are visually distinct. Screen readers receive item position and a concise
name, not the full body on every arrow key.

After deleting a result, focus moves to the next result or the preceding result
if the last item was deleted.

## 8. Duplicate and delete UX

**Duplicate** creates a separate prompt and opens its detail page. Announce
`Prompt duplicated`. Copy history, pending enrichment, and edit drafts are not
copied.

**Delete** is in More options only. It is permanent in version 1. The dialog:

- names the prompt;
- states that the prompt record and its private metadata will be deleted;
- states that shared intents remain;
- focuses **Cancel** by default;
- supports `Esc` as Cancel;
- exposes **Delete prompt** as the destructive action.

No single-key delete shortcut is assigned.

## 9. Settings

Settings pages:

### General

- Theme: System, Light, Dark
- Launch destination: New prompt or Library
- Spell check
- Library density

Start with Windows is not in version 1 because no tray or global capture
affordance exists.

### AI metadata (optional)

- master enable switch;
- provider: Ollama or Azure AI Foundry/OpenAI-compatible;
- concise privacy statement;
- scoped provider status;
- **Configure** action;
- detected-Ollama affordance.

When loopback Ollama is discovered after first render, show:

`Local Ollama detected - enable AI metadata?`

Actions are **Configure**, **Not now**, and **Do not suggest again**. Discovery
sends no prompt content and does not enable the provider.

### Provider configuration

Common fields:

- endpoint;
- model/deployment;
- API-key credential status for cloud;
- **Save configuration**;
- **Test connection**.

Save and Test are independent. Testing is cancellable and does not block
navigation. Ollama defaults to `http://127.0.0.1:11434`.

Advanced fields contain API version, route profile, timeout, and remote plain
HTTP acknowledgement. Entra ID is not shown in version 1.

Privacy text states:

- local capture/search never leaves the device;
- when enabled, at most the first 8192 UTF-8 bytes of the prompt are sent;
- truncation is indicated to the provider;
- intent names/catalog and existing prompt metadata are not sent;
- local intent matching happens after provider output returns.

### Data and recovery

- show the single data folder and **Open folder**;
- Back up now;
- Restore backup;
- Rebuild search index;
- local diagnostic counters.

There is no misleading **Clear metadata cache** action. Provider credentials
are removed separately and never delete prompts.

### Keyboard shortcuts and accessibility

Show the normative shortcut table and accessibility behavior. Do not hide
shortcuts solely in tooltips.

## 10. Normative keyboard shortcuts

| Shortcut | Action |
| --- | --- |
| `Ctrl+N` | New prompt and focus editor |
| `Ctrl+Enter` | Save prompt or save changes |
| `Ctrl+Shift+Enter` | Save and copy |
| `Ctrl+Shift+C` | Copy current editor text or selected Library prompt |
| `Ctrl+S` | Save changes in Prompt Detail |
| `Ctrl+F` | Open Library and focus Search |
| `Ctrl+,` | Open Settings |
| `Ctrl+Z` / `Ctrl+Y` | Editor undo/redo |
| `Ctrl+A/C/X/V`, `Shift+Insert` | Standard text operations |
| `Alt+Left` | Navigate back |
| `F6` / `Shift+F6` | Cycle page-specific regions |
| `Shift+F10` | Context menu |
| `Esc` | Close transient UI or perform staged Search clearing |
| `F1` | Shortcut/help content |

Global commands do not override standard editor behavior. `Tab` moves focus out
of the editor; version 1 has no insert-tab mode.

Page `F6` regions:

- Capture: header, editor, metadata, page actions/status.
- Library: header/search, filters, results, page status.
- Prompt Detail: header/actions, editor, metadata, page status.
- Settings: category list, setting page, page actions/status.

## 11. Focus and announcement contract

- Launch/New prompt: editor.
- Library via `Ctrl+F`: Search.
- Library via navigation: preserve prior Library focus; otherwise heading.
- Prompt Detail: heading.
- Dialog/flyout close: invoking control.
- Delete completion: adjacent result.
- Autosave, enrichment, result updates, and connectivity never move focus.
- Live status regions never receive focus.
- Save/copy success is polite and deduplicated.
- Draft/save failure is assertive once and remains visible.
- Technical endpoint URLs are not repeatedly announced.

Provider and connectivity states are scoped:

- `Local storage ready`
- `Ollama unavailable - local capture and search are available`
- `Cloud provider unavailable - metadata will retry`
- `Search index rebuilding - recent prompts may be incomplete`

Do not show a global `Offline` state based only on internet connectivity.

## 12. Accessibility implementation requirements

Target WCAG 2.2 AA principles and Windows UI Automation.

- Use standard WPF controls and automation peers before custom controls.
- Expose page titles as headings.
- Expose the editor as a multiline text control named `Prompt`.
- Give every command an accessible name that includes context, such as
  `Copy prompt "Azure architecture diagram for Contoso"`.
- Associate validation/error text programmatically with its control.
- Expose confidence as `Intent match confidence, 78 percent, needs review`.
- Represent metadata as list items with separately named Edit/Remove actions.
- Progress indicators have stable accessible names and status text.
- Virtualized results retain stable Automation IDs and focus when containers
  recycle.
- Tooltips are keyboard-accessible, dismissible, and not the only source of
  required information.
- Focus indicators meet 3:1 contrast and remain visible in Windows contrast
  themes.
- Normal text meets 4.5:1; large text, controls, icons, and focus meet 3:1.
- Color is never the only error, confidence, selection, or provider signal.
- Touch targets are at least 32 x 32 effective pixels; primary actions target
  40 x 40.
- Respect Windows text scaling, contrast themes, reduced motion, system colors,
  and system font fallback.
- Title-bar drag regions never overlap commands, including at 200% scaling.
- Every custom style defines default, hover, focus, pressed, selected, disabled,
  validation-error, busy, and high-contrast states.

Automated UIA tests cover capture, save, copy, search, result navigation,
intent review, delete focus recovery, provider error, dialogs, and degraded
startup. Manual Narrator qualification covers the same workflows on x64 and
ARM64 release candidates.

## 13. Acceptance criteria

1. On normal warm launch, a typed character enters the editor without a click
   or Tab before database/provider work completes.
2. On degraded launch, the user can type and copy while recovery proceeds.
3. A 500-word paste preserves line breaks and accepts input without a modal.
4. Draft backup restores exact text and a valid caret/selection after a crash.
5. Save never clears text or changes focus, selection, undo context, or scroll.
6. Copying an unsaved draft does not create a Library record.
7. Scores at or above 85% visibly map; lower valid candidates create a
   provisional intent; low-information cases use `Unsorted`.
8. Review later retains the current assignment; apply resolves review.
9. Search handles literal punctuation, quotes, and structured filters without
   surfacing SQLite errors.
10. Duplicate and delete follow the documented data semantics and focus rules.
11. Capture, save, copy, new prompt, search, review, and provider-error recovery
    are fully keyboard operable.
12. Narrator completes all primary flows with meaningful names and stable focus.
13. The UI remains usable at 560 x 420 and at 200% text scaling without
    horizontal scrolling of primary content.
14. Light, dark, and Windows contrast themes retain visible focus, selection,
    error, disabled, and busy states without color-only meaning.
15. Provider settings disclose the exact capped payload before enablement.
