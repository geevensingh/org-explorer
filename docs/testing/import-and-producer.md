# Reviewed Import and producer verification

[Return to Testing](../testing.md).

Open this reference when changing reviewed Import, failed-Apply reentry/discard, producer capture, collector leasing or resumable collection policy.

For local Commit/refusal and recovery, read [storage and recovery](workspace-storage-recovery.md). For publication/adoption of accepted batches and complete resources, read [synchronization](workspace-synchronization.md). The [Import format](../import-format.md) and [local collection protocol](../../.github/skills/update-org/collection.md) supply supporting format/source rules. For independent writing, read [Person profiles](person-profiles.md) and [note writing](note-writing.md).

## Producer process, collector policy and fictitious inputs

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

## Captured source evidence and photo verification

`OrgExportTests` covers the local producer's public capture and
Ingest/Profile/Photo seams using fictitious session events and synthetic PNG bytes.
The required core lane builds the bundled producer through the same project reference;
it verifies exact ordered request correlation, native/MCP/error-wrapped
payloads, shared-stream worker isolation, retained partial successes, explicit
incomplete-result blockers, rejection of unverified host-saved references
(including files replaced after completion), and unchanged photo
length/content/hash validation.
It performs no live WorkIQ reads or desktop automation.

## Failed Apply, explicit discard and scoped reentry

The invisible compiled recovery/Import probe admits one preview from the actual
ready-control event and pumps a nested dispatcher while that original handler
is still active. Recovery finishes its refresh before exposing Ready controls,
without a duplicate caller refresh invalidating the next applicable review.
Status-only finalization keeps a failed retry visibly failed, retryable and
read-only with its exact profile draft while the original file remains held.
Ordinary Preview/Apply refresh remains deferred. The existing twenty-second
observations, one request and genuinely changed-revision invalidation case remain.
The automatic-opening recovery fixture requests its one new Preview at the
actual reenabled Open control boundary, then requires Apply to remain enabled
after queued recovery presentation settles within the same 20-second observation.
Recovery drains its pending refresh before returning, rather than leaving an old
invalidation behind the new applicable review. An independent real-folder case
adopts a genuinely different revision after that review and still requires Apply
to become disabled; no stale review is accepted or replayed.

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

Import's own failed Apply is different from independent writing: its picker,
entry, Preview and Apply reuse the workflow's existing scoped `CanEnterImport`
admission. The actual compiled Apply-failure/reconcile/People/reentry/Preview/
Apply loop retains the original error until successful explicit retry and
independently reopens the reviewed result. A separate invalid note still
prevents that admission; no fault is cleared to enter Import.

## Reviewed SQLite batches and held-Apply evidence

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
