# Local Workspace and snapshot formats

The normal desktop and `ApplicationWorkflow.CreateFileBacked` Create/Open path
uses local SQLite. There is no activation flag, readable-file authority, dual
writer, direct-external-editing path or hidden fallback.

**Settings -> Create new local Workspace** chooses a fresh app-managed folder
under `%LOCALAPPDATA%\OrgExplorer\LocalWorkspaces`. **Open Workspace...** can
open an existing compatible local Workspace or create one in an empty chosen
folder. Known OneDrive roots, network shares, folder links and installation
locations are rejected for authoritative storage. Keep it outside any other
synchronization provider too; the app cannot discover every provider.

Remembered startup/update reopening is existing-only: it never recreates a
missing folder or invents empty state. A failed open does not replace the last
successful startup preference. Presentation preferences remain device-local,
separate from portable Workspace data and update settings.

## Local SQLite format 3

`local-workspace.format` contains the immutable discriminator
`OrgExplorer local SQLite Workspace`. It identifies the format, not a second
population authority. The local SQLite database is the sole committed-state authority,
with SQLite application ID `0x4f455850`, local format 3, a Workspace identity
and committed revision. In WAL mode this includes committed frames in
`workspace.sqlite-wal`, not just the `workspace.sqlite` main file.

Creation writes the discriminator before the database so interrupted creation
cannot become an empty fallback. A missing discriminator/database, corrupt
state, incompatible version, or mixed `workspace.json`/SQLite authorities is an
explicit open failure. Retired readable-file formats (including readable-file
version 3) and earlier staged SQLite formats are unsupported. Preserve the old
folder and create a new empty Workspace. No conversion, overwrite, deletion,
downgrade or accumulated legacy-folder cleanup is performed. Import version 1
is a different contract and is unchanged.

SQLite uses verified WAL mode, `synchronous=FULL`, foreign keys and transactions.
Each actual connection verifies FULL, and ordinary connections refuse non-WAL
operation. Compatible existing format-3 data is fully validated before WAL is
established. If that negotiation is unavailable after complete validation,
including on a cold open, the full validated graph/resources remain browsable
but explicitly read-only with a retained WAL error. Ordinary writes, automatic
retry and normal exit stay blocked until explicit successful reconciliation;
a later background/manual refresh alone cannot clear that fault. Independent
unknown transaction/receipt faults remain independent. Invalid or unsupported
data still fails closed, not as an empty or read-only fallback.
Unsupported formats are not converted. Success means `Commit` completed, not dispatch/enqueue, timer
expiry or an optimistic view-model value. Creation, reviewed Imports, profiles,
photos, favorites, archival, Manager changes and note edits commit coherent
state with the new revision. OS/storage-device durability limits still apply.
Do not synchronize or externally edit the live database, WAL, SHM or receipts.

SQLite's default automatic checkpoint and last-connection cleanup are used.
Older readers retain a coherent snapshot without excluding a later ordinary
writer. They can delay a checkpoint and grow the WAL; that does not undo a
confirmed local Commit. Other writers, disk/I/O failures and crash recovery may
still report busy/failure/unknown outcomes and require the existing recovery
protocol. No custom checkpoint worker or exit-time checkpoint is added.
Never copy only the main file or manually delete WAL/SHM as a backup/repair
technique. Logical export reads committed state through SQLite, including
uncheckpointed frames; only complete `.oesnapshot` files enter the provider folder.
The bundled native engine is pinned to the supported `SQLite` NuGet package
3.54.1, including SQLite's 2026 WAL-reset fix and its
[Windows mapped-SHM recovery correction](https://sqlite.org/src/info/7c103a0b7d).
The latter invalidates the SHM header with a checked eight-byte write when the
first exclusive dead-man-switch owner cannot truncate an old mapped section,
then recovers the index from the original WAL. It is not an application Open
retry or a blanket suppression of I/O errors. The existing wrappers, format 3,
WAL/FULL policy and default checkpoint ownership are unchanged.

### Logical data and validation

The local store retains complete Person profiles keyed by stable email, accepted
Import envelopes and logical fingerprints, Profile observations/changes, Person
and Team notes, Team confirmation/context revisions, frozen Historical team
contexts, and required photo bytes.

Favorites adds `favorite_order` to SQLite format 3 without changing the
directory-exchange format. Its single
row has `singleton=1`, `format_version=1` and a JSON `sequence` of stable Person
emails. An absent table denotes a supported unordered store: after complete
validation and local recovery, initialize A-Z in one evidenced local transaction.
While WAL negotiation or an original receipt blocks persistence, retain the
validated A-Z projection and private content for read-only browsing. Explicit
recovery completes initialization before enabling writes. An existing table
requires that row and the complete, valid sequence, including `[]`; missing,
duplicate, unknown, non-Favorite or incomplete identities are rejected, never
alphabetized as recovery. Save revalidates the stored sequence inside the same
transaction as membership, Import or order changes. Initialization rechecks its
expected absence before writing; snapshot adoption revalidates the local order
before replacing any content. Older local Workspace
formats remain unsupported and are not migrated.
Confirmed SQLite writer-lock noncommits during initialization likewise retain
the validated read-only projection and defer persistence to explicit recovery.
Invalid state and unresolved outcomes are not treated as successful initialization.

Synchronization metadata's `snapshot_format=2` records ordered publication
support. An absent column or value 1 denotes a supported pre-transport store.
After full validation and local recovery, initialize that marker once with a
fresh evidenced continuation revision, preserving an already saved arrangement
and all private content. This avoids changing the bytes of an already published
interchange-1 revision. A pristine, empty, unconfigured or pending-Join seed initializes its
empty Favorites order (if absent) and transport metadata atomically without
advancing its unpublished revision, preserving Join eligibility. This also
applies to supported format-3 seeds with neither ordering nor synchronization
tables; it does not change any published snapshot.
Read-only WAL failures and unresolved transaction evidence defer the upgrade,
retaining validated browsing and the existing explicit recovery requirement.
Unsupported marker values refuse opening/writing, including commit-time
revalidation; they are not silently downgraded.

**Favorites ordering (#142 and #143):** retain the saved
Favorite sequence as private Workspace state, using stable Person identity
rather than displayed names. A workspace without saved ordering initializes it
once from its existing A-Z displayed-name sequence, preserving relative order
among equal names. A saved empty sequence is still initialized state, not a
reason to alphabetize again.

Membership changes and completed reorders commit coherently with the local
Workspace state. New Favorites append; removing one discards its position.
After another local client completes a failed Favorite change, a successful
unchanged retry retires only that change's failure, without a new revision or
Profile history. Independent failed writing remains retained.
Renaming or archiving a Person does not move them. A failed reorder save leaves
the previously committed sequence authoritative and reports the failure.
Validated complete snapshots must retain the saved order together with Favorite
membership, including across restart and external sync. Divergent arrangements
follow the existing whole-snapshot choice, with no order merge or independent
per-device preference.

Ordinary Person Import cannot supply a custom order. It preserves existing
Favorites' relative order and appends newly favorited people from that batch in
A-Z displayed-name order, retaining input order for equal names. Existing
Favorite omission/false and accepted-batch replay semantics remain unchanged.
Ordinary Person export continues to exclude private Favorite status and order;
complete-snapshot sync is distinct from that exchange. See the
[product behavior](design.md#favorites-ordering).

Explicit profile/Preferred-name Save always reads the actual SQLite head and
format. If that revision still matches the already validated local state, Save
reuses that state instead of loading/validating the whole population again.
A failed head read is not cached success; a changed revision fully reloads and
revalidates. The write transaction rechecks the head before mutation. Immutable
draft bases, staged-photo/source validation, Team-name eligibility and
unknown/paused receipt recovery remain required.

Opening checks SQLite integrity before exposing data. Person text, complete
reporting graph, history/evidence consistency, accepted-Import provenance,
note identity/owner/title/UTC timestamps, capture versions and resource
associations must all validate. Missing data is an error, never permission to
open a smaller population, repair data or substitute empty state.

Photos are decoded JPEG/PNG bytes keyed by SHA-256 plus their decoded extension.
Current profiles, all retained Profile observations/changes and frozen
former-chain profiles retain their required bytes after replacing/clearing a
photo or removing the selected producer file. Hashes, decoding and associations
are validated. Routine snapshot rotation does not prune domain photo/history
resources from the database.

Imports preserve omission/null/false semantics and whole-batch graph validation.
All accepted data, resources and history commit together. Preview changes to
the base, JSON or producer-photo bytes require a fresh review. Exact original
and later accepted-batch replay remains a no-op after subsequent edits; a reused
batch identity with different logical data is rejected. Notes and Team names
cannot be supplied, overwritten or cleared by Import.

Team names retain private owner values, including dormant labels. Applicability
and ordered owner-attributed paths follow the current non-archived reporting
graph: an eligible selected Manager's own name precedes ancestor names,
duplicates have distinct owners and unnamed gaps retain higher context.
Visibility/editability requires an active owner with current active direct
reports. Save rechecks eligibility. Label-only changes create no Profile
changes, Team-note review revisions or frozen Team-name history.

### Drafts, failures and local recovery

The shared workflow and observable view model retain immutable owner/note bases,
stable pending-creation IDs and latest generations independently of selection.
Person browsing never waits for a save, Import, export, adoption or divergence
decision. Returning shows the exact latest owner writing. Late accepted returns
acknowledge only their submitted generation; newer typing remains pending.

Note autosave continues on a bounded cadence while typing continues. Explicit
profile and Preferred-name drafts require their own Save/Cancel and are never
submitted by blur, navigation, Settings or generic close/update draining.
Profile Cancel/discard does not clear unrelated writing or storage recovery.
Storage I/O is not performed while holding the short draft bookkeeping gates.

An ordinary request outstanding for three seconds, including queueing and
scheduling from the oldest request, becomes explicitly stalled/not confirmed
saved. Further typing cannot postpone that deadline. Failed or unknown local
outcomes retain drafts and pause new edits, not browsing. Validation rejection
is distinct from a failed local storage device.

Each local transaction durably flushes `local-write-<id>.json` before changing
payloads. Revision-only format-1 evidence binds its identity, base and intended
next revision without note contents. The writer holds a lease through commit
acknowledgment and receipt deletion, releasing it only after retirement succeeds
or reports an explicit failure. Reconciliation likewise retains read/write
exclusivity through deletion. Delete sharing permits retirement through the
owned handle; it does not admit a competing reader or writer. This prevents
a public reopen from acquiring the receipt between lease release and deletion.
This is local recovery evidence, not another authority or a
synchronized artifact.

Timeout is not rollback. A replacement write or adoption cannot compete with an
unknown original outcome. Explicit **Retry** recovery validates the committed
database and reconciles the original transaction before permitting retry.
An exclusively accessible intent is retired only when the validated revision
establishes that exact commit or rollback. Active, malformed, unmatched or
unavailable evidence remains visible and read-only, including a real sharing
refusal; elapsed time or a known commit alone does not establish receipt
availability. Known commit with failed cleanup is committed maintenance, never
permission to replay.

On reopen, validated committed data remains available for browsing while
unresolved evidence pauses editing. No valid local state means an explicit
recovery failure, not a successful empty Workspace. Unsaved editor buffers are
not durable restart data.

## Complete logical snapshots (interchange 2; supported legacy 1)

Configure a separate, non-nested provider-carried folder in Settings. Multiple
computers are supported one at a time. A new empty local Workspace may explicitly
**Join folder**, then **Sync now**; a pristine seed identity is replaced only
after complete validation.

An immutable `<workspace-id>-<revision>.oesnapshot` owns a complete logical
version and every required resource byte. It is not a SQLite database copy.
The strict JSON envelope contains `format: "OrgExplorer complete logical Workspace"`,
`version: 2`, uppercase `sha256` of decoded `content`, and base64 `content`.
SHA-256 establishes completeness/integrity, not authenticity.

Decoded content has `workspaceFormat: 3`, `workspaceId`, `revision`, ordered
root-through-current `ancestry`, and complete arrays:

| Member | Retained data |
|---|---|
| `people` | `{key,value}` Person profiles, including private dormant Team names |
| `history` | Complete Profile observations, confirmations and changes |
| `acceptedImports` | Original accepted envelopes, fingerprints and application times |
| `teamContexts` | Owner/context revision values |
| `historicalContexts` | Complete frozen former-chain/note/transition evidence |
| `notes` | Scope, ID, owner, metadata and full Markdown |
| `photos` | Immutable references and base64 resource bytes |
| `favoriteOrder` | Exact private sequence of stable Person emails, including initialized `[]` |

Interchange 2 requires `favoriteOrder`: every current Favorite appears exactly
once, with no duplicate (including case variants), unknown or non-Favorite
identity. Missing/null ordering, invalid values and membership disagreement are
explicit failures, never a legacy initialization or alphabetical fallback.
Publication reads membership and ordering from the same committed SQLite
transaction; adoption installs both in the same local transaction. Order-only
delivery preserves navigation, and repeated sync/reopen retains that exact order.

Supported interchange 1 is unordered and must not contain `favoriteOrder`,
even null. Its fully validated population initializes A-Z by displayed name,
preserving equal-name relative order from the incoming population. Initialization
and a fresh ordered continuation revision commit in the original adoption
transaction; the original incoming revision remains in ancestry and its artifact
is not rewritten. An initialized empty interchange-2 sequence does not initialize
again. Choosing a retained legacy version likewise initializes its complete
selected state in the existing fresh resolution transaction. Future interchange
versions remain unsupported. Invalid delivery retains the last valid local
order, membership and private content with a separate synchronization error.

Optional `resolvedRevisions` records exact branch revisions acknowledged by a
deliberate whole-version choice. The chosen data remains intact; its ancestry
gains a fresh resolution revision. A known rejected revision is stale; an unseen
descendant remains divergent. There is no semantic merge or timestamp-based
last-writer-wins.

Local synchronization metadata records the configured folder, join/divergence
state, published revision, ordered revision lineage and acknowledged branches.
Each local note/profile/Import commit appends its new revision in that same
transaction. Export/hashing is not per-edit work. Durable committed versus
published revisions rediscover unpublished work after restart without mutation
replay. A lineage mismatch fails closed instead of resetting ancestry.

### Publication, receiving and scheduling

Export reads a coherent committed transaction, validates the complete logical
version/resources, durably writes a unique `.pending` artifact, then publishes
create-only. An identical existing artifact permits idempotent retry, including
interruption before its publication receipt. An older export never acknowledges
a newer local revision. Local commits remain independent during export.

Three facts remain distinct: **locally saved**, **published to this computer's
configured folder**, and **provider delivery elsewhere unknown**. Publication
is not proof of transfer to another computer.

The observable view-model lifecycle runs publication and incoming checks
approximately every 60 seconds, starting at open/configuration rather than the
latest edit. Sync now and normal exit request the same single-flight async
lifecycle immediately. Concurrent requests coalesce into one subsequent cycle.
Disposal stops future timer requests without awaiting an active export.

Incoming validation checks envelope, format, identity, ancestry, full logical
state/history and decoded resources in isolated staging before one atomic local
installation. Partial, corrupt or incompatible delivery leaves the valid local
Workspace usable with a separate synchronization error, not stale or edit-paused.
Duplicate/stale/reordered delivery cannot roll back current data. A file event,
filename or apparently complete folder is not a receipt.

Automatic continuation adoption requires a published local base with no retained
writing or unresolved outcome. Selection/navigation is preserved where possible.
Draft-admission leases cover core and view-model request/generation lifetimes,
including accepted-return gaps, canceled/discarded buffers with unknown outcomes
and later typing. Short commit admission is sealed under the draft gate; no
commit I/O runs under it. A non-base lease protects independent unknown saves
until actual reconciliation. Recovery epochs fence late returns. Ordinary
success or unrelated Cancel cannot release another operation's protection.
Incoming Commit failure enters the same local transaction recovery path.

### Divergence and retention

Preserve both complete versions on divergence, pause edits and keep browsing.
Settings **Review complete versions / retained recovery** presents exact local,
incoming and retained identities/counts. **Continue using selected complete
version** selects one entire state, not fields, through a fresh revision-bound
review. New arrivals invalidate the decision; arrivals during commit are
rechecked and retained. A durable paused marker survives restart until the final
check succeeds.

Chosen and unchosen conflict artifacts remain in `snapshot-conflicts` after
selection and later ordinary publication. Only explicit fresh, confirmed
**Delete selected retained recovery** removes an unneeded retained copy, never
the active version or unresolved conflict. Unknown/provider copies are untouched.

Routine publication retains current and previous available known-good complete
snapshots. Adoption preserves the validated local base even if its old export
was removed. Removal of an export is not deletion of a Workspace.
`snapshot_inventory` binds canonical artifact names and hashes to the configured
folder. Cleanup excludes retained conflicts, unknown/provider-named files,
pending writes, newer continuations and divergence. Each artifact owns its
resources; routine cleanup never prunes Profile changes or Historical contexts.

A per-folder `.orgexplorer-snapshot-lease` coordinates app publication/adoption/
review/reclamation, not ordinary local commits or the external provider.
Candidates are revalidated, renamed uniquely to `.retiring`, and checked again
before deletion. Changed/provider bytes and interrupted retirement remain
preserved with an error. This does not serialize noncooperating providers.
Local `snapshot-*` directories are disposable validation staging, never
accepted recovery or a second local authority.

## Close and determined exit

Normal close drains autosaved notes and immediately requests publication of the
latest committed version. It requires actual latest local/published revision
equality, not success of an older sync task or provider delivery elsewhere.
Unsubmitted profile/Preferred-name drafts are not autosaved by close.

A nonmodal live decision distinguishes locally unsaved writing that may be lost
from saved-but-unpublished work remaining on this computer. **Cancel** invalidates
that exact close intent while retaining drafts/status; late work cannot close the
canceled window. New writing needs its own commit and publication.

**Exit anyway** directly terminates the process without secondary window/editor/
updater vetoes or worker/Dispose waits. It does not manufacture storage success:
incomplete SQLite transactions roll back; committed state/evidence must still
validate on reopening. Unpublished work and unresolved divergence remain
discoverable without replay. With no synchronization folder, normal publication
cannot complete; configure one or make the informed Exit anyway choice.
The signed updater keeps its existing safe local-drain/restart/refusal contract.

## Observation dates, notes and Historical reading

Person identity, partial Imports and Manager-owned Team scope are unchanged.
Person notes never inherit; every active Team note on the current formal chain
contributes, not just an ongoing designation. Ongoing choices and exact remembered
views are device-local preferences, never portable domain data.

Initial Import creates a baseline, not pre-Import changes. Explicit same-value
updates retain observations without inventing changes. Omission does not
reconfirm a field. Imported source Observation, local recording time and
unknown real-world effective time stay separate. Ordered Manager evidence can
bound an interval; missing/equal/older evidence cannot establish an exact date.

Direct or ancestor reporting moves and active-to-Archived closures capture the
coherent former chain and locally known active Team-note versions once per
affected Person, using complete old/final graphs. New/Archived creation,
reactivation, accepted replay, label-only edits and unrelated attribute changes
do not manufacture transfers. Empty chains/no-note captures remain meaningful.

Captures retain exact IDs, former owner profiles (without Team names), causal
Profile evidence, note IDs/title/Markdown/UTC dates, confirmation revision,
captured review state and content-version digest. Later rename/edit/confirmation/
archival/Import cannot rewrite frozen content. WPF keeps collapsed, read-only,
copyable **Capture details** separate from frozen Markdown, including complete
provenance, exact identities/versions, owner/review labels and former-chain
navigation. Local capture time is not the unknown actual transition instant.
The full compiled/hosted responsive disclosure and Team-name matrices remain.

## Restoration

Preserve the local folder before manual repair, replacement or uninstall.
Installer rollback changes binaries, not Workspace data. A valid local database
uses transaction reconciliation; a preserved complete snapshot can join a new
empty compatible local Workspace. Divergent retained versions require explicit
whole-version review. Do not copy selected private database tables, reinterpret
retired canonical-file recovery selectors or overwrite an existing folder.

See the [storage safety protocol](incremental-workspace-storage-protocol.md),
[testing strategy](testing.md), [update protocol](update-protocol.md) and
[accepted design](reliable-nonblocking-saves.md).
