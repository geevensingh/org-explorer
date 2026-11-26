# Local Workspace storage and recovery

[Return to Testing](../testing.md).

Open this reference when changing local SQLite Commit, WAL readers, storage observation, paused recovery, receipts or crash reconciliation.

For Create/Open and exit choices, read [opening and exit](workspace-opening-exit.md). For complete-snapshot publication, adoption or conflict retention, read [synchronization](workspace-synchronization.md). For generations and independent writing, read [Person profiles](person-profiles.md) and [note writing](note-writing.md).

## Local Commit outcomes and storage observation

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
[storage protocol](../incremental-workspace-storage-protocol.md)
defines local recovery, independent snapshots and guarded retention.

## WAL read snapshots and physical reader lifetime

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

## Browse-safe reconciliation and blocked replacement

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
For inherited retained-owner mode preservation, read [note reading and context](note-reading-context.md#read-and-edit-presentation).

## Crash boundaries, receipt leases and explicit recovery

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

## Compiled and hosted recovery evidence

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

## Retired-engine coverage reconciliation (#100)

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
