# Testing

This is the authoritative test-placement and execution strategy for developers
and agents ([approved spec #21](https://github.com/geevensingh/org-explorer/issues/21)).
Choose the lowest-cost agreed surface that can prove the behavior, not a project
name. A desktop click alone does not establish a successful navigation or save.

Visual-refresh journeys can capture named, already-observed states through the
shared `CaptureVisualEvidence` harness helper. It reads actual desktop pixels
without opening, focusing, resizing or repairing a view. The existing hosted
desktop lanes retain these PNGs beside JSON recording the source revision,
run/attempt, executable hash, DPI and captured bounds. Calls require unique
state names within a scenario and never overwrite an earlier observation.
Optional application-monitor capture retains the full actual monitor, including
the taskbar region, and labels its scope separately from a window-bounds capture.
Captures follow the same desktop-isolation rules below; they are comparison
material, not behavioral proof or owner visual acceptance.

Browsing-restoration acceptance compares every originally visible Person's exact
identity and viewport-relative horizontal/vertical position within one rendered
pixel, together with the existing Favorites, expansion and guarded-destination
checks. A larger viewport may expose additional bottom rows; it cannot move or
replace any original row. Scroll percentages and view sizes remain diagnostics,
not proxies for restored content: a horizontal scrollbar disappearing increases
the vertical viewport and changes the percentage even at an unchanged offset.
The owner approved this assertion correction after the hosted #71 captures.
The original 20-second observation budget and full fixture/size matrix remain.
Fixture preparation still makes one end-measurement request and one interior
scroll request, without replaying either. If virtualization remeasures the extent
after that request, a genuinely interior position establishes the recorded row
baseline; an unchanged endpoint still fails. The later exact-row restoration
check, not the requested percentage, proves that browsing was preserved.

## Placement

| Category | Observes | Examples and limits |
|---|---|---|
| Workflow / view model | The real shared `IApplicationWorkflow` and observable `WorkspaceViewModel`, primarily in memory | Identity, every textual field, import omission/null/false, whole graphs, inheritance, frozen context, dates, query permutations, favorite state and safe-close errors. In-memory success is not durability or WPF evidence. |
| Filesystem integration | The same interface backed by real isolated folders | Save/reopen, copied and decoded images, locks, atomic publication, invalid/incomplete delivery, concurrent clients, bounded watcher events, version refusal/recovery and retained context. A projection does not prove rendered pixels. |
| Desktop smoke | Normal WPF executable and actual UI Automation | Representative import/edit/save/reopen journeys; selection/bindings, Manager/report navigation, draft rejection feedback, local photo pixels, Markdown reading, current/Historical attribution, archive/filter/reactivation, Settings and refresh reaching an open client. |
| High-resolution smoke | Normal WPF executable on a separate disposable hosted 1920 x 1080 desktop | Required responsive geometry/type/width/divider/overlay/search/rejection and Team/history matrices; ordinary fixtures, not the 10,000-Person UI-scale suite. |
| Non-UI performance | Complete real-file 10,000-Person operations | Load/reopen, search, durable mutations, reviewed imports, refresh and full rich semantic assertions. Targeted large-graph correctness remains in ordinary workflow coverage. |
| UI scale | Real WPF at the full rich population | Tree realization/scrolling, large-population search/navigation, actual photo/Markdown rendering and responsive Settings while real load/mutation is pending. Neither smaller fixtures nor view-model substitutes prove these risks. |
| Runner / CI contract | Observable execution and saved result contracts | Named results, sequential isolation, failure diagnostics, required selection, deadlines, required-lane failure matrix and product-output isolation. |

`OrgExplorer.Core.Tests` is **not synonymous with isolated unit tests**. Its two
file-backed `WorkspaceCompatibilityTests` carry `Category=Filesystem` and still
run in the core lane; they observe unsupported-version nonmutation and validation
retry through the view model. `DesktopScenarioRunnerTests` also write real result
and fixture files (`Category=Runner`) without opening WPF. Most other core cases
use in-memory workflow state. Classify dependencies by what a test actually does.

`OrgExportTests` also run in the core lane with isolated fictitious files. They
exercise the shared `ExportSession` and the actual compiled producer CLI through
a project reference: mixed profile outcomes, corrected-input recovery, retained
evidence, publication rejection, earlier-success rejection, identity correction,
JSON array cardinality and process exit status.
No live M365 connection or desktop automation is involved.

Use independent expected values at the agreed public surfaces. Keep parsing,
storage mechanics and graphs behind the shared workflow; private-method tests,
per-record public repositories and success-only app flags are not migration seams.
`BeginNoteDraft` captures an immutable Person/Team document base (or expected
absence) at the workflow/view-model seam. Editors display that snapshot and
`RetainNoteDraft` marks it dirty without waiting behind in-flight Workspace I/O.
Close/restart stays blocked through first autosave and retries; `SaveNoteDraft`
returns the next base on success, retained again for newer typing. Real-file tests cover delivery before
first save, absent → created and deleted documents, retained external bytes,
discard/intentional edits and ordinary I/O retry. Parser boundary tests remain
behind that same seam; Team confirmation and record-version checks stay explicit.
Preferred-name Save/Enter submits the displayed field's immutable Person base
through `SavePersonTextDraft`; typing, blur, navigation, Settings and generic
update/close draining do not submit it. `DiscardPersonTextDraft` cancels only
that field's unsaved failure, never other fields, profile sessions, quick actions,
notes, Import, committed maintenance or durable publication conflicts.
Changed-field delivery rejects save/retry;
other-field delivery is retained, and success returns the exact saved base for
newer typing rather than rereading a possibly newer provider revision.
Ordinary profiles use `BeginProfileEdit`, staged text/Manager/photo operations,
and explicit `SaveProfileEdit` / `CancelProfileEdit`. `ProfileEdit` exposes the
person-scoped immutable baseline and complete draft. `TryLeaveProfileEdit`
observes Save / Discard / Stay; only successful Save or explicit Discard permits
leaving. Profile Cancel clears only that session, never independent note or
quick-action failures. Unsaved profiles block safe close and updater restart.
Person More actions require confirmation before the same profile leave decision
and apply archival/reactivation immediately through `EditPersonArchived`, not as
profile-draft fields. Cancellation preserves the draft and saved history; Stay
and rejected leave-save retain the owner. Profile Discard never clears note drafts.
File-backed profile-session cases cover complete publication and reopening,
Cancel nonmutation, real locks and missing photos, Manager/history consistency,
independent quick changes, same-record/different-field stale saves and retries,
previously incorporated delivery and unrelated-person delivery. Save revalidates
persisted data even without an explicit view refresh.
Import navigation permits returning to its own failed Apply recovery view without
clearing the retained Import error or claiming safe close. Independent note,
quick-action and dirty-profile writing still block entry. A real-folder case holds
the publication lock, rejects Apply, returns to People, releases the lock, reenters
Import and explicitly previews/applies again before checking durable values.
The explicit Import-only discard clears the rejected batch and review without
clearing independent Person/Team-note errors, Preferred-name errors or profile
drafts. Real-folder cases prove unchanged saved bytes, retained writing, and
safe-close/restart becoming available only when no other writing remains.
People and exact-note browsing remain available after failed Import or recovery
maintenance without clearing either error or claiming safe close. Independent
writing and publication conflicts still reject navigation; rejected startup
restoration reports recovery without publishing a partial remembered view.
Blocked Workspace navigation is distinct from an attempted open failure. The
compiled Retry action must still complete current storage maintenance without
reopening or creating the blocked destination or resetting the current view.
Control only genuine external effects (clock, browser/release/installer boundary);
do not mock away the business behavior being asserted.

`WorkspaceWriteObservationTests` drives the real file-backed workflow with an
optional `IWorkspaceFileObserver`; payload writes and durable flushes stay real.
It accounts for transient and repeated writes, failed publication/cleanup, and
current/recovery/transaction/index writes, not just surviving file sizes.
Older-version rejection has zero observed writes. The
[incremental storage protocol](incremental-workspace-storage-protocol.md)
defines the shipping publication, recovery and guarded reclamation contract.

`IncrementalProfileStorageTests` exercises shipping version 3 through
the same file-backed workflow (and the view model for text drafts). It creates
an empty Workspace, seeds accepted Import evidence, and reopens profile text,
favorites, relationships, notes, images and history. The rich small regression
compares the same edit with 6 and 258 People, counting every observed payload
and metadata write, including transient stages. It checks reused unrelated
document bytes as well as a bounded byte increase; final directory size alone
cannot pass it. Real pointer replacement failures prove retained drafts,
retry/discard and no staging adoption. Receipt failure distinguishes a committed
edit from incomplete transaction maintenance. All tests use the public factory;
there is no activation bypass. Small regressions do not claim desktop coverage
or 10,000-Person evidence.

`IncrementalRecoveryTests` and the extended rich profile-write regression drive
the same file-backed workflow with independent sealed recovery. They restore
through `RecoverWorkspace` and the normal reader after current Person/history,
photo and note corruption, checking commit identity and independently expected
relationships, evidence and writing. Real publisher/lock failures cover recovery
object/candidate staging, current replacement, committed receipt and recovery
selector replacement. Interrupted candidates are never adopted on reopen;
maintenance-only retry preserves current bytes/history/note dates and an
unchanged Import preview. Recovery inventories, seals, staged payloads and
selector/receipt writes all participate in byte accounting. The 6/258-Person
comparison also proves zero copies of unrelated recovery documents, not merely
a smaller surviving directory. There is no cleanup or desktop evidence here.
With #46's required staging-path inventories and retirement evidence, the
6/258-Person comparison allows a fixed additional 4 KiB only for measured
affected-path ownership provenance. That provenance delta is independently
capped; non-provenance writes retain the original 8-KiB delta allowance.
Every transient write still counts toward the unchanged 32-KiB total ceiling,
and zero-unrelated-copy assertions are unchanged.

`IncrementalReclamationTests` drives automatic retirement/deletion through that
same real-file workflow. Thirty-six edits with unrelated rich profiles count all
transient writes, bound redundant document/index/bookkeeping retention separately
from genuine growing history, then reopen and restore independently expected
evidence, notes and frozen context. Locked deletion proves explicit committed
maintenance failure, partial progress, durable pending state across restart and
maintenance-only retry without edit replay or stale Import review. Cleanup-time
pointer/index/document/photo and invalid/unsupported delivery stays stale until
complete, while ambiguous intent/candidate/receipt failures, changed superseded
bytes, unknown/conflict files and historical photos remain protected. These are
small required filesystem cases, not legacy cleanup,
desktop or supported-population scale evidence.
Recovery-selector, object, index and seal delivery after validation also suspends
cleanup without deleting the previously selected independent graph; retry protects
the committed current state without replay. Same-capture index-byte reuse must
not hide changed index or seal bytes from the next publication/deletion guard.
The reviewed 128-Person publication also bounds ownership-file count and proves
each inventory batch is durably flushed before any corresponding payload write,
then corrupts current documents and restores the independently expected population.
Interrupted scalar inventories remain readable alongside bounded batches.
Malformed interrupted ownership reports maintenance failure and blocks safe close
without deleting recorded files; partial-batch cleanup retains immutable evidence.

The #43 extension counts actual index writes for both creation and update of a
reviewed 128-Person Import, including shared paths, and checks old graph visibility
at the real publication boundary. Small relationship, archival/reactivation and
favorite cases assert unchanged document bytes/references and durable context.
The existing photo suite also rejects an actual selector replacement with a
multi-Person graph/image update, explicitly retries it, then clears the images
and reopens retained historical resources. Existing reporting/replay, typed
evidence, unknown prior evidence, frozen context, photo, archival, inherited
reading/search/favorite, Team-note review and note-draft assertions run against
the shipping factory. Current-format fixtures retain all rich record families;
older archives serve only explicit nonmutation/refusal cases. These remain
ordinary small filesystem tests, not supported-population scale evidence.

`IncrementalDeliveryTests` covers valid direct Person/history/note edits, stale
Import previews, selector/index/document/history/photo arrival permutations,
invalid and unsupported index graphs, and preservation of provider conflict
copies. Missing accepted evidence or photos retained only by history/captured
profiles prevents acceptance. Real publication callbacks deliver Person/history
or same-path photo bytes immediately before/after replacement; refresh must
detect them rather than bless an unvalidated post-save scan. Existing external
refresh, local-publication, live-generation, profile-session, text-draft and
note-draft cases run through the same shipping factory.
These are real-folder/workflow assertions, not desktop or cleanup evidence.

The owner-approved publication-conflict extension deliberately replaces the old
unconditional final-window rejection assertion. Real selector callbacks deliver
same-Person/history changes; Person and Team note callbacks deliver writing at
replacement. Assert positively published conflicts, exact independent sealed
captured bytes, retained local writing/source roots, safe-close/restart blocking,
generic-discard/maintenance nonresolution and restart survival. Explicit review
and acknowledgment bind conflict IDs and the complete current byte revision;
newer delivery and damaged evidence reject acknowledgment. Unconfirmed callbacks
before/after replacement and actual partial file effects remain indeterminate,
not clean refusals or automatically adopted recovery. Creation/deletion/backup
collisions use the shipping publisher's explicit modes. Actual linked recovery
selectors are never advertised or reused. These focused cases do not establish
hardware power-loss durability, complete capture of noncooperating delivery,
desktop interaction or supported-population scale.
Journal-only sibling failures block both profile and note writers before staging,
without relying on watcher delivery. Null evidence/path entries are rejected through
normal storage errors. Reviewed base/displaced provenance includes absence; later
changes require another review. Resolved interrupted note stages remain intact while
new intentional edits use distinct inventoried paths, and verified sibling resolution
clears only the conflict error while retaining independent drafts.
A barrier-held real publication also proves dispatcher-facing conflict snapshots
return without waiting on the save's I/O gate and remain immutable after publication.

`OrgExportTests` covers the local producer's public capture and
Ingest/Profile/Photo seams using fictitious session events and synthetic PNG bytes.
The required core lane builds the bundled producer through the same project reference;
it verifies exact ordered request correlation, native/MCP/error-wrapped
payloads, shared-stream worker isolation, retained partial successes, explicit
incomplete-result blockers, rejection of unverified host-saved references
(including files replaced after completion), and unchanged photo
length/content/hash validation.
It performs no live WorkIQ reads or desktop automation.

`WorkspaceStartupTests` uses app-local `WorkspaceSettings` and the real
workflow/view-model with isolated folders. It covers first run, preference/app
reopen, missing-folder noncreation and Retry, locked/invalid Workspace rejection,
alternate-folder recovery, intentional manual creation, failed attempts retaining
the previous default, and visible corrupt/unwritable preference errors. These
cases also prove that both manual and existing-only settings opens retain a
dirty profile, independent quick changes, saved bytes and the successful
preference until an explicit leave decision. These are filesystem/settings
assertions, not WPF evidence or a second profile-mode policy.

Person-note presentation is observed through `WorkspaceViewModel.PersonNotesView`
and the shared owned-note commands, not a control-specific test adapter.
Person-note cases cover explicit ambiguous designation, a changed choice without
note mutation, exact Other notes/search targets (including empty bodies), direct
Read versus new-note editing, observable secondary access, independent profile
writing and rejected retained-draft navigation. `WorkspaceStartupTests` extends
the real settings/reopen seam with version-1 preference compatibility, canonical
Workspace/owner/scope identity, retained unavailable/archived choices, locked or
corrupt preference rejection and byte-for-byte unchanged note documents.
Invalid preferences are preserved until deliberately repaired outside that
failed write; manually opening a Workspace is not permission to overwrite them.
Failed designation retains both currently displayed notes by exact identity,
including sole-note reading without saved designations. Real-file cases require
unchanged preferences and Workspace writing before explicit retry succeeds.
Unrelated pane, exact-view and startup-write failures keep their own feedback
without replacing ambiguous/archived ongoing-note guidance; unreadable preferences
and designation failures remain explicit.
`NoteDraftPersistenceTests` retains a failed secondary draft and its exact editor
while ongoing navigation is rejected, including subsequent external delivery.
The existing Person-note desktop journey additionally observes the collapsed
Other notes picker, direct rendered Read/Edit, ambiguous designation, exact
secondary search, empty invitation, unavailable choice and visible preference
and note-save failures. Those assertions require hosted Windows; non-UI success
does not constitute their execution.

Owned Team presentation reuses that same observable owned-note interaction.
Both compiled controls share the ongoing/Other status, identity and action
presentation; their local draft guards and Team inheritance/review remain separate.
`TeamNoteTests` covers all-ancestor and secondary-note inheritance, exact
owner-correct review navigation, retained independent Person writing, new/empty
Team writing and frozen search identity after designation, editing and archival.
`WorkspaceStartupTests` extends the existing real-file seam with Team designation
reopen, canonical owner/scope/GUID identity, unchanged Workspace writing,
preserved pane proportions, locked/corrupt/unsupported/invalid preferences and
missing/archived targets. The secondary-draft file-backed regression runs for
both scopes, retaining exact editing and delivered bytes on rejected ongoing
navigation. No preference write changes synchronized Workspace schema.
The later-owner regression retains a failed preference until deliberate
designation succeeds, then verifies that choice survives archival/reopen.
Both Team/history desktop selections explicitly record the reviewed owner's
ongoing choice before asserting unavailable-choice feedback after archival.

The #75 alignment extends those existing seams, not the note model. Focused
Person-note cases cover true collection emptiness versus an exact empty document,
an archived ongoing choice and rejected first-save writing for both scopes.
The real settings/reopen suite additionally rejects an empty invitation while
ongoing preferences are unreadable, preserving exact preference and Workspace
bytes until deliberate repair. Existing ownership, inheritance, frozen evidence,
independent drafts, external bases, retry/discard and restoration assertions remain.
The existing delivered-owner-removal draft case also requires explicit unavailable
Person guidance rather than an empty invitation before first-save rejection.

The existing Person-note and normal/responsive Team-history selections capture
unique populated, empty-document, ambiguous, failed, unavailable, collapsed-review,
current-owner and frozen-owner states with `CaptureVisualEvidence`. Responsive
Team/history retains 700/1100 and exact reopen checks, adding complete long
current/frozen Markdown. Explicit reading-range scrolling exposes the asserted
prose before its capture; it does not repair People browsing or a destination.
All original GUID, attribution, saved-byte, review and rejection checks, scenario
names and lane budgets remain. Captures require hosted Windows and are still
pending for each new complete candidate; they are not whole-pass visual acceptance.

Adaptive People navigation is observed through `WorkspaceViewModel.PeopleNavigation`
and its navigation/query/browsing operations. Focused cases preserve an exact
secondary note and frozen search destination, and retain owner/editor, expansion,
scroll position, overlay and actionable feedback on rejected navigation.
The in-session browsing bookmark captures horizontal as well as vertical offset;
neither belongs to synchronized Workspace data or a preference schema change.
It also retains the measured content width. Root reporting rows reserve that
width while unseen wide labels are virtualized, keeping the bookmarked horizontal
offset reachable instead of clamping it to zero. This minimum applies only to
root containers, not recursively to descendants or the outer People pane.
Existing accepted/rejected exact-note tests retain both axes and measured width.
`WorkspaceStartupTests` uses real settings/files to prove percentage reopen,
canonical per-Workspace identity, interleaved designation/startup writes, unchanged
Workspace bytes and locked/corrupt/invalid-version/invalid-percentage rejection.
These do not prove WPF geometry.

The #77 search extension exercises live, query-relevant excerpts without navigation
or mutation of the complete note body. The invisible compiled replay distinguishes
query Enter, row selection and explicit result Enter, then observes the real matched
fact and current/frozen owner-correct document sections. It retains the full cold
two-axis browsing replay and exact-row criterion; logical focus without a native
window is not proof of actual keyboard delivery.
Existing normal/responsive search and scale callers now explicitly focus/select
and activate a result rather than relying on selection to navigate. Query Enter
must preserve the current Person and exact note before focusing results. Hosted
search checks observe actual matched-field focus, real note Read surfaces and
exact document/capture identities, not a duplicate generic inspector. The existing
responsive draft journey also rejects search activation with overlay/query, exact
failed editor and unchanged Person bounds retained. Unique visual captures cover
matched facts, mouse activation, exact/empty Other notes, current/frozen attribution
and rejected writing within those existing journeys. No scenario, population,
deadline, required lane or explicit-scale selection is removed. These captures
and hosted assertions still require execution for the complete candidate.

Exact startup view uses the same observable People/owned-note state and real
settings/files in `WorkspaceStartupTests`. Cases close/reopen secondary Person
and owned Team notes by GUID after rename, retain Read mode and ongoing choices,
preserve same-Person Team selection and interleaved per-Workspace percentages,
and resolve inherited/frozen note ownership without substituting current writing.
Unavailable/archived Persons/notes, failed Workspace validation and missing
folders preserve preferences and writing. Corrupt/unsupported/invalid preferences
and locked publication are explicit; a failed view write retains valid in-session
reading without creating a designation. Settings-driven existing-only reopen
cannot replace retained independent drafts or enable unsafe close/restart.

The existing Person-note journey's second launch requires the exact secondary
Person GUID/title/kind in Read mode before deliberately opening ongoing writing.
Both normal and responsive Team/history journeys similarly reopen the secondary
owned Team note; the latter also retains the 700/1100 resize assertions.
Normal/responsive search journeys observe archived-target startup recovery,
unchanged Workspace bytes and deliberate archived access with reopened ratios.
No additional whole journeys, manifest changes or deadline increases are needed.
Startup helpers require the exact Workspace, completed activity, current
validated data, expected Person and absence of visible Workspace/view recovery
errors. No-selection expectations remain for fixtures without saved valid views.
Their runner regression rejects an earlier open-success observation until the
saved destination is actually present. The full UI-scale mutation journey now
waits for that validated startup restoration instead of issuing a redundant
manual open whose completion could clear its new query. Its 10,000-Person fixture,
180-second waits and final durable profile/Manager/favorite/photo checks remain.
Local helper/file/control checks do not prove hosted reopen or UI-scale execution.

The explicitly selected high-resolution `responsive-workspace` journey measures the adaptive host at
700 x 500 and 1100 x 900 window DIP, maximized, and available host widths
959/960/961 around its 960-DIP breakpoint. It observes unchanged overlay viewport
bounds, all-facts-before-full-width-notes composition, anchored identity during
actual long reading, People 240 / Person allocation 420 minimums,
600-DIP bounded actual note prose, stable long/missing fact slots, unchanged compact typography, mouse/keyboard
People-divider effects, wide/compact/wide and actual reopen proportions, and real
reporting-tree expansion/scroll restoration during persistent and overlay search.
Wide browsing setup expands its fixture branch only if needed. Compact setup
only observes the already-restored expanded state: it never reissues Expand or
repairs lost expansion before measuring scroll/search restoration. Runner
regressions replay the observed external provider's redundant-expansion side
effect and require collapsed restored state to fail, not be silently repaired.
Before capturing each 70% browsing baseline, one explicit scroll to the fixture
end measures cold virtualized rows, followed by the single target scroll.
Neither action is retried, and neither occurs after search/clear restoration.
The cold-cache compiled replay first reproduces percentage drift caused by a
changing measured extent, then exercises this same preparation and the exact-row
restoration criterion above. Hosted logs retain unmeasured/measured
view sizes, focus and owner, plus the original/failure diagnostics: calibration
must not conceal later extent drift or change the selected Person.
These are harness-action contracts, not a local reproduction of native WPF focus.
Post-resize collapsed-context assertions wait only for read-only UIA discovery
within the existing bounded wait. An expanded section still fails immediately;
the helper never collapses it, and permanently missing sections still fail.
Runner coverage replays the observed missing-then-present context provider.
The invisible compiled replay additionally drives the 44-Person/long-note
fixture through real tree scrolling with cold virtualization and recorded
available viewport geometry, exact-note search/clear and wide/compact
restoration, including recycled tree-item load events. Compact replay establishes
a real 39-DIP horizontal offset, then drops the cached measurement and invalidates
the tree's measure while browsing is hidden, retaining all People and expansion.
This reproduces the lost horizontal range that clamps even a correctly retained
offset. Every original row must return to the same horizontal and vertical
position; offset-only restoration goes red, while reserving the captured width
keeps that position reachable. It does not reproduce
native focus or establish that a hosted UIA timeout is fixed. Hosted browsing
failures retain the exact-row/visibility assertion and capture the
before/failure pixels, viewport percentages, bounds and focus without corrective
actions or a longer deadline.
The high-resolution `responsive-draft-navigation` journey additionally rejects one compact People destination
with the exact failed Person-note editor retained and actionable feedback.
Selection helpers deliberately open compact browsing when needed; they do not
treat a hidden tree or a closed panel as successful navigation. Existing exact
current/archived/frozen destinations and independent draft assertions remain.
Only hosted Windows execution supplies this presentation evidence; the size
matrix belongs to its separate required smoke suite, not a substitute for complete explicit UI-scale work.

## Moving coverage

1. Read the actual assertions at both layers before removing a desktop check.
   A similarly named test is not equivalent coverage.
2. Add missing lower-level assertions first and run them. For example, a failed
   Person-note retry must not overwrite externally delivered writing; deliberate
   discard accepts that version, and a subsequent intentional edit reopens.
3. Retain the representative journey and any unique WPF behavior. Exhaustive
   field, query, note-scope, inheritance, graph, transfer, replay, archival,
   compatibility and delivery permutations belong below WPF; file safety stays
   file-backed. One rendered profile is not proof of every field's business rules.
4. Validate the migrated runner contract locally without UI, then run the real
   scenarios hosted. Keep missing/unexecuted work red. There is no separate
   coverage-map document; tests and these placement rules remain authoritative.

## Required PR/main execution

Every PR and `main` commit, including docs/test-only changes, retains four
independent parallel jobs: workflow/view-model, filesystem, ordinary desktop,
and high-resolution desktop on its own disposable GitHub-hosted Windows runner.
The stable aggregate `CI` requires all four successes. Failure, required skip,
cancellation, missing result and timeout cannot pass. Preserve the existing
5/11/11-minute original lane deadlines, an 11-minute high-resolution lane and
2-minute aggregate deadline: 40 minutes summed, at most
13 minutes on the parallel critical path, excluding queues and later CD.
The five-minute target includes setup, restore, build, fixtures, tests and cleanup;
passing a hard deadline does not claim that target or a new latency SLA.

Default local verification is non-UI:

```powershell
.\eng\Run-CiLane.ps1 core
.\eng\Run-CiLane.ps1 filesystem
```

Each runner isolates build artifacts. They may run in independent processes.
The filesystem runner excludes only `Category=Performance`; ordinary persistence,
delivery, recovery and watcher coverage remains required. The core runner also
checks the CI gate, separate-suite selection/results and named desktop-result
contracts without launching WPF.
For a narrow change, use `dotnet test` with combined selectors rather than
rebuilding/rerunning unrelated suites. Contract checks without launching WPF:

```powershell
.\tests\CiGate.Tests.ps1
.\tests\CiRunner.Tests.ps1
.\tests\DraftDiscard.Tests.ps1
.\tests\Performance.Tests.ps1
.\tests\DesktopResults.Tests.ps1
.\tests\UiScale.Tests.ps1
dotnet test tests\OrgExplorer.Core.Tests --filter 'FullyQualifiedName~DesktopScenario|FullyQualifiedName~NavigationRejectionTests|FullyQualifiedName~ControlReadinessTests|FullyQualifiedName~UninstallProcessTests'
dotnet build tests\OrgExplorer.Desktop.Smoke -c Release
dotnet build src\OrgExplorer.Desktop -c Release
dotnet run --project tests\OrgExplorer.Desktop.Smoke -c Release --no-build -- `
  src\OrgExplorer.Desktop\bin\Release\net10.0-windows\OrgExplorer.Desktop.exe --verify-xaml
```

`--verify-xaml` is a separate non-UI build/load contract, not a desktop scenario.
It constructs the actual compiled MainWindow BAML and event connectors on STA
with isolated settings, requires the window to remain invisible with no native
main-window handle, then closes it. It never calls Show or runs the application
dispatcher; startup restore/update Loaded handlers and an external UI Automation
client are not run.
This catches XAML connection failures that compilation alone misses. It does not
prove rendered controls, navigation, dialog discovery or any smoke/scale result.
It also checks that all ten profile Copy targets accept focus and Tab navigation.
The actual compiled Person-note surface and secondary title list must expose
distinct automation identities, so collapsed/empty-list discovery cannot select
the visible surrounding surface instead.
The Team surface also has a distinct identity. The compiled contract requires
collapsed Other Team notes, collapsed inherited details with warnings outside,
and secondary History containing profile changes, confirmations, observations
and frozen Team evidence.
The compiled chrome contract keeps folder management and Import inside Settings,
the quiet Workspace name non-interactive, and search outside the People-only view.
Settings occupies the bounded main work area rather than growing above it;
operational feedback and startup recovery stay outside that scrolling view.

The separate `--verify-profile-refresh` option runs that load contract and then
drives the actual compiled controls' open/select/Save events on a bounded STA
dispatcher with isolated real files, still without showing a window or creating
its native handle. It observes note content at profile Save completion, before a
later watcher callback could hide stale same-owner reading, and proves both clean
delivery refresh and dirty-note retention. It does not run Loaded startup/update
handlers or UI Automation. The desktop lane runs this additional regression
within its existing budget. Actual Tab/context-menu Copy and Import-only discard
remain in the representative hosted profile journey.
The same invisible-control regression exercises all ordinary facts, labelled
missing-value slots, long identity/facts, compact initials, full editing of an empty field and Cancel,
and Settings-to-Import rejection retaining independent failed Person-note writing
and delivered bytes. It is not rendered-layout or actual keyboard evidence.
Its inline profile check measures paired fields at wide widths and stacked fields
at the minimum Person allocation, then covers 700 x 500, 1100 x 900 and wide
content. It requires enabled, Tab-accessible, readable-width fields without
horizontal page overflow, notes below the form, and identity/Save/Cancel remaining
anchored during scrolling. Logical entry/Cancel focus, routed field Enter and
retained staged text are also checked without creating a native window handle.
Physical focus, Tab scrolling, Enter delivery and appearance still require hosted
WPF; these compiled observations cannot substitute for them.
It also measures/arranges the actual compiled content and drives its real
People splitter DragStarted/DragDelta/DragCompleted events through minimum/maximum
drags, 27% balance recovery and a narrower wide viewport. Facts and notes must
remain full-width and ordered inside the actual ScrollViewer viewport while
held and after completion. A long document must size to content, move with facts
under the main scroll, and leave identity fixed. Compact layout uses fewer fact
columns, and opening its overlay must not move the Person viewport. The actual
clear-search action returns to browsing without replacing the selected Person.
These are compiled wiring/measurement checks, not rendered acceptance.
The same invisible regression drives the actual Preferred-name Save/Cancel
controls: typing and a generic update drain do not publish; failed Save retains
its input/local error across an unrelated Favorite success; Save retry retains
the original base across external delivery, and deliberate cancellation permits
a new entry against the delivered value. Real held profile/note publication
proves that cancellation closes without blocking behind admitted I/O, cannot
revoke it or permit premature close, and newer input waits for another explicit
Save. The Popup is never opened while the window is invisible. Its actual
discovery, focus and physical Enter/Escape/outside-click behavior remain hosted
proof, not an inference from these controls.
Paired proportional columns keep both opposing People/Person minimums in WPF's
shared resize constraint. This does not replace
hosted physical gesture/hit-target or rendered-pixel acceptance.
The same real control also receives routed Left/Right keys after the maximum
drag, requiring the exact 10-DIP change to survive layout and preference
publication. Its unshown input source has no rendering target: this tests the
actual key handler, not physical focus or native input delivery. Pane layout
callbacks must not restore the previous ratio during the key's layout/save
transaction.
It additionally drives actual compiled owned-Team create/read/designate/Other
controls, an exact empty secondary note, a locked preference rejection and a
failed secondary-note save with protected ongoing navigation and successful
retry, then deliberate preference recovery and archived-choice feedback.
Save completion requires Ready and the note-specific success message, allowing
the explicit informational storage-reclamation deferral suffix after a failed
transaction. Existing failed-save and publication-conflict guards still apply.
The same real controls receive timestamp-only note delivery while clean, then
save from the refreshed immutable base. Person and Team archive/restore actions
also require the restored exact note to become the durable Read destination.
Ongoing designation keeps note/navigation mutations on the dispatcher while
its host runner performs preference I/O. Compiled controls retain query/overlay
changes made during that write and publish the resulting navigation on the UI thread.
Both compiled controls preserve Show archived notes when selecting active writing.
Read/Edit changes immediately present locked/corrupt exact-view preference errors
and clear the warning after a successful explicit retry, without an extra refresh.
These invisible checks do not show/focus WPF or prove rendered geometry.
The same compiled-profile-refresh selection also measures long Person, owned
Team, current inherited and frozen historical prose at 700/1100 and wide
available sizes, requiring content-sized reading, bounded measure and aligned
full-width sections. It observes owned/inherited warnings and current-owner
attribution with details still collapsed, and exact empty-document Edit
invitations for both scopes. The window stays invisible without a native handle;
these measurements do not supply rendered, keyboard, hosted or visual-acceptance
evidence.
It also drives the actual Settings button through the WPF dispatcher and checks
logical focus on the folder, including closure before a queued focus request.
The focus request waits behind layout and the current input event; the unchanged
hosted Space-key/`HasKeyboardFocus` assertion remains the physical-focus proof.

It also exercises the existing public note-flush boundary after a failed save,
successful retry and newer writing while Settings hides the editor. With the
lane's ephemeral trusted-update build, it additionally drives the actual signed
Check for updates command through in-process WPF button providers: failed writing
blocks handoff, retry and newer writing reach the controlled installer effect,
and that effect observes the final file hash. These calls still never show/focus
the window, create its native handle, or use an external UI Automation client;
they do not replace the real hosted update-settings journey.
`--verify-update-yield` is a non-UI compiled-window regression in the required
desktop lane, within its existing budget. It requires the ephemeral
trusted-update/effects build and does not replace a desktop scenario.
It composes the actual window with the real file-backed workflow through an
internal application-construction seam and the same held note publisher used
by persistence tests. A dispatcher hook selects the real post-drain Background
yield, admits one note save at Normal priority, and waits for publication to
reach the hold. A ContextIdle observer requires the update to remain pending,
with unchanged saved bytes and no early handoff, before releasing publication.
The same regression repeats with a deliberately queued Preferred-name Save
command and held real profile publication. Merely unconfirmed Preferred-name
typing is separately rejected by the drain; only this explicit command admits it.
The original single update action must then hand off the final durable bytes
and matching file hash; there is no update retry. The shared work drain
rechecks its gate after dispatcher settling and waits again for an admitted
save, while existing failed-save, profile/draft and final restart guards remain
active. This regression never shows/focuses a window or creates its native
handle.
The failed-note case also opens Settings and attempts Close once. Rejection must
reveal the retained work area instead of leaving Settings over the failed editor,
including after queued focus work settles. Owner, title, Markdown, actionable
failure text and saved file bytes must remain intact. The hosted Person-note
journey retains its original Close action and visible failure/content assertions.

Dependency lock files are authoritative. Restore missing assets with locked mode;
do not install unrelated tooling. A build proves compilation, not a desktop pass.

`tests\ApplicationIcon.Tests.ps1` decodes all ten native ICO frames and checks
transparency and C1 tile/card/Person content. With `-Executable`, it inspects the
real default PE icon group and compares every frame's bytes, without executing
the product. The core lane checks the asset; ordinary desktop product-output
isolation checks the published executable. The invisible compiled-window check
also requires the complete ICO to load through the window's actual `Icon`.
Existing installer-artifact checks inspect setup, full-package app and
`setup.ico`, portable launcher/app resources, and produced product/version
metadata. No installer is launched by these checks.

The existing profile and installed desktop journeys compare the live window's
small/large `WM_GETICON` pixels with the matching produced native frames, saving
unscaled PNGs and a light/dark compositing plate with executable hash and candidate
metadata. This is native-resource evidence, not a screenshot of the taskbar or
Start menu. Alongside the existing window screenshot, those journeys also save
actual full application-monitor pixels with the same candidate/executable
identity. Review the monitor PNG for the visible taskbar and app button; neither
registrations nor the HICON plate proves those shell pixels. Capture does not
open Start, reveal an auto-hidden taskbar, change theme/focus, or replay setup.
Start-menu and setup-dialog appearance therefore remains unproven by this path.
The isolated installed lifecycle also resolves the real Desktop/Start
shortcuts and Installed Apps icon target after install, upgrade and reinstall.
These checks do not replace actual Windows small-size/surface review or the
whole-pass owner review. They must not be run on the shared local desktop.

Installer-runner process tests use a separate non-UI .NET fixture, never the
installed app or setup. Its parent exits with application files already removed
while a named-event-gated cleanup child still owns the fixture installation.
Reinstall must remain blocked until that child exits; a held child must consume
the original remaining deadline, fail, and be terminated with its owned job.
These process-boundary regressions do not replace hosted installation/Workspace
preservation verification.

## Real desktop execution

**Use hosted Windows or a dedicated isolated interactive desktop. Local desktop
automation requires separate explicit permission.** Routine test implementation,
this strategy and a request for CI do not grant permission to open/focus windows on
a shared workstation. An unavailable/locked interactive desktop is a visible
prerequisite failure, not a skipped photo assertion or an invitation to change
shared-machine settings.

On that authorized desktop, the required command is:

```powershell
.\eng\Run-CiLane.ps1 desktop
```

It builds the real product and harness, runs all selected scenarios **sequentially**
in one process, verifies complete named results, then publishes a local
product-check folder to reject leaked fixtures/test assemblies. That publish is
not installer verification or release publication. After the lane prepares the
ephemeral embedded update trust and fixture environment, the runner's invocation
has this form (the lane uses its isolated artifact paths):

```powershell
dotnet run --project tests\OrgExplorer.Desktop.Smoke -c Release -- `
  src\OrgExplorer.Desktop\bin\Release\net10.0-windows\OrgExplorer.Desktop.exe
```

The nine ordinary required names live in `tests\DesktopScenarios.json`. The lane
verifies native current-mode and monitor bounds at 1024 x 768 / 32 bpp / 96 DPI
without changing its display. They cover:

- `profile-journey`: preview/apply/rejection, Manager/report identity, preferred/directory
  binding, readable Preferred name and one-click Add/Edit, explicit Save and
  physical Enter, retained inline validation failure, Cancel/Escape/outside-click
  dismissal without mutation, popup discovery/focus and uniquely named normal,
  failure and dismissal captures,
  quiet chrome and Settings keyboard entry/focus, compact photo/identity bounds,
  all ordinary facts with stable labelled missing-value slots and no More details gate,
  readable ordinary values and idle state after profile Cancel,
  representative partial change/clear, save/reopen, actual copied PNG
  and JPEG pixels, photo clear/placeholder and Profile timeline. Import is opened
  explicitly, replaces the people view, and returns to reading with Import
  controls absent. Choosing/previewing retains the saved population; changing the
  path invalidates Apply until reviewed again.
  Inline editing additionally requires initial Directory-name focus, Tab to Alias,
  actual field Enter followed by Tab without submission, unchanged saved office
  data before explicit Save, and focus returning to Edit profile after Cancel.
  Named captures retain the field-Enter draft and staged photo clear.
- `draft-navigation`: explicit dirty-profile Save / Discard / Stay, failed
  leave-save retention and Import protection versus independent failed Person/Team drafts,
  explicit blocked actions, retained owner/draft, deliberate discard, then
  separately successful navigation. Opening the dedicated Import view exercises
  all three profile decisions and a locked-file leave-save rejection, then checks
  reading values after returning; independent failed notes still block Import.
- `person-note-rendering`: Read-first existing notes with both editors hidden,
  one-click editing, empty-section actions, direct New-note editing and empty-body
  accessibility, actual Markdown text, loopback
  no-fetch assertion, failed filesystem save, restored tree selection,
  Workspace/close rejection, retry, independent ownership and durable reopening.
- `team-context-history`: explicit Team ownership, all-ancestor attribution,
  ongoing/Other designation and exact secondary search, Read-first owned notes,
  direct New-note editing and empty-body accessibility, visible preference/save
  failures and collapsed attributed warnings at the ordinary desktop size,
  protected owner review navigation/confirmation and archive/restore,
  secondary History with confirmations/observations, actual current/frozen
  reading and honest historical dates through a representative
  ancestor move, exact frozen search and reopen.
- `search-favorites`: representative profile/note queries, real current,
  archived and frozen destinations, reading, favorites, failed-save rejection
  and retained favorites after reopening.
- `archival-navigation`: Person More/confirmation cancellation, dirty-profile
  Save/Discard/Stay and independent-note protection, rejected Manager archival, failed local save and retry,
  current-tree filtering, retained archived profile, selection protection,
  reactivation and explicit no-Manager UI.
- `compatibility-settings`: global Settings before open, current-format reopening,
  older/future-version nonmutation and write blocking, validation retry and an
  explicitly chosen supported alternative. Startup coverage also checks first-run controls, automatic
  same-Workspace reopening without a selected Person, an honest empty recovery
  state with Retry/Choose actions and no replacement creation, successful Retry,
  failed manual attempts retaining the old default, and alternate-folder reopen.
- `update-settings`: actual default-enabled startup and manual checks against a
  controlled loopback feed with ephemeral embedded public trust, responsive local
  editing/saving during a pending check, signed availability, persisted opt-out
  and reopen, unknown signer/tampered signature/unsupported channel/rate-limit/
  offline rejection, and a visible signature failure on a later startup.
  Preference changes wait for the app's accepted-setting guidance as well as the
  checkbox state before closing; a visually toggled checkbox alone is not a save.
  Authorized results are consumed automatically: signed invalid package bytes
  fail Velopack verification. A valid controlled signed package then exercises
  real locked-note save rejection, retained writing, explicit retry, and final
  draft durability before the external installer boundary. The unpackaged
  boundary reports a deliberate installation failure; it is not an install pass.
  The lane prepares this normal-product test build and later verifies a fresh
  production-trust publish; see [the protocol contract](update-protocol.md#controlled-verification).
- `external-refresh`: actual folder delivery changes the open profile, note
  editors, inherited reading/review and same-path photo pixels; invalid graph
  delivery becomes visibly stale, retains last good data and recovers. Person
  and Team typing across delivery also verifies first-autosave/retry rejection,
  retained editor/delivered writing and deliberate discard through real WPF.

The fourth required lane is `.\eng\Run-CiLane.ps1 high-resolution`, restricted
to its disposable GitHub-hosted Windows VM. Its exact named selection is
`tests\HighResolutionScenarios.json`, invoked with `--high-resolution`:

- `responsive-workspace`: the search/Favorites journey plus the complete
  700 x 500, 1100 x 900, host-width 959/960/961 and maximized matrix, actual
  Segoe UI text, People 240 / Person allocation 420 minimums, 600-DIP prose cap,
  all-facts ordering and long/missing text, anchored main scrolling, overlay bounds/focus,
  mouse/keyboard People resizing, percentage resize/reopen and search/clear position.
- `responsive-draft-navigation`: the complete draft-protection journey plus
  actual compact overlay rejection retaining the exact owner/editor and feedback.
  Its inline form also traverses every ordinary field and Manager by Tab at
  700 x 500, 1100 x 900, host widths 959/960/961, maximized and the minimum
  Person allocation. Each focused control must fit the actual scroll viewport
  without moving identity or Save/Cancel; stable email stays read-only and notes
  stay below the draft. Named top/Manager and failed-leave-save captures support
  final whole-pass comparison, not automatic visual acceptance.
- `responsive-team-history`: the complete owned/inherited Team, independent
  draft and frozen-history journey plus the 700/1100 rendered warning/history
  matrices. All ordinary domain/ownership/history assertions remain in the
  ordinary journey as well.

The split is by explicit scenario name, never a conditional screen-size skip.
The two smoke manifests and explicit UI-scale manifest are nonempty, unique and
disjoint; each lane requires its own complete named `scenarios.json`. Ordinary
results cannot stand in for the high-resolution suite.

Before any app launch, the high-resolution fixture requires GitHub Actions,
`RUNNER_ENVIRONMENT=github-hosted`, Windows and 96 DPI. It enumerates the actual
primary display's monitor-compatible modes, requires an advertised 1920 x 1080
32-bpp mode, tests it with `CDS_TEST`, and applies it dynamically exactly once
with no registry/persistence, unsafe-mode, primary-device or DPI changes.
It then observes the exact selected mode, actual 1920 x 1080 monitor bounds,
96 DPI, work area >=1800 x 1000 and track capacity >=1100 x 900 within the normal
bounded observation. Driver rejection, restart requests, missing modes, wrong
read-back or inadequate capacity fail the lane; there is no synthetic fallback.
`display.json` records actual native prerequisites, not rendered acceptance.
The job VM is disposable, so no restoration step is needed. This is not
permission to change any local, self-hosted or shared display. Explicit UI-scale
execution reuses the same preparation on hosted VMs; an already-authorized
isolated local desktop receives read-only capacity verification, never a change.

The filesystem update tests exercise actual cryptography, staged files, HTTP,
Velopack integrity/explicit launch arguments, real pending Import publication,
failed Person/Team saves, current-format restart, unsupported-data
nonmutation and durable unconfirmed-restart recovery. Only actual external
transport/process effects and clock are controlled. Neither a success flag nor
a per-record repository is an update safety seam.

Actual authenticated install/restart is additionally covered by the hosted-only
`installer-verification.yml` workflow. Its two real self-contained fixture
versions embed ephemeral public trust and a build-time external transport
adapter; the normal verification/workflow and actual Velopack installer are
unchanged. It requires the final running version, automatic same-Workspace
reopen, pre-handoff file hashes, retained final WPF writing, unauthorized-pending
protection, and both owned uninstall-tree/directory-absence barriers. Missing
fixture or automatic-update evidence fails the hosted lifecycle. The final
production build rejects test trust/effects; always-run cleanup removes private
fixture material. This is not production-update/public-key provisioning evidence.

Both required desktop suites use ordinary fixtures. They do not construct the
complete scale population; all genuine UI-scale work has explicit execution below.
Every scenario opts out of startup update checks before launching the app,
except the controlled `update-settings` and `installer-update` scenarios.
This includes ordinary smoke, manual normal-build execution and installed
create/reopen flows; they must never fetch or apply a production release.
Every scenario also supplies its own `ORG_EXPLORER_SETTINGS_DIRECTORY`, initially
without a remembered Workspace. Launch fails closed without an explicit absolute
settings directory, so no fixture falls back to the workstation owner's actual
preferences or Workspace. Repeated launches within one scenario deliberately
share only that scenario's settings to exercise durable reopening.

For investigation, append one name to the harness command, not the app
(`profile-journey`, for example). Older `migration`, `--search-favorites`,
`--person-notes`, `--team-notes` selectors remain aliases.
The obsolete combined `scale` / `--scale` selectors are rejected; use `--ui-scale`
or a single explicit scale scenario name. `--high-resolution` selects only the
three required high-resolution scenarios and is never part of default ordinary smoke.
A targeted pass is not the complete required lane. `DESKTOP_RESULTS_DIRECTORY`
may select a separate result directory for manual runs.
The full selection and `update-settings` require the prepared update fixture and
matching test-trust build; a production-trust build cannot substitute for that
controlled evidence. Other focused scenarios may use a normal build.

`scenarios.json` is written before execution with all selected names `NotRun`,
then `Running`, then `Passed` or `Failed`. It includes times, fixture locations
and exception diagnostics. Interrupted work remains incomplete, never green.
The required lane validates against the manifest, not just exit code or PASS text.
Failures continue to independent scenarios while budget remains. Each has a fresh
fixture; successful fixtures are removed after workflows/apps stop. Failed small
fixtures stay at their reported path, and `*-ui.log` captures available control
IDs/names before abnormal app termination. Scale fixtures also live at their
reported scenario paths: apps/workflows stop before cleanup, successful fixtures
are removed, and failed fixtures remain for investigation. Hosted artifacts
include result JSON and logs, not the complete fictitious Workspace.

Successful navigation waits for meaningful local completion and asserts destination
email/context. Explicit rejection paths invoke once, assert retained owner,
selection/draft and actionable status, and never retry their way into success.
Pending and in-progress messages must explain waiting; failures require
retry/discard. Poll bounded eventual states; keep existing 20-second smoke and
180-second scale limits. Remote content is never fetched just to render notes or
profiles; loopback listeners and actual pixels/text are evidence, not filenames
or image dimensions alone.

UI Automation invocation can return before WPF processes the queued button
handler. Click and value helpers acquire an enabled control and its required
pattern within the existing bounded observation wait, then invoke or set once
without a second control lookup. Only preparation is retried, never the action.
Raw Click/Set and idle/startup observations never open or close Settings.
Journeys prepare Settings explicitly before folder, Import or update actions,
and deliberately return to People before subsequent writing. Rejected-close
observations require Settings to be closed by the app before retrying the
retained note; input helpers cannot repair a hidden editor and mask that failure.
The headless readiness regression scripts missing/disabled controls and transient
pattern discovery at that shared helper; hosted WPF remains the actual UI proof.
Startup restore observation likewise accepts an absent/offscreen recovery label
only after the requested folder, Ready activity and current validation agree;
no selected Person or visible recovery error is accepted. Its headless replay
covers the collapsed-label hosted failure and rejects wrong-folder, pending and
stale observations without retrying Open. Failed-startup smoke still requires
the actual visible error and enabled Retry/Choose recovery controls.
The exact opened folder is read from the quiet Workspace label's accessibility
description; the visible full-path editor remains in Settings. Restore checks
still require the exact folder and live current-data/owner/recovery observations
within the same bounded wait, not just a successful folder-name match.
The profile navigation scenario enters Edit, stages a value, and exercises the
real Save / Discard / Stay decision, including a locked-file leave-save failure.
Completed rejection requires concrete failure feedback, a closed leave dialog,
enabled profile Save controls, retained owner/value, and pending unsaved-draft
activity. It must not require Ready while that rejected draft is still retained.
Accepted profile Discard refreshes both the displayed values and status/activity
before successful navigation: Ready only when no independent writing remains.
Discarding a profile while a Person/Team note failed must retain pending activity
and that note's owner/editor until its own deliberate discard.
External-delivery retries use the same distinction for profile and Person/Team
note drafts: settled main-work controls plus concrete rejection and pending
writing, followed by unchanged owner/editor and delivered-byte assertions.
Before invoking Retry once, subscribe to actual activity changes and require that
retry's saving activity followed by retained-writing Pending completion. The
preceding rejection or an intermediate Ready alone cannot satisfy observation;
headless runner replay covers queued and fast retries and missing transitions
within the existing observation budget.
The scale mutation scenario retains actual in-progress-work rejection while
Settings renders. A rejection check invokes once, then observes the actual rejection
message within the existing deadline while requiring the original Person to
remain selected. A generic pending message or a completed save is not rejection
evidence. The runner's non-UI ordering regression covers pending → rejected
observations and refuses missing feedback, wrong feedback or changed destination;
hosted WPF still supplies the real UI evidence.
Headless draft-discard coverage compiles the actual note coordinator with only
its timer substituted. Busy retries must refuse discard before any refresh or
draft mutation; idle explicit discard remains available.
Person-selection assertions always require the current tree and also require
the Archived people list when its filter is on. A collapsed Archived list is not
a missing-control failure. Headless replay of the shared assertion covers that
absence and still rejects wrong selections or missing required visible lists;
hosted WPF supplies the real selection/visibility evidence.

## Separate scale commands and results

The complete workloads are outside required PR smoke, not removed:

| Command | Required selection | Evidence |
|---|---|---|
| `.\eng\Run-Performance.ps1` | Complete `Category=Performance` filesystem workload | `.ci-results\performance\result.json`, unique run directory, complete named TRX and logs |
| `.\eng\Run-UiScale.ps1` | Both names in `tests\UiScaleScenarios.json` | `.ci-results\ui-scale\result.json`, unique run directory, `scenarios.json` and UI/step logs |

Non-UI performance proves genuine 10,000-Person load/reopen, current/historical
search, durable mutations, reviewed 10,000-record imports, original/latest batch
replay, external delivery/recovery and complete retained-data comparisons.
Targeted large-graph/no-import-cap correctness remains in required core coverage.

UI scale builds and drives the **normal WPF executable**, sequentially in one
interactive Windows job, with a fresh rich 10,000-Person fixture per scenario:

- `scale-navigation-rendering`: Settings visibly works while real opening is
  pending; expand/collapse, scroll and select a distant realized tree row;
  current/frozen/deep-chain/final-archived-Person search and Manager navigation;
  actual photo pixels and rendered current/frozen Markdown.
- `scale-mutation-responsiveness`: Settings visibly works while an actual
  file-backed profile mutation is pending; explicit navigation rejection retains
  the owner and explains waiting; favorite mutation, complete reviewed import,
  external note delivery/rendering, and durable profile/Manager/favorite/photo
  reopening. Valid external delivery automatically completes independent recovery
  maintenance before the ordinary Ready, safe-close and durable-reopen checks;
  the runner must not rescue that successful delivery through Settings retry.

The same shared `ScaleFixture` preserves 9,900 active/100 Archived people, the
complete 42-edge chain, 2,500 photo references, 1,000 Person notes, 111 Team notes,
200 frozen contexts and retained Profile evidence. No population/chain truncation,
import cap or non-UI proxy replaces either WPF scenario. The old combined scale
runner is retired; it is not also invoked by smoke or the explicit command.

For investigation on an authorized interactive desktop, append `--ui-scale` to
the harness invocation above, or one scale name. Use `Run-UiScale.ps1` for the
complete bounded candidate-bound suite. A one-scenario pass is not suite success.
Missing/locked interactive desktops and unavailable pixels are visible failures;
never skip rendering, change shared-machine settings or launch local automation
without separate permission.

Each suite has its own **660-second whole-execution pass/fail budget**. The hosted
watchdog allows thirteen minutes so termination, failure reporting and artifact
upload can finish after a failed budget; the extra time cannot make the run pass.
`SCALE_STARTED_AT` begins before checkout/SDK setup;
restore, builds, fixture generation, assertions and cleanup all count. These
are execution bounds, not new latency thresholds or a baseline collection scheme.
Both standalone commands use `eng\ScaleRunner.ps1` for the budget, owned command
lifecycle, fresh reports, exact-candidate checks and cleanup. Workload selection
and complete named-result assertions remain in their separate entry points.
See [performance validation](performance-validation.md) for fixture detail and
historical measurements, not current command authority.

Each invocation marks a fresh root report `running` before execution and writes
revision, expected revision, dirty/untracked state, selected tests/scenarios,
outcome, timestamps and timings. Successful suite status requires all named work;
missing/duplicate/partial results, failed assertions, skipped/cancelled/timed-out
work, changed candidates, cleanup errors and exhausted budgets fail closed.
Interrupted reports remain incomplete, never an old passing result.
While an owned command is running, atomically replaced reports publish a heartbeat
every five seconds, its active command, and the most recent unmatched phase begin.
Non-UI progress comes from `scale-measurements.log` in the current unique run
directory, not buffered passing-test stdout. The reader ignores unrelated inherited
`SCALE_TIMING_FILE` paths and falls back to command stdout until the current run's
file exists, using the same source for the final progress capture.
Reports distinguish command execution, termination and cleanup, and become terminal
only after cleanup and the unchanged budget check.
Owned-command termination does not depend on successfully publishing its report;
report-write failures still reject verification.

Scale logs retain `SCALE BEGIN`/`SCALE END` markers even when an operation fails.
Fixture generation, current files/indexes, open/validation and independent recovery
are separate phases. The real workflow observer reports capture, validation,
recovery preparation/publication, object writes, reclamation and fresh rechecks,
with completed writes/bytes, durable flushes, publications and deletes.
Storage phases also record process CPU time; reports retain processor/runtime/image
metadata for host comparisons. Nested counters, wall time and process CPU time are
inclusive, so do not sum overlapping phases.
Actual WPF logs also separate Import navigation, preview, apply, return, close and
cleanup. No file contents or Person writing are added to storage-phase diagnostics.
Scale fixture observers retain diagnostic-write/output failures without throwing
through storage callbacks. Fixture disposal reports them as verification failures
after closing clients and cleaning owned fixtures, preserving real save outcomes
and original storage errors rather than silently accepting missing diagnostics.

The direct scale workflow's `extended_observation` input is explicitly diagnostic.
It lets the unchanged UI workload be observed for up to 900 seconds with a
seventeen-minute watchdog, while retaining the original 660-second verdict.
Reports set `diagnosticOnly`, distinguish observed completion from the verdict,
and never report candidate-verification success in this mode, even if observed
completion is under 660 seconds. Its artifact name starts with
`diagnostic-ui-scale-`, not the normal verification prefix. Normal release
verification and non-UI execution do not opt into it.
For repeated measurements, freeze one full revision and retain each run's
separate artifacts. Invocations remain independent; no concurrency group
replaces a pending trial.

The scale tree locates its distant row through the reporting branch's UI
Automation item provider, requests realization/scrolling once, then observes
visible geometry and real viewport movement before selecting and checking the
destination Person. Realized-descendant enumeration alone cannot find a
virtualized row. A timeout identifies the target and last observed geometry,
viewport offset and selected email; it retains the existing 180-second bound.

## Nightly and manual execution

`.github\workflows\scale-verification.yml` owns **both** independent jobs and
calls the exact commands above. Separate invocations can overlap on the same ref;
no scale concurrency group serializes or cancels them. Each job uses its own
hosted Windows runner. UI scenarios are serial within their own job, not parallel
competing windows. Each suite records the same exact candidate SHA
in independently named results/artifacts; artifacts upload even on failure, and
missing artifacts fail. Neither job is another required PR check.

The workflow declares **10:23 UTC nightly**, direct manual dispatch and
`workflow_call` with a required exact candidate. The nightly schedule and new
workflow dispatch registration become active only after the file reaches the
default branch; wiring them on integration does not activate a schedule.

Before that registration, use the existing `ci.yml` **manual-only opt-in bridge**
from a pushed candidate branch, with the repository-authorized account:

```powershell
gh workflow run ci.yml --repo geevensingh/org-explorer --ref YOUR_PUSHED_BRANCH -f run_scale_verification=true
gh run list --repo geevensingh/org-explorer --workflow ci.yml --event workflow_dispatch --limit 5
gh run view YOUR_RUN_ID --repo geevensingh/org-explorer --json headSha,status,conclusion,jobs
gh run watch YOUR_RUN_ID --repo geevensingh/org-explorer --exit-status
```

Verify `headSha` is the intended candidate, both scale jobs succeeded, every named
result passed and both artifacts identify that SHA. The bridge calls the same
local reusable workflow at `github.sha`; it duplicates no suite commands. The
Boolean defaults to false and its guard requires manual dispatch. The ordinary
four lanes still run on a bridge dispatch, but their independent `CI` aggregate
does not attest to scale success: inspect both scale jobs and the whole run.
Omit the flag for ordinary focused CI only.

Once GitHub registers the scale workflow, direct manual execution is:

```powershell
gh workflow run scale-verification.yml --repo geevensingh/org-explorer --ref YOUR_PUSHED_BRANCH -f revision=FULL_40_CHARACTER_CANDIDATE_SHA
```

Blank `revision` uses that workflow dispatch's commit; supplied values must be a
full immutable SHA, not a mutable ref. Both jobs check that checkout equals the
requested SHA before invoking the runner with `-ExpectedRevision`.

## Active pre-publication integration

`release.yml` calls the reusable scale workflow with the exact source SHA after
successful trusted main CI and product-impact planning. Every product publication
uses a fresh clean checkout and **both** established commands:

```powershell
.\eng\Run-Performance.ps1 -ExpectedRevision FULL_40_CHARACTER_CANDIDATE_SHA
.\eng\Run-UiScale.ps1 -ExpectedRevision FULL_40_CHARACTER_CANDIDATE_SHA
```

Expected-revision execution refuses a different SHA, tracked changes or
non-ignored untracked files, and rechecks after assertions. Exploratory execution
without that argument reports modifications honestly; it is not candidate proof.
The publisher requires both successful, complete, same-revision suite results
from its own run/attempt, and blocks on failure, absence, required skip,
cancellation or timeout.
Earlier nightly/manual artifacts are diagnostic evidence, **never** reusable
deployment approvals. Wiring is not a successful hosted execution or owner-key
provisioning. Before merge, the registered `ci.yml` default-off
`run_release_verification=true` bridge invokes the same fresh scale workflow,
then genuine packaging/signing with ephemeral keys and production-trust
isolation, with no production publication. See [release operations](releasing.md)
for the exact safe hosted command, required results and remaining prerequisites.

Core CI also runs the non-UI `Release*.Tests.ps1` contracts: real temporary Git
history, controlled ephemeral cryptography, exact suite evidence, archive
isolation and fake external GitHub publication effects. They never call a real
publication endpoint, open WPF or run an installer.
The core runner-category signer interoperability test also invokes the real
PowerShell signer with an ephemeral key and feeds its exact envelope to the real
`UpdateChecker`, checking pinned bytes and mutation rejection without network IO.
Metadata renewal contracts additionally cover due/no-product/no-op evaluation,
original source/trust/provenance, copied bootstrap bytes, unchanged app versions
and stale/failed/concurrent fake GitHub publication. The hosted release bridge
renews its genuine fixture package with ephemeral signing and uploads named
renewal evidence, never a production release.

The trust-verifier lifetime regression compiles a minimal public-resource
assembly, runs both real trust checks, and immediately deletes the fixture tree
in the same PowerShell process. It reproduces the hosted `Assembly.LoadFile`
mapping/cleanup denial without WPF or installation. Byte-based inspection retains
all trust/controlled-effects checks without holding the package DLL mapped.
Cleanup must finish before the hosted release result can report success; there
is no retry, swallowed deletion error or connection to the parked note issue.

Both scale fixtures save the app's ordinary startup-update opt-out before launch,
so a production-trusted historical candidate cannot download a production update
while being tested. The runner's file-backed regression observes both preferences
before scenario actions and verifies update/installed-update selections remain
untouched. No product default, public key, transport effect or scenario assertion
is replaced. Automatic renewal refuses historical candidates predating this
isolation instead of modifying their exact-source test checkout.

## Isolation and remaining blockers

Use fictitious people and local images, never company exports or production
credentials. Keep fixtures/results out of product output. Dispose workflows,
watchers, streams and application processes before cleanup. Preserve version,
data, error and rejected-save semantics while relocating tests.

Test migration neither repairs nor waives the separately parked rare
note-publication failure. If observed, report the exact failure rather than
masking it with retries. Installer verification, owner signing/trust provisioning and release operating
prerequisites remain separate evidence; test reorganization is not v1 acceptance
or permission to publish.
