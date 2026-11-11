# Incremental storage safety protocol (proposed, not active)

This is the implementation design for [#38](https://github.com/geevensingh/org-explorer/issues/38),
established by [#41](https://github.com/geevensingh/org-explorer/issues/41).
It supplements [ADR 0007](adr/0007-incremental-workspace-storage.md).
The shipping reader/writer still uses [Workspace version 2](workspace-format.md),
including its existing conversion behavior. This document neither activates a
new format nor authorizes deleting or converting an existing Workspace.

## Internal profile tracer foundation (#42)

The version-3 profile tracer is available only through the internal
`ApplicationWorkflow.CreateIncrementalFileBacked` test factory. The public
factory, desktop and version-1/2 behavior are unchanged. This is not a supported
user format. Independent recovery and reclamation are implemented internally
below; shipping activation is still #47.

`ApplicationWorkflow.IncrementalIndex.cs` implements the current selector and
bounded radix index. A selector contains `schemaVersion: 3`, a `commit` GUID,
the fixed `roots` families (`people`, `profileHistory`, `acceptedBatches`,
`photos`, `teamContext`, `historicalTeamContext`), and the unchanged
`recordVersions` map. A null family root explicitly represents an empty family.
Each version-1 index node identifies its family and contains either `entries`
(normalized logical identity to document location) or hexadecimal `children`,
never both. Identity SHA-256 partitions bound fanout and depth. Leaf buckets
are bounded; a save copies the affected paths, not a population-wide manifest
or an ever-growing delta chain.

`ApplicationWorkflow.IncrementalStorage.cs` resolves and validates the complete
current graph behind the workflow. Person and Profile-history documents retain
record version 2. Each accepted-batch and Team-context document contains one
entry of the existing map representation; Historical team context retains its
version-1 document and Person/Team Markdown stays independently atomic.
Photos have unique physical locations and their existing logical resource
identities; current bytes, rather than a filename hash promise, are validated.

`SaveIncrementally` writes a durable transaction intent before creating
objects. Each new document/index node has its own durable creation inventory
record (relative location, byte length and digest). It flushes and validates
the candidate, rechecks the complete base and producer resources, then publishes
the staged selector through the existing publisher. The accepted revision is
the validated base plus the operation's own bytes, not a post-publication reread.
A post-publication committed receipt is separate from the intent. Receipt
failure reports saved locally with incomplete maintenance and blocks safe close,
rather than inviting the edit to be repeated as if it were unsaved.

Unreferenced staged/retired objects and transactions do not become current data
on reopen. Failed attempts are deliberately preserved for later classification;
the original foundation neither selected recovery nor deleted objects. The
recovery slice below extends this transaction boundary with an independently validated candidate,
post-publication receipt/selection and maintenance-only retry. The delivery
slice owns complete race/out-of-order coverage; the Import slice owns full
multi-Person/resource behavior and batching of index updates. Reclamation consumes
inventories and durable retirement information, not ownership inferred from
paths. Final activation remains blocked on #47.

`WorkspaceFixtures.IndexedDocuments` is the storage-aware inspection helper.
Functional tests still drive the existing workflow/view-model; no desktop
caller or public per-Person repository interprets these paths.

## Independent recovery implementation (#45, internal only)

The internal version-3 workflow now prepares independent recovery before each
profile/Import or independently atomic note publication. The shipping factory
still uses version 2; no migration, recovery UI, deletion or shipping activation
is introduced here.

`recovery\current.json` is a version-1 recovery envelope containing the originating
transaction GUID, exact accepted current-byte fingerprint (`sourceRevision`),
the ordinary version-3 selector (`workspace`), and two additional bounded index
roots (`noteRoots.personNotes` and `noteRoots.teamNotes`). Captured notes are
uniquely named Markdown documents inside those recovery families; materialization
places the selected bytes at their ordinary GUID-named note locations. This is
one selected note state, not user-visible note edit-version history.

Recovery index and document paths resolve only below `recovery`, never to current
files. Every immutable recovery object has an adjacent ASCII SHA-256 `.sha256`
seal. Current document bytes are copied exactly, not reserialized. Changed
documents and affected index paths get new physical files; unchanged recovery
references are reused. Recovery metadata and ownership records may be compact
JSON, as are current index nodes and their inventories; document readability
and externally supplied formatting are preserved.

The candidate envelope is durable at `transactions\<id>\recovery.next`. After
current publication succeeds, `committed.json` records version 1, the transaction
GUID (`commit`), accepted fingerprint (`revision`), and envelope digest
(`recoveryDigest`). Local publications also bind a `publication` record in the
envelope and receipt: the selector/note relative path and before/after byte
digests (a null before digest means creation). Acceptance-only maintenance has
no local publication. Only a matching receipt and complete sealed, semantically
validated closure permit recovery selector replacement. The previous selector
is retained at that transaction's `recovery.previous.json`. The recovery selector,
not the highest GUID or newest file, identifies the protected committed state.
Reopening never selects an orphan candidate, including one matching current.

`RetryStorageMaintenance()` is separate from edit/Import/note retry. In the same
session it completes the already prepared committed candidate only if current
still matches its accepted fingerprint. After reopening or newer valid delivery
it independently captures the completely validated current state in a new
acceptance transaction; it does not reinterpret an unreceipted orphan as a commit.
Accepted external note edits/deletions and other valid current changes therefore
reach recovery without invented Profile changes, Import replay or new note dates.
Recovery-only writes never replace current files or change a pending preview's
current fingerprint. A committed maintenance error survives generic draft
discard and blocks safe close/restart until maintenance succeeds.

### Roots handed to reclamation

The reclamation implementation protects the current `workspace.json`
closure and captured ordinary notes; `workspace.previous.json` during current
replacement; selected `recovery\current.json` and all its ordinary/extra note
index closures plus seals; retained history/evidence/photo resources; and every
unretired transaction. Transaction roots include `selector.next`, `recovery.next`,
`recovery.previous.json`, receipts and all staged objects. Receipt absence is
ambiguous, not proof of abortion. Recovery selection alone releases none of those
roots; the #46 retirement protocol below supplies that separate decision.

Current inventories remain `created-<number>.json`. Recovery inventories are
`created-recovery-<number>.json`, with `path`, `stagingPath`, byte length and digest
written before the corresponding staged payload. Both locations are relative
to the Workspace, including the `recovery` prefix. Recovery documents, nodes and
seal files each have their own record. Selector/receipt stages remain inside
their owning transaction; payloads stage at unique `recovery\<id>.next` paths
recorded in those inventories. No population-wide inventory is rewritten.

## Automatic reclamation implementation (#46, internal only)

The internal workflow runs reclamation after a successful current publication,
committed recovery receipt and validated recovery-selector replacement, while
still holding the same local gate and writer lease. Profile/Import and atomic
note saves use this ordering. The shipping factory remains unchanged.

Before any deletion, maintenance validates actual current bytes and selected
independent recovery, and durably publishes `maintenance\reclamation.json`.
This fixed-size pending plan identifies the selected transaction, accepted
revision and recovery-envelope digest. It is not a new current/recovery selector.
Only the just-completed, still-selected transaction can publish its own
`transactions\<id>\retired.json`: version 1, transaction identity, accepted
revision, recovery-envelope digest, and digests of that transaction's creation
inventories and known intent/selector/candidate/receipt/previous-recovery files.
The record is proportional to the operation, never the population on an ordinary
edit. Existing ownership/retirement records are not rewritten on subsequent saves.

Retirement releases that transaction's candidate and recovery-transition roots,
not any current or selected-recovery objects. Every pass recomputes:

- The complete current six-family closure and ordinary notes, plus the complete
  `workspace.previous.json` index/document closure.
- Selected independent recovery's six families, captured Person/Team notes,
  all seals and its committed receipt.
- Every unretired transaction's inventories (both final and staging locations),
  current/recovery candidates, previous recovery closure/seals and required
  receipts. A missing receipt never classifies an operation as aborted.

An unretired incomplete intent without a recovery candidate can still need
reused base objects. Preserve all object versions in that case, and report
reclamation deferred for preserved unretired transactions. Complete ambiguous
candidates retain their transitive roots. Neither case promotes staging to
current or recovery, and neither is silently classified from today's selector.
Malformed, unsupported, missing or changed structural delivery suspends deletion;
changed retired structural metadata also requires resolution rather than guessing
that its old retirement still covers a newly delivered candidate.

Current inventories now include `stagingPath` as well as `path`, byte length and
digest, durably recorded before payload creation. Current objects stage beside
their unique final locations with `.next`; recovery retains its unique staged
locations and separately inventoried seals. Only byte-identical, length-matching,
inventoried files from durably retired transactions, outside all protected roots,
are deleted. Unknown files, provider conflict copies and changed superseded bytes
remain untouched, even inside transaction directories. Links are rejected.

Deletion runs in batches of at most 32 individual file deletions. Before each
batch and after it, maintenance rechecks the accepted actual-byte revision,
previous/current/recovery selectors, protected bytes/seals and transaction
membership/structural metadata. Providers do not honor the lease: a selector
racing a batch cannot be accepted as a smaller valid state. Subsequent refresh
still requires the complete semantic/resource graph.

Removing an inventory only after processing its payload/stage is incremental
deletion progress; a missing file is idempotent on restart. Changed payloads are
preserved as unknown, not repeatedly reclaimed. Inventories for protected files
remain as ownership evidence. Known transaction metadata is removed only after
its inventories are exhausted and no protected receipt needs it; an empty
transaction directory can then be removed nonrecursively. No broad Workspace,
generation, resource-family or unknown-directory deletion is used.

The pending plan is removed only after the pass revalidates successfully.
Locks/access errors leave it durable, report **saved locally; storage maintenance
incomplete**, and block safe close/restart even after reopening or generic draft
discard. `RetryStorageMaintenance()` uses the existing maintenance-only recovery
acceptance path, recomputes all roots and resumes idempotently; it never replays
the committed profile/Import/note mutation. Current bytes and an unchanged Import
preview remain unchanged by successful maintenance. Ambiguous interrupted work
may therefore retain additional space, explicitly deferred, while ordinary
successful local edits retain active data, independent recovery, one previous
current graph and bounded ownership/transaction bookkeeping. Genuine history
and its resources remain unbounded user data, not cleanup targets.

## Delivery validation and cleanup protection (#44)

The internal reader accepts direct edits to the currently indexed documents,
not only app-produced replacement selectors. Person/history consistency, accepted
Import provenance and decoded photo resources are validated together before the
open view changes. Required photos include initial observations, old/new change
values and evidence, and captured former Reporting-chain profiles, even after
the current photo is cleared. `RequiredIncrementalPhotos` defines that logical
resource closure; physical reachability still follows the validated photo index.
Imported evidence in current history and captured transitions requires its
accepted-batch document. An explicit empty family root cannot erase resources
or accepted evidence that other families still require.

The delivery tests exercise pointer/index/document/history/photo arrival in
different orders. Until all declared and semantically required bytes validate,
retain the last accepted view, mark it stale, refuse writes, and leave the
delivered bytes untouched. Unknown/provider-conflict files are preserved, not
selected as substitutes for missing indexed documents. Same-path photo changes
are detected from bytes even with the original timestamp. Publication callbacks
deliver notes, Person/history pairs and photos before or after actual selector
replacement: the accepted revision remains the validated base plus local writes,
so refresh must still validate the intervening delivery.

For the reclamation slice, suspend deletion when a newly observed selector is
unvalidated, an index or document is missing/unsupported/malformed, a Person and
history pair is inconsistent, required accepted evidence is absent, or a required
image is missing/invalid. Revalidate actual bytes even when index references and
timestamps are unchanged. Objects arriving before their selector are not current
data, but that does not prove they are owned, retired or deletable. Likewise,
staged/retired/unknown objects and provider conflict copies need the separate
provenance and retirement checks below. A last-good in-memory view or an ignored
recovery-only event is not permission to reclaim a competing in-flight graph.
Recovery-only changes remain outside the canonical current revision and must
not invalidate an otherwise unchanged Import preview. These tests do not
implement or activate reclamation, recovery selection or the shipping format.

## Mutation semantics and resource roots (#43)

The internal writer collects changed document locations by family before
updating the index. A reviewed multi-Person Import writes each affected index
path once for that publication, including when many updates share an existing
branch. Unchanged branches and documents remain referenced in place. All
proposed People, relationships, evidence, contexts and decoded resources are
validated together before the single current-selector replacement. Rejected
publication leaves the old graph authoritative; staged documents and images
are not an accepted Import and explicit retry requires a fresh review.

Accepted-batch documents retain the original source envelope, fingerprint and
application time individually. Later profile saves do not rewrite that
collection. An exact accepted replay writes nothing and records no additional
Observations or changes. Omitted fields preserve their values and evidence;
explicit null/false retain their existing field-specific meanings. Unknown
prior evidence remains unknown rather than being reconstructed from envelopes.
Reporting changes freeze the old all-ancestor chain, attributed Team notes
and transition evidence once per affected Person, not intermediate Import
record order. Later profile/note edits cannot rewrite those captures.

Cleanup and recovery must preserve this complete resource closure:

| Root | Required resources |
|---|---|
| Current selector | All six family indexes and every indexed document, including all indexed photo bytes, not just images displayed by active People. |
| Current People and Profile history | Current photo references; every photo Observation; each change's old/new values and current/previous evidence values. Initial Import photo evidence remains a root after replacing or clearing the photo. |
| Historical team context | Every captured chain profile's photo reference, plus the immutable captured note text, ownership, dates, version and causal evidence in the context document itself. Current note files are not substitutes for frozen text. |
| Accepted Imports | Each retained original envelope and application record, with logical imported photo resources retained through typed Profile-history Observations. Producer-relative source paths are evidence, not paths to reopen in the Workspace or cleanup candidates. |
| Independent recovery | Its own complete selected indexes/documents and captured Markdown/images, never physical aliases of current files; recovery receipt and transition roots remain governed by the recovery slice. |
| Transactions and maintenance | Every inventoried staged object/candidate until publication/abort classification and durable retirement permit release under the reclamation protocol. |

Replacing or clearing a photo does not remove its entry from the current photo
family. Resources needed only by history remain readable and indexed after
reopen; the complete reader must reject a graph missing a required historical
resource. This slice does not prune even apparently unused images or authorize
deletion of old physical versions. Reachability is necessary but not sufficient
for reclamation: provenance, recovery publication and retirement are still
required.

Current Person/Team Markdown remains independently atomic at its existing path,
with stable ownership and dates, archival and draft-base conflict protection.
Profile/Import saves do not copy current Markdown or create note edit-version
history. Independent recovery captures those bytes under its separate protocol.
The internal factory remains opt-in; shipping activation is still #47.

## Roots and physical independence

The proposed version-3 layout keeps readable JSON, Markdown, JPEG and PNG files:

| Root | Purpose |
|---|---|
| `workspace.json` | Atomic current selector: format/family versions, commit identity and fixed-size family-root references. No population-sized file list. |
| `index\<id>.json` | Persistent bounded-fanout radix-tree nodes keyed by family and normalized identity hash. Leaves map logical identities to document locations. |
| `documents\<family>\<id>.<extension>` | Uniquely named app copy-on-write People, per-Person Profile history, per-accepted-batch evidence, per-Manager context revisions, per-Person Historical team context, and photo resources. |
| `notes\`, `team-notes\` | Existing independently atomic current Markdown documents and ownership/date semantics. No user-visible note edit-version history. |
| `recovery\current.json` | Atomic selector for the last completely validated, independently stored **committed** recovery snapshot. |
| `recovery\index\`, `recovery\documents\` | Recovery-owned radix nodes and document bytes, including captured Person/Team notes. Never physical aliases of current files. |
| `transactions\<id>\` | Durable intent, staged selector, changed-object inventory, candidate recovery root, and eventual publication/abort receipt. Not a current or accepted recovery selector. |
| `maintenance\` | Incremental ownership/retirement receipts and restartable deletion progress; not canonical user data. |

These are proposed responsibilities, not a requirement for callers to interpret
paths. Storage-aware test helpers may follow the declared format. The existing
`IApplicationWorkflow` remains the functional boundary.

Each radix node has bounded entries and depth (bounded identity-key length),
validated child references and family/key constraints. Updating one leaf copies
only its ancestor path, not every leaf or the complete population. The selector
contains a fixed number of family roots. Splitting accepted batches and Team
revisions prevents unchanged large maps from being serialized on profile edits.
Do not replace this with an ever-growing delta chain that needs periodic
full-population checkpoint writes. Traversing the complete trees for validation
or reachability is permitted; rewriting them on an ordinary edit is not.

App-created document locations are unique, but their current bytes are externally
editable. A Person/history pair must still be semantically consistent; a photo
location is not a permanent content-hash assertion. Fingerprint actual bytes,
including same-path photos. Structural metadata must describe a complete,
version-supported graph; missing nodes/documents are unavailable delivery, not
empty families. Validate containment, links, duplicate identities, graph rules,
history/evidence, accepted envelopes, notes and decoded images as today.

Recovery references point exclusively into its own namespace. Changed current
documents are copied into new recovery files, never hard-linked, reflinked or
shared with current bytes. Unchanged references can be shared between successive
recovery snapshots, but never across the current/recovery boundary. Capture the
actual validated current bytes, not a recomputed version that might lose external
edits. Recovery seals include byte digests so damage to recovery can be detected;
those seals do not prohibit valid direct edits to current documents.

## One transaction, two publication decisions

Let **B** be the complete validated base byte revision, **C** the proposed local
committed state, and **R** the currently selected independent recovery snapshot.
For notes, the state includes the actual note document set and bytes, not just
the profile selector. A note-only commit has its own receipt identifying the
published note and its before/after bytes; it does not pretend the profile
selector changed or add a permanent note-history feature.

1. Acquire the existing in-process I/O gate and exclusive local writer lease.
   Check supported versions before creating the lease. Capture and completely
   validate B; reject a stale preview/draft against its original base. The lease
   coordinates local clients, not the external provider.
2. Flush a transaction intent before creating its uniquely named objects. Record
   each created location and its intended family/ownership in bounded incremental
   inventory records. Keep R selected and untouched.
3. Write only changed current documents and ancestor index nodes. Write/flush a
   candidate independent recovery snapshot of C, reusing unchanged **recovery**
   references. Compare actual captured bytes with R: a previously accepted direct
   external edit must also reach recovery, even when the local operation changes
   a different field. Changed history can legitimately grow; unrelated data may
   not be recopied. Note-only updates change that note's recovery leaf/path.
4. Close/flush every payload and index file to local storage (`Flush(true)`), then
   validate the complete C graph and the independent recovery candidate with the
   normal semantic reader. Verify recovery's seals and independence. Recheck B's
   byte revision and producer resources immediately before publication.
5. Atomically publish the current selector through `IWorkspaceFilePublisher`
   (or the independently atomic note replacement for a note operation). This is
   the local commit point. Before it succeeds, C and its recovery candidate are
   uncommitted, regardless of how complete their staged files look.
6. On success, account for exactly B plus this operation's written bytes. Never
   replace the accepted revision with an unconditional post-publication reread:
   delivery inside the publisher callback must remain detectable afterward.
   Flush a committed receipt tying C's selector/note publication to the prepared
   independent candidate. The receipt is written **after** successful current
   publication; a mere intent, staged pointer or candidate is insufficient.
7. Atomically replace `recovery\current.json` only with that receipted, validated
   candidate. R remains protected until replacement completes. Recovery now
   represents C (the most recent successfully protected local commit), not a
   snapshot of every autosave and not an unaccepted external candidate. Keep the
   prior recovery selector as a transition root until maintenance safely retires
   the transaction. External delivery after C's publication does not turn C into
   an uncommitted state or authorize overwriting the delivered current bytes.
8. Retire obsolete transaction roots durably and run bounded reclamation below.
   Never perform deletion before the replacement recovery selector is durable.

A newly created Workspace follows this protocol with an empty validated base.
An externally delivered valid state may become the accepted current view without
being a local commit. Keep the previous recovery until a fully validated,
independently captured delivered state can be selected with an acceptance receipt;
do not manufacture a local profile change, replay an Import, or record new
Observation dates for this maintenance. The receipt must identify the exact
validated delivery revision, not whatever files happen to exist after copying.

The ordinary post-publication path protects the latest local commit. A crash
after current publication but before its receipt can leave recovery at the
previous committed state. This is intentional and observable: reopening reads
the actual current selector normally; it does **not** promote an orphan candidate
based on its filename, apparent completeness, or similarity to current files.
New maintenance can independently capture the validated current state instead.

## Failure states and user-visible outcomes

| Boundary | Authoritative state and required outcome |
|---|---|
| Intent/current/recovery staging or validation fails before current publication | B remains committed, R remains selected. Reject save; retain drafts/errors and block safe close/restart. Never adopt the staged candidate. |
| Base or producer resources change before publication | Reject without replacing current files. Keep last validated view stale until complete delivery validates; require explicit retry/review/discard as today. |
| Current publication throws | Report unsaved unless the publication boundary positively confirms success; do not infer success from leftover stages. Preserve B/R and unsaved writing. Test actual atomic replacement failures. |
| Current publishes, receipt or recovery maintenance fails | C is committed, R remains valid. Say **saved locally; recovery maintenance incomplete**, not "not saved" or "all protected". Retain a maintenance error and block safe restart/close until retry or an explicit acknowledged leave policy is implemented; never replay the mutation to retry maintenance. |
| Recovery candidate or selector publication is interrupted | Select only a complete validated receipted snapshot. Old R remains protected until replacement; no half-updated recovery tree is accepted. |
| Cleanup fails after C and recovery commit | Keep C and selected recovery; surface a maintenance failure separately from mutation failure. Retry deletion work idempotently, not the edit/Import. Do not silently report successful maintenance. |
| Provider delivers partial, invalid, inconsistent or unsupported data | Retain last validated view and recovery, mark current unavailable/stale, refuse unsafe writes, and do not repair, merge or rewrite delivery. Settings/guidance remains accessible. |

Later implementation must represent committed-with-maintenance-error separately
from rejected save; a Boolean failure that encourages repeating a committed edit
is not sufficient. Preserve retained note/profile drafts and safe-close errors
for genuinely rejected saves. Recovery-only maintenance does not alter current
byte revisions, invalidate an unchanged preview, or raise invented profile edits.

## Reclamation is a reachability and provenance protocol

Under the same local gate/lease, validate the current accepted revision and
recovery selectors before selecting deletion work. Protect the transitive closure
of **all** these roots:

- The complete accepted current index, its documents and all required resources.
- Both selectors during a current or recovery selector replacement.
- The selected verified recovery graph, including captured Markdown and images.
- All recorded Profile history and its old/new photo references, accepted Import
  envelopes and required resources, frozen Historical team context, and its
  captured profiles/notes/evidence. Clearing a current photo is not proof that
  its bytes can be removed.
- Active local transactions and prepared candidates; interrupted transactions
  whose publication/abort state has not been conclusively classified.
- In-progress recovery replacement and maintenance receipts needed to resume
  safely. A transaction root is released only after its durable retirement record.
- Any newly observed, not-yet-validated selector/candidate delivery. Incomplete
  metadata prevents computing its closure: suspend reclamation, do not guess.

Use the app's incremental creation inventory and durable retirement receipts to
identify redundant versions. A file must be app-created, conclusively retired
by successful successor publication **and** independent recovery publication,
outside every protected closure, and still match its recorded bytes before it
can be deleted. Neither "not in today's pointer", a hash-looking filename, nor
file age establishes ownership or retirement. Unknown files, provider conflict
copies, externally changed retired files, and ambiguous interrupted work are
preserved. Never recursively delete an entire generation/root to avoid checking
its contents.

Flush the maintenance plan and retirement transitions first. Recheck the accepted
current revision and selectors before each bounded deletion batch, abort if they
changed, and revalidate after the batch before accepting further provider data.
Record completed deletions incrementally; resume only after recomputing protected
closure. Missing already-deleted files are idempotent progress, not permission to
delete neighbors. Do not retire a still-needed structural node merely because a
newer node exists. Keep ownership/retirement bookkeeping incremental as well;
ordinary maintenance must not write a population-sized inventory/checkpoint.

External providers do not obey the local lease. A pointer can arrive between any
two instructions, and old objects can arrive before or after it. The guarantee
is preservation of the accepted current state and independent committed recovery,
not indefinite restoration of every superseded pointer a provider might replay.
A newly arriving pointer is not accepted while maintenance owns the local gate.
If it races deletion of already retired objects, subsequent validation must keep
the last-good view/R and block writes rather than treating missing objects as a
smaller valid Workspace. Only complete valid delivery may subsequently be
accepted. Resolve competing/obsolete provider versions outside the app; do not
invent provider acknowledgments, distributed locks, automatic downloads, semantic
merges, or a time-based promise that delayed delivery cannot occur.

This distinction is essential: a quiescence delay can reduce provider races but
cannot prove deletion safety. Protect accepted roots and receipts even with a
delay. If the implementation cannot establish the ownership/retirement conditions,
preserve the ambiguous files and report deferred maintenance instead of weakening
them to achieve a disk-size target. Bounded overhead is expected for ordinary
successful local edits, not arbitrary conflicting or perpetually partial delivery.

## Recovery verification and restoration

Validate the selected recovery receipt, index closure, seals and complete semantic
state before reporting it usable. Never edit the protected recovery snapshot to
repair current data. With clients closed, back up the original folder, materialize
the selected snapshot into a separate restore folder (including captured notes
at their ordinary locations), and open that folder with the normal reader. Check
independently expected committed People, history, evidence, notes, relationships
and decoded images, not merely a "recovery ready" flag. This procedure must not
require an uncommitted transaction to finish or a missing current document to be
reconstructed. Installer rollback is not Workspace restoration.

The development workflow exposes `RecoverWorkspace(destinationPath)` for this
procedure. It deliberately reuses receipt/seal validation and the normal semantic
reader instead of introducing a second format-aware export script:

1. Close other clients and preserve the original folder. Use a fresh internal
   version-3 workflow to open the source. An unavailable current selector does
   not prevent the explicit recovery operation from validating its independent
   recovery roots. Never point a writer directly at the protected recovery tree.
2. Resolve genuinely unsaved drafts first. For an open valid current state with
   incomplete maintenance, explicitly retry maintenance; generic Discard cannot
   claim that already committed writing was never saved.
3. Call `RecoverWorkspace` with an empty isolated destination outside the source
   tree. It rejects nonempty destinations and links, verifies selected provenance
   and bytes before and after copying, materializes captured notes, and opens the
   result through the normal workflow. Rejection never edits source/current or
   selected recovery. A failed copy can leave a partial destination; preserve or
   remove that separately rather than retrying into it.
4. Inspect independently expected committed People, relationships, history,
   evidence, notes and images in the opened destination. Its selector retains the
   protected commit identity. The source remains untouched, including damaged
   current files. The destination initially contains restored current data only;
   call `RetryStorageMaintenance()` there to establish its own independent
   recovery before safe close/restart. This does not manufacture another edit.

These are internal development operations until final activation; they are not
instructions to convert a shipping version-1/2 Workspace.

## Deterministic scenarios for the later slices

Drive these through file-backed `IApplicationWorkflow` and isolated folders, using
`WorkspaceViewModel` only for observable draft/compatibility/safe-close behavior.
Keep all file writes, flushes and replacements real. Use the existing publisher
to pause/throw/deliver at publication, and narrow filesystem effects or a bounded
non-UI child process for later maintenance interruption points.

| Scenario / controlled boundary | Independent assertions |
|---|---|
| Rich small fixture, one Preferred-name edit, reopen | Expected Person/history survives; unrelated People/history, photos and batches have zero payload copies in current **and recovery**. Count ancestor metadata writes/bytes, including transient stages. |
| Dozens of small edits, then maintenance | Every expected Profile change remains; recovery restores the selected committed values; redundant objects/nodes are bounded, allowing real history growth. Full-copy-then-delete still exceeds write limits. |
| Kill/fail after intent, payload, node or candidate flush but before current publication | Original committed state and R reopen; no stage is accepted; retained writing/error blocks safe close on non-crash rejection. |
| Fail actual current replacement | Original bytes and independent R remain; explicit retry applies once; rejected Import does not add accepted evidence. |
| Deliver different note, Person/history or same-path image inside publication callback | Local commit accepts only its own expected revision; refresh detects delivery, validates actual bytes, or stays stale; no delivered writing is silently overwritten. |
| Fail after current publication, before receipt or recovery selection | C reopens, previous R restores; candidate is not advertised. Maintenance-only retry never repeats C's history or accepted batch. |
| Interrupt recovery document/node writing and recovery selector replacement | At least previous R remains complete; no partial candidate is selected; failure is visible. |
| Edit/overwrite current referenced documents after protecting C | Recovery bytes do not change; normal-reader restoration gives C, not externally damaged or later edited current bytes. |
| Valid direct Person/history edit, note edit or same-path photo replacement, then local edit | Delivered values survive refresh and reopen; recovery captures changed actual bytes only; unchanged unrelated recovery documents are reused. |
| Pointer first, index first, documents last, missing image, invalid graph/history, unsupported record version | Last-good state retained, unsafe writes blocked, no cleanup of candidate resources; eventual complete valid delivery accepted without invented edits. |
| Pause cleanup, then deliver incomplete/competing selector or resource | Abort/defer when detectable; no accepted root/R/history resources removed; a racing unaccepted incomplete pointer cannot become an accepted smaller population. |
| Fail/kill midway through deletion and resume | C/R restore, required historical resources remain, deletion progress resumes idempotently, maintenance error is explicit, unknown/conflict files remain byte-for-byte. |
| Recovery-only maintenance while Import is reviewed | Unchanged current preview still applies; recovery files do not contaminate the current revision. |
| Fresh version-3 folder; old/future versions presented | Fresh state reopens; incompatible originals/directories remain byte-for-byte, with accessible refusal guidance and blocked writes. No migration. |
| Explicit rich 10,000-Person workload | Representative edit has bounded changed-document/index writes, not full-population writes, with the existing full retained-data assertions. Keep scale outside required small smoke. |

## Observation established by the prefactor

`CreateFileBacked(fileObserver: ...)` accepts an optional
`IWorkspaceFileObserver`. It reports completed payload writes (path/byte count),
successful durable flushes, successful publication (source/destination/backup),
and successful delete calls. Delete includes a completed no-op for a missing
stage. It is not a filesystem mock, a functional repository, an OS-wide tracer,
or a replacement for `IWorkspaceFilePublisher`. An observer must be thread-safe,
must not throw or modify the Workspace, and must accumulate events, not compute
totals from whichever files survive.

Counts are application payload bytes accepted by completed managed writes, not
allocation units, OS metadata, hardware write amplification or unknowable partial
bytes from a throwing write. A returned write remains counted even if its later
flush/publication fails or its file is deleted. Publish is a rename/replace, not a
second payload copy; backup movement is carried in the same event. Lock-file and
directory metadata, app-local preferences, and external-provider/test-publisher
writes are not app Workspace payload. Existing conversion copies/overwrites are
observed only to preserve coverage of today's real recovery path; conversion is
not part of the future format.

Later recovery/reclamation writers must use this same narrow effects boundary;
no new maintenance engine is introduced here. Tests compare observed writes to
independently inspected real staged files, exercise repeated failed note stages,
remove entire failed generation payloads at the publisher, and exercise existing
recovery copies/overwrites. They prove observation survives full-copy-then-delete
without making today's inefficient full-copy count a permanent storage contract.
