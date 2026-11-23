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
Keep the photo/placeholder, identity, and principal actions together rather than
stacking each into a separate full-width row. Preserve Preferred name versus
Directory name and non-editable email identity.

Show a compact, read-only Person category badge beside the name in identity,
including on archived profiles. Initially the labels are `Employee` and `Vendor`;
provide an accessible category label rather than relying on badge color.
Keep the name primary and preserve the anchored header's photo footprint,
full accessible text, and narrow-window behavior.

The approved November 14 photo-size amendment (#81) specifies a permanent
144-logical-unit circular photo or initials placeholder at every supported
window size, with proportionally larger initials and the current centered
circular crop. This supersedes compact photo sizing, not the anchored header,
explicit profile Save/Cancel, or the 700-by-500 minimum window. See the
[anchored identity contract](visual-refresh-design.md#anchored-identity) for
the complete amendment and rendered acceptance requirements. The native control
implements this sizing; hosted rendered verification and owner visual acceptance
remain separate delivery gates.

Manager remains anchored in identity. Always show Team name as the first
scrolling fact immediately below that header, keeping the full wrapping path
out of the fixed-height identity band. Display the applicable names nearest-first,
including an eligible selected manager's own label, separated by ` > `. Each
segment identifies its owning manager on hover or keyboard focus; inherited
labels are read-only. An edit action changes only the eligible selected manager's
own label, never the displayed path. Preserve duplicate labels and skip unnamed
managers. Use `Not set` when no names apply, including on archived profiles;
unlike other missing facts, this row does not use a dash or disappear. Eligibility
requires at least one current, non-archived direct report in the workspace.
A retained label is hidden and not editable while that requirement is unmet.

Immediately after Team name, show a compact `Reporting structure` fact when the
selected Person has at least one current, non-archived direct report. Use the
ordinary facts grid, not a separate card, chart, or large-number tiles. Put Total
and every supported category count in short inline text, initially Total,
Employees, and Vendors, including zero counts. Only the count value uses a
borderless, read-only selectable text control: selection and Copy are available,
but the derived counts cannot be edited. Keep ordinary fact typography and
content-sized spacing. Let the text wrap at narrow widths
without reserving a separate row for each category. Add the scope hint
`Known current reports, all levels`: counts cover imported Reporting descendants,
exclude the selected manager, and do not establish company-directory completeness.
Hide this fact when there are no current reports; retain the category badge.
The existing direct-reports list and its direct-only count remain distinct.
See [Person categories and reporting counts](design.md#person-categories-and-reporting-counts)
for classification and population rules.

All ordinary facts are exposed by default in a width-dependent, consistently
ordered grid. Missing facts retain a labelled dash; populated values do not
repack the grid. Role and email remain in identity, alongside labelled Manager navigation and a
compact Office line combining building and room with a middle dot. Show only
available Office parts, or a neutral dash if both are missing. Department is a
labelled fact, not an unlabelled identity value. Long header values retain their
full accessible text, Copy action and tooltip when ellipsized; the header must
not grow beyond the existing photo footprint for ordinary reading. Edit
profile still exposes all editable fields, including empty ones, and retains
its explicit Save/Cancel and stale-draft behavior. Preserve independent
Preferred-name and Favorite quick actions, Manager/report navigation, and
explicit external-link opening.

Outside search, People navigation provides Favorites and the reporting tree.
During search, temporarily replace those regions with a full-height results
list instead of stacking several competing small lists. Clearing the query
without activating a result restores Favorites and the previous reporting-tree
position. Successful activation instead reveals the chosen Person as described
below.

Search still covers existing profile and note information, including its
current/historical distinctions; it is not reduced to a name-only filter.
Secondary notes remain searchable: their matches lead to their Person, whose
notes remain available through the normal note surfaces. Preserve
archived-information access and the distinction between the Imported population
and a complete company directory.

### Person-level search

This approved design supersedes the destination-level search behavior described
in the implemented slices below. The #107/#108 implementation covers grouped
Person results, distinct-Person counts, independent highlighting, query-field
keyboard interaction and guarded ordinary-profile activation with accepted
clear/reveal/focus. Exact-candidate hosted evidence is recorded in
[integration PR #109](https://github.com/geevensingh/org-explorer/pull/109);
deliver the combined implementation and this documentation together.

- Show one result per Person, using the existing email identity, across matching
  profile fields, Person/Team notes and history. Different people with the same
  display name remain separate. Show the Person's name and email with a compact
  explanation of matching fields/sources, retaining current, archived and
  historical attribution rather than repeating the Person for each match. Lead
  with the strongest matching field/source and its excerpt, not the first match
  encountered. Retain the other matching evidence through the existing
  presentation; do not add scores, explanatory prose or controls.
- Count distinct people, not individual matches. Preserve existing matching:
  trim surrounding query whitespace and use case-insensitive literal-substring
  inclusion. Ranking must not add or remove qualifying People. Do not introduce
  fuzzy, reordered-name, noncontiguous-word or accent-insensitive matching.
- Rank each Person by their strongest matching evidence, using these tiers from
  best to weakest:
  1. Exact match on their own Preferred name, Directory name, Alias or email.
  2. A whole word or contiguous whole-word phrase in their own Preferred name or
     Directory name.
  3. A word-prefix match in their own Preferred name or Directory name.
  4. Any other substring match on their own Preferred name, Directory name,
     Alias or email.
  5. Their other current profile fields, except Manager, or their owned current
     Person/Team notes.
  6. Their current Manager field or currently inherited Team-note context.
  7. Historical evidence, including former identity and reporting information
     and frozen Team notes, regardless of textual match quality.
- Give Preferred name and Directory name equal standing within those tiers.
  Whitespace and punctuation separate name words. A name-word-prefix match
  begins at a name-word boundary but can end partway through a word; a whole-word
  or whole-word-phrase match also ends at a boundary. This classification does
  not relax literal-substring inclusion. Treat Alias and email as identifiers,
  not collections of name words.
- For example, searching `sachin` places an exact Alias `sachin` in the first
  tier; names `Sachin Singh`, `Sachin-Singh` and `(Sachin)` in the second;
  `Sachindra Singh` in the third; and a name `JaySachin` or an email containing
  `sachintala` in the fourth. Typing a complete email produces an exact identity
  match. Searching `Sachin Sri` does not newly match `Sachin Kumar Srivastava`.
- Sort equal-tier People alphabetically by their displayed heading: Preferred
  name, falling back to Directory name and then email. Use email as the final
  tie-break for identical headings. Use the same relevance rules for current
  and archived People; archival alone does not make their latest profile
  historical. Additional weak matches do not boost a Person's rank. Retain
  existing evidence order when that Person has equally strong matches.
- Automatically highlight the first result and reset the highlight to the first
  result whenever the query changes. With no results, there is no highlighted
  result. Highlighting is not activation: typing or moving the highlight must
  not change the open Person/profile, load a preview, open notes or navigate
  History. No delayed profile-preview timer is needed.
- Up/Down in the search field move the result highlight while leaving keyboard
  focus in the field so typing can continue. Keep the highlighted result visible
  and stop at either end rather than wrapping.
- Enter in the search field or results activates the highlighted Person; the
  automatic initial highlight makes a single Enter choose the first result.
  Clicking a result activates that Person through the same navigation guards.
  Activation opens the normal Person profile, not a particular matching field,
  note or historical entry. Matching notes and history remain available through
  the Person's ordinary surfaces. With no results, Enter leaves search open.
- Only after successful activation, clear the query and return to People
  browsing. For a current Person, expand/reveal and select their reporting-tree
  row, scrolling to it instead of restoring an old offset that hides it. For an
  Archived person, reveal the existing archived-people list and select/reveal
  their row without unarchiving them or inserting them into the current tree.
  In the wide layout, focus the chosen tree or archived-list row. In the compact
  layout, preserve accepted-navigation dismissal of the People overlay and
  return focus to the People button.
- Preserve existing pending-edit, profile and navigation guards. Rejected
  activation retains the query, highlighted result, panel/owner and exact
  unsaved writing, with actionable feedback; it must not clear search or dismiss
  the overlay. Moving the search highlight alone does not navigate away from
  an editor.

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
Other notes opens the exact selected document in Read mode; new notes start
editing. Search opens their ordinary Person profile; exact secondary documents
remain available through Other notes. An unavailable or archived designated note keeps its
recorded choice, gives visible recovery guidance and never selects replacement
writing.

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

The refreshed Person and owned-Team surfaces share full-width section headings,
restrained actions, light separators and aligned, bounded reading text. True
empty collections offer Write a Person/Team note without a blank viewer or
disabled action collection. An existing empty document instead keeps its exact
title, kind and Read/Edit access, with an invitation to Edit. Archived-only
collections, unavailable choices, ambiguous designations, unreadable preferences
and failed writing retain their own guidance rather than claiming emptiness.

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
The implemented search replaces Favorites/tree with a full-height result list.
Manual clearing restores their in-session expansion and scroll position, including
after overlay use. Accepted activation instead reveals the chosen browsing row.
Person-level results retain current, archived and frozen
distinctions in match explanations instead of separate destination rows.

The #77 slice adds single-line reporting names with modest guide-line indentation,
quiet selection and starred Favorites. Full email/Directory-name identity remains
available to accessibility and through row tooltips; virtualization and reporting
depth are unchanged. Only root containers reserve the bookmarked measured width,
so restoring a horizontal offset does not compound minimum widths down the tree.
Grouped results show name, email, distinct attributed matching fields/sources and
a query-relevant excerpt. Full note ownership, titles and historical provenance
remain in accessible help and the row tooltip. Selecting a row alone is not
activation. Click or selected-row Enter uses the existing draft/profile/navigation
guards and opens ordinary profile/ongoing-note reading, never an arbitrary
matching destination. Other notes and secondary current context/History retain
their independent access. Rejection retains the highlighted row, overlay, query
and exact failed editor. The first result is highlighted without activation;
query-field Up/Down keep typing focus and clamp at either boundary. One Enter
accepts that highlight. Success clears search and realizes/selects/reveals the
current tree row or retained archived-list row; wide mode focuses that row.
Compact success closes People and focuses its button, retaining revelation for
the next People opening. These implementation and invisible replay checks do
not claim hosted input/layout or owner visual acceptance.

The numeric implementation choices are:

| Token | DIP value / behavior |
|---|---|
| Wide breakpoint | Available Workspace host width >= 960; this excludes native window chrome and the 20-DIP outer margin on each side |
| Supported window | Minimum 700 x 500; initial 1100 x 900, unchanged |
| Pane minimums | People 240; one Person allocation 420, preserving at least 360 for content after padding/scrollbar |
| Facts | Stable labelled slots, one to four equal columns from available width (240-DIP target per slot), above notes; Team name stays near Manager |
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
explicit ambiguous choice, stable owner/scope/GUID preferences, Person-level search,
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
inheritance and frozen owner identity stay distinct in ordinary reading and
grouped search evidence.
The same surfaces live within the existing wide/compact adaptive host; headers
wrap and Markdown retains its 600-DIP reading limit. Existing title/Markdown
autosave, retry/discard, archive/restore and external-change protections remain.

Current context keeps readable ancestor attribution outside its collapsed
details as well as the attributed review warnings. Full-width native disclosures
align their headings with the content; History uses the same typography and
bounded prose rather than nested boxes. Owned, inherited and frozen Markdown
all grow within the one Person reading scroll. Its reserved scrollbar gutter
keeps fact slots stable when populated and empty notes have different heights.
This does not change the separate People browsing bookmark or measured range.

Historical captures use a concise local capture date and former Reporting chain,
with a visible reminder that locally known writing is not proof of its state at
the actual change time. Each capture has a collapsed **Capture details**
disclosure for complete transition evidence, observations/recordings, batch
identity and frozen note IDs, versions and update timestamps. Its read-only text
can be selected and copied. These are capture metadata, not Team-note content.
Each frozen note has a separate ruled reading section with its title, former
owner and review state when captured outside that disclosure. Technical evidence
is not repeated in the capture-count summary or note headings. Historical search
opens the Person; ordinary History exposes the immutable capture/note sections. Expanding evidence
does not replace writing or introduce a second reading scroll.

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
mode. Other notes remains exact; search deliberately returns to ordinary
Person/ongoing reading. Open ongoing note remains independently available.
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
