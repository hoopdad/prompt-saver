# Prompt Saver UI/UX Critique

## Executive assessment

The design has the right product center: launch directly into a local, focused
editor; keep enrichment optional; and make saved prompts easy to find and copy.
It is not yet implementation-ready. The largest risk is not visual styling but
behavioral ambiguity between a transient draft, a saved prompt, autosave, and
copy. Several specifications also contradict one another:

- `Ctrl+Shift+C` is described as copying an existing prompt in the UI design,
  but as **save and copy** in the system design.
- The UI design keeps the saved prompt visible after save, while the system
  design clears and refocuses the editor.
- The product and system rules create a new intent below 85%, while the UI
  design asks the user to confirm creation and permits leaving intent unset.
- Search uses `Ctrl+F` in the UI design and `Ctrl+K` in the system design.
- A 720-pixel minimum width conflicts with common half-screen snapping on
  1366-pixel displays and becomes more restrictive under text scaling.

These must be resolved before view models, command routing, UI Automation
properties, or acceptance tests are written.

## Prioritized changes

### P0 - Required before implementation

#### 1. Define one capture state model

Use four explicit states and expose the state in text, not only through button
availability:

| State | Meaning | Primary action |
| --- | --- | --- |
| **Empty** | No text exists | Type or paste |
| **Draft saved locally** | Text is crash-recoverable but is not a Library record | **Save prompt** |
| **Prompt saved** | A Library record exists and the displayed text matches it | **Copy prompt** |
| **Unsaved changes** | A saved prompt has newer crash-recoverable edits | **Save changes** |

Do not use “Draft saved” and “Saved locally” interchangeably. The former means
recoverable working text; the latter currently sounds like a completed Library
save. Recommended status strings are:

- `Draft backed up locally`
- `Saving prompt...`
- `Prompt saved`
- `Changes backed up locally - save to update prompt`
- `Save failed - your text is still in the editor`

Autosave should never create multiple Library records. An explicit save creates
the first record; subsequent saves update that record until **New prompt** is
chosen.

#### 2. Make save, copy, and new-prompt behavior deterministic

Adopt these command semantics everywhere:

- `Ctrl+Enter`: save the current draft or save changes.
- `Ctrl+Shift+C`: copy the current editor text. If it has never been saved, copy
  it without silently creating a record and announce `Copied unsaved draft`.
- **Save and copy** may be offered as a secondary command, but must not be hidden
  behind the same shortcut as ordinary copy.
- After save, keep the text and caret/selection in place. Do not replace the
  editor with a success panel and do not clear it automatically.
- **New prompt** intentionally moves to a blank editor. If the current draft is
  backed up, no modal is required; show a lightweight choice only when draft
  persistence failed.

This supports the real workflow: paste or type, save, copy, switch to the agent
workspace, and return without losing context.

#### 3. Preserve first-keystroke readiness under every startup condition

The startup target should be measured from process activation to an editor that
accepts input, not merely a visible window. Specify:

- Focus is assigned after the window is activated and the editor is loaded.
- Provider checks, backups, indexing, and nonessential migrations cannot delay
  editor creation.
- If the database is unavailable or migration recovery is required, open a
  memory-backed emergency editor first. Clearly state that text is not yet
  persisted and provide **Copy text** plus recovery guidance.
- On second-instance activation, bring the existing window forward without
  replacing its draft or selection.
- A recovered draft opens with the caret at its last known position when
  reliable, otherwise at the end. Announce `Recovered draft` once through a
  polite live region; do not move focus to the announcement.

Add a usability acceptance measure: on a warm launch, a user can type a
character without clicking or tabbing; on a degraded launch, a user can still
type before provider or database recovery completes.

#### 4. Resolve the intent-threshold contradiction

The application rule says a score below 85% creates a new intent. The UI should
not block saving or imply that no intent exists. Create a **provisional new
intent** immediately, assign it to the prompt, and mark it `Needs review`.
Review may rename it or replace it with an existing intent. `Skip for now`
retains the provisional assignment rather than leaving the prompt without an
intent.

For 85% and above, show `Matched to existing intent, 91%`. For below 85%, show
`New intent created - review suggested`. Avoid presenting provider confidence
as objective certainty. The review panel should explain why the alternatives
are shown and identify the source (`Local analysis` or the configured provider).

#### 5. Specify focus and announcement behavior as interactions

The accessibility section states goals but not implementable focus behavior.
Add a focus contract:

- Launch/New Prompt: focus the editor.
- `Ctrl+F`: navigate to Library and focus Search.
- Library opened by pointer/navigation: focus the Library heading only if focus
  would otherwise be lost; otherwise preserve the user's last Library focus.
- Opening a result: focus the Prompt Detail heading, then allow `F6` to reach
  the editor. Do not place the caret and enter edit mode unexpectedly.
- Closing a flyout/dialog: return focus to the invoking control.
- Deleting a result: focus the next result, or the preceding result if the last
  item was deleted.
- Clearing search: retain focus in Search. The first `Esc` clears text, the
  second clears filters, and the third moves focus to results; show this staged
  behavior in help.
- Live status never receives focus. Copy/save confirmations are polite and
  deduplicated; save failures are assertive once and remain visible.

Define `F6` regions per page rather than using the vague global sequence:
Navigation, page header/commands, primary content, secondary metadata, status.
The editor itself must not trap `Tab`; `Tab` should move focus unless a future
explicit “insert tab character” command is provided.

#### 6. Design for realistic narrow windows and text scaling

Do not enforce a 720-pixel minimum width. It prevents half-screen use on common
1366 x 768 displays. Recommended constraints:

- Preferred initial size: 1000 x 720 effective pixels.
- Supported compact size: 560 x 420 at 100% scaling.
- Avoid a hard minimum greater than 480 x 360; below the supported size, allow
  scrolling rather than clipping.
- Test 200% text scaling at a 1280 x 720 work area and Windows contrast themes.

At widths below 720 pixels, remove the persistent navigation rail. Use a compact
top command row with labeled **New** and **Library** actions plus an overflow
menu containing Settings and Help. Icon-only navigation is not sufficiently
discoverable for the primary workflow, even with tooltips.

The editor, its visible label, save/copy action, and persistence state must
remain on screen. Metadata becomes a bottom sheet/flyout or a section below the
editor. Do not pin a global status bar if it consumes the final usable text row.

### P1 - High-value refinement

#### 7. Reduce brain-dump friction

- Make the whole central surface visually point to the editor; remove redundant
  “New Prompt” headings when the destination is already selected.
- Keep a persistent, real label above the editor. Placeholder text disappears
  while typing and is not an adequate accessible name.
- Remove the live character count from the default visual hierarchy. Show it
  only near the size limit or expose it to assistive technology on request.
- Keep metadata collapsed before and during capture. Do not animate it open
  automatically after save.
- Preserve selection, scroll position, undo history, and IME composition through
  autosave. Autosave status must not cause layout movement.
- Accept plain text paste immediately. When rich content is pasted, silently
  retain readable text and line breaks; do not show a formatting warning.
- Support `Shift+Insert` and standard context-menu Paste. Do not repurpose
  `Ctrl+V`, `Ctrl+C`, or `Ctrl+X`.

#### 8. Make search discoverable without teaching syntax first

Use `Ctrl+F`, the standard find gesture, consistently. Keep a visible
**Library** navigation item with a search icon and label at normal widths. The
search field label should be `Search prompts`; move the searchable-field list
to supporting text or an accessible description.

Do not expose four filter dropdowns in the first visual row. Start with:

```text
[ Search prompts                                      ] [Filters]
  24 results · 2 filters active                         Sort: Recent
```

The Filters flyout contains Intent, Skill, Entity, and Date sections. Selected
filters appear as removable chips beneath the search field. Structured query
syntax may remain supported for expert users, but it needs autocomplete or a
Help example; otherwise tokens such as `skill:` are undiscoverable.

Search results should update incrementally, but screen-reader announcements
should be debounced separately from search execution. Announce `24 results`
after a pause, not on every keystroke. Do not announce loading for sub-300 ms
local searches.

#### 9. Simplify result rows for scan and copy

The prompt excerpt should be the dominant text, not a generated title that may
repeat it. Use:

1. Optional title, when user-defined or meaningfully distinct.
2. Two-line prompt excerpt.
3. One subdued metadata line: intent, modified date, and at most two skills.
4. A visible **Copy** button on hover, keyboard focus, and touch/pen; it should
   remain available through the context menu at all times.

Do not make the entire row and nested buttons one ambiguous accessibility
target. Model the row as a list item with an explicit **Open prompt** action,
followed by separate **Copy prompt** and **More options** buttons. Selection
must be visually distinct from keyboard focus.

When copy succeeds, announce the named item, for example
`Copied "Azure architecture diagram for Contoso"`. Do not change selection,
scroll position, or navigation.

#### 10. Make metadata understandable before making it powerful

The proposed chip editing model is dense for WPF, screen readers, and keyboard
users. Use a simple summary in Prompt Detail:

```text
Metadata
Intent     Draw a diagram for a customer     Matched, 91%  [Review]
Skills     Azure; diagramming                             [Edit]
Entities   Contoso (Organization)                         [Edit]
```

Open one focused edit flyout/dialog per metadata type. Each editor needs a
visible label, a standard list of current values, an Add field, and ordinary
Remove buttons. Avoid tiny `x` affordances as the only removal mechanism.
Suggested and accepted values should not coexist as visually cryptic chip
styles; place suggestions in a clearly labeled `Suggestions` group with
**Accept** and **Dismiss** actions.

Provenance belongs in the review surface, not in tooltips alone. Tooltips are
poor primary disclosure for keyboard, touch, and screen-reader users.

#### 11. Reduce Settings to progressive disclosure

The initial Settings scope is too large for a capture utility. Use these pages:

- **General:** theme, launch destination, spell check, and Library density.
- **AI metadata (optional):** master enable switch, provider selection, concise
  privacy statement, provider status, and **Configure**.
- **Data and recovery:** data folder, backup, restore, export/import, and
  diagnostics.
- **Keyboard shortcuts and accessibility:** shortcut reference and any explicit
  accessibility preferences not already inherited from Windows.

Provider configuration should be a dedicated page. Put common fields first
(endpoint, model/deployment, authentication, credential status) and place API
version, route profile, timeouts, and remote-HTTP exceptions under
**Advanced**. Keep **Save configuration** separate from **Test connection** so
offline users can save without an action labeled “Save and test.” Testing must
not block navigation.

Do not include **Start with Windows** unless a clear capture affordance such as
a global hotkey or tray behavior is designed; starting a full window at sign-in
adds noise without shortening an intentional capture flow.

#### 12. Clarify errors and connectivity

Avoid a global `Offline` state based only on internet connectivity. Prompt Saver
is local-first, Ollama may work without internet, and cloud connectivity can
fail while the device is online. Report scoped states:

- `Local storage ready`
- `Ollama unavailable - local capture and search are available`
- `Cloud provider unavailable - metadata will retry`
- `Search index rebuilding - recent prompts may be incomplete`

For save failure, keep the editor editable and display a persistent inline
error adjacent to Save with **Try again** and **Copy text**. Include a
diagnostic ID only under expandable technical details. Never place raw endpoint
URLs in frequently announced live text.

For database corruption or migration failure, use a dedicated recovery page
while preserving an emergency editor. Offer **Open backups**, **Export
recoverable prompts**, and **Copy current text** according to what remains
safe. Do not show a success-shaped draft status when persistence is unavailable.

### P2 - Polish and consistency

#### 13. Tighten visual hierarchy and Windows conventions

- Use one page title, one primary action, and one persistence message per page.
  The current global status bar plus inline status plus toast risks triple
  reporting.
- Prefer standard WPF Button, TextBox, ListBox/ListView, Menu/Flyout, and Dialog
  semantics over custom clickable containers.
- Give command buttons text labels until space is constrained. `+`, magnifier,
  gear, ellipsis, and chevrons alone are insufficient in compact mode.
- Provide access keys in menus/dialogs where practical, and show shortcut text
  in menu items.
- Follow Windows dialog ordering and place the safest action as default for
  destructive confirmations. `Esc` closes without action.
- Ensure title-bar drag regions do not overlap app commands and remain usable at
  200% text scaling.
- Use system colors and metrics where possible. Define focus, hover, pressed,
  selected, disabled, error, and high-contrast states for every custom style.

#### 14. Specify screen-reader semantics for complex controls

Implementation guidance should require:

- Page titles exposed as headings.
- The prompt editor exposed as a multiline text control named `Prompt`.
- Result collections exposed with item count and selected position, without
  reading the full prompt body on every arrow key.
- Confidence spoken as `Intent match confidence, 91 percent, matched to an
  existing intent`.
- Progress rings with a stable accessible name and status text; animation is
  not the only indication.
- Metadata values represented as list items with separately named Edit/Remove
  actions, not a single long chip string.
- Virtualized result items to preserve stable names and focus when recycled.
- No focus change when autosave, enrichment, or result count updates.
- Automated UI Automation checks plus manual Narrator testing for capture,
  search, copy, metadata review, dialogs, and error recovery.

## Revised textual wireframes

These wireframes define hierarchy and commands, but implementation also needs
the interaction/state table in the following section.

### Launch and capture - empty or recovered draft

```text
┌─ Prompt Saver ───────────────────────────────────────────── _ □ × ┐
│ New prompt   Library                                      [⋯]    │
├───────────────────────────────────────────────────────────────────┤
│ Prompt                                                            │
│ ┌───────────────────────────────────────────────────────────────┐ │
│ │ [focused multiline editor]                                    │ │
│ │                                                               │ │
│ │                                                               │ │
│ └───────────────────────────────────────────────────────────────┘ │
│ Draft backed up locally                                           │
│                                                                   │
│ [Review metadata]                       [Copy draft] [Save prompt] │
└───────────────────────────────────────────────────────────────────┘
```

Behavior:

- The caret is visible in the editor on activation.
- **Copy draft** appears after non-whitespace text exists.
- **Save prompt** is the sole primary button.
- The status line is a fixed-height live region so text changes do not reflow
  the editor.
- **Review metadata** opens below the editor only when requested.

### Saved prompt without navigation interruption

```text
┌─ Prompt Saver ───────────────────────────────────────────── _ □ × ┐
│ New prompt   Library                                      [⋯]    │
├───────────────────────────────────────────────────────────────────┤
│ Prompt                                                            │
│ ┌───────────────────────────────────────────────────────────────┐ │
│ │ Create an Azure architecture diagram for Contoso...           │ │
│ └───────────────────────────────────────────────────────────────┘ │
│ Prompt saved · Reviewing metadata...                              │
│                                                                   │
│ [Review metadata]                    [New prompt] [Copy prompt]    │
└───────────────────────────────────────────────────────────────────┘
```

Focus and selection stay in the editor after save. The copy confirmation uses
the same fixed status line and a polite UI Automation announcement.

### Library - normal width

```text
┌─ Prompt Saver ───────────────────────────────────────────── _ □ × ┐
│ New prompt   Library                                      [⋯]    │
├───────────────────────────────────────────────────────────────────┤
│ Prompt Library                                                    │
│ [ Search prompts                                      ] [Filters] │
│ [Intent: Diagram ×] [Skill: Azure ×]        24 results · Recent ▾ │
│                                                                   │
│ ┌───────────────────────────────────────────────────────────────┐ │
│ │ Azure architecture diagram for Contoso                       │ │
│ │ Create an Azure architecture diagram for Contoso that...     │ │
│ │ Draw a diagram for a customer · Modified 2 hours ago         │ │
│ │                                      [Copy prompt] [More...]  │ │
│ ├───────────────────────────────────────────────────────────────┤ │
│ │ Draft a utility application that parses...                   │ │
│ │ Write utility application · Modified yesterday               │ │
│ │                                      [Copy prompt] [More...]  │ │
│ └───────────────────────────────────────────────────────────────┘ │
└───────────────────────────────────────────────────────────────────┘
```

### Compact snapped window

```text
┌─ Prompt Saver ───────────────────── _ □ × ┐
│ [New] [Library]                         [⋯]│
├────────────────────────────────────────────┤
│ Prompt                                     │
│ ┌────────────────────────────────────────┐ │
│ │ [focused editor]                       │ │
│ │                                        │ │
│ │                                        │ │
│ └────────────────────────────────────────┘ │
│ Draft backed up locally                    │
│ [Metadata]          [Copy] [Save prompt]   │
└────────────────────────────────────────────┘
```

Settings is in the labeled overflow menu, not an unexplained gear icon.
Secondary commands collapse before the editor loses a useful height.

### Low-confidence intent review

```text
┌─ Review intent ────────────────────────────────────────────────────┐
│ A new intent was created because no existing intent matched at    │
│ 85% or higher. You can keep it or use an existing intent.         │
│                                                                   │
│ Current provisional intent                                        │
│ [ Prepare a customer architecture workshop                    ]   │
│ Source: Local analysis · Needs review                             │
│                                                                   │
│ Existing alternatives                                              │
│ ( ) Draw a diagram for a customer                         78%      │
│ ( ) Prepare for a customer meeting                        64%      │
│ (•) Keep the provisional intent shown above                        │
│                                                                   │
│ [Review later]                              [Apply selection]      │
└───────────────────────────────────────────────────────────────────┘
```

`Review later` keeps the provisional intent and returns focus to the control
that opened the review surface.

### Local save failure

```text
│ Prompt                                                            │
│ ┌───────────────────────────────────────────────────────────────┐ │
│ │ [editor remains available with all text intact]               │ │
│ └───────────────────────────────────────────────────────────────┘ │
│ Error: Prompt was not saved. The database is temporarily busy.    │
│ Your text remains in the editor.                                  │
│ [Technical details]                    [Copy text] [Try again]     │
```

### Provider configuration

```text
┌─ Settings > AI metadata > Ollama ─────────────────────────────────┐
│ Ollama metadata enrichment                                        │
│ [On] Prompt text is sent to this configured server for metadata.  │
│                                                                   │
│ Server URL                                                        │
│ [ http://127.0.0.1:11434                                      ]   │
│ Model                                                             │
│ [ llama3.2                                                    ]   │
│ [Advanced settings]                                               │
│                                                                   │
│ Status: Not tested                                                │
│ [Test connection]                         [Save configuration]     │
└───────────────────────────────────────────────────────────────────┘
```

Saving and testing are independent, and status is scoped to the provider.

## Implementation-ready interaction specification

The textual wireframes alone are not implementation-ready. Before XAML work,
each surface should have a compact specification containing:

| Required item | Example |
| --- | --- |
| Initial focus | `PromptEditor` after window activation |
| Tab order | Header commands -> editor -> metadata -> page actions |
| Command and shortcut | `SavePromptCommand`, `Ctrl+Enter` |
| Enabled rule | Non-whitespace body and no local save in progress |
| Busy behavior | Editor stays enabled; duplicate save command suppressed |
| Success behavior | Record ID retained; selection and focus preserved |
| Failure behavior | Persistent inline error; Copy and Retry available |
| Accessible name/help text | `Save prompt`; `Saves this draft to the Library` |
| Live announcement | `Prompt saved`; polite, once |
| Compact layout | Secondary actions move to overflow below 560 px |
| 200% scaling | Content scrolls; primary action and editor remain reachable |
| High contrast | System colors; no status communicated only by icon/color |
| Automation ID | Stable ID for editor, search, result list, status, and actions |

Also provide component states for default, pointer-over, keyboard focus,
pressed, selected, disabled, validation error, busy, and high contrast. Define
truncation/wrapping rules and minimum target sizes rather than relying on box
drawings to imply dimensions.

## Recommended acceptance criteria

1. On normal launch, typing immediately enters text in the prompt editor with
   no click or Tab.
2. A 500-word paste appears without a modal, formatting warning, or provider
   delay, preserving paragraphs and line breaks.
3. Save acknowledges local persistence before enrichment and never clears the
   editor, selection, or undo context.
4. Copy works for both drafts and saved prompts and announces exactly what was
   copied without navigating.
5. A user can launch, paste, save, copy, start a new prompt, and search using
   only the keyboard.
6. Narrator completes capture, save, Library result navigation, copy, intent
   review, and provider-error recovery with meaningful names and stable focus.
7. Search and filters remain usable at 560 x 420 and at 200% text scaling
   without horizontal scrolling of primary content.
8. Save failure leaves all text accessible and offers Retry and Copy; provider
   failure still presents prompt save as successful.
9. Scores of 85% and above visibly map to an existing intent; scores below 85%
   create a provisional new intent and visibly mark it for review.
10. Light, dark, and Windows contrast themes retain visible focus, selection,
    error, and disabled states without color-only meaning.

