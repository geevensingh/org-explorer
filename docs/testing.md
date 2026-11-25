# Testing

This is the authoritative test-placement and execution strategy for developers
and agents ([approved spec #21](https://github.com/geevensingh/org-explorer/issues/21)).
Choose the lowest-cost agreed surface that can prove the behavior, not a project
name. A desktop click alone does not establish a successful navigation or save.

## Person and writing contracts

- **Profiles, identity and reporting:** read [Person profiles](testing/person-profiles.md) for whole-profile editing, Team names, photos and Copy assertions.
- **Preferred-name Save/Enter:** read [Person-text submission](testing/person-text.md) for defaults, original bases, failed retries and cancellation.
- **Person-note or Team-note edits:** read [note writing](testing/note-writing.md) for autosave, admitted/newer generations and independent failures.
- **Ongoing/Other or current/frozen notes:** read [note reading and context](testing/note-reading-context.md) for exact selection, modes and attribution.
- **Person search or browsing:** read [search and navigation](testing/person-search-navigation.md) for ranking, accepted destinations and retained writing.
- **Responsive reading or editor geometry:** read [Person reading layout](testing/person-reading-layout.md) for restoration, anchored content and evidence limits.

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

For compact backdrop contrast and exact-row browsing restoration, read [Person reading and responsive layout](testing/person-reading-layout.md#browsing-restoration).

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

The collector workflow cases cross the same public helper/compiled-CLI seam:
atomic standing-policy startup, serial/two-worker leasing, exact prepared
requests, original lookup timestamps, stopped-worker handoff, compact sampled
estimates and complete/degraded publication. Versioned-state cases retain exact
legacy snapshots, attempts and policy through an explicit resumable upgrade,
and reject future/unknown policy formats, active work and sealed exports.
All source responses, native event streams, identities and images are fictitious.

Use independent expected values at the agreed public surfaces. Keep parsing,
storage mechanics and graphs behind the shared workflow; private-method tests,
per-record public repositories and success-only app flags are not migration seams.

For bounded shared-Team-note excerpts, read [Person search and browsing](testing/person-search-navigation.md#grouped-person-search-and-retained-writing).

For note bases, held entry and temporary busy-discard allowance, read [Person-note and Team-note writing](testing/note-writing.md#immutable-drafts-and-scoped-cancellation).

For Preferred-name submission, defaults and original bases, read [Person-text and Preferred-name submission](testing/person-text.md#submission-and-immutable-bases).

For profile Save/Cancel and leave decisions, read [Person profiles, reporting and editing](testing/person-profiles.md#profile-editing-and-cancellation).

For Person categories and reporting summaries, read [Person profiles, reporting and editing](testing/person-profiles.md#categories-and-reporting).

Read-only multi-control native checks batch their requested IDs and properties
in one fresh UI Automation observation, avoiding a full-window search for each
field. Each readiness poll and post-action check captures again; observations
do not survive navigation, focus/scroll changes or updates. Text-pattern, pixel,
selection and keyboard assertions remain actual provider/desktop observations,
with the same complete scenario selection, size matrices and execution budgets.
Responsive editor checks acquire the displayed identity inside the existing
readiness poll, so a transient post-resize absence cannot bypass that wait.

For file-backed profile sessions and stale saves, read [Person profiles, reporting and editing](testing/person-profiles.md#profile-editing-and-cancellation).

For owned and inherited Team names, read [Person profiles, reporting and editing](testing/person-profiles.md#manager-owned-team-names).

Import navigation permits returning to its own failed Apply recovery view without
clearing the retained Import error or claiming safe close. Independent note,
quick-action and dirty-profile writing still block entry. A real-folder case holds
the publication lock, rejects Apply, returns to People, releases the lock, reenters
Import and explicitly previews/applies again before checking durable values.
The compiled replay also awaits the actual reenabled Open control after each
foreground operation: its view-model outcome can precede WPF work cleanup and
does not yet authorize a competing Settings/Import action.
The explicit Import-only discard clears the rejected batch and review without
clearing independent Person/Team-note errors, Preferred-name errors or profile
drafts. Real-folder cases prove unchanged saved bytes, retained writing, and
safe-close/restart becoming available only when no other writing remains.
People and exact-note browsing remain available after failed Import or recovery
maintenance without clearing either error or claiming safe close. Independent
writing and divergence retain owner drafts while Person browsing stays available;
rejected startup restoration reports recovery without publishing a partial remembered view.
Blocked Workspace replacement is distinct from an attempted open failure. The
compiled Retry action must still reconcile the current local store without
reopening or creating the blocked destination or resetting the current view.
Both profile and notice recovery actions permit actual query/Enter Person
browsing while the same real reconciliation read remains held. Their separate
browse-safe recovery mode keeps editing and Workspace replacement guarded and
never routes reconciliation through the ordinary local-save admission queue.
Both note containers also remain enabled for exact owned-note selection/Read
and inherited-context expansion; their mutation controls stay disabled while
the same reconciliation is held.

For inherited retained-owner mode preservation, read [Note reading, selection and Team context](testing/note-reading-context.md#read-and-edit-presentation).

Control only genuine external effects (clock, browser/release/installer boundary);
do not mock away the business behavior being asserted.

Local SQLite Historical reads reject omitted nested capture fields, including
frozen Team-note review status, without rewriting local or published bytes.
Direct Person/Team creation is tracked before waiting behind a real Import
Commit and reaches the unchanged three-second stall while queued. Actual note
Commit plus follow-up-read refusal keeps every workflow/view-model/retained-draft
projection unknown/not confirmed saved, distinct from confirmed noncommit.

`WorkspaceWriteObservationTests` drives the real SQLite-backed workflow with an
optional `IWorkspaceFileObserver`; actual local Commit/evidence effects and
diagnostic failure isolation stay real. Retired-format rejection has zero
observed writes. Canonical document/index write-count bounds are retired with
that engine, not reinterpreted as SQLite page-write measurements. The
[storage protocol](incremental-workspace-storage-protocol.md)
defines local recovery, independent snapshots and guarded retention.

The `local-sqlite-read-snapshot` observation uses that same observer seam.
Begin follows the successful committed identity/revision read under the actual
explicit deferred transaction: it is a known-held snapshot point, not the
earlier acquisition time. The same operation's End is emitted only after its
transaction and connection dispose successfully. Failed is conservative and
does not confirm release when read/cleanup failed. Observer callback faults are
diagnostic, never a new storage outcome. Its elapsed time includes observer
work/holds; the default CPU field is not a CPU measurement for this phase.
Real-store contracts gate the public reader there, and separately hold its End
callback while another workflow successfully commits, proving the distinction
between a live method/task and a still-held physical reader. The original causal
RED is retained in session evidence. Its Person/Team regression now requires a
later same-owner/GUID generation to commit and independently reopen while that
same actual public reader remains held, without retry; the old reader still
returns its coherent prefix. Separate real WAL write refusal preserves known
noncommit/recovery coverage. Additional cases prove safe mode negotiation and
complete logical publication/peer Join of notes, profiles, photos and history
while an older public reader prevents checkpointing. Owned abrupt-death cases
observe a live committed WAL before process termination and then reconcile it.
If the existing immediate public reopen fails, a bounded owning-thread
first-chance observation retains SQLite code/extended code/HResult and the first
managed frames in memory, then detaches reliably. Only after that failed Open
returns does it emit those facts and non-atomic database/WAL/SHM existence,
size and read/share probes. These later facts are not original I/O timestamps
or contemporaneous lock-holder proof. Diagnostic faults remain explicit without
replacing the original failing Open assertion. There is no retry, delay, repair,
checkpoint, write-capable probe or live-file deletion; a successful Open performs
no diagnostic logging or metadata probes. Local nonreproduction is not evidence
that a hosted Error 10 is safe, unrelated or attributable to its host.
None of these is hosted-actor attribution, a natural-duration benchmark or
native acceptance.

The #99 close cases use that same real-storage workflow/observable view-model
seam: both note scopes commit/reopen current writing during an older held
publication, and close readiness requires the latest actual published revision.
Cancel preserves unsubmitted profile/Preferred-name drafts, selection, exact
stalled owner writing and the original local fault without waiting for the
admitted transaction/export. Missing folders and failed exports remain distinct
from local loss; a fresh close retries publication without mutation replay.
The existing exit-publication read observer also holds that actual provider read
while the same public Workspace saves and independently reopens newer note
writing. The old publication cannot acknowledge the new head. The blocked-folder
cases count calls at the public workflow proxy and require no repeated expensive
exit verification while publication is already known to be unavailable.
The invisible compiled `--verify-exit` check (also in the existing required
`--verify-profile-refresh` command) drives the real BAML close/Cancel controls,
repeated/canceled intents, newer writing and late exports. Nine owned non-UI
child processes invoke the compiled Exit anyway action with unconfigured/failed/
hung publication, pending/stalled precommit writes, postcommit lost acknowledgment,
failed local saving, an active explicit profile Save and real divergence.
The existing pending-save case also holds a real same-process public SQLite
reader at its known-held snapshot point; actual Exit anyway must terminate both
it and the admitted writer without waiting for either disposal.
It requires actual process death without returning through secondary Closed/
disposal guards, then independently reopens and reconciles committed versus
incomplete data, retained conflict versions and unpublished work. This never
shows a window, creates a native handle or substitutes a success flag for exit.
The existing ephemeral signed/effects `--verify-update-yield` command also requires
an owned process to terminate through Exit anyway while the real signed update
command waits at its safe point behind an admitted, held SQLite transaction.
Its ordinary safe-drain and unsubmitted-Preferred-name refusal proofs remain.

The existing hosted `compatibility-settings` journey adds the native counterpart
using those actual compiled WPF processes and real SQLite/snapshot artifacts.
It observes visible enabled exit choices, exact Cancel/owner writing and Person
browsing, then actual Exit anyway process death for the complete matrix.
A separate native signed-update case requires the same forced termination
without changing the updater's preexisting safe-point/edit-disable contract.
A separate held-export journey cancels normal close, enters newer native note
writing, independently reopens it before releasing the old export, requires the
canceled process to remain open/unpublished, then requests normal close once and
requires latest publication plus normal process death. Its candidate/run/attempt/
product-hash-bound `*-sqlite-exit-lifecycle.json` and named `sqlite-exit-*` captures
are hosted-only evidence. The normal creation/reopen fixture explicitly
chooses the saved-but-unpublished exit risk before folder configuration; it still
requires all local writing to finish first. Existing rejected-close journeys now
Cancel the decision before their original retry, preserving drafts/bytes and
profile navigation Save/Discard/Stay without leaving a latent close intent.
No scenario, sample, previous assertion scope, lane budget or latency target is
removed or extended. Local compiled/process success is not hosted UI acceptance.
The existing invisible Configure/Sync contract also queues both Configure and
Join at the public workflow boundary, replaces the Workspace through actual
compiled Open controls, and requires rejection without configuring the new
Workspace or creating the captured folder. The Manager-choice and independent
note-leave contracts pump through actual `Closed`; its normal handler owns model
disposal, so a canceled close cannot be reported as completed cleanup.
The compiled close contract also holds accepted folder configuration at that
queue boundary. Cached publication cannot close the window before the operation
finishes and its resulting folder is published; Exit anyway still bypasses waits.
Waiting for window-level update/configuration/version work does not repeatedly
read or hash its already published snapshot.

Fixtures expecting ordinary process exit first establish its real publication
precondition through the existing Settings Configure/Sync controls and an
independently reopened exact local/published revision. Opening Settings for this
preparation must preserve an underlying Import view and its selected owner.
Publication/close preparation preserves an already configured transport by
default; the smoke sibling folder is only for an unconfigured Workspace.
Strict normal-close preparation additionally rejects a changed configured
folder, not just a changed head. Its two-store compiled close/reopen replay
performs both normal closes on the original incoming folder and observes a
delivered continuation entering/completing the actual adoption Commit phase.
The rich normal-note phase publishes to its existing isolated transport after
the unchanged latency samples and before its original 10-second close check;
the publication stage still counts toward the whole suite budget. A pending
publication is not a missing-folder error, and neither permits substituting
Exit anyway or bypassing the normal close guard. The compiled
Rich normal-close preparation avoids a redundant Sync now only when a fresh
independent Open confirms the exact current revision is published in the intended
folder, no unpublished changes remain, and safe close is available. Otherwise
the original synchronization and 20-second observation remain. The existing
before/after identity, revision, publication and configured-folder checks are
unchanged; general incoming synchronization is never skipped by this helper.
The compiled close-preparation contract covers both already-published nonmutation
and a genuinely newer unpublished revision that still synchronizes exactly once. The compiled
`--verify-normal-close-publication-preparation` regression drives
actual Configure/Sync, a genuinely
newer Import and normal Closed with an independent complete-snapshot receiver.
It remains in the full compiled selection. The complete later rich background
export and profile phases and all original hosted exit cases remain required.
After verifying and capturing the latest rich publication in Settings, the
driver returns to browsing before the unchanged note-idle/normal-close checks.
Those checks require the actual browsing note surfaces, not a Settings-only
automation subtree. Neither local-idle criteria nor normal exit is bypassed.
The owner-approved rich export fixture hold is bounded by the remaining original
660-second whole-suite deadline, not a separate 180-second publisher timeout.
The runner passes that exact deadline to owned commands through process-only
`SCALE_DEADLINE_AT` and immediately restores its caller's environment. The rich
host rejects a missing/invalid deadline and an expired hold; no new full-duration
clock starts at publication. Ordinary automatic holds remain 30 seconds, and
all workloads, samples, observation limits and the whole-suite ceiling remain
unchanged. Every held assertion still requires the same unreleased publisher.
The invisible rich-host restoration contract exercises that actual deadline
consumer: released within-budget publication succeeds, an expired deadline
still rejects release, and missing/malformed deadline input fails explicitly.
Its ordinary automatic publisher remains independent of the rich deadline.
On build-tree cleanup refusal, `cleanup-failure.json` retains the exact owned
failed path, original error and a bounded read-only Windows owner query through
the existing file-lease helper. The helper is compiled alone, not loaded from
the test binaries being removed. Missing owners and query failures stay unknown;
there is no deletion retry, owner shutdown or passing cleanup fallback.
Observation faults cannot replace the original lane failure. A real held-file
runner contract verifies this evidence and unchanged failing verdict.
Only the normal-close preparation wrapper requires the pre-operation head to
remain unchanged. The shared refresh/publication helper permits legitimate
incoming adoption and independently checks the post-operation current and
published revisions instead. `--verify-incoming-smoke-publication` drives that
same helper through actual compiled Sync controls and two real stores, verifies
new incoming profile/note data and matching current/published revisions, and
separately requires close-only preparation to reject an unexpectedly advanced
head without losing the adopted version. It is also in the full compiled selection.
The normal-close preparation replay observes the same enabled Sync now and
locally-saved-and-published text predicate as native publication preparation,
not only the underlying model's completion.
Real-store synchronization coverage requires three full semantic validations
when two retained incoming versions precede a genuinely newer outgoing version,
not repeated reconstruction of the same content during retention.
Retention still rereads and integrity-checks
actual artifacts; changed bytes must receive fresh semantic validation, even
during that same publication. No validation result survives into a later pass.
The rich no-window diagnosis reproduced the original 20-second readiness
predicate failure with two retained snapshots; reusing exact-content validation
within that pass removes repeated SQLite reconstruction without changing the
native observation, workload, close guard or scale budgets. This is not complete
hosted rich-scale acceptance or attribution of the historical runner's exact phase.

For exact secondary selection and held-export Edit preparation, read [Note reading, selection and Team context](testing/note-reading-context.md#read-and-edit-presentation).

The exit host's one preparation Open waits for the actual compiled Ready activity
and enabled Open control inside its original bounded preparation observation.
Product Loaded performs asynchronous startup/update recovery even with startup
update checks opted out; a second Loaded handler cannot assume that work has
finished. `--verify-exit-host-startup`, also included in the existing compiled
profile/exit checks, replays both real Loaded handlers without Show or a native
handle. It requires observed pending startup, exactly one subsequent Ready Open,
the real populated/selected Workspace and actual owned process death/reopen.
It does not establish hosted startup/exit acceptance.
Native exit cases retain bounded `*-sqlite-exit-<case>-readiness.json` sidecars
even when window discovery or fixture readiness fails. They record the case,
parent/child stages, actual process exit/code, available child stderr/error and
observation-read failures; unknown live-host outcomes remain explicit. Child
stages are capped at 24 and error text at 8192 characters with paths redacted;
these observations contain no editor bodies or backing-store paths. Diagnostic
write/report failures cannot replace the original case failure.

`SqliteWorkspaceTests` observes the normal shipping path through the same real-storage
`IApplicationWorkflow` / `WorkspaceViewModel` seams. It covers ordinary Create/Open,
reviewed People-only Import, all supplied attributes, exact accepted evidence and
replay, startup reopening, stale review, actual file-access failure, unavailable
or corrupt local storage, explicit unsupported actions and preservation of an
existing retired Workspace. It does not inspect private tables as a business seam or substitute an
in-memory success. The existing `compatibility-settings` hosted journey also
creates the app-managed local format without an opt-in, previews/applies, browses rendered Person facts and reporting
navigation, closes and reopens the same population, and checks the old Workspace
bytes. All prior scenario steps remain.
Compiling this journey or passing filesystem cases does not establish hosted
WPF interaction evidence or any 10,000-Person responsiveness claim.
The existing invisible `--verify-profile-refresh` contract also drives the
compiled ordinary Open Workspace and SQLite Create actions, requiring both to
return from Settings to the browsing/start view. It then invokes normal Settings
Import and previews real initial People without accepting them before Apply.
It never sets presentation state to bypass navigation or shows a native window.
This guards the hosted helper's required starting view; the unchanged hosted
journey still establishes actual create/import/browse/close/reopen interaction.

For format-qualified note authority, first-edit cadence and retained generations, read [Person-note and Team-note writing](testing/note-writing.md#immutable-drafts-and-scoped-cancellation).

For durable note generations, held navigation and native latency, read [Person-note and Team-note writing](testing/note-writing.md#durable-typing-and-save-handoffs).

For SQLite profile saves and held-navigation evidence, read [Person profiles, reporting and editing](testing/person-profiles.md#durable-saves-and-held-navigation).

`WorkspaceSnapshotTests` covers #96 through two independent real SQLite
workflow/view-model stores and controlled copying of actual `.oesnapshot`
artifacts. It observes complete logical publication/fresh identity join, selected
Person/query preservation, private dormant Team labels and derived owner paths,
current/retained decoded photos, accepted Import replay and complete frozen
capture provenance/note versions/review. Hash-valid missing-resource delivery,
truncation, incompatible envelopes, bad schema/ancestry/identity and missing
accepted evidence retain the last valid local state with explicit errors.
Actual local writer locks and filesystem staging observations admit Preferred-name,
profile and note drafts during validation; adoption must refuse them without
losing writing. An actual publication-boundary hold allows another local commit
and proves that publication of the older snapshot cannot acknowledge it.
Reviewed partial/photo Imports use that same ancestry/commit proof, including
accepted later replay after current photo clearing and full two-store frozen
history/resources. An Import accepted during held export must remain separately
unpublished until its own complete revision is exported.
Both note scopes also hold the actual successful SaveNoteDraft return, retain a
newer generation, independently publish that committed revision and deliver its
real child. The non-base admission lease must prevent adoption through that VM
handoff even when core saving is complete. After clean admission/seal and actual
transaction writes, the existing observer waits at `snapshot.adopt-commit`
**before** invoking SQLite Commit; it is not an engine blocked by a reader.
Explicit release permits that same operation's real Commit, and the fixture's
failure/expiry state cannot stand in for a live phase. Browsing and later profile draft
admission must return while the same commit is still held, and installation
must preserve that later draft. Both note scopes additionally retain their exact
owner/writing/immutable base after that seal, then commit/reopen and publish a
valid child which the first store must accept. A failed/timed-out commit cannot
satisfy this structural nonblocking proof.
The joint #95 cases create a real committed-but-lost note return in both scopes,
with and without newer generations. The observer gates the actual public reader
at `local-sqlite-read-snapshot`, after its committed identity/revision read under
the explicit deferred transaction, while reconciliation reaches its real core
draft acknowledgment. This is a known-held snapshot, not an outer phase or
`BEGIN EXCLUSIVE` assumed to exclude WAL readers. Browsing must return before
that same identified read is released and disposed; a read
failure or merely pending task is not sufficient. Committed-ack removal must
release adoption admission, whereas newer writing must survive, retry/reopen and
refuse a truly divergent child. Explicit buffer discard cannot erase an unknown
outcome or authorize adoption. A published Person save held at its real return
uses an unadvanced injected request clock to isolate the pre-timeout lifetime,
not manufacture a timeout or claim latency. A real rejected SQLite adoption
Commit must retain the old root and enter actual local recovery before safe retry.
Recovery also invalidates only an already-ended save worker's epoch before
clearing its paused state. The queued-request regression holds that exact worker's
public completion notification (not a timer callback), then requires the explicit
recovered retry to enter while the old notification is still held. A stale worker
cannot veto the new worker or silently resume queued writing after reconciliation.
The first-edit cadence, actual active transaction and oldest-request clocks are
unchanged.
Interruption before/after real immutable publication reopens and retries without
replaying edits; duplicates/reordering cannot roll back; divergence preserves
complete versions and pauses editing across restart. No private-table assertions
or fake hosted provider are used.

The existing invisible `--verify-profile-refresh` selection also drives compiled
Settings folder/Join/Sync now against those actual artifacts and observes a clean
incoming continuation while preserving the selected Person. It never shows a
window or creates its native handle. The existing hosted `compatibility-settings`
tail adds native folder configuration, truthful publication/provider status,
fresh second-local-store joining and visible incoming adoption, without a new
scenario or a deadline/assertion change. `*-sqlite-snapshots.json` binds source
revision/run/attempt/product hash, process, independent stores, Workspace/version
IDs, actual artifact hashes and exact photo/Team/history evidence. Captures are
`sqlite-sync-folder-published`, `sqlite-sync-joined-person-history`,
`sqlite-sync-normal-incoming-continuation`, `sqlite-sync-held-adoption-draft` and
`sqlite-sync-incoming-continuation`, with the shared actual-pixel metadata.
Normal-EXE fresh Join and an unpaused incoming continuation remain native checks.
Only the forced held-adoption segment relaunches at an independently verified
clean setup seam into a controlled actual-WPF host, using the identified production
Desktop DLL/MainWindow and normal FileBacked/WAL/FULL store. It uses no production
startup flag or replacement save path. Saved Workspace/revision/Person/exact-note
preferences, empty query and actual size must survive; no intent is implicitly
saved, discarded or recreated to make the seam clean. The normal EXE resumes
before the original conflict/restoration tail.
The existing observer gates the real admitted `snapshot.adopt-commit` operation
after its transaction/seal, **before** invoking SQLite Commit. It is not an
engine waiting behind an external reader. The same unique operation and live
phase are required through native post-seal input, selection and capture, then
explicit release and actual Commit completion. The exact owner
draft must survive success before its explicit Cancel exposes the new committed
reading. `heldAdoption` records the real phase/action timestamps and boundaries;
no busy-time extension or new latency claim is used.
The native driver prepares only actual control/pattern references before starting
Sync now. It does not cache ready values: post-request adoption phase and disabled
Sync now are observed first. Before the one input, fresh exact Avery identity and
heading must both be visible with finite positive bounds; JobTitle must be enabled,
onscreen, finite positive size and expose a supported writable ValuePattern with
actual `IsReadOnly=false`. Fresh post-input owner/heading/editor/value observations
must agree before the one selection. The destination then requires fresh exact
Morgan identity/heading with visible positive bounds, not visibility of Avery's
now-hidden editor. These are bounded element-cache observations. A fixed-size
same-host named mapping transfers only primitive phase/identity/clock fields.
Two bounded slots keep the last complete record readable while its successor
is assembled; publication changes the committed sequence only after the full
payload. Missing/faulted/incomplete state cannot pass.
`--verify-snapshot-adoption` is a narrow invisible compiled replay of this driver;
its fixed 1.2-second preparation delay precedes admission. The controlled hold
retains the original one-second observation horizon, explicitly as a fixture
maximum rather than an engine busy-time guarantee. Delaying the actual action
by 1.2 seconds must reject the expired/ended phase even when the real WAL adoption
has successfully completed; neither late success nor failure proves a held phase.
`*-sqlite-held-adoption-observation.json` is written on success or failure, even
when the later main snapshot marker is absent. It binds candidate/run/attempt,
separate controlled-host EXE/PID and production DLL hashes, raw Stopwatch
timestamps/frequency, bounded preparation/request/observation/capture/release
milestones, unique actual operation/lifetime, attempted input/selection counts
and non-atomic window feedback before host cleanup. The controlled host is never
labelled normal EXE. Preparation time is not subtracted from any acceptance
clock. An observation `Passed` proves only this held interval; the existing later
commit, immutable draft/base, data/revision, original-Workspace restoration, close
and complete scenario results remain independently required.
`--verify-rich-host-restoration` independently replays **both** actual factories:
the rich automatic factory with its injected clock, and the held-adoption factory.
Each raises actual Loaded, waits for current/Ready/enabled Open, restores exact
remembered Read note/Person and reaches normal Closed without Show/Hwnd.
Advancing the rich factory's real supplied 60-second clock must reach an actual
periodic snapshot-local-read, so the two-argument held factory cannot satisfy
that contract accidentally. The held factory additionally executes its Ready/
remembered-view/normal-close path in an owned no-window child. This is startup/
IPC evidence, not the hosted native segment.
`nativeUsability` retains accepted before-input, after-input and destination
readbacks plus the last actual person/editor frames when a predicate rejects.
Unavailable/nonfinite bounds remain invalid (named floating values are serialized
as diagnostic strings rather than normalized into usable rectangles).
`SnapshotNativeUsabilityTests` rejects recorded enabled/correct-text observations
which are offscreen, zero-sized, read-only or lack a ValuePattern, and incorrect/
hidden/zero-sized identities or headings. These helper cases are not native UI
evidence. The actual artifact serializer is also exercised with NaN, Infinity and
negative Infinity frames, retaining those invalid named values and the original
failure type/message in valid JSON. The invisible compiled replay keeps its real control/draft/base checks
without manufacturing native IsOffscreen, focus or provider geometry.
These are not periodic synchronization, conflict-resolution/retention, redesigned
exit, native latency or 10,000-Person background-export proof.

The #98 cases extend the same public real-storage/view-model seam with a
controlled `TimeProvider`: fixed 60-second checks during repeated edits, shared
manual/periodic export, both owner-note scopes committing/reopening within the
existing bounded publication window while actual immutable publication is held,
older-export/latest-revision truth, corrupt delivery, unavailable folders, lost
synchronization returns and reopening pending publication without replay.
Periodic delivery also refuses unacknowledged newer note generations and actual
unknown outcomes after buffer discard. The held accepted-return case advances
only the scheduling clock while its request timestamp stays unadvanced,
preserving its pre-timeout lifetime.
An already-admitted timer/manual cycle also encounters an actual later lost
committed note return: refused recovery, independent profile/field Cancel and a
held unrelated successful return cannot release the unknown-outcome lease, even
after the note buffer is discarded. Actual successful reconciliation then permits
the delivered child; neither path replays the note.
The periodic/manual coalescing case uses the existing ten-second publication
hold/observation bound, not an additional one-second deadline for each Flush.
It independently reopens the exact latest owner writing before release and
requires the original publisher callback to remain active. Its elapsed local
commit observations are diagnostic; the ordinary and rich background-export
native percentile targets remain unchanged.
Busy-periodic regressions hold the real snapshot publisher in both note scopes,
commit newer owner writing independently, and require the original manual
request to finish after one actual synchronization pass. Its older publication
cannot acknowledge the newer head. The original next 60-second tick still
publishes and independently delivers that writing without an inactivity reset.
Separate explicit-during-active cases retain the needed second pass even when
a later busy periodic tick arrives; real reopen/Join verifies the exact newer
owner/note data. Only the timer callback coalesces busy ticks: explicit requests
keep their existing follow-up flag and no concurrent worker is introduced.
The full-rich compiled 20-second publication predicate reproduces the duplicate
pass failure path when an ordinary periodic tick arrives during manual
publication. Eliminating that demonstrated path is not attribution of the
historical native E9 Publishing-text timeout or complete hosted acceptance.
The combined #97/#98 case admits an exact-version choice while clean, then waits
at the real isolated candidate's `snapshot.validate-commit` observer phase after
its writes and **before** SQLite Commit invocation. Both note scopes
commit locally and lose their real return while that same validation remains
held; explicit buffer discard cannot authorize installation or replace the newly
committed child. Reconciliation and a fresh review allow deliberate selection;
the complete local child, note and photo remain recoverable afterwards.
This is a combined stale-authorization/Unknown refusal proof, not an isolated
causal proof of the fault lease: the later commit independently invalidates the
shown review. The admitted timer/manual cases above isolate that lease.
`snapshot.validate-write` controls preparation and `snapshot.validate-commit`
holds/releases that real candidate transaction; neither substitutes a pending
task or a WAL reader lock for the phase. The original owner/generation/error/
revision and cleanup assertions remain. Their bounded fixture hold is not a
claim about an engine busy-wait duration, and no transaction timeout changes.
The invisible compiled snapshot selection additionally observes automatic
publication/status, writable exact owner notes and Person switches during a real
export, later publication and selected-Person incoming refresh. It never shows a
window or creates its native handle.
It prepares a real selectable whole-version review before held automatic export
and requires Sync now and all Review/Continue/Delete readiness to be disabled
during that export. Automatic proof precedes the retained deliberate-choice proof;
neither proof or native tail substitutes for the other.

The existing `compatibility-settings` journey adds an owned hosted process running
the actual compiled MainWindow with real SQLite and `.oesnapshot` artifacts.
Only its public clock and external publication boundary are controlled. No Sync
now action triggers its periodic cycles. Native controls must show actual export
status and remain writable/navigable while that same export is held; independent
reopen proves newer owner writing before release. Older completion must leave it
unpublished until a later tick. Actual delivered continuations update visible
reading without changing the selected owner, and an unavailable folder reports
an error while native writing still commits and later publishes.
The incoming observation waits for the same exact note GUID and writing as
well as the updated Person heading inside its original 20-second observation;
an updated heading alone does not establish that the editor provider is ready.
The invisible continuation also follows the actual Settings round trip and
requires the current attached, enabled editor with the unchanged owner/title/
writing. It does not treat a cached headless Read peer subtree as native proof.
The native automatic tail uses the shared snapshot usability predicate, extended
with complete clipping to the actual scroll viewport and window. Before its one
held-export SetValue, fresh Morgan identity/heading, original Team-note ID/title,
finite-positive visible editor, supported ValuePattern and exactly-false
IsReadOnly must agree. The pattern used for that one input comes from the same
updated-cache element as its accepted frame. Immediate fresh post-input owner,
note GUID/title metadata with valid finite geometry, fully viewport-contained
enabled/nonoffscreen supported explicitly nonreadonly editor and exact value
must agree too. `NoteWritingObserved` permits only the note title to move outside
the scrolling viewport after input; it does not relax owner/editor/lifetime
checks or turn metadata into rendered-reading evidence. Strict `NoteReady`
still requires the full title and body before input and for returned captures.
Avery and Morgan destinations require fresh usable identities/headings; returning
to Morgan intentionally
reveals the same note before fresh value/usability checks and capture.
References and initial reveal setup precede the hold where possible. Each reveal
uses ordinary actual-geometry navigation, at most one centering scroll, followed
by read-only observation; failed readiness never retries input or selection.
No pre-request geometry/value/readiness is reused. Publishing feedback and the
disabled Sync now button must both fit the visible Settings viewport when
observed and captured. Returned note heading/editor must both fit the profile
viewport. Captures bracket fresh accepted frames, not offscreen property reads.

The recorded post-input frame from native run `43940045839/1` is replayed through
both predicates: exact owner/note/new writing and the complete writable body fit
the viewport, while only the title is above it. It must pass AfterInput and fail
strict capture-context readiness. The invisible compiled provider replay uses
actual BAML, SQLite, `IValueProvider.SetValue` and a deliberately planned public
`BringIntoView` during pending feedback. At the matched 416-DIP viewport, feedback
temporarily leaves 328.38 DIPs for a 381.7-DIP title/body group; ordinary editor
visibility settlement moves content about 70.47 DIPs and the offset persists
after durable save. This quantitatively reproduces the captured geometry without
changing product layout or manufacturing native focus/visibility. It does not
identify the unlogged historical native focus/caret event. The correction is
phase-specific harness acceptance, not a storage fix or a new latency target;
native post-input observation never scrolls/focuses to repair failed readiness.

The actual held publisher callback signals entry and signals completion in its
own finally, including timeout/failure. Completion is never reset, so a later
invocation cannot impersonate the first. Input, post-input, destinations, returned
note and both held captures require that same invocation to remain in flight and
unreleased. The existing 30-second hold, 20-second observation and 900-second lane
budgets are unchanged; no UI time is subtracted. One held input, two selections,
independent durable reopen, old-versus-new publication, outage/local-writing/
retry and actual close checks remain required.
`*-sqlite-automatic-snapshots.json` schema 2 binds candidate/run/attempt, host/product hashes,
process, independent stores, exact revisions/owner/note/writing, raw monotonic
request/continued-editing timestamps, actual artifact hashes and named actual-pixel
captures. It also names `*-sqlite-automatic-native-observation.json` (schema 1),
written on success or failure with accepted/last-rejected native frames, last
actual reveal geometry, bounded action/capture/lifetime observations and the
original failure. Both use the shared diagnostic serializer, preserving NaN and
infinities as named invalid values rather than masking failure or normalizing
geometry into usability. Focused shared-predicate cases reject hidden/clipped/
zero/nonfinite, readonly/unsupported, wrong owner/note/value and ended-operation
observations; they do not manufacture native flags in invisible checks.
The main JSON is still pre-close; the sidecar precedes final process cleanup.
Normal close/process exit, terminal hosted PASS, successful phase completion and
the complete scenario/lane results remain required.
The automatic host owns a scoped output/cleanup lifecycle rather than the shared
desktop StopApplication path. Sidecar output, held-work release and abnormal-exit
diagnostics are attempted independently; diagnostic I/O cannot prevent the owned
PID's termination/wait attempt. Secondary reporting follows termination, with
explicit fallback reporting and structured stage/error attribution on the final
exception. An original body failure is rethrown with its original identity/stack;
without one, any output/cleanup failure still fails the scenario. The automatic
terminal PASS is emitted only after that lifecycle returns successfully.
`AutomaticHostLifecycleTests` uses the existing real non-UI deferred-process
fixture and actual missing-directory writes: successful normal exit plus output
failure cannot pass the scenario; primary plus sidecar/cleanup/reporting failures
must preserve attribution and confirm actual owned-child termination before test
safety cleanup. These are runner/process checks, not visible WPF reproduction.
This controlled owned-host evidence is not normal-EXE latency, provider delivery
or the rich 10,000-Person/background-export proof retained by #100. Both desktop
900-second budgets, all previous assertions/size matrices and all p95 targets
remain unchanged; compiling the tail is not hosted acceptance.

The #97 cases extend the same two-store public workflow/view-model seam with
deliberate complete-version selection, exact review/new-arrival refusal,
publication of a new resolution revision and later recovery of either retained
version. Recovery after ordinary rotation preserves independently decoded photos,
accepted Import replay, complete frozen note versions/provenance and Profile
changes. Both note scopes hold a real successful save return, with newer writing
and optionally a lost acknowledgment: choosing a whole Workspace must not bypass
that VM-owned admission lifetime or unknown outcome. This structural handoff
uses the existing unadvanced public request clock, not a new wall-clock save SLA.
The first normal save return is observed asynchronously within the unchanged
five-second entry bound, including a refused/null return rather than signalling
only success. Failure context records monotonic public call/return observations
and actual workflow/view-model status. Newer writing must be observable immediately
with its same immutable base before synchronization/choice I/O; the exact writing,
owner and accepted-or-unknown base remain asserted after the released handoff.
Separate both-scope real-store cases advance that clock to exactly three seconds
at the same held successful return and require truthful paused-input refusal,
not admission or silent loss of writing. The ten-second held-return bound,
production stall policy and ordinary latency gates are unchanged. A real incoming-file lock
after the actual selection Commit proves failed arrival validation leaves the
committed state browseable but read-only across reopen. Routine current/previous
rotation preserves unknown files and provider-named snapshot copies; explicit
fresh recovery deletion cannot erase the current version or unresolved conflicts.

The existing invisible compiled snapshot selection additionally drives the actual
Settings review/list/Continue controls with no default choice, requiring edit
pause, usable Person selection and retained unchosen data without showing or
creating a native window. The existing hosted `compatibility-settings` journey
keeps every prior assertion/capture and appends native conflict visibility,
disabled edit actions, continued Person browsing and deliberate exact-version
selection, followed by independent full-resource/history reopening.
`*-sqlite-workspace-conflicts.json` binds candidate/run/attempt/product hash,
process, independent stores, presented local/incoming versions, resulting revision
and retained recovery IDs to `sqlite-conflict-visible`,
`sqlite-conflict-browsing-paused`, `sqlite-conflict-deliberate-choice` and
`sqlite-conflict-selected-complete` actual-pixel captures. Compiling or invisibly
replaying that tail is not hosted interaction acceptance, redesigned exit or
rich 10,000-Person/background-export latency evidence. All existing lane budgets,
latency targets, scenario manifests and size/assertion matrices remain unchanged.

The #94 reviewed-Import path uses that same SQLite commit boundary for partial
multi-Person updates, photos, accepted evidence, Profile changes, reporting
transitions, Team-note review and frozen context. `SqliteWorkspaceTests` compares
complete public reading state across original/later replay and reopen, checks
older observations/confirmations, stale reviews and changed source resources,
and proves rollback after a real SQLite trigger refuses the final revision write.
The trigger controls only storage refusal; retained data is asserted through the
public workflow, never by inspecting private tables. Reopening the failed batch
must still offer a non-no-op review before retry. Private Team-name set/null/operation
rejections, duplicate owner paths, unnamed gaps, dormant archival/restoration and
absence from frozen profiles/evidence remain covered. Both note scopes retain
newer owner writing and an unsubmitted profile during a real held bulk Apply;
changed profile bases still reject explicit Save rather than silently merging.

The invisible `--verify-profile-refresh` contract additionally exercises compiled
Back to People, search, Person switches and writable retained Team-note controls
while the same actual SQLite writer holds Apply, then independently reopens the
coherent update/photo/history and exact owner writing. The `compatibility-settings`
hosted tail adds representative reviewed update/photo Apply, held-transaction
native browsing/writing, original/later no-op replay and separate-process reopen.
Its `*-sqlite-reviewed-import.json` schema 2 records candidate/run/attempt/product hash,
distinct editing/reopened process IDs, exact batch/capture/note/version/resource
identities and complete reopened-state digest. It retains raw native Back/Person
navigation samples as diagnostics and a separately labelled bulk duration
with no ordinary-edit one-second budget. Named `sqlite-reviewed-import-preview`,
`sqlite-reviewed-import-retained-owner`, `sqlite-reviewed-import-history` and
`sqlite-reviewed-import-reopened` captures carry actual desktop pixels/metadata.
Compiling this tail or running invisible controls is not hosted interaction
acceptance. All existing note workloads, native sample counts/targets, required
scenario manifests and deadlines remain unchanged; combined rich 10,000-Person
and background-publication proof remains #100.

The held-Import JSON also retains bounded `heldApplyBrowsing.decomposition`:
three samples (Back, Avery Select, Morgan Select), request start/return offsets,
the original unadjusted total, poll count, and started/completed/total/max time
for each fresh email/heading query. Queries retain the original short-circuit
predicate and run only after the actual native request; no pre-request cache
warming or extra Select is introduced. Retained-writing SetValue start/return
offsets use the bulk Apply clock; each sample uses its original Back/Select clock.
Exception evidence contains type/HResult, never writing or error messages.
This decomposition is emitted with the existing final JSON, not before a failed
earlier lifecycle step. Native request time includes provider/dispatcher work,
and a fresh query can wait for product work; neither duration alone establishes
pure observer overhead or a product root cause. No component is subtracted.
The existing invisible compiled selection additionally records handler/layout
durations and requires the same Apply/writer held before and after each existing
Person switch. Those are structural no-save-wait diagnostics, not native proof.
The owner approved aligning this extra held-bulk check with the agreed contract:
both selections must reach the exact usable Person while the same actual Apply
transaction remains held and unpublished, without losing retained owner writing.
The two unadjusted times remain diagnostic, not a per-selection deadline or a p95
estimate. Schema 2 marks `heldApplyBrowsing.timingUse` as `diagnostic` and removes
the old per-case `budgetMs` field. The original 42 native navigation samples are
informational; ordinary durability, continued typing and functional checks remain.

For native note latency observation and invisible query-work evidence, read [Person-note and Team-note writing](testing/note-writing.md#durable-typing-and-save-handoffs).

For bounded live Team-name discovery, read [Person profiles, reporting and editing](testing/person-profiles.md#manager-owned-team-names).

For continued-typing workload, immutable observation time and policy history, read [Person-note and Team-note writing](testing/note-writing.md#durable-typing-and-save-handoffs).

The #95 cases extend the same public real-storage workflow/view-model seam with
controlled request clocks and held/lost return boundaries. They prove the oldest
queued three-second deadline, never-returning admission, explicit edit pause,
continued arbitrary Person browsing, exact independent drafts/Team-name profiles,
late accepted generations and real committed-state reconciliation before retry.
Lost acknowledgment cannot create another note. `OrgExplorer.Storage.ProcessFixture`
is an owned, non-UI child killed at the existing storage observer's actual
SQLite pre/post-Commit boundaries for both note scopes and complete profiles.
Reopening uses the normal validating reader; neither Dispose nor private-table
inspection supplies crash evidence. The child identifies its actual PID, observed
commit boundary and whitelisted request/base/next-revision metadata before signaling
admission; the parent verifies it is terminating that exact process. Unexpected
recovery and remaining-evidence diagnostics include the exited launcher, validated
revision/error and read-only Windows Restart Manager owner snapshots for only those
owned receipt paths. These non-atomic snapshots never shut down/restart an owner,
force-delete evidence, inventory unrelated processes or log note contents. Missing
owners at observation do not establish that no earlier holder existed.
Five focused `SqliteShmRecoveryTests` cases retain the original owned child and
public validating Open. The test host explicitly maps that child's existing
physical SHM file at its current length, without taking a SQLite DMS lock or
resizing it. After actual Commit Begin/End admission and death within the
original five-second deadline, one public ExistingOnly Open must recover the
exact committed/noncommitted revision and owner writing while the same section
and view remain held. Profile and both note scopes retain complete history,
frozen Team context and photo bytes. The stale-index case captures opaque SHM
bytes under an actual older public read snapshot, releases that reader, kills
the writer, and only then restores those bytes into the original held section.
Hashes require original database/WAL bytes to remain unchanged; no live
database/WAL copy, deletion or backup protocol is introduced.
The original nine death cases and healthy public WAL-reader controls remain.
The causal 3.53.4 RED is `SQLITE_IOERR_TRUNCATE` (10/1546) in the first public
Open; supported native `SQLite` 3.54.1 must turn it GREEN without an Open retry
or releasing the mapping before assertions. Actual package signature, resolved
locks, native DLL hash/version/source ID and compile options are acquisition
evidence, not substitutes for that behavior. These controlled cases neither
attribute the historical hosted holder nor waive fresh candidate-bound hosted
crash/functional acceptance; partial-first-frame and read-only-SHM race fixes
outside this release are not claimed.
Crash acceptance distinguishes committed-state durability from current receipt
availability (owner-approved reconciliation on 2026-11-16). Every pre/post-Commit
case must still expose the exact admitted base/next revision, previously committed
writing, correct interrupted mutation and unchanged history cardinality. A real
receipt-acquire sharing violation (`0x80070020`, no acquired handle) may instead
retain `Indeterminate`/`Unknown`, its evidence, read-only Person browsing and an
unsafe-close warning; it must not resume edits or claim recovery from the revision
alone. Other unexplained recovery failures do not satisfy this exception.
Two controlled pre/post-Commit profile cases admit an actual read-only receipt
reader while the identified writer is still paused at its commit boundary, then
keep that reader open after killing and waiting for the writer. The reader shares
read/write/delete access rather than unnecessarily excluding an existing
write-capable handle; it never receives write access itself. Production recovery's
read/write-exclusive receipt lease still cannot coexist with that open reader. Reconciliation
must remain refused while that holder exists. Only after deliberately releasing
it and proving one actual exclusive acquisition succeeds does one explicit
reconcile restore editing with the same revision/profile/history, without replay.
The ordinary cold cases do not poll, sleep, retry or force availability. This is
an acceptance reconciliation, not identification or a fix of the historical hosted
holder; sharing and retirement were unchanged at that historical checkpoint.
Receipt failures also retain their actual reconciliation stage (acquire, read,
validate, release or retirement), native HRESULT, originating method and observed
managed-handle lifetime in the existing error diagnostic. They do not change
lease sharing, retirement or recovery policy. Failure attribution is captured before
release; a later cleanup failure is retained alongside the original. Borrowed-handle
state is a non-atomic observation, not historical ownership or OS availability proof.
The receipt-retirement correction keeps the writer lease through actual deletion
and uses a delete-sharing, read/write-exclusive reconciliation lease until its
own retirement completes. Three focused real-store cases hold the existing
observer's `local-write-retire` and `local-write-reconcile` phases to reproduce
public reopen taking the receipt during the former release/delete gap: both
note scopes and concurrent reconciliation must no longer pause the retiring
client. The competing reader still reports Unknown/read-only while the owning
lease exists; explicit reconciliation after release verifies exact committed
owner/writing with no duplicate or replay. A real non-delete-sharing external
reader still causes committed-cleanup refusal and retained evidence.
These phases do nothing without an observer, introduce no SQL/retry/deadline
change, and are not historical hosted-holder attribution or hosted acceptance.
A real post-Commit Team-note gate
calibrates acquire refusal with exact committed writing still browseable/read-only,
then permits reconciliation only after the actual owned writer releases its lease.
Active unresolved real writers yield
validated read-only browsing rather than an empty success.
Reviewed-Import rollback requires explicit local-save reconciliation before retry;
an outstanding bulk Apply alone does not acquire the ordinary-save stall deadline.

The existing invisible compiled selection additionally drives the real three-second
stall, disabled edit controls, native-handle-free Person selection and exact newer
writing through late commit. The hosted `compatibility-settings` tail preserves
all prior cases/samples and adds visible failure, retained complete profile and
independent note writing, read-only browsing and stalled-owner proof with actual captures. Its
deterministic delay hosts the normal product MainWindow BAML in an isolated
test-only smoke process with the public real-storage commit observer held; there
is no production injection switch or global scheduling change. The
`*-sqlite-local-recovery.json` records that distinction, revision/run/attempt,
distinct host and product executable/assembly disk hashes, observed notices,
actual phase/outcome and exact owner. The three capture metadata files identify
the dotnet host executable, not the loaded product. This pre-close manifest is
partial evidence until the terminal hosted PASS follows actual idle, normal
close and the existing ten-second process-exit check.
The separate ordinary executable lifecycle and original native latency samples
remain unchanged. Compiling or invisibly replaying this tail does not establish
hosted pixels or a new latency claim; all lane budgets and matrices remain.

Recovery browsing uses an explicit Person-selection mode that skips the local-idle
prerequisite, not destination observation. Failed/stalled/indeterminate work remains
non-idle under the unchanged idle predicate. Every native recovery switch requires
the actual visible email/heading, exact retained fault phase/outcome and paused
mutation controls; owner drafts and late committed writing are checked separately.
Healthy ordinary selections still await idle; retained-write/recovery journeys
explicitly omit that obsolete prerequisite while preserving usable-destination
and exact-owner assertions. Runner cases exercise this shared caller
coordinator, including wrong-destination rejection, and the invisible compiled
replay drives real Failed and Stalled owner switches through it. Neither replaces
the hosted native recovery journey.

The existing named invisible `--verify-legacy-save-feedback` command is retained
for runner compatibility, not a legacy engine. It now uses actual SQLite
noncommit/reconciliation, exact retained owner writing, paused edits and available
browsing, followed by complete snapshot publication before normal cleanup close.
It invokes the enabled compiled `SaveProfileButton` Click connector, not a private
save method, and observes failed local completion and retained profile writing.
It remains part of the compiled profile-refresh selection.

The #100 hosted-failure regressions also run in that full selection. Focused
`--verify-cutover-publication`, `--verify-cutover-note-modes`,
`--verify-cutover-acknowledgment`, `--verify-cutover-failure-feedback`,
`--verify-cutover-incoming` and `--verify-cutover-close-publication` selectors
load actual BAML without showing a window or creating a native handle.
They use public SQLite creation and complete
snapshot transport, not canonical-file edits. The folder adapter creates the
actual synchronization directory before the picker can select it. Clean note
owner returns reopen Read; pending/failed writing returns Edit with its exact
owner, identity, draft and error. The native journeys retain both-scope reading,
historical context and independently reopened writing assertions.

For rejected profile Save and independent Import failure, read [Person profiles, reporting and editing](testing/person-profiles.md#profile-editing-and-cancellation).

Clean incoming-version portions of the migrated external-refresh journey
request actual Sync now instead of expecting a retired immediate file watcher
inside a twenty-second observation. The compiled regression separately proves
no automatic check at synchronization-clock second 59, adoption at 60, and
immediate manual reception of the next complete version. Save-request and
three-second clocks remain real; the existing automatic hosted journey remains.
These are focused causal regressions, not hosted/native or rich-scale acceptance.
The compiled local-recovery fixture uses a separate synchronization scheduling
clock, releases and joins its own admitted local save, then publishes through
the shared real lifecycle and waits for actual normal Closed before returning.
`--verify-local-recovery-cleanup` and the full selection reject a leftover logical
window/exit intent. A bare Close on an unpublished fixture is not cleanup: normal
exit correctly remains pending and can keep reading its local head during
directory removal. Production timer/disposal and forced nonjoining exit semantics
are unchanged; actual save-request and three-second clocks remain real.
The close-publication selector drives actual Closing/Closed and decision/Cancel
controls. Fresh exact published heads avoid candidate-validation/export replay;
real sibling-advanced heads reload and publish, with independent joined-data
checks. Unavailable publication and persisted known divergence remain vetoed,
without clearing retained bytes or review authorization. The existing older
export/newer writing/repeated/canceled-intent compiled regression remains.
Public real-store tests additionally cover changed receipt bytes, locked/corrupt
artifacts, unavailable local storage and an actual pre-Commit sibling transaction
whose unresolved receipt must refuse cached Ready until explicit recovery.

For independent note-leave guard and recovery-copy checks, read [Person-note and Team-note writing](testing/note-writing.md#immutable-drafts-and-scoped-cancellation).

Import's own failed Apply is different from independent writing: its picker,
entry, Preview and Apply reuse the workflow's existing scoped `CanEnterImport`
admission. The actual compiled Apply-failure/reconcile/People/reentry/Preview/
Apply loop retains the original error until successful explicit retry and
independently reopens the reviewed result. A separate invalid note still
prevents that admission; no fault is cleared to enter Import.

For saved note restoration and paused versus writable Edit, read [Person-note and Team-note writing](testing/note-writing.md#immutable-drafts-and-scoped-cancellation).

For demand-driven Manager choices, read [Person profiles, reporting and editing](testing/person-profiles.md#profile-editing-and-cancellation).

The explicit `--diagnose-rich-navigation` command is a separate, nonaccepting
actual-BAML investigation at the complete rich population: 42 action-to-layout
observations during 21 notes with six nominal 250-ms inputs apiece, raw managed
allocations and picker work, followed by full independent owner/writing/resource
verification. It never replaces native action-to-usable observations, subtracts
observer time or runs as another required scale lane.

For Preferred-name held-save retention and stale retries, read [Person-text and Preferred-name submission](testing/person-text.md#older-completion-and-newer-writing).

For Preferred-name incoming archival deferral, read [Person-text and Preferred-name submission](testing/person-text.md#incoming-owner-changes).

### Retired-engine coverage reconciliation (#100)

`WorkspaceFixtures.CopyCurrent` now caches durable bytes produced by normal
public SQLite creation, reviewed Imports, decoded photos, note creation/archive,
reporting transfer and a later Team-note edit. The same five fixture People and
exact textual business values remain. Publicly generated note/capture identities
replace ZIP constants; tests discover by owner/title once, then retain exact
IDs/versions/provenance across reopen and transfer. Old archives are refusal-only.
There is no private-table fixture builder or second public per-record repository.

The following protocol-only coverage is deliberately retired with its production
implementation, not skipped or relabeled as passing:

| Retired coverage | Semantic replacement |
|---|---|
| `IncrementalRecoveryTests`, `IncrementalReclamationTests`, `LiveGenerationTests` | `SqliteRecoveryTests` verifies actual pre/post-Commit process death, accessible/unavailable evidence, read-only recovery, nonreplay and explicit reconciliation; `WorkspaceSnapshotTests` verifies complete retained versions/resources and guarded current/previous rotation. Old selectors, independent canonical document copies, receipt publication and radix ownership inventories no longer exist. |
| `NoteDocumentValidationTests` frontmatter/filename rules | Snapshot note identity/owner/metadata/confirmation validation, exact Markdown/archive/reopen and stale-base preservation. Direct `.md` editing is unsupported. |
| Old portions of `IncrementalDeliveryTests` and `LocalPublicationDeliveryTests` | Complete snapshot validation for absent accepted evidence, history, historical-only photos, Person/history consistency and invalid delivery; focused snapshot race/divergence/revision-bound choice/retained-both-branches cases replace canonical replacement-window journals and external acknowledgment. |
| Canonical index/write-cost portions of `IncrementalProfileStorageTests` and `WorkspaceWriteObservationTests` | Public profile/evidence/replay and full 128-Person graph assertions remain; real SQLite lock/Commit/refusal/recovery replaces pointer replacement and private file-count ceilings. No SQLite write-amplification claim is inferred from old limits. |
| Per-file creation/deletion/owner removal and timestamp-only delivery tests | Real two-local-store public edits and snapshot transport preserve exact owner drafts, immutable bases, independent scopes and clean-note refresh; adoption waits behind retained writing. No unsupported note/Person deletion operation is invented as a test seam. |
| Canonical version-family/read-lease/maintenance/restore tests | Every readable-file version and staged SQLite1/2 refuses without mutation; current SQLite/snapshot schema/identity/provenance failures remain. Exact remembered secondary/inherited/Historical destinations and failed-replacement preservation remain. |
| Pending-navigation/non-overridable-close expectations | Actual native and compiled browsing retains drafts during pending/failure/unknown/divergence; explicit exit matrices require Cancel retention, latest normal publication and actual Exit anyway process death. Import/Workspace replacement and safe updater restart still protect unsubmitted writing. |

Mixed persistence classes retain domain assertions for every textual field,
omission/null/false and accepted replay, all relationships, sticky Team review,
notes/archival/favorites/photos, exact Profile evidence/dates, unknown ordinary
field provenance, frozen capture identity/version/provenance, private Team-name
eligibility/dormancy and duplicate attributed paths. Missing original Manager
observations cannot be manufactured by mutating retired history files: normal
reviewed Person creation requires explicit Manager/no-Manager. Existing
omitted-field evidence and Historical timing rules remain independently covered.
Invalid complete snapshots leave valid local reading/editing available with a
separate synchronization error; stale note rejection is a semantic conflict,
not a fabricated failed storage device. The focused SQLite/snapshot suites and
both explicit full-rich suites remain required at their existing selections.

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

For Person/Team ongoing choices, preference failures and empty invitations, read [Note reading, selection and Team context](testing/note-reading-context.md#ongoing-and-exact-selections).

For hosted long current/frozen reading and historical pending acceptance, read [Note reading, selection and Team context](testing/note-reading-context.md#current-and-frozen-reading-evidence).

For People navigation, two-axis bookmarks and settings limitations, read [Person search and browsing](testing/person-search-navigation.md#browsing-bookmarks-and-destinations).

For grouped search, publication guards and retained owner drafts, read [Person search and browsing](testing/person-search-navigation.md#grouped-person-search-and-retained-writing).

For Read-demand, functional and archival note readiness, read [Note reading, selection and Team context](testing/note-reading-context.md#read-and-edit-presentation).

The explicit `--diagnose-rich-navigation` diagnostic keeps all 42 selections,
20 Team plus one Person Add, full rich population, per-sample independent
reopens and real 60-second synchronization during sampling. Managed
dispatch/layout, observation milestones, allocations and independent reopen work
are separate raw observations, never additive or subtracted from native timing.
Only fixture-owned future timers stop after sampling; its own publication and
callbacks must finish before actual Closed and cleanup. This is not a required
lane or native acceptance, and changes no editor/navigation/durability target,
deadline, workload or existing scale selection.

For identity/context ranking and native keyboard/viewport acceptance, read [Person search and browsing](testing/person-search-navigation.md#identity-and-context-ranking).

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

For Team/history Read preparation and empty-viewport readiness, read [Note reading, selection and Team context](testing/note-reading-context.md#read-and-edit-presentation).

For secondary-note exact reopen and responsive matrix, read [Note reading, selection and Team context](testing/note-reading-context.md#ongoing-and-exact-selections).

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

For adaptive reading, cold virtualization and retained-draft geometry, read [Person reading and responsive layout](testing/person-reading-layout.md#responsive-reading-and-retained-drafts).

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
required logical lanes: workflow/view-model, filesystem, ordinary desktop,
and high-resolution desktop. Ordinary desktop uses two parallel, isolated
GitHub-hosted Windows jobs (`people` and `workspace`), approved on 2026-11-16.
High-resolution desktop likewise uses two isolated workers (`reading` and
`context`), with owner approval after the complete serial suite exhausted
its existing fifteen-minute limit.
Each native worker has its own disposable desktop; UI automation never runs
concurrently on one desktop.
The stable aggregate `CI` requires all four lanes, including both ordinary
and both high-resolution desktop workers. Failure, required skip,
cancellation, missing result and timeout cannot pass. Preserve the 5-minute
workflow/view-model, 11-minute filesystem, 15-minute limit for each ordinary
desktop worker, 15-minute limit for each high-resolution worker and 2-minute aggregate
deadlines: 78 minutes
summed, at most 17 minutes on the parallel critical path, excluding queues and
later CD. All four native desktop workers enforce matching 900-second whole-execution
budgets, including setup, temporarily raised with owner approval on 2026-11-15.
Both full scenario/size/assertion matrices, including profile-copy and SQLite
save coverage, remain required. Core/filesystem deadlines, separate scale-suite
budgets and save-latency targets are unchanged. If fifteen minutes is insufficient,
investigate rather than automatically extending it again.
The five-minute target includes setup, restore, build, fixtures, tests and cleanup;
passing a hard deadline does not claim that target or a new latency SLA.

`DesktopPartitions.json` assigns every existing ordinary scenario exactly once.
The `people` worker runs profile, draft, Person-note, Team/history and search
journeys. The `workspace` worker runs archival, compatibility, update and external
refresh journeys, followed by every existing compiled profile/note-refresh,
update-yield, release-isolation, release-trust and application-icon check.
Neither partition removes actions, samples, size matrices or assertions.
`HighResolutionPartitions.json` assigns `responsive-workspace` to `reading`
and the complete responsive draft and Team/history journeys to `context`.
Every original scenario, input, check and screen size remains required exactly
once. The original unpartitioned desktop and full high-resolution commands
remain available for an explicitly isolated desktop; explicit scale is unchanged.

Each worker uploads `scenarios.json`, `execution.json`, timing, logs and captures
in an artifact named `desktop-<partition>-attempt-<runAttempt>` or
`high-resolution-<partition>-attempt-<runAttempt>`.
`CI` validates both pairs of current-attempt artifacts against the exact candidate/run,
complete native selections and successful required companion commands. Missing,
duplicate, unexpected, stale or partial results fail closed even when a matrix
job reports success. Rerun each complete partition pair together; an earlier attempt's
successful artifact cannot fill a missing current-attempt result.
This changes execution capacity, not application performance targets or the
unidentified cause of longer UI observation calls.

Default local verification is non-UI:

```powershell
.\eng\Run-CiLane.ps1 core
.\eng\Run-CiLane.ps1 filesystem
```

`DraftDiscard.Tests.ps1` now runs both named Person/Team public workflow/view-model
cases from the already-built Core test assembly (the core lane supplies its
isolated assembly path). It refuses missing/skipped/partial results. The actual
shared draft coordinator, not a compiled retired `NoteDraftState` with a fake
dispatcher timer, proves admitted-discard refusal, exact owner writing and
scoped idle discard preserving independent unsubmitted input. Filesystem cases
remain responsible for SQLite durability and actual effect barriers.

Each runner isolates build artifacts. They may run in independent processes.
The filesystem runner excludes only `Category=Performance`; ordinary persistence,
delivery, recovery and watcher coverage remains required. The core runner also
checks the CI gate, separate-suite selection/results and named desktop-result
contracts without launching WPF.
It also runs `DocumentationHandoff.Tests.ps1` against isolated local Git
repositories and a bare remote, including a source branch that advances beyond
the approved documentation commit. Local path/file fixtures explicitly opt in.
Credential-route cases disable all transports and check rejection of SSH,
plain HTTP, embedded credentials and applicable Authorization headers, while
allowing harmless/unrelated headers without disclosing credential values.
Ref-preservation checks include local-only and remote-only tags with global and
remote pruning enabled. Mixed Markdown/gitlink commits must still be rejected
when clone settings hide submodules. These checks need no network or desktop.
For a narrow change, use `dotnet test` with combined selectors rather than
rebuilding/rerunning unrelated suites. Contract checks without launching WPF:

```powershell
.\tests\CiGate.Tests.ps1
.\tests\CiRunner.Tests.ps1
.\tests\ImplementationBaseline.Tests.ps1
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

`ImplementationBaseline.Tests.ps1` runs in the core lane and invokes the real
preflight in child PowerShell processes against isolated local Git repositories
and bare remotes. It checks fetched versus stale local refs, divergence, failed
fetches, explicit remote/local integration baselines and checkout preservation.
Local remotes require the harness's explicit fixture opt-in. Credential-route
cases reject matching configured Authorization headers without contacting GitHub
or printing header values.
It needs no GitHub credentials, network or desktop.

`--verify-xaml` is a separate non-UI build/load contract, not a desktop scenario.
It constructs the actual compiled MainWindow BAML and event connectors on STA
with isolated settings, requires the window to remain invisible with no native
main-window handle, then closes it. It never calls Show or runs the application
dispatcher; startup restore/update Loaded handlers and an external UI Automation
client are not run.
This catches XAML connection failures that compilation alone misses. It does not
prove rendered controls, navigation, dialog discovery or any smoke/scale result.

For compiled Copy focus and absent values, read [Person profiles, reporting and editing](testing/person-profiles.md#compiled-profile-and-copy-assertions).

For distinct compiled note identities and collapsed context, read [Note reading, selection and Team context](testing/note-reading-context.md#ongoing-and-exact-selections).

For compiled frozen capture disclosure and attribution, read [Note reading, selection and Team context](testing/note-reading-context.md#current-and-frozen-reading-evidence).

The compiled chrome contract keeps folder management and Import inside Settings,
the quiet Workspace name non-interactive, and search outside the People-only view.
Settings occupies the bounded main work area rather than growing above it;
operational feedback and startup recovery stay outside that scrolling view.
It requires one Open Workspace action and a focusable, copyable displayed path,
without a pending-path TextBox or separate Choose action. Healthy reading collapses
the operational notice rather than keeping Ready/validated/saved furniture.
The Window's accessible ItemStatus and HelpText expose the actual existing
workflow activity and operation feedback; the Workspace label's ItemStatus exposes
actual validation/freshness. These are bound to the same detailed status, not
test-only readiness or success flags.
Notice classification uses the workflow's operation error and actual retained
local-save/recovery state, not English success-message lists or maintenance substrings.
Preferred-name cancellation and successful profile/note saves leave healthy
reading quiet. Both note sections collapse routine operation feedback while
exposing that same feedback through their surface's accessible ItemStatus.
Pending/failed drafts remain visible; independent save and preference errors,
unavailable local data, synchronization divergence and committed cleanup remain actionable.
An idle failed operation is summarized once, without an additional generic
pending row or a second copy of its technical rejection. Real in-flight work
and distinct failures still appear. Preferred-name failure keeps a short local
retained-entry message, full diagnostic HelpText and a keyboard-accessible
Details disclosure inside the same popup; reading Details does not require
an outside click. The compiled check activates that real disclosure and retains
the failed input, original saved bytes and unsafe-close state.
Hosted note waits read the real accessible feedback rather than requiring a
permanent success TextBlock. Discard checks the restored saved reading and bytes.

For compiled profile refresh and Copy payloads, read [Person profiles, reporting and editing](testing/person-profiles.md#compiled-profile-and-copy-assertions).

Hosted Workspace-path primary-click nonmutation observes the subsequent physical
right-click opening its Copy path menu before reading the sentinel, without invoking
Copy or reseeding the clipboard. That ordered input proves WPF processed the primary
click, rather than sampling early.

For anchored facts, archived identity, editor and photo geometry, read [Person profiles, reporting and editing](testing/person-profiles.md#compiled-profile-and-copy-assertions).

For compiled splitters, main reading and independent fact-row heights, read [Person reading and responsive layout](testing/person-reading-layout.md#compiled-layout-and-divider-assertions).

For report visibility and accepted tree selection, read [Person profiles, reporting and editing](testing/person-profiles.md#categories-and-reporting).

For Preferred-name controls, cancellation and missing owners, read [Person-text and Preferred-name submission](testing/person-text.md#compiled-controls-and-native-popup-evidence).

For retained creation failure after independent profile delivery, read [Person-note and Team-note writing](testing/note-writing.md#durable-typing-and-save-handoffs).

For public control seams and separate update-drain proof, read [Person-text and Preferred-name submission](testing/person-text.md#compiled-controls-and-native-popup-evidence).

For proportional minimums and routed divider keys, read [Person reading and responsive layout](testing/person-reading-layout.md#compiled-layout-and-divider-assertions).

For compiled Team designation, archive, delivery and preference failures, read [Note reading, selection and Team context](testing/note-reading-context.md#ongoing-and-exact-selections).

For bounded prose, note timeout diagnostics and body ink, read [Note reading, selection and Team context](testing/note-reading-context.md#current-and-frozen-reading-evidence).

It also drives the actual Settings button through the WPF dispatcher and checks
logical focus on Open Workspace, including closure before a queued focus request.
The focus request waits behind layout and the current input event; the unchanged
hosted Space-key/`HasKeyboardFocus` assertion remains the physical-focus proof.
The existing internal application-construction seam substitutes only the external
folder-picker result in invisible checks. Accept/cancel, rejected invalid delivery,
retained note writing, startup-preference bytes and explicit Import-path Enter
preview all run through the actual compiled handlers and real file-backed workflow.
This does not establish native picker discovery, input, selection or cancellation.
The ordinary profile and Person-note journeys use the actual native dialog once,
with explicit selectable-folder setup, exact accepted/cancelled outcomes and
unchanged preference/writing assertions. Installed, responsive and scale callers
use that same visible picker, never a hidden direct-path adapter.
The native picker helper targets the observed Folder Edit field (ID 1152), not
the file-dialog entry or a Search box. Select Folder/Cancel match both their
native automation ID and Button role before obtaining InvokePattern: shell
folder items can reuse numeric IDs 1/2 and are not dialog actions. Exactly one
action is invoked before the unchanged close observation; no second click or
action retry repairs a still-open picker. Cancellation compares actual before/after
orientation, displayed path, preference bytes and untouched Workspace contents;
HelpText need not be empty because WPF can supply useful tooltip fallback.
Installed-flow failure diagnostics only read the existing automation tree and
retain the original exception; they never call the maximize/open-window helper.

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
by persistence tests. Observing the real Import control becoming disabled for
handoff queues one note save at Normal priority for the first dispatcher yield,
and waits for publication to
reach the hold. A ContextIdle observer requires the update to remain pending,
with unchanged saved bytes and no early handoff, before releasing publication.
The same regression repeats with a deliberately queued Preferred-name Save
command and held real profile publication. Merely unconfirmed Preferred-name
typing is separately rejected by the actual signed Check for updates command,
with unchanged Workspace bytes and no installer effect; only explicit Save admits it.
The original single update action must then hand off the final durable bytes
and matching file hash; there is no update retry. The shared work drain
rechecks its gate after dispatcher settling and waits again for an admitted
save, while existing failed-save, profile/draft and final restart guards remain
active. This regression never shows/focuses a window or creates its native
handle.

For independent ordinary/responsive fixture expectations, read [Person search and browsing](testing/person-search-navigation.md#selection-visibility-and-shared-fixtures).

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
  binding, readable Preferred name and one-click Add/Edit, exact popup defaults
  and focus, physical Save of the unchanged Directory default and physical Enter
  clearing an equivalent existing name, retained inline validation failure,
  Cancel/Escape/outside-click of untouched defaults and distinct writing
  dismissal without mutation, popup discovery/focus and uniquely named normal,
  failure and dismissal captures,
  quiet chrome and Settings keyboard entry/focus, anchored photo/identity bounds,
  all ordinary facts with stable labelled missing-value slots and no More details gate,
  readable ordinary values and idle state after profile Cancel,
  primary copying of every eligible value, representative Enter/Space and the
  retained mouse/keyboard Copy value menus, exact multiline/leading-zero payloads,
  actual hand cursors/full-value/owner hints and rendered transient confirmations.
  Clipboard-sentinel checks protect absent values, Heading/Manager navigation,
  editor Enter and Workspace-path primary clicks. A genuinely held OS clipboard
  rejects copying without success or loss of staged profile writing; explicit
  retry returns to quiet reading. The responsive journey repeats the eligible
  property set, long/truncated/wrapped values and duplicate Team-name activation
  throughout its unchanged size matrix. Both remain hosted-only evidence,
  representative partial change/clear, save/reopen, actual copied PNG
  and JPEG pixels, photo clear/placeholder and Profile timeline. Its marked
  300 x 300, 300 x 150, 150 x 300, 72 x 72 and 72 x 36 PNGs prove centered
  reduction/enlargement, preserved square-marker proportions, independently
  expected pre-mask fill sizes (144 x 144, 288 x 144, 144 x 288, 144 x 144,
  288 x 144 DIP), axis-band positions and intentional circular exclusion.
  UIA requires both the existing photo viewport and accessible Image to stay at
  144 x 144 DIP. Centered fill happens inside a square drawing using the decoded
  bitmap; the independent pre-mask expectations are checked through source-band
  positions and square-marker proportions in actual pixels, not oversized
  accessible bounds. Bounds and pixels use effective Windows scaling. Initials retain
  their accessible text and blue/white colors; UIA text geometry and rendered ink
  prove proportional enlargement, and pixel spans prove the matching 144-DIP
  circle. Choosing/clearing stays staged until explicit Save; Cancel and
  Person switching retain the correct saved photo/initials footprint.
  An original initials timeout also writes failure-only
  `photo-rejection-<scenario>-ui.png` / `.json` diagnostics. The PNG is the last
  already-captured rejected `ReadPhotoPixels` buffer, retained before its GDI
  disposal, never a later screenshot. JSON carries its original acquisition
  interval, crop/viewport/text/previous-photo coordinates, DPI, the first false
  guard or probe and observed RGB, computed glyph bounds and circle-edge offsets.
  Unreached stages stay unobserved. Only one rejected buffer (up to 2 MiB) and
  bounded probe records are retained; a later pre-pixel refusal is labelled
  separately from that buffer. Native window/PID binding observations have
  their own timestamp, distinct from pixel acquisition. Source/run/attempt,
  app/workflow/harness and native hashes are bound after the original failure.
  PNG/JSON are emitted only after the unchanged twenty-second failure; output
  faults remain attached to the primary exception and separately reported.
  No extra desktop acquisition, GetPixel, UIA or business query is made, no
  predicate is replayed, and no capture/serialization time is deducted. All
  geometry, colour tolerance, expected values, focus and cursor behavior remain.
  Synthetic managed-raster recorder/PNG/refusal tests run in Core; they do not
  claim actual desktop pixels or fix app rendering. These diagnostics do not
  replace normal visual evidence or any required acceptance check.
  Clear is invoked once and its visible staged-clear notice must appear before
  asserting the unchanged saved photo or cancelling; pixels already present
  before the click do not establish that the handler ran.
  Import is opened
  explicitly, replaces the people view, and returns to reading with Import
  controls absent. Choosing/previewing retains the saved population; changing the
  path invalidates Apply until reviewed again.
  Inline editing additionally requires initial Directory-name focus, Tab to Alias,
  actual field Enter followed by Tab without submission, unchanged saved office
  data before explicit Save, and focus returning to Edit profile after Cancel.
  Named captures retain the field-Enter draft and staged photo clear.
- `draft-navigation`: retained profile/Preferred-name and Person/Team writing across
  real tree/search/Manager browsing, plus explicit dirty-profile Save / Discard / Stay, failed
  leave-save retention and Import protection versus independent failed Person/Team drafts,
  protected Import/Workspace replacement, retained owner/draft and deliberate discard.
  Opening the dedicated Import view exercises
  all three profile decisions and a locked-file leave-save rejection, then checks
  reading values after returning; independent failed notes still block Import.
- `person-note-rendering`: Read-first existing notes with both editors hidden,
  one-click editing, empty-section actions, direct New-note editing and empty-body
  accessibility, actual Markdown text, loopback
  no-fetch assertion, failed local transaction, usable retained-draft browsing,
  Workspace replacement/close decision, retry, independent ownership and durable reopening.
- `team-context-history`: explicit Team ownership, all-ancestor attribution,
  ongoing/Other designation, grouped Person search and independent exact secondary access, Read-first owned notes,
  direct New-note editing and empty-body accessibility, visible preference/save
  failures and collapsed attributed warnings at the ordinary desktop size,
  exact owner review navigation/confirmation with retained writing and archive/restore,
  secondary History with confirmations/observations, actual current/frozen
  reading and honest historical dates through a representative
  ancestor move, historical-source Person search, ordinary History access and reopen.
  Capture details additionally exposes complete transition/note evidence
  separately from frozen Markdown, with keyboard expansion/collapse and
  nonmutation checks. Named actual-pixel states retain the collapsed capture
  entry, expanded evidence and intermediate frozen-note boundary, including a
  note needing review when captured. The responsive journey repeats these states
  at 700 x 500 and 1100 x 900 alongside its complete long current/frozen reading.
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

The fourth required lane uses
`.\eng\Run-CiLane.ps1 high-resolution -HighResolutionPartition reading` and
`-HighResolutionPartition context` on separate disposable GitHub-hosted Windows
VMs. Its complete selection remains `tests\HighResolutionScenarios.json`;
`HighResolutionPartitions.json` supplies the exact disjoint split through
`--high-resolution-reading` and `--high-resolution-context`. The original
`--high-resolution` selector still requests the whole suite:

- `responsive-workspace`: the search/Favorites journey plus the complete
  700 x 500, 1100 x 900, host-width 959/960/961 and maximized matrix, actual
  Segoe UI text, People 240 / Person allocation 420 minimums, 600-DIP prose cap,
  all-facts ordering and long/missing text, anchored main scrolling, overlay bounds/focus,
  mouse/keyboard People resizing, percentage resize/reopen and search/clear position.
  At each reading size and the minimum Person allocation, the same real photo
  and a Manager's initials retain their 144-DIP circle without a footprint
  change on selection. Identity actions remain visible, focusable and beside
  the photo; the photo stays anchored during the existing long-note scroll.
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
disjoint; each worker requires its own complete named `scenarios.json`. Ordinary
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

The installed authenticated-update boundary also captures versioned preservation
evidence before handoff and after the restarted app closes normally. Exact
Workspace identity/local head/folder, complete public Person/Archived/profile/
note/history/frozen-context/photo state and no-op previews of both known fixture
Imports must match. Replay inputs are reconstructed outside the private Workspace;
capture never calls Sync or publishes. Normal close must publish that exact head.
Only the exact SQLite database/WAL/SHM filenames may differ physically across
this application lifecycle; every other private file remains byte-identical.
Original unpackaged safe-point byte checks and nonmutating installer/uninstall
all-file checks remain strict. Both receipts and changed filenames are retained
for diagnosis. This is a controlled two-import fixture oracle, not arbitrary
accepted-import enumeration. Normal production builds exclude these oracle types.

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
The obsolete combined `scale` / `--scale` / `--ui-scale` selectors are rejected;
use the isolated `--ui-scale-prefix` and `--ui-scale-tail` selectors with their
candidate-bound logical handoff. A single explicit scale scenario is investigative,
not complete scale acceptance. `--high-resolution` selects only the
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
180-second scale limits. Photo/initials visibility and geometry poll within that
existing smoke bound; persistent mismatches report the last observed geometry.
Missing desktop pixels or failed capture still fail explicitly rather than
being retried as layout. Remote content is never fetched just to render notes or
profiles; loopback listeners and actual pixels/text are evidence, not filenames
or image dimensions alone.

UI Automation invocation can return before WPF processes the queued button
handler. Click and value helpers acquire an enabled control and its required
pattern within the existing bounded observation wait, then invoke or set once
without a second control lookup. Only preparation is retried, never the action.
Raw Click/Set and idle/startup observations never open or close Settings.
Physical copy click/hover preparation likewise captures a visible usable point
within that wait, then sends input once. Eligible targets must be enabled;
deliberate missing-value checks retain their disabled automation peers.
Copy/owner hovers send actual absolute mouse movement on the verified primary
display, not position-only `SetCursorPos`, which left the hosted cursor suppressed
despite hitting the correct value. The native visible-hand/non-hand assertion remains.
Hosted cursor observation samples twice to retain its repeated-read regression.
Every read reinitializes `CURSORINFO.cbSize`, which the API can clear on return;
reusing that output made later reads fail with Win32 87. Invisible local contracts
never query the interactive cursor, which can be inaccessible on a locked desktop.
Tooltip discovery checks native top-level popup children before the same descendant
query, avoiding full reading-document traversal when the popup is top-level. Exact
tooltip content, cursor, clipboard and rendered-feedback predicates are unchanged.
Copy readiness failures retain the target's enabled/offscreen/focus state and bounds.
Named physical-click targets are located inside that same readiness wait, so queued
input cannot race an unguarded control lookup before preparation begins.
The ordinary copy fixture scrolls its reading surface to the top once after profile
Save, before acquiring any fact target, as the responsive copy fixture already does.
UIA's offscreen flag alone can be false even when retained edit scrolling clips the
fact above that viewport; acquiring an actual clickable point remains required.
Rendered contrast uses one actual desktop bitmap capture, then the original six-pixel
sample grid and luminance threshold against its pixels, rather than repeating desktop
readback for every sample. This is neither offscreen rendering nor a reduced predicate.
Feedback reads request name, offscreen state and bounds together in a fresh UIA cache
snapshot per observation; no metadata is reused across gestures, and the same actual
desktop-pixel check still proves rendering.
Exact clipboard-copy observation uses the existing bounded STA dispatcher pump,
servicing the harness's own OLE clipboard-owner messages while another process copies.
The real clipboard equality and 20-second bound remain; no copied-text effect is faked.
Only a read-side `COMException` with `0x800401D0 (CLIPBRD_E_CANT_OPEN)` remains
pending within that same wait. It never substitutes empty text, repeats a copy
gesture/write, or restarts the deadline. Timeout records the last read's availability
or text mismatch and preserves both the wait failure and latest cannot-open exception;
unexpected read errors propagate. Runner-category `ClipboardObservationTests` drive
the same tooling observer with a controlled external reader and bounded wait, without
accessing the local clipboard or opening a window. Actual native proof stays hosted.
Bounded `DIAGNOSTIC: harness-runtime` JSON lines investigate ordinary-lane
runtime without changing the workload or acceptance clocks. Every native scenario
and compiled profile-refresh/update invocation emits a start/end record; compiled
copy and SQLite, native copy and SQLite note/profile work have named child phases.
Copy starts before its first existing PASS, with fixed payload/Team/missing-value
checkpoints; SQLite checkpoints separate held navigation and repeated dispatch.
An unmatched start/checkpoint identifies interrupted work, not success.
Each phase carries the exact `GITHUB_SHA`, run and attempt (null when unbound
locally), its parent/context and outcome. Fixed-size, thread-safe in-memory
counters retain started/completed counts, total and maximum elapsed observation
cost for existing discovery, shared native requests, waits/predicates, filtered
note snapshots, capture/pixels and compiled copy/SQLite layout. Capture measures
screenshot acquisition, PNG encoding and metadata writing;
standalone path-safe state validation records no capture operations. The existing
invisible capture-name regression verifies that distinction without taking screenshots.
These diagnostics add no per-poll writes, UIA reads/cache warming, actions or retries. Existing
direct native latency requests and samples remain unchanged. Nested/inclusive
measurements overlap and concurrent snapshots are non-atomic; these are neither
database commit times nor replacement product latency, and nothing is subtracted
from the raw acceptance samples or lane budget. Expected diagnostic output failures
cannot replace an action's result/exception. No note/profile values or exception text
are recorded by these diagnostics. Harness regressions cover bound early
failure/interruption records, unchanged outcomes on output failure and bounded
parallel aggregation. These probes are investigation, not a root-cause fix.
Expected serialization/output failures retain process-wide bounded counts and
last exception type/HResult, never exception messages. They emit the fixed stderr
marker `DIAGNOSTIC: harness-runtime-output-failure investigationIncomplete=true`,
without retrying the failed writer recursively. If stderr also fails, its
count/type/HResult remain observable through `HarnessRuntime.OutputFailures`.
Later records carry `diagnosticsComplete=false` and `outputFailures`; action
outcome is separate from evidence completeness. Missing boundaries or any output
failure make investigation evidence incomplete, even when actions passed.
Unexpected diagnostic programming errors are not silently caught.
`AwaitLocalWorkIdle` preserves its original exact `Ready` activity and ordinal
non-`Pending` status guards before requesting one fresh, four-ID filtered native
snapshot per eligible poll. The snapshot contains only Email, WorkspaceStart and
the two note surfaces, never elements/properties retained across polls or owners.
Absent Email still requires the actual visible starting view; empty Email retains
its original optional-note behavior. A selected Person still requires Person then
Team feedback without `Pending`, including offscreen note reading. Required-control
absence and provider failures are not readiness, and no local-save phase/status is
normalized. The existing twenty-second observer/catches remain unchanged.
Runner regressions pin short-circuit order, startup/required-control failures,
per-poll query count, changed owners/feedback and unavailable providers. These are
observation contracts, not rendered usability, local durability or latency claims.
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
description; the full copyable path is display text in Settings. Restore checks
still require the exact folder and live current-data/owner/recovery observations
within the same bounded wait, not just a successful folder-name match.
The native-picker helper invokes the visible action once, fills the native folder
entry and accepts or cancels once, then observes dialog closure. It never creates
folders, repairs Settings, retries Open or substitutes an app-side path setter.
Empty folders and malformed selectable Workspaces are explicit scenario fixtures;
the rejected-open checks retain their complete byte/preference assertions.
Quiet status is read through the real accessible properties above, without
expanding Details as a hidden prerequisite. Retry observation subscribes to the
same actual activity on the Window, preserving saving-to-Pending completion.

For reserved scroll gutter and unrepaired restoration, read [Person reading and responsive layout](testing/person-reading-layout.md#compiled-layout-and-divider-assertions).

For native leave-save and independent-note Discard, read [Person profiles, reporting and editing](testing/person-profiles.md#profile-editing-and-cancellation).

For external-delivery retry observation and retained drafts, read [Person-note and Team-note writing](testing/note-writing.md#durable-typing-and-save-handoffs).

The scale mutation scenario retains actual in-progress-work rejection while
Settings renders. A rejection check invokes once, then observes the actual rejection
message within the existing deadline while requiring the original Person to
remain selected. A generic pending message or a completed save is not rejection
evidence. The runner's non-UI ordering regression covers pending → rejected
observations and refuses missing feedback, wrong feedback or changed destination;
hosted WPF still supplies the real UI evidence.

For busy and idle scoped discard, read [Person-note and Team-note writing](testing/note-writing.md#immutable-drafts-and-scoped-cancellation).

For current and Archived selection assertions, read [Person search and browsing](testing/person-search-navigation.md#selection-visibility-and-shared-fixtures).

## Separate scale commands and results

The complete workloads are outside required PR smoke, not removed:

| Command | Required selection | Evidence |
|---|---|---|
| `.\eng\Run-Performance.ps1` | Complete `Category=Performance` filesystem workload | `.ci-results\performance\result.json`, unique run directory, complete named TRX and logs |
| `.\eng\Run-UiScale.ps1 -Worker prefix` | `scale-mutation-save-prefix` | Candidate/run/attempt-bound worker result, original rich latency/lifecycle artifacts and complete logical handoff |
| `.\eng\Run-UiScale.ps1 -Worker tail -PrefixResultsDirectory <prefix-artifacts>` | `scale-mutation-delivery-tail`, then `scale-navigation-rendering` | Separate worker result, public Join receipt, ordinary profile samples and complete tail/navigation evidence |
| `.\eng\Assert-UiScaleUnion.ps1` | Both workers; every scenario in `tests\UiScaleScenarios.json` and phase in `tests\UiScalePhases.json` | `.ci-results\ui-scale-union\result.json` and unique run directory containing both complete worker artifacts |

Both rich worker commands require `-ExpectedRevision`, `-RunId` and
`-RunAttempt` (the last two default to the current GitHub Actions environment).
These selectors are not standalone complete-suite commands. The hosted workflow
owns isolated execution, current-attempt artifact transfer and union verification.

Non-UI performance proves genuine 10,000-Person load/reopen, current/historical
search, durable mutations, reviewed 10,000-record imports, original/latest batch
replay, external delivery/recovery and complete retained-data comparisons.
Targeted large-graph/no-import-cap correctness remains in required core coverage.

UI scale builds and drives the **normal WPF executable** on two isolated
interactive Windows workers. The original mutation scenario splits only at its
existing process-reopen boundary, after the combined held-export host has
returned from normal process death:

- `scale-navigation-rendering`: Settings visibly works while real opening is
  pending; expand/collapse, scroll and select a distant realized tree row;
  current/frozen/deep-chain/final-archived-Person search and Manager navigation;
  actual photo pixels and rendered current/frozen Markdown.
- `scale-mutation-save-prefix`: repeated normal-executable usable Team editors,
  Person selection and ordinary durable saves while typing continues, followed
  by actual compiled WPF at the same complete rich population during a held real
  profile Commit and a held complete-snapshot export. Settings and Person
  browsing must finish while those same operations remain admitted, without
  losing the original owner's writing. Both complete 20/42/21 phases, real held
  profile proof, all 21 background profile saves and latest publication stay together.
- `scale-mutation-delivery-tail`: **public FileBacked Join of the first worker's
  actual published complete `.oesnapshot`**, not an equivalent newly seeded
  fixture. It resumes all 21 ordinary normal-EXE profile Save samples, favorite mutation, complete
  reviewed Import, two-local-store public note edits/snapshot delivery/rendering,
  latest publication and durable profile/Manager/favorite/photo reopening.
  Valid incoming adoption cannot require a Settings recovery retry.

The source handoff is captured only after the original held-export flow returns.
Its envelope/payload hashes, exact Workspace identity/head, source/test/runner
trees, workflow blob, candidate/run/attempt/first-worker invocation, full runtime component inventories
and loaded native SQLite version/source/hash are bound to the receiving worker.
Normal product and harness builds map their unique build/checkouts to fixed
debug-source paths so separately built executable/runtime hashes must agree.
The artifact contains only the actual immutable logical snapshot, recorded
preferences and metadata; never a raw database, WAL, SHM, or reseeded note/history
identities. Preferences retain the actual exact selection, ongoing notes and
pane proportions, relocating only the original Workspace path to the receiver's
fresh physical folder. Public Join validates the complete snapshot. Independent
public reopen compares all People, exact notes/owners/GUIDs, profiles/timelines,
current/inherited/frozen contexts and decoded photo bytes/dimensions before the
tail launches the normal executable. The receipt truthfully identifies a new
worker/store/process, not continuation of the first physical process.

`tests\UiScaleWorkers.json` selects every scenario once. Both terminal worker
results, exact ordered original phase begins and successful ends, all required
raw note/profile/lifecycle artifacts and the matching validated handoff receipt
are required by the union. A missing/failed/skipped/duplicate/substituted worker,
stage or payload cannot pass. The tail never calls `ScaleFixture.Build`; only
the separate original navigation scenario retains its own fresh fixture.
The reusable manual/nightly bridge uploads both workers on failure and retains
the existing candidate/run/attempt `ui-scale-...` artifact name for the complete
union. Release gates revalidate both workers, freshness and the same handoff;
they do not relabel their summed wall time as one 660-second execution.

`--verify-ui-scale-handoff` runs a full-rich public-filesystem contract without
showing WPF or using desktop automation. It deletes the original physical
fixture after capture, then verifies actual Join/reopen with the complete rich
population, resources/history and recorded selection/preferences. Its retained
`.ci-results\ui-split-validation\logical-handoff-*\handoff` is contract evidence,
not hosted native acceptance. Focused `UiScaleHandoffTests` cover mismatched
candidate/attempt/native/product hashes, corrupted bytes, different selection
and forbidden raw-file bundles. PowerShell union contracts cover complete and
missing/failed/mismatched/over-budget selections.

The same shared `ScaleFixture` preserves 9,900 active/100 Archived people, the
complete 42-edge chain, 2,500 photo references, 1,000 Person notes, 111 Team notes,
200 frozen contexts and retained Profile evidence. No population/chain truncation,
import cap or non-UI proxy replaces either WPF scenario. The old combined scale
runner is retired; it is not also invoked by smoke or the explicit command.

The rich mutation scenario retains twenty Team-editor, forty-two Person-navigation
and twenty-one ordinary save samples per phase. Healthy samples drive the normal
product EXE. The background-export phase drives the actual product MainWindow in
an owned hosted WPF process, controlling only the public 60-second clock and real
external snapshot publication. The actual held profile transaction separately
proves pending-work usability; its deliberate hold is not a healthy-save latency
sample. The snapshot must remain held through the same complete continued-typing/
browsing workload and independently reopened local writing. Its older publication
cannot acknowledge the later local revision; subsequent Sync now must publish
that exact latest revision.

The controlled host and following normal-EXE reopen inherit the fixture's saved
view, not an empty profile. Their exact expected Person comes from an independent
prelaunch preference snapshot. Missing or unreadable preferences cannot become a
blank-view fallback. `--verify-rich-host-restoration`, also in the full compiled
selection, replays the actual controlled-host construction and Loaded handlers,
exact saved Person/Team-note reading, subsequent Person restoration, grouped
search and normal Closed without creating a native window. It does not replace
the hosted rich lifecycle, change its 180-second readiness observation or extend
the whole 660-second scale budget.

Rich ordinary typing uses six predetermined inputs at nominal 250-ms spacing,
not a new 60-ms stress deadline. Raw native request/observation arrays, nearest-rank
`ceil(0.95 * count)` p95, sample counts, exact candidate/run/attempt, product EXE/
assembly and host hashes, environment and monotonic clock boundaries are retained
in the two `*-sqlite-note-latency-rich-*.json` artifacts and the
`*-sqlite-rich-export-lifecycle.json` artifact. On 2026-11-20 the owner approved
informational editor/navigation timings so functional validation can finish
without further performance work. Their historical 100-ms `budgetMs` references
remain, with `budgetEnforced: false` and `timingUse: informational`; every sample,
percentile and functional usable-state assertion is retained. Ordinary
durable-save p95 retains its 1,000-ms goal. On 2026-11-24 the owner approved a
temporary 1,050-ms **enforced** ceiling only for these rich ordinary note saves
in both `normal-executable` and `background-export`. The note result contract
records `localDurability.goalMs: 1000`, `budgetMs: 1050`,
`budgetEnforced: true`, `temporaryCeiling: true` and `timingUse: acceptance`.
`goalMet` is computed from the original nearest-rank p95 against 1,000 ms; an
accepted p95 above the goal must report `goalMet: false`, not claim one-second
success. The union recomputes p95 from all 21 raw samples and requires this exact
policy metadata in both phases. At 1,050 ms it accepts; above it, it rejects.
All commits must still be observed during continued typing. Profile saves,
small-fixture note/first-commit one-second checks, queue-inclusive three-second
stall, held-adoption capture proof and both 660-second worker budgets are
unchanged. This is temporary acceptance pending later optimization/review, not
informational-only durability or authority to increase another limit. Historical
failed runs retain their original measurements and verdicts.
Missing/incorrect UI, owner, writing or durability remains a failure within the
original observations.

For postcommit scoped acknowledgment and causal negatives, read [Person-note and Team-note writing](testing/note-writing.md#commit-acknowledgment-and-cancellation).

Request-to-postcommit-acknowledgment latency is a conservative upper bound on
Commit latency, not direct storage-only time. Every rich sample independently reopens real local storage
to verify its request is durably covered under the original owner, never the
selected report. Full-population reopen verification duration is reported
separately: load/validation cost is not substituted for application Commit latency.
Reopen proves data correctness; it is not used to retroactively date Commit.
The two former after-typing reads (prefix backing and final exact-writing
readiness) now share one genuinely fresh public ExistingOnly reopen per check at
the original final UI-readiness point. No harness mutation intervenes; late saves
can settle, so an earlier/in-memory reader is never reused. That one coherent
read checks the original request prefix, exact final title/body, exact owner and
wrong-owner absence, inside the unchanged final observation. Original Ready and
current-owner checks are also retained after the reopen. An earlier durable
prefix with a legitimately later final title/body still pending, or changed UI
readiness, returns false to the same original20-second Await; each later check
reopens freshly, never reuses the earlier reader or submits another save.
Missing/corrupt storage, wrong owner or an uncovered original request prefix
remains an explicit refusal. All 21 successful independent verification
durations remain in
`independentFullReopenVerificationMs`, with
`independentVerificationScope: fresh-final-prefix-owner-and-exact-writing`.
The shared numeric sample validator rejects JSON booleans, including these 21
verification durations in both note phases. Finite nonnegative numeric values,
including zero and fractions, remain valid; counts and p95/policy checks are
unchanged.
They measure the successful final reopen/validation/disposal, never replace the
ACK sample or deduct any pending-check work from the observation. The actual
compiled regression enters this same final predicate when a completed real
work operation exposes Ready/latest editable UI while a later real Commit is
held. It stays pending without success/exception, then proves fresh final owner
data after release with one success record and the original ACK timestamp.
Separate cold pre-Commit negatives and
post-Commit positives still require their distinct real states/readers.
Focused two-Person
20/42/21 samples keep their existing independent-reopen timer, original 20-second
observation, 60-ms continued-input workload and p95 contracts unchanged.
No per-worker budget, functional observation or sample-count change is permitted.

For pending observation cancellation and owned-child limits, read [Person-note and Team-note writing](testing/note-writing.md#commit-acknowledgment-and-cancellation).

For investigation on an authorized interactive desktop, use the worker selectors
or one scale name with the explicit candidate/run/attempt handoff. Use both
`Run-UiScale.ps1` workers and `Assert-UiScaleUnion.ps1` for complete bounded
candidate-bound acceptance. A one-scenario or one-worker pass is not suite success.
Missing/locked interactive desktops and unavailable pixels are visible failures;
never skip rendering, change shared-machine settings or launch local automation
without separate permission.

Non-UI performance and **each isolated rich UI worker** have their own
**660-second whole-execution pass/fail budget**. The hosted
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

Non-UI rich performance also measures twenty-one actual explicit profile Save
requests through the real VM queue and SQLite acceptance, not quick
`EditPersonText` as a proxy for the shipping explicit Save path. Its nearest-rank
p95 retains the one-second target. Same-revision saves still read the real head;
changed/failed heads, stale bases, photos, eligibility, newer generations and
unknown receipt recovery remain covered in focused real-storage suites.
Native rich profile samples repeat that explicit Save path in both normal-EXE
and actual held-export WPF phases, with exact whole-Person and decoded-photo/hash
reopen checks. `*-sqlite-rich-profile-latency-*.json` separates native
request-to-postcommit-acknowledgment from full-reopen verification.
These observations use the real Directory-name editor's automation provider:
visible in Edit and offscreen after Save, alongside the exact owner/value,
enabled Edit action and Ready state. The layout-only `ProfileForm` StackPanel
has no WPF automation peer and cannot be a native UI Automation target.

Only the fixture's synchronization cadence is controlled through its independent
TimeProvider. Actual save-request/latency/three-second clocks remain real for
performance acceptance. The clock-independence real-store regression advances
the synchronization clock during an actually admitted blocked save without
advancing its deadline, then advances the separate save clock by three seconds
and requires Stalled/unknown with browsing available. Non-UI ordinary measurements
admit exports explicitly, without accidental timer exports contaminating the
healthy editing phase. Cleanup stops fixture-owned future scheduling and waits
for already admitted export I/O before deleting its data. Production timer/
Dispose/forced-exit semantics are not changed to make cleanup pass.

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
It requests observation for up to 900 seconds while retaining the original
660-second per-worker verdict and thirteen-minute worker watchdog.
Reports set `diagnosticOnly`, distinguish observed completion from the verdict,
and never report candidate-verification success in this mode, even if observed
completion is under 660 seconds. Its artifact name starts with
`diagnostic-ui-scale-`, not the normal verification prefix. A diagnostic prefix
is never an accepting handoff; diagnostic reports cannot pass the union. Normal release
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

`.github\workflows\scale-verification.yml` owns the independent non-UI job, the
two isolated rich workers and their strict union collector. Separate invocations
can overlap on the same ref; no scale concurrency group serializes or cancels
them. Each execution job uses its own hosted Windows runner; the tail consumes
its successful prefix's current-attempt handoff. UI scenarios are serial within
their own worker, not parallel competing windows. Each suite records the same
exact candidate SHA in independently named results/artifacts; artifacts upload
even on failure, and missing artifacts fail. These are not additional required
PR checks.

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
does not attest to scale success: inspect non-UI performance, both rich workers,
the strict union collector and the whole run.
Omit the flag for ordinary focused CI only.

Once GitHub registers the scale workflow, direct manual execution is:

```powershell
gh workflow run scale-verification.yml --repo geevensingh/org-explorer --ref YOUR_PUSHED_BRANCH -f revision=FULL_40_CHARACTER_CANDIDATE_SHA
```

Blank `revision` uses that workflow dispatch's commit; supplied values must be a
full immutable SHA, not a mutable ref. All execution jobs check that checkout equals the
requested SHA before invoking the runner with `-ExpectedRevision`.

## Active pre-publication integration

`release.yml` calls the reusable scale workflow with the exact source SHA after
successful trusted main CI and product-impact planning. Every product publication
uses the complete hosted workflow described above: non-UI performance, both
isolated rich workers and their strict union. Its execution entry points are:

```powershell
.\eng\Run-Performance.ps1 -ExpectedRevision FULL_40_CHARACTER_CANDIDATE_SHA
.\eng\Run-UiScale.ps1 -Worker prefix -ExpectedRevision FULL_40_CHARACTER_CANDIDATE_SHA -RunId RUN_ID -RunAttempt RUN_ATTEMPT
.\eng\Run-UiScale.ps1 -Worker tail -PrefixResultsDirectory PREFIX_ARTIFACTS -ExpectedRevision FULL_40_CHARACTER_CANDIDATE_SHA -RunId RUN_ID -RunAttempt RUN_ATTEMPT
.\eng\Assert-UiScaleUnion.ps1 -PrefixDirectory PREFIX_ARTIFACTS -TailDirectory TAIL_ARTIFACTS -ExpectedRevision FULL_40_CHARACTER_CANDIDATE_SHA -RunId RUN_ID -RunAttempt RUN_ATTEMPT
```

The two rich invocations belong on separate isolated authorized desktops, not
this shared machine. Use the hosted workflow for artifact transfer and complete
candidate verification, not a single rich invocation.
Expected-revision execution refuses a different SHA, tracked changes or
non-ignored untracked files, and rechecks after assertions. Non-UI exploratory
execution without that argument reports modifications honestly; it is not
candidate proof. Rich workers always require explicit candidate/run/attempt binding.
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
Publication descriptions are observed through the real `Publish-Release` draft
body at that fake GitHub boundary. Real Git fixtures cover three accumulated
mainline changes (including the earlier durable Workspace-save change), literal
Unicode/Markdown subjects, a candidate older than checkout HEAD, and merged work
without WIP duplicates. They also cover an explicit first publication, unchanged
technical/renewal descriptions, failed/metadata publications retaining the
original app baseline, and pre-tag refusal of missing, shallow, non-ancestor,
off-first-parent, empty or unusable-subject history.
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
