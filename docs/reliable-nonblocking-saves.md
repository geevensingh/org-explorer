# Reliable, nonblocking local saves

**Status:** Implemented normal SQLite/snapshot path (#91-#100); final joint
delivery requires fresh normal and explicit full rich-scale hosted verification
of one exact integration candidate. Implementation alone is not acceptance.

This design replaces canonical-file persistence with local SQLite transactions
and separates local durability, interaction responsiveness, and external-folder
synchronization. It covers all ordinary Person and note edits, not just Add Team
note. It does not change note ownership, reporting inheritance, or the meaning of
recorded Profile changes and Historical team context.

See [ADR 0008](adr/0008-local-transactions-independent-sync.md) for the architectural
decision and the [implemented formats/protocol](workspace-format.md).
Its documentation and matching implementation must reach the default branch
together under the [documentation handoff contract](agents/documentation-handoff.md).

## Why replace the design

The reported symptoms are slow Add Team note interactions, saves remaining
pending, and inability to navigate or close while saves are pending. Read-only
inspection of the retired desktop workflow confirmed disabled controls during
background work and vetoes navigation and close for pending work or unsaved
drafts. It does not establish the cause of the reported latency or reproduce a
permanently stalled save.

Replacing the engine does not require a separate root-cause investigation of
discarded storage machinery. The replacement must nevertheless exercise the real
end-to-end interaction paths: changing the database alone will not remove UI
guards, scheduling delays, or draft-lifetime problems. The retired storage,
direct-edit and control-only draft/veto machinery has now been removed rather
than retained as a fallback.

## Local authority and durability

- Each computer has an authoritative local SQLite database outside the
  externally synchronized folder. Never synchronize an open live database, its
  journal, or its write-ahead log as the interchange mechanism.
- The implemented local policy is verified WAL with `synchronous=FULL` on actual
  writers. An independent public reader retains a coherent old version while a
  later ordinary note generation commits and a fresh reader sees it. SQLite
  still serializes writers; the existing busy/stall and explicit recovery
  contracts are unchanged. Default checkpoints may wait behind long readers;
  neither checkpoint completion nor the main file alone defines local Commit.
  Complete logical export includes committed WAL frames. No new exit checkpoint
  or raw database/WAL/SHM interchange is introduced.
- The database is app-managed. Direct external editing of backing storage is no
  longer a supported workflow. Human-readable canonical files are not required.
- A local transaction supplies the commit boundary. Report an edit as locally
  saved only after successful durable commit, not after dispatch, enqueue,
  debounce expiry, or an in-memory update.
- Commits preserve coherent workspace state, including resource associations.
  Readers must not observe a partially applied mutation. Preserve all existing
  domain history; this change does not add an edit-history feature for notes.
- A process crash or Exit anyway must leave committed state recoverable and
  incomplete transactions uncommitted. Do not promise durability beyond the
  actual operating-system and storage-device guarantees.
- Local changes not yet published must remain discoverable after restart so
  publication can resume. A successful export followed by a crash must not
  cause blind replay of a local mutation.

The schema, SQLite access library, treatment of large resource bytes, and exact
transaction/scheduling implementation remain implementation choices. Resource
storage must satisfy the same coherence, recovery, and snapshot requirements.

## Navigation and draft lifetime

Navigation never waits for a save, export, incoming synchronization, or conflict
resolution. This applies during bulk/background work too, even though its
completion times have separate performance criteria.

Adding a note opens a usable editor immediately while persistence proceeds.
Navigation displays the selected Person's usable view rather than just a busy
indicator. Keep drafts and pending creations associated with their actual Person
and note identities across selection changes. Navigating away must neither discard
them nor save their contents against the newly selected Person.

Returning to a note must show its latest retained draft, including edits that
are still unsaved. Late completion of an older save must not overwrite newer
edits or mark those newer edits saved. Coalescing work must not drop distinct
mutations, reorder their meaning, or duplicate a note after retry.

Do not use a global pending-work flag to disable ordinary browsing. Keep the UI
responsive even when an underlying storage operation cannot be promptly canceled.

## Local failure and stalled writes

When a local write fails, retain unsaved drafts, show an actionable error, and
pause new edits until local storage recovers. Browsing remains available. Do not
silently discard, roll back, or report these drafts as saved.

For ordinary saves, measure the stalled-save deadline from the oldest outstanding
save request, including queueing time. Continuous typing must not reset that
deadline indefinitely. If a save has not completed within **3 seconds**, replace
ordinary pending status with an explicit stalled state: its changes are not
confirmed saved. Pause new edits and expose the failure state without blocking
navigation or the exit decision.

A timeout is not proof that a transaction failed. Distinguish an operation known
not to have committed from an operation whose outcome is unknown. Reconcile an
unknown outcome before starting a replacement write, replaying a mutation, or
resuming edits. A later successful completion can resolve a stalled state only
when the correct committed revision and remaining drafts are accounted for.
Allow safe retry after the outcome is known; never run competing retries against
an unresolved write.

On restart, recover a validated local state for browsing when one is available.
Preserve unresolved work/conflict evidence and keep edits paused until safe.
If no valid local state is available, show an explicit recovery failure rather
than inventing an empty workspace or claiming success.

Pending, stalled, failed, and recovered states must be distinguishable and
diagnosable without recording private note contents.

## Independent external-folder synchronization

Support multiple computers used one at a time, with an existing folder provider
such as OneDrive carrying app-managed synchronization artifacts. No new hosted
synchronization service is required.

Three different facts must remain visible:

- **Locally saved:** the edit has durably committed on this computer.
- **Published:** a complete workspace version is available in this computer's
  configured synchronization folder.
- **Delivered elsewhere:** the external provider has transferred that version.
  Local publication alone does not establish this fact.

Exchange complete, consistent logical workspace snapshots, not a stream of
individual edit operations. A snapshot includes all necessary data and resources
for its workspace version. Its packaging and resource-reuse mechanism remain
implementation choices; a complete logical version does not require a new copy
of every unchanged photo.

While the app is open, target background publication and incoming-version checks
approximately every **60 seconds**. Coalesce intermediate edits without relying
on the user becoming idle. Sync now and normal exit request publication
immediately, without waiting for the periodic interval. Background synchronization
must not starve ordinary local commits.

Periodic ticks arriving while the synchronization worker is active coalesce into
that work rather than forcing another complete pass before it can finish.
They neither set nor clear an explicit follow-up request. Sync now during active
work still requests a follow-up for the latest local writing/incoming check.
New writing not included in an older publication remains unpublished and is
picked up by explicit synchronization or the next scheduled tick after the
worker completes. The fixed 60-second schedule is not restarted by typing,
completion or activity; this does not require the user to become idle.

The interval is an app scheduling target, not a provider-delivery SLA. If export
cannot keep up or the folder is unavailable, show the actual state and retry
safely rather than claim successful synchronization. Slow or failed
synchronization alone does not pause local editing.

## Receiving versions and resolving divergence

Incoming delivery can be partial, reordered, duplicated, stale, or corrupt.
Validate completeness, integrity, format compatibility, and the complete logical
workspace before adoption. A directory event or apparently finished filename is
not proof that all required bytes have arrived.

Keep the last validated local workspace usable when an incoming version cannot
be accepted. Do not partially install it, silently repair it, or replace local
state with a successful-looking empty fallback.

Use explicit workspace identity and version ancestry, not timestamps alone, to
distinguish a continuation from divergence. Do not roll back to stale versions
or mistake duplicate delivery for a new edit.

Automatically adopt a fully validated continuation when there are no unpublished
local edits or unsaved drafts. Preserve the selected Person and navigation state
where possible. Coordinate adoption with local work so it cannot overwrite a
draft or race a new local commit.

If computers have produced divergent versions:

1. Preserve both versions, including the local work involved in the conflict.
2. Pause new edits, but keep navigation available.
3. Require an explicit choice of which complete workspace version to continue
   using. Do not silently use last-writer-wins or perform semantic merging.
4. Preserve the unchosen version for recovery until explicit deletion.

Conflict resolution must bind to the versions actually presented. New arrivals
must not turn an earlier choice into authorization to overwrite unseen work.
Selecting a version is not permission to delete the other one.

## Exit behavior

Normal exit requires all current edits to be locally committed and the latest
local workspace version published to the synchronization folder. It does not
wait for confirmed delivery to another computer.

If either local saving or publication remains incomplete, present a responsive
exit decision that explains the actual condition and offers **Exit anyway** and
**Cancel**. Normal save/publication may continue while the user waits. An export
of an older revision does not satisfy the latest-version requirement.

Distinguish locally unsaved edits, which may be lost, from locally saved but
unpublished edits, which remain on this computer but may be absent elsewhere.
This publication requirement deliberately replaces the earlier interview choice
to let synchronization-pending exits proceed without confirmation.

Exit anyway must actually exit, including when a write is stalled, publication
fails, or a conflict is unresolved. No secondary draft, pending-work, or
safe-close guard may silently veto the confirmed decision. Do not depend on a
hung worker returning before honoring it, and do not weaken transactional
recovery to implement it.

Cancel keeps the app open and preserves drafts and visible save/synchronization
state. If Exit anyway leaves committed work unpublished, resume publication
from durable local state on the next launch.

## Retention and compatibility

Bound routine synchronization snapshot retention to the current and previous
known-good complete versions. Retain divergent/conflict versions until the user
explicitly deletes them, including after choosing which version to continue
using. Such explicitly retained versions can consume additional storage.

Reclamation must not remove data required by the local workspace, retained
snapshots, in-progress publication/adoption, or conflict recovery. Do not delete
unknown files or provider conflict copies, or interpret removal of an old export
as deletion of the workspace. Resource reuse must not make retained versions
dependent on mutable bytes from a newer version.

Snapshot retention is not domain-history retention. Keep all recorded Profile
changes and Historical team context within the workspace.

Use a distinguishable new storage/interchange format. Migration of the existing
workspace is not required: the owner accepts recreating it. Preserve the existing
folder; do not silently convert, overwrite, or delete it. Reject incompatible
formats explicitly instead of partially opening them for writes.

## Acceptance evidence

Use the existing rich 10,000-Person fixture as the representative workload,
including its notes, photos, and Historical team context. Extend coverage to the
real Add Team note, editing, and navigation paths rather than substituting a
database-only benchmark.

For healthy local storage under ordinary editing load, measure separately:

| Outcome | Target |
| --- | --- |
| Add Team note opens a usable editor | Informational p95; historical 100-ms reference, not a pass/fail gate |
| Person navigation displays a usable selected view | Informational p95; historical 100-ms reference, not a pass/fail gate |
| Ordinary edits become locally durable | p95 goal at most 1 second; temporary note enforcement exception below |
| Outstanding ordinary save becomes explicitly stalled | After 3 seconds without completion |

The owner approved making editor/navigation timings informational on
2026-11-20 so functional validation can finish without further performance work.
Retain every raw sample and the historical reference; do not relabel earlier
failed runs. Exact usable views, writable/focused editors, owner-correct writing
and bounded functional observations remain required.

On 2026-11-27 the owner approved increasing note-durability acceptance after
observed 1,247-ms first-commit and 1,878-ms rich-note measurements. The selected
**temporary enforced ceiling is 2,000 ms** for ordinary small-fixture first
commits and native small/rich Person/Team-note p95, including normal-executable
and held-background-export phases. This supersedes the earlier 1,050-ms rich
ceiling. The **1,000-ms goal remains**; a p95 above that
goal and at or below the temporary ceiling is accepted without claiming the
one-second goal was met. Optimization is deferred for later review, not replaced
by informational-only durability or an automatic further increase. Preserve raw
samples and nearest-rank p95, all 21 commits observed during continued typing,
exact owner/data/reopen and lifetime assertions, and the original suite budgets.
The predetermined typing windows span the full two-second ceiling, without
waiting for a save or stopping early: 36 inputs at nominal 60-ms spacing in the
small native journey and ten at 250 ms in the rich journey. The public first-commit
fixture likewise retains writing for 36 iterations at 60 ms. Profile-save
one-second enforcement, the queue-inclusive three-second stall, held-adoption
capture proof and all worker budgets are unchanged. Two seconds retains margin
before the production stall deadline; this is not a change to save scheduling.

The local-durability target includes save scheduling and queueing, and applies
while typing continues, not only after typing stops. Sync/provider delivery time
is not part of local-save latency. Bulk import and initial loading have separate
timings; their existence is not permission to freeze browsing.

Record the environment, workload, sample count, timing boundaries, and percentile
calculation. A spinner, optimistic status change, or queued task is not a passing
interaction or durability measurement. A baseline from the old engine is useful
but is not a prerequisite for implementing the replacement.

Exercise at least these behavior and failure cases:

- Repeated note creation, continuous typing, immediate Person switching, and
  returning to pending drafts without misassociation, duplication, or lost edits.
- Slow/failed local writes, a never-completing write, and late completion after
  the 3-second deadline; browsing and the exit decision remain responsive.
- Local crash/reopen around transaction boundaries and after local commit but
  before publication; only committed edits are reported saved, and pending
  publication can resume.
- Unavailable synchronization folders and long exports during editing; local
  commits remain independent and meet their healthy-storage targets.
- Partial/corrupt, duplicate, stale, and reordered delivery; only validated
  complete continuations are adopted.
- Sequential use across two local stores, stale-computer divergence, explicit
  version selection, and retention of the unchosen version.
- Every close state, including unsaved edits, locally saved/unpublished work,
  stalled work, and unresolved conflicts; Exit anyway is never vetoed.
- Routine snapshot/resource reclamation without loss of retained workspace data,
  conflict versions, or domain history.

Follow [the testing contract](testing.md): prefer non-UI local checks, select
scale coverage explicitly, and use hosted Windows or an explicitly authorized
isolated desktop for desktop interaction evidence. Do not infer desktop latency
from synchronous workflow timings alone.

## Implementation and documentation delivery

Replace the old persistence machinery rather than retaining it as a hidden
second authority. Preserve the existing domain behavior while updating the
dependent workflow, desktop state/guards, synchronization, recovery, and tests.
Implementation must update the workspace-format, storage-protocol, design, and
user-facing documentation that would otherwise describe the retired mechanism.

This planning change ships no application behavior. Its focused documentation
commit must stay pushed and unmerged until incorporated into the matching
implementation. Establish the integration owner and receiving branch before
implementation or child dispatch, verify incorporation of the immutable handoff,
and deliver the documentation and working replacement together.
