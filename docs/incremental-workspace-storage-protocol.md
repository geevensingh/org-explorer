# Local transaction and snapshot safety protocol

The normal shipping path implements [ADR 0008](adr/0008-local-transactions-independent-sync.md).
The prior readable-file selector/index/publication/recovery/reclamation engine,
direct-edit protocol and non-overridable close veto are retired. This existing
document path now describes their replacement; Git history and the superseded
[ADR 0007](adr/0007-incremental-workspace-storage.md) retain the old rationale.
No compatible old reader, dual writer or hidden authority remains.

The [Workspace format](workspace-format.md) defines the exact local SQLite and
interchange-1 artifact shapes. Import version 1, stable Person/note identity,
private Team-name rules and all domain history are unchanged.

## Local commit and recovery

1. Validate the mutation against its immutable owner/record base and current
   coherent local state. Keep profile/note/photo/Import associations atomic.
2. Durably flush a revision-only `local-write-<id>.json` intent and retain the
   writer lease. It binds the actual base and preselected next revision.
3. Perform the SQLite transaction with verified local WAL and
   `synchronous=FULL` on the actual writer. Commit the payload and ordered ancestry together.
4. Acknowledge only successful durable Commit, then retire accessible evidence.
   Keep the writer lease until deletion completes or explicitly fails.
   Reconciliation also retains read/write exclusivity through receipt deletion;
   delete sharing allows this owned retirement without admitting competing I/O.
   Failed cleanup is committed maintenance; it cannot authorize replay.

The oldest outstanding ordinary request includes queueing and scheduling.
Three seconds without completion becomes explicit stalled/not confirmed saved,
not inferred rollback. Retain owner-correct latest generations and pause edits,
not browsing. Late accepted returns cannot clear newer typing, another save's
fault or a later recovery epoch.

An unknown outcome forbids competing retry/adoption until reconciliation of the
original transaction. Reopen validates the whole committed database first.
An exclusive receipt handle plus an exact matching base/next revision establishes
rollback/commit; unavailable, malformed, active or unmatched evidence remains
read-only and visible. A validated committed revision does not prove that the
receipt is currently accessible. No valid local state means explicit recovery
failure, never invented empty data.

Draft bookkeeping/admission locks remain short and contain no storage I/O.
Read-only query snapshots preserve browsing during transactions, bulk Imports
and recovery. Continuous note typing uses a bounded autosave cadence; explicit
profile/Preferred-name drafts are never submitted by navigation or close.

Public independent readers retain coherent old snapshots without preventing a
later writer's Commit. SQLite still serializes writers; the one-second busy
setting, three-second oldest-request stall, unknown-outcome fences and explicit
recovery are unchanged. Compatible format-3 state is fully validated before WAL
negotiation, and a failure never silently selects rollback journaling.
Complete validated cold-open data remains available for read-only browsing if
mode negotiation fails. Its sticky local WAL recovery fault blocks ordinary
write admission until explicit successful reconciliation, without replacing an
independent unknown transaction fault or clearing buffers. Incomplete/invalid
validation cannot enter this recovery state.
Default SQLite checkpoints remain independent of provider work. A confirmed
Commit may live in WAL while an old reader delays checkpointing; live main/WAL/
SHM files stay local and are never copied/deleted as a substitute for logical export.

## Independent publication and adoption

1. Read one coherent committed local transaction.
   This includes committed, uncheckpointed WAL content; checkpoint completion
   is not a publication prerequisite.
2. Package every logical family, complete ancestry and all required resource
   bytes into a self-contained `.oesnapshot`.
3. Validate complete logical state in isolated local staging, durably write a
   unique `.pending` artifact and publish create-only.
4. Verify the actual artifact before recording its revision as published.
   An existing identical immutable artifact permits idempotent retry.

Export never synchronizes an open database, holds an ordinary local writer
through provider work or marks a newer commit published from an older export.
Status distinguishes local durability, publication to this computer's configured
folder and unknown delivery elsewhere.

A single-flight async lifecycle checks incoming versions and publishes
approximately every 60 seconds without waiting for idle. Sync now and normal
close request it immediately; requests during a cycle coalesce into a later
cycle. Folder failure is a synchronization error, not local data loss.

Receive only after strict envelope/version/identity/ancestry/resource and full
domain validation. A clean continuation may install in one local transaction
only when the base is published and no writing/unknown outcome is retained.
Core and view-model admission leases cover request generations, accepted-return
gaps and independent unknown saves even after buffer discard. Sealed adoption
does not perform Commit I/O under the draft gate; actual adoption failure uses
the same local recovery protocol.

Duplicate, stale or reordered delivery cannot roll back state. Partial/corrupt
delivery keeps valid local state usable with explicit synchronization feedback.
No watcher, filename, timestamp or queue completion proves successful adoption.

## Whole-version divergence and reclamation

Divergence preserves local and incoming complete versions, pauses edits and
keeps browsing available. Explicit selection binds to the exact presented
revision/hash inventory, then commits one whole state with a fresh resolution
revision and acknowledged branch identities. New arrivals require a new review;
post-Commit rechecking keeps unseen divergence paused. There is no semantic
merge, last-writer-wins or external-file acknowledgment path.

Chosen and unchosen versions remain separately retained until explicit
revision-bound confirmed deletion. Routine rotation retains current and previous
known-good complete snapshots. Unknown/provider-named files, conflict versions
and in-progress artifacts are not routine garbage. Required photo/history bytes
belong to each artifact; no retained version depends on mutable bytes from
another version.

The folder lease coordinates app clients, not the external provider or ordinary
local commits. Inventoried canonical names/hashes, full validation and guarded
rename/recheck precede deletion. Changed displaced bytes are preserved and
reported. Interrupted reclamation can retain extra files rather than lose data.
Domain history is never routine snapshot garbage.

## Exit and compatibility

Normal close requires latest local Commit and latest local publication, not
remote delivery. A clean published exit freshly checks the authoritative local
head and transaction evidence plus the exact known complete artifact and receipt;
cached Ready alone is insufficient. It need not repeat receive/adoption/export
solely to quit. Changed local heads reload and revalidate; unconfirmed publication
requests the shared real publication immediately and rechecks the latest head.
Failed or unavailable probes, retained writing, unresolved receipts and known
divergence cannot authorize close or clear retained versions/review evidence.
The nonmodal decision states unsaved-local versus
saved-but-unpublished risk. Cancel invalidates that intent without losing drafts.
Exit anyway directly terminates without a secondary guard, worker or Dispose
wait; recovery still distinguishes committed from incomplete transactions.
The signed updater retains its safe-point, unsubmitted-draft refusal and
production-trust/isolation contracts.

Every retired readable-file format and incompatible SQLite version is explicitly
unsupported. Preserve the folder and create a new local Workspace. No migration,
overwrite, deletion, old recovery export or direct backing-file editing ships.

## Verification surfaces

Use the shared public workflow/observable view-model with real storage and the
actual compiled/native WPF surface, never private-table business repositories.
SQLite lock/Commit/evidence interruptions, snapshot publication/adoption holds
and owned process death exercise actual effects. The normal shipping factories
are also the fixture factories.

`SqliteWorkspaceTests`, `SqliteRecoveryTests` and `WorkspaceSnapshotTests` retain
the focused local, crash, resource, two-store, divergence, retention, cadence and
exit contracts. Ordinary/high-resolution desktop retains all owner/Historical/
Team-name and exit matrices. Both explicit scale suites retain the full rich
10,000-Person workload and exact-candidate/run/attempt evidence. WPF action-to-
usable and durable-save percentiles, including real background-export
independence, require hosted Windows or an explicitly authorized isolated
desktop. Local non-UI checks do not establish native or full-scale acceptance.
See [testing](testing.md) for selections, thresholds and unchanged suite budgets.
