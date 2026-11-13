# Agreed adaptive UI redesign

Status: agreed design, delivery in progress. Recorded after the November 11, 2026
design interview and review of conceptual wide/compact mockups.

The [Person workspace visual refresh](visual-refresh-design.md) supersedes the
earlier composition where they differ. Its reading slice (#71) implements
facts above full-width notes, anchored identity, always-visible fact slots and
the single People divider; the remaining refresh is still in progress.
No whole-pass hosted or owner visual acceptance is claimed here.

This refines [the v1 design](design.md), not the product's identity, supported
platforms, or data-safety boundaries. Keep the native Windows .NET/WPF direction
in [ADR 0005](adr/0005-native-windows-dotnet.md). This is not a request for a web
dashboard, an org-chart canvas, or a new framework.

## Goal and visual direction

The primary workflow is finding a Person and immediately understanding their
profile and the owner's recorded context. Writing notes is directly accessible,
but opening a Person defaults to reading rather than editing.

Use the selected calm, modern Windows direction:

- Clear, consistent sans-serif typography, including rendered notes.
- Restrained accent colour, subtle surfaces and separators, and fewer heavy
  borders or boxed controls.
- Compact but comfortable spacing, not tiny type or a dense administrative UI.
- Strong identity and content hierarchy, with routine management and explanatory
  material secondary.

Better space usage does not mean filling every pixel. Avoid both today's long
single-column profile surrounded by unused width and excessively long lines of
note text in a maximized window.

## Everyday chrome and Settings

Keep the everyday toolbar focused on search and Settings. A quiet,
non-interactive Workspace-name label provides orientation; it is not a separate
Workspace selector or dropdown.

Display the full copyable current folder path in Settings. One **Open Workspace...**
action opens the native folder picker and immediately attempts the validated open
after acceptance; cancellation leaves writing, selection and startup preferences
unchanged. There is no editable pending path or separate Choose/Open sequence.
Workspace changes are expected to be very rare. Import is also infrequent,
roughly every week or two rather than daily, so put a clearly labelled Import
action in Settings instead of permanently occupying toolbar space. Import still
opens its dedicated preview/review/explicit-Apply workflow; opening Settings is
not applying an import. Enter in the Import path previews, never applies.
Keep Workspace, Import and Updates in a bounded scrollable in-app page, with
compatibility/recovery/technical material behind a disclosure.

Keep Settings accessible before opening a Workspace and during invalid,
incompatible, or unavailable Workspace states. Preserve existing opening,
validation/conversion, recovery, update, and draft-protection flows.

Healthy reading omits routine Ready/validated/saved rows and inactive recovery
actions. Concise notices and accessible Details retain errors, stale/unavailable data, pending or failed
saves, unsaved-work warnings, and relevant Team-note review warnings must remain
conspicuous. Moving management controls must not hide operational problems or
turn failed actions into success-shaped status.

## Adaptive layout

### Wide windows

Use People navigation beside a Person workspace. A compact identity header
anchors that workspace outside the reading scroll. All ordinary facts sit above
the full-width notes/context area:

```text
Quiet Workspace label | Search local information                 | Settings
----------------------+--------------------------------------------------
People navigation     | Compact anchored Person identity and actions
                      |--------------------------------------------------
Favorites             | All ordinary labelled facts
Reporting tree        |--------------------------------------------------
                      | Person note reading / editing surface
                      | Secondary Team context
                      | History access
```

Keep the selected Person understandable while making their writing useful
without scrolling past the full profile and its provenance.

Use sensible adaptive pane proportions and a draggable People divider. Remember
user-chosen proportions as percentages, not absolute pixel widths. Apply minimum
usable widths rather than blindly shrinking panes to satisfy a ratio. Switching
to a compact layout must not overwrite remembered wide-layout proportions.

The notes pane may expand without stretching prose across its entire width.
Maintain comfortable reading line lengths; purposeful breathing room is allowed.

### Compact windows

When the wide arrangement no longer fits, replace persistent People navigation
with a clearly labelled People control opening a temporary panel. Keep search
and Settings directly accessible.

Keep identity anchored above the shared facts-and-full-width-notes scroll.
Do not squeeze three columns into the window, shrink text to compensate, or use
horizontal scrolling as the normal way to read a profile.

The temporary People panel overlays the Person view; it does not compress or
shift that view into a narrow column. Selecting a destination returns to the
full-width Person view, subject to existing navigation/save protections.

The compact search mockup could visually suggest a shifted underlying view.
That is a drawing limitation, not the agreed behavior: use an overlay.

## Person profile and navigation

Emphasise name, role, department, Manager, location, and contact information.
Keep the photo/placeholder, identity, and principal actions compact rather than
stacking each into a separate full-width row. Preserve Preferred name versus
Directory name and non-editable email identity.

All ordinary facts are exposed by default in a width-dependent, consistently
ordered grid. Missing facts retain a labelled dash; populated values do not
repack the grid. Role, department and email remain in identity, not duplicated
in the facts. Edit
profile still exposes all editable fields, including empty ones, and retains
its explicit Save/Cancel and stale-draft behavior. Preserve independent
Preferred-name and Favorite quick actions, Manager/report navigation, and
explicit external-link opening.

Outside search, People navigation provides Favorites and the reporting tree.
During search, temporarily replace those regions with a full-height results
list instead of stacking several competing small lists. Clearing the query
restores Favorites and the previous reporting-tree position.

Search still covers existing profile and note information, including its
current/historical distinctions; it is not reduced to a name-only filter.
Secondary notes remain discoverable and openable through search. Preserve
archived-information access and the distinction between the Imported population
and a complete company directory.

## Ongoing notes and secondary notes

The interview exposed a difference between the existing multiple-note UI and
the owner's expectation of one ongoing document about a Person. Change the
default presentation, not the note cardinality or existing writing.

- Provide one designated ongoing Person note for ordinary continuing use.
- Apply the same presentation to the selected owner's own Team notes: one
  ongoing Team note, with other owned Team notes secondary.
- Open the ongoing note directly for ordinary use rather than requiring a
  permanent title list or note picker.
- Keep other notes accessible through a secondary Other notes action and search.
  Do not remove the ability to keep separately titled notes.
- Where several notes already exist, let the user choose the ongoing note rather
  than silently choosing the newest or last-viewed note. Keep the designation
  changeable later.
- Never automatically merge notes, delete other notes, or create a replacement
  document to avoid that choice.

The ongoing designation is not a new note type, a transfer of ownership, or a
change to inheritance. A secondary Team note does not stop contributing current
context merely because another Team note is designated ongoing.

Existing notes open in rendered Read mode with an obvious one-click Edit action.
Keep title/Markdown editing, independent autosave, retry/discard protections,
archival/restoration, and external-change handling. New writing still starts
directly in editing. The main view should feel like a readable continuing
document, not a collection-management screen.

Empty note areas need a compact, purposeful invitation to write, not a fixed-height
empty list plus disabled controls. Notes with empty bodies remain accessible.
Concealing secondary actions must not conceal unsaved drafts or failures.

### Implemented Person-note slice

The Person-note host now opens its ongoing document directly and puts the
collection behind Other notes. Multiple active candidates require explicit
Use as ongoing; a sole active note may be read without recording a designation.
Search and Other notes open the exact selected document in Read mode; new notes
start editing. An unavailable or archived designated note keeps its recorded
choice, gives visible recovery guidance and never selects replacement writing.

Designations extend version-1 app-local WorkspaceSettings with optional
per-Workspace entries, not synchronized Workspace data or a format migration.
Workspace paths are canonical and matched case-insensitively on Windows; keys
include stable owner email, Person/Team scope and the existing note GUID.
Preference writes preserve other Workspace entries and publish atomically.
Invalid data/versions and read/write failures are explicit and cannot overwrite
previous preferences or mutate note documents. Owned Team writing reuses this
same observable interaction and designation storage.

This section covers ongoing-note behavior and shared designation support.
Exact startup restoration is described below; hosted WPF acceptance remains
an exact-candidate delivery gate.

### Implemented adaptive host

The host retains profile editing and independent note surfaces. Its circular
photo/initials and identity actions stay above a shared content scroll. At every
width the fixed-order facts precede full-width notes/context. Ordinary facts
have no More details gate, and reading documents size to their content rather
than using nested fixed-height scrolling boxes. Editors and bounded lists
retain their own appropriate scrolling.
People becomes a labelled overlay, not a squeezed column. Escape/Close returns
focus to People; accepted destinations close the overlay, while rejected
navigation retains its panel, owner, exact writing and actionable feedback.
Search replaces Favorites/tree with a full-height result list. Clearing restores
their in-session expansion and scroll position, including after overlay use.
Exact current, archived and frozen search destinations remain distinct.

The numeric implementation choices are:

| Token | DIP value / behavior |
|---|---|
| Wide breakpoint | Available Workspace host width >= 960; this excludes native window chrome and the 20-DIP outer margin on each side |
| Supported window | Minimum 700 x 500; initial 1100 x 900, unchanged |
| Pane minimums | People 240; one Person allocation 420, preserving at least 360 for content after padding/scrollbar |
| Facts | Eight stable labelled slots, one to four equal columns from available width (240-DIP target per slot), above notes |
| Default proportions | People 25% of navigation/Person space, excluding the divider; the legacy profile ratio is retained but unused |
| Divider | People only, 8 wide; mouse drag and 10-DIP keyboard arrow steps, constrained to usable minimums |
| Compact People overlay | Up to 360 wide, over the unchanged Person viewport |
| Typography | Segoe UI 14 for ordinary text and Markdown; identity 24; note-section headings 18; no compact type reduction |
| Spacing | Outer 20; pane/content separation 12; ordinary field spacing 4/8/12 |
| Note reading limit | 600-wide, left-aligned prose with no extra Markdown page inset; section structure remains full width for Person, owned/inherited Team, historical and search reading |

Percentages extend the same optional per-Workspace version-1 app-local settings
entry as ongoing designations. Divider writes preserve other Workspaces,
designations and the startup folder, use the existing lock/atomic publication,
and report failure without claiming durability or changing the last valid
balance. Resize/minimum clamps are not preference writes; compact transitions
never save over wide choices. No synchronized Workspace schema changes.
Both sides of the People divider remain proportional columns, so dragging
respects both minimums inside the available viewport rather than allowing
a fixed-width column to push reading offscreen. Existing optional inner-ratio
preferences are neither migrated nor erased, and do not affect composition.

The required high-resolution `responsive-workspace` journey measures actual bounds/text at 700 x 500,
1100 x 900, maximized, and available host widths 959/960/961, then exercises
mouse/keyboard People resizing, minimums, all-facts ordering, anchored scrolling,
long/missing values, bounded prose, resize/reopen balance and
search/clear restoration. `responsive-draft-navigation` adds a real
protected compact-overlay rejection; `responsive-team-history` retains the
700/1100 rendered attributed-warning and frozen-history matrices. The nine
ordinary desktop journeys remain on 1024 x 768 without display changes.
The separate disposable hosted job prepares only an advertised, driver-tested
1920 x 1080 / 32-bpp / 96-DPI mode and verifies actual native capacity before
launch; prerequisite failure never skips or shrinks the matrix. Explicit
10,000-Person UI scale stays separate and reuses that preparation.
These are implemented assertions, not a
claim that hosted acceptance has run for this candidate; compiled/invisible
local checks do not prove these rendered numeric choices.

## Team context and history

Person notes are the main reading/writing surface. Current inherited Team context
is a clearly separated secondary section that is easy to expand. Preserve the
distinction between notes owned by the selected Person and notes inherited from
every formal management ancestor.

Keep source attribution and review state understandable. Relevant warnings must
be visible even when the detailed context is collapsed. Selecting an ancestor's
context leads to its owner's existing review/editing workflow; it does not turn
inherited writing into an editable part of the selected Person's note.

Put structured profile history, field confirmations, source observations, and
Historical team context behind secondary History access. They must remain
available without occupying the main path to everyday notes.

Preserve [manager-owned context](adr/0003-manager-owned-team-context.md), frozen
historical captures, and all current-versus-historical distinctions. Do not
silently hide review-needed writing, transfer notes to replacement Managers, or
rewrite history as part of this layout change.

### Implemented owned-Team and secondary evidence slice

Owned Team notes now reuse the Person ongoing/Other interaction, including
explicit ambiguous choice, stable owner/scope/GUID preferences, exact search,
Read-first existing notes, direct new-note editing and unavailable-choice
guidance. Designating a Team note never filters inherited context or mutates
writing. Preference updates preserve the adaptive host's pane proportions.

Person writing remains first. All active formal-ancestor Team notes remain in a
separate expandable context section. Attributed review warnings for inherited
and owned Team notes stay outside collapsed details/Other notes. Following an
inherited note validates its current applicability and opens the exact owner's
existing review/edit workflow; failed or retained independent writing rejects
that navigation without replacing its editor.

History starts collapsed and retains the structured Profile timeline, field
confirmations, source observations and frozen former-chain writing. Current
inheritance, frozen owner identity and exact historical search stay distinct.
The same surfaces live within the existing wide/compact adaptive host; headers
wrap and Markdown retains its 600-DIP reading limit. Existing title/Markdown
autosave, retry/discard, archive/restore and external-change protections remain.

Workflow and real-file regressions cover secondary inheritance/navigation,
durable Team designation, preference failures/nonmutation and retained drafts.
The existing hosted Team/history journey adds actual ongoing/Other, collapsed
warnings, protected owner navigation and current/frozen evidence assertions at
700 x 500 and 1100 x 900. These are implemented assertions, not a claim that
hosted acceptance has run for this candidate.

## Startup and remembered view

Within the reopened Workspace, restore the last Person and the exact last-viewed
note, including a secondary note. This explicitly supersedes the earlier interview
suggestion to always show the ongoing note on startup. A secondary note remains
identifiable and the ongoing note stays easy to reach.

When there is nothing valid to restore, use a purposeful search/Favorites starting
view, not an empty or disabled profile form. Restore only available, validated
data; a remembered selection is not permission to reactivate archived data,
create replacement records, or conceal a failed Workspace reopen.

Remembered view preferences do not imply restoration of unsaved drafts or a new
draft-recovery system. Preserve the existing save/navigation/restart safeguards.

### Implemented exact-view restoration

The shared observable People navigation now retains the last valid destination.
Successful existing-only reopening resolves that destination against validated
current Workspace data before presenting a Person. Existing secondary Person
and owned Team notes override their ordinary ongoing default and reopen in Read
mode. Other notes and search remain exact, and Open ongoing note is deliberate.
Same-Person navigation and compact/wide transitions do not substitute a different
note or write over remembered pane proportions.

Version-1 app-local WorkspaceSettings accepts optional `lastView` on each
canonical, case-insensitive Workspace entry. It contains stable Person email;
note destinations additionally identify source, owner email and existing GUID.
Frozen Team destinations also identify the historical capture GUID. Resolution
uses current notes, current formal inheritance or the specified frozen capture,
not title, list order or a later current note with the same GUID. A Person-only
destination is valid when no note was selected. No synchronized schema changes
or unsaved-buffer persistence are involved.

Unavailable/archived Persons or notes leave the remembered preference and all
writing intact. The UI explains the failure and offers search/Favorites/People
re-selection without opening ongoing replacement writing, creating records or
unarchiving anything. First use and absent view preferences use the same
purposeful start rather than a disabled profile. Failed Workspace reopen never
attempts selection restoration or creates a missing folder.

All preference writes preserve ongoing choices, pane proportions, other
Workspaces and the successful startup folder. Atomic publication and explicit
read/write/version/identity errors retain prior durable values. A failed view
write does not remove valid in-session reading or claim that it was remembered;
deliberate destination selection retries it. Existing profile Save/Discard/Stay,
independent note drafts, external-change checks, safe close and safe restart
remain in force, including settings-driven reopen.

Real-file tests exercise exact secondary reopen/rename, owner/context identity,
unavailable targets, compatibility, failed preference publication and retained
writing. Existing Person-note, Team/history and search journeys reuse their
scenario-local settings and repeated launches for exact GUID/Read-mode reopen
and invalid-target recovery. Responsive Team reopen retains the 700/1100 matrix.
These are implemented assertions, not a claim that the final candidate's hosted
lanes or the overall #57 joint delivery are complete.

## Scope and implementation decisions still required

The intentional behavior additions are ongoing-note designation, remembered
Person/note selection, and remembered proportional pane sizing. Everything else
must preserve the existing ownership, import, editing, synchronization, history,
and data-safety contracts.

The concept images illustrate reading and searching at wide and compact sizes.
They are not pixel specifications or proof of working responsiveness. Incidental
mockup icons, sample content, or earlier drawings' invented Groups/Organizations
navigation are not new features.

Resolve these details before their dependent implementation:

- Hosted acceptance of the adaptive numeric choices above against rendered WPF,
  rather than treating compilation or a computed mode as usable-layout evidence.
- Final exact-candidate hosted acceptance of remembered secondary-note reopen
  and unavailable-target recovery alongside the existing safety journeys.

Visual research inspected a running isolated sample Workspace at normal and
maximized sizes, including profile view/edit and note reading. Minimum-size
inspection was not completed reliably. Compact layouts are approved concepts,
not observed behavior of the current application.

## Delivery boundary

This documentation is a planning baseline, not completed implementation or
permission to merge. Follow the
[documentation handoff contract](agents/documentation-handoff.md): retain its
focused source commit on a pushed, unmerged branch, establish the integration
owner/branch before implementation, and deliver these design documents with the
matching behavior.

The producing session returns the immutable source/base commit record after
pushing and verifying it. A later spec and every implementation ticket must carry
that verified record; do not substitute a narrative reconstruction of this design.
