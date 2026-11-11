# Workspace format, version 3

The Workspace is a chosen local folder, not installation data or an Import file.
The normal desktop/workflow creates and opens version 3. Older versions (including
1 and 2) and future versions are rejected **without mutation**, before creating a
writer lease. There is no automatic conversion, downgrade or legacy cleanup.
Preserve the old folder and explicitly choose a new empty folder to create a
Workspace; do not delete the owner's data. Import version 1 is independent and
unchanged.

`workspace.json` is the atomic current selector:

```json
{
  "schemaVersion": 3,
  "commit": "14000000-0000-4000-8000-000000000000",
  "roots": {
    "people": null,
    "profileHistory": null,
    "acceptedBatches": null,
    "photos": null,
    "teamContext": null,
    "historicalTeamContext": null
  },
  "recordVersions": {
    "people": 2,
    "profileHistory": 2,
    "acceptedBatches": 1,
    "personNotes": 1,
    "teamNotes": 1,
    "photos": 1,
    "teamContext": 1,
    "historicalTeamContext": 1
  }
}
```

This illustrates an empty Workspace, not a population-wide manifest. Each nonnull
root names a version-1 bounded radix index below `index\`. A node has a family
and either up to 16 identity/location `entries` or up to 16 hexadecimal `children`,
partitioned by normalized-identity SHA-256. Missing indexes/documents never mean
an empty family. Unknown Workspace/family/record versions block writes; missing,
duplicate or malformed metadata is unavailable data, not permission to guess.
The [storage safety protocol](incremental-workspace-storage-protocol.md) specifies
containment, indexes, transactions, recovery and reclamation in detail.

Current documents have unique locations below `documents\<family>\`. App saves
replace only affected documents and ancestor index nodes; unchanged documents
remain referenced in place. There is no full-generation writer or activation
flag. These physical locations are not domain identities or immutable content
hash promises: valid direct external edits remain supported.

Person documents retain record version 2, stable email, Directory name, Preferred
name, Alias, job title, department, office building/number/state/country,
LinkedIn URL, explicit Manager or null, archival, favorite and photo state.
Canonical properties include `directoryName`, `preferredName`, `alias`, `jobTitle`,
`department`, `officeBuilding`, `officeNumber`, `officeState`, `officeCountry`
and `linkedInProfileUrl`; Import `displayName` maps to `directoryName`.
Office identifiers remain text; unknown optional values stay unknown.
Email inside the record is authoritative. All textual values and the complete
reporting graph are validated on load as well as edit/Import.

Each accepted-batch document retains one entry of the version-1 map representation:
batch identity, logical fingerprint, original source envelope (producer and
Observation times) and local application time. Each Team-context document
similarly retains one Manager's version-1 review-revision entry.
Profile-history documents retain version 2, `email`, `changes` and `observations`.
Changes retain typed old/new values, accepted evidence and nullable prior evidence.
Evidence includes the supplied field/value, source (`0` Import, `1` local edit),
source `observedAt` (null for local edits), local `recordedAt`, batch/producer.
Initial creation is a baseline; same-value explicit Imports add evidence without
inventing a change. Missing prior evidence stays unknown, not reconstructed from
timestamps or envelopes. External Person/history edits must be mutually consistent.

Person `photoPath` is a logical SHA-256-named `.png`/`.jpg` resource identity,
resolved through the photo-family index to ordinary copied image bytes. Images
must be contained and decode successfully; damage is an error, not a placeholder.
Fingerprint actual bytes even if timestamps and paths did not change.
Import-batch identity includes the producer image's exact digest.
All indexed images and images required by history/evidence/captured former chains
remain roots after replacing or clearing a current photo. Producer source paths
are evidence, never Workspace resource locations to reopen or reclaim.

**Settings and upgrade guidance** remains accessible before open or after
incompatibility/unavailable data. `IApplicationWorkflow.Compatibility` exposes
NotOpened, Compatible, Unsupported, RecoveryRequired or Unavailable, supported
and observed versions and explicit guidance. **Retry Workspace validation /
storage maintenance** revalidates, then completes maintenance for a compatible
current Workspace; it never converts incompatible bytes. Restart receipts live outside
the Workspace. Normal restart uses this same reader/refusal policy, and a failed
open never replaces the remembered successful folder. Installer rollback is not
Workspace rollback. See [the update protocol](update-protocol.md).

## Observation dates and the timeline

The shared workflow exposes typed changes, all recorded evidence, and current
field-specific confirmations. The desktop timeline orders changes by recording
order, not by source dates, and labels imported Observations separately from
local edits and application times. Same-value older evidence remains reviewable
but does not replace a newer confirmation of that value. Explicitly accepting a
different value from older evidence records the difference and staleness; it does
not silently reject the user's reviewed choice.

A Manager change may show an observed interval after the prior Manager
confirmation and by the new Manager Observation only when those source times
are ordered. Equal/older source dates and missing prior source evidence show
detection time with an unknown effective date. These are evidence bounds, never
claims of an exact transfer date. Office-only evidence does not reconfirm the
Manager. Previews warn about older supplied evidence, including evidence that
predates a local edit; exact accepted replays remain warning-free no-ops.

## Commit and recovery

### Ordinary local saves

Under the process-local I/O gate and short exclusive `.workspace.lock`, validate
the complete current byte revision and proposed state. Providers do not honor
that lease. Flush durable intent/individual creation inventories before payloads,
write only changed documents and affected bounded index paths, and prepare
independent recovery. Flush every write and validate the candidate graph and
producer resources before atomically replacing `workspace.json`. Retain
`workspace.previous.json` as a protected transition root. Multi-Person Imports
publish one coherent selector, never intermediate graph states. Exact accepted
replay writes nothing. Current Person/Team Markdown remains independently atomic
at its stable GUID location; it does not gain user-visible edit-version history.

The accepted revision is the validated base plus the operation's exact written
bytes, not an unconditional post-publication scan. Delivery inside the publisher
boundary therefore remains detectable. Fresh complete byte fingerprints include
the selected current index/document closure and ordinary notes, not recovery,
transactions or maintenance. Read snapshots never fall back to uncaptured bytes.
Handles allow external write/delete sharing and close before local publication.
The process-local gate coordinates read lifetimes, not synchronization or merges.
Watcher callbacks defer rather than occupying workers behind that gate.

Incomplete/invalid delivery retains the last valid view, marks it stale and
blocks writes without repairing provider files. Validate all required history,
accepted evidence, notes, context and decoded resources, including same-path
changes. Missing indexed data cannot be silently accepted as a smaller population.
Unknown/provider-conflict files are not substitutes for required documents.
Stale previews/drafts require renewed review; Apply rechecks source/photo bytes.
Numeric Import version 1 representations accepted by preview (such as `1.0`)
still reopen and replay; Workspace and record header rules remain independent.

### Independent committed recovery

`recovery\current.json` selects a complete independently stored snapshot, with
its own bounded indexes/documents and captured Person/Team Markdown. No current
file is physically aliased, hard-linked or shared with recovery. Unchanged
recovery references may be reused across recovery snapshots only. Every recovery
object has a SHA-256 seal. Capture actual validated bytes, not reconstructed data.

Recovery selection requires a durable receipt binding the accepted revision and
candidate envelope to successful current/note publication, plus complete
seal/semantic validation. An intent, newest GUID or complete orphan candidate is
not a committed snapshot. If current publication succeeds but receipt/selection
fails, report **saved locally; storage maintenance incomplete**, retain the prior
recovery and block safe close/restart. `RetryStorageMaintenance()` protects the
already committed current state without replaying edits/Imports, reconfirming
notes or inventing dates. Generic draft discard cannot erase this obligation.
After reopen, maintenance independently accepts completely validated current
bytes; it does not adopt an unreceipted orphan. Recovery-only maintenance does not
invalidate a preview of unchanged current data.
The existing Settings validation/maintenance action provides this explicit retry
after a committed save failure or validated external delivery.

### Bounded reclamation

Only after durable committed recovery selection may maintenance retire its
transaction and reclaim redundant objects. A durable pending plan survives
interruption. Protect exact validated current/previous/recovery closures,
receipts, retained history/evidence/photos, and every unretired in-flight
transaction. Delete only individually inventoried, durably retired,
unreferenced files whose bytes still match ownership digests, in batches of at
most 32 with selector/root guards before and after each batch.

Unknown files, provider conflicts, changed retired bytes and ambiguous interrupted
work remain untouched. Malformed or incomplete structural delivery suspends
deletion. Absence of a receipt is not proof of abortion. Retry recomputes roots
and resumes idempotently; locked/access failures remain explicit and block safe
close. No broad directory deletion, legacy-generation cleanup or time-based
provider-acknowledgment assumption is permitted. Genuine history and its required
resources are retained user data, not redundant storage.

### Restoration

With other clients closed, back up the source. `RecoverWorkspace(destinationPath)`
validates the selected recovery receipt, seals and complete semantic graph, copies
it to an **empty isolated destination outside the source**, materializes captured
notes at their normal paths, and opens it through the normal reader. The source
and protected recovery are never repaired or edited, even if current is damaged.
A failed copy may leave a partial destination; do not retry into it. The restored
folder initially contains current data only; explicitly run maintenance there to
establish its own independent recovery before safe close/restart. This is a
workflow operation, not a new recovery UI or an old-format migration.

Unsaved-write failure remains different from committed-maintenance failure:
retain the actual draft and visible error until explicit retry or deliberate
discard. Refresh/unrelated successful saves cannot acknowledge another failed
operation. Invalid files and incompatible versions are never repaired or
downgraded. Stages are not automatically applied after restart. Resolve competing
provider versions externally; installation/upgrades must not remove this folder.
Dispose file-backed workflows before releasing fixture/folder ownership: watcher
reads finish under the same gate and cannot reenter after disposal.

## Private Person notes

`notes\<note-id>.md` holds one UTF-8 Markdown Person note. Notes are outside
the indexed profile graph so editing or archiving a note replaces only that document;
the app does not create a full note edit-version history. Multiple documents may
belong to the same Person, including an Archived person. Reporting relationships
never inherit Person notes.

Each document starts with JSON front matter between `---` lines:

```markdown
---
{
  "schemaVersion": 1,
  "kind": "person",
  "id": "30000000-0000-4000-8000-000000000001",
  "ownerEmail": "avery@example.com",
  "title": "Meeting context",
  "archived": false,
  "createdAt": "2026-11-01T17:00:00+00:00",
  "updatedAt": "2026-11-01T17:00:00+00:00"
}
---
# Meeting
Private **writing** about Avery.
```

The nonempty UUID and email identify the note and its Person independently of
names or Manager changes. Keep the filename equal to the UUID plus `.md`; do not
rename it, change its ID, or omit metadata. Dates are UTC. Title, writing, and
archival state may be edited with local tools while retaining valid metadata.
Unknown owners, properties, unsupported versions, missing metadata, conflicting
copies, and linked/junction note paths are errors, not silently repaired files.
These Workspace documents are not Import batches. Imports still reject all
Person/Team note payloads and note operations.

Autosave flushes a staged `.next` file and atomically publishes the Markdown
document under the same short Workspace lease. An archive changes metadata but
retains all writing; **Show archived notes** makes it readable and restorable.
There is no sharing endpoint, upload, or app-hosted backend. Device and external
sync-account permissions govern access.

The open workflow watches real local folder events for Person, note, photo, and
Workspace-pointer changes, coalesces event bursts, and retries incomplete delivery.
It validates a complete before/after-fingerprinted Workspace, including scalar
formats, stable identities, the full reporting graph, note ownership, and decoded
photos, before publishing one coherent current state to the UI. No restart or
manual reload is required. Invalid/unsupported/missing data retain last valid
information with an explicit **STALE / unavailable** warning and blocked writes.
Only a subsequent complete valid delivery removes that warning. Removing a note
document is a valid external removal; removing a referenced Manager is not a
new reporting root. Provider conflict copies in canonical Person/note locations
are reported, not merged, renamed, or repaired.

Workspace readers permit external write/delete sharing, including clients
waiting on a failed or incompatible open. They do not hold read leases that
prevent an owner/provider from replacing incomplete bytes with a valid delivery.
Before/after byte fingerprints and complete validation establish the accepted
view; a concurrently changed or invalid read is unavailable/stale and retried,
not rewritten.

Ordinary compatible-version refresh is read-only: it does not replay accepted imports, invent Observations,
or write current documents/history. Own-save event bursts match the accepted byte
revision and cause no refresh notification or recursive save. The watcher is
disposed when the client closes or switches Workspaces. Local edits invalidate
old import previews. Save errors keep the draft visible and explicitly **NOT
saved**; leaving/closing is blocked until retry succeeds or the owner deliberately
chooses **Discard unsaved draft**. Resolving competing versions remains the
external provider/owner's responsibility; the app does not merge them.
If an external note version arrives while a failed draft is retained, retry
cannot overwrite it implicitly; the owner must discard that draft before editing
the delivered version. Valid external profile/photo changes can still be shown
without erasing the visible unsaved note draft.

The reading view renders CommonMark headings, emphasis, lists, quotations, code,
and links as native WPF text. Markdown images are labeled **Image not loaded**,
including local references; raw HTML is inert text. No image, stylesheet, embedded
object, or remote document is fetched. Only a deliberately clicked HTTP/HTTPS
link launches the user's external handler; other URI schemes are inert.

## Private manager-owned Team notes

`team-notes\<note-id>.md` contains an explicitly opted-in Team note. It uses the
same UTF-8 JSON-front-matter/Markdown layout as Person notes, with `kind: "team"`
and the additional required `confirmedContextRevision` UUID:

```markdown
---
{
  "schemaVersion": 1,
  "kind": "team",
  "id": "90000000-0000-4000-8000-000000000003",
  "ownerEmail": "avery@example.com",
  "title": "Reporting subtree context",
  "archived": false,
  "createdAt": "2026-11-01T17:00:00+00:00",
  "updatedAt": "2026-11-01T17:00:00+00:00",
  "confirmedContextRevision": "00000000-0000-0000-0000-000000000000"
}
---
# Context
Private **writing** for Avery's current formal descendants.
```

The UUID is a local review marker, not an Observation/effective date, shared team
identity, or note-version history. Keep it when editing with external tools;
external text edits do not silently confirm a warning. Use the app's confirm/edit
actions to deliberately acknowledge current applicability. Ownership always stays
with `ownerEmail`, even if that Person stops managing reports or is archived.
The zero revision is the initial baseline. The version-1 Person/Team-note encodings and ownership/date semantics are unchanged.

The committed `teamContext` index maps Person emails to individual version-1
revision-map documents containing opaque context revision UUIDs. A new revision is committed atomically with a role
(job title/department), archival, or complete Reporting-chain change. All affected
descendants' context revisions change for an ancestor move; ordinary office/name
changes and exact batch retries do not. Notes whose confirmation differs from
their owner's current revision remain visible but need review, even after a
change is reverted. These markers are canonical review state, not reconstructed
history. An absent marker means the initial baseline; later commits preserve
existing markers.

Confirmation or editing saves the note's current owner revision without changing
ownership. Archival retains writing and confirmation state while excluding the
note from current inheritance; restoration does not implicitly confirm it.
Imports may change context revisions but never supply or rewrite note bodies.
Invalid imports and failed saves never publish review changes.

Both note directories and the committed Team context revisions are loaded inside
the same whole-Workspace validation and before/after fingerprint boundary as
profiles, photos, accepted batches, and Profile history. Invalid/incomplete
delivery retains all last-valid writing and context with the Workspace's
**STALE / unavailable** warning; Team-note writes and review actions are blocked.
Valid external writing and review metadata refresh owned and inherited views
without reopening, confirming notes, recording history, or rewriting provider files.
Staged/flushed document replacement, conflict refusal, unsaved-draft safety,
metadata validation, and private Markdown rendering apply to both kinds.
`IApplicationWorkflow` exposes explicit Team-note create/query/edit/confirm/archive
and inherited-context queries, with `InheritedTeamNote` carrying the owning Person.
The existing note-refresh hook refreshes the complete Workspace coherently;
`RefreshTeamNotes` uses that same workflow. No provider-specific sync adapter,
per-record public repository, permanent-team model, or sharing endpoint is added.
These mutable current documents never serve as frozen history.

## Immutable Historical team context

Each document selected by the `historicalTeamContext` index contains
`schemaVersion: 1`, `email`, and a `contexts` array for a Person with captured
former contexts. A capture contains a UUID `id`, stable `personEmail`, local UTC
`capturedAt`, the former `reportingChain`, frozen `teamNotes`, and causal
`transitions`. Chain members retain their old Person values for identity,
attribution, and coherent old Manager links; they are not live current profiles
or references to current photos.

A direct or ancestor-chain transition captures once per affected existing active
Person, comparing the complete accepted old population with the complete final
proposed population. Newly created people establish a baseline, even when their
Manager appears later in the same batch. Exact accepted replay, case-only
references, same-value confirmations, office/name edits, and role-only changes
do not manufacture transfers. Role/context review markers still follow
the `teamContext` family independently. Reversion and subsequent moves preserve
separate captures rather than overwriting or reopening an earlier one.
Explicit active-to-archived status changes also close the Person's old context,
even without a Manager change. A combined Manager/status change produces one
capture with both causal changes, never intermediate captures. A root archival
can have an empty Reporting chain and no inherited notes. Archived creation is a
baseline, not a discovered prior context. Reactivation resumes the current valid
chain without reopening earlier captures or inventing a transfer while archived.

Each frozen note retains `id`, `ownerEmail`, `title`, `markdown`, UTC
`createdAt`/`updatedAt`, its `confirmedContextRevision`, `needsReview` at capture,
and `version`. The version is SHA-256 of the canonically serialized identity,
title/body, note dates, and confirmation revision, not a full edit-version
history or an actual-transfer timestamp. Review status is retained separately,
not used to change that document-content version. Only active Team notes known
on the former chain are captured, including visible notes needing review;
archived notes no longer contribute inherited context. Person notes never
inherit or enter the capture. Empty writing/absent Team notes do not erase the
former Reporting chain.

Transitions attribute each changed Manager edge to its Person and retain the
existing structured `ProfileChange` and `FieldEvidence`, including previous
evidence, import/local source, source Observation, local recording time,
batch identity, and producer. For ancestor moves, the causal ancestor's Manager
evidence supplies dates; an unaffected descendant's Manager is not reconfirmed.
Ordered source dates can bound that edge's observed interval; missing/equal/older
evidence uses detection information with unknown effective date. Multiple changed
edges have separate evidence bounds, not a guessed common effective instant.
Captured note text is **locally known at processing time**, not guaranteed to
have been the writing at the unknown actual transfer instant. File timestamps
are never used to fill unknown evidence.

An archival closure adds a transition for the captured Person with `field:
"archived"`, Boolean `oldValue: false`/`newValue: true`, and matching accepted
evidence. It is not a changed Manager edge or proof of departure. Imported
closures retain the source Observation and local recording time; in-app closures
retain local-edit provenance with no source Observation. The UI labels these
**Archival closure**, showing detection information rather than a guessed exact
real-world effective date. Validation requires the closure to belong to the
captured Person, not an ancestor, and permits at most one transition per
Person/field. Existing Manager-only version-1 captures remain readable; this uses
the same history document shape, not a new import/Workspace version.

The workflow exposes read-only frozen values through `GetHistoricalTeamContexts`.
WPF presents **Historical team context** and its read-only rendered writing
separately from current inherited Team context. Later note edits, confirmations,
archival, owner renaming, or new imports cannot rewrite earlier captures.
Historical Markdown uses the same private renderer: images and raw HTML do not
load content, and links launch only on deliberate selection.

Captures publish under the same atomic selector as profiles, Profile history,
accepted batches, and Team-context revisions. A failed commit publishes none of
them. The bounded index declares historical documents, so withheld files
are incomplete delivery, not silently missing history. Opening/automatic refresh
validates frozen identities, chain coherence, note attribution/content versions,
and causal evidence before accepting the complete byte revision. Invalid
delivery retains the last valid current/history views with the common STALE
warning and blocked writes. Compatible-version refresh is read-only: it loads delivered captures
without another transition, note confirmation, or replay.

An empty historical family means no captured prior context, not fabricated
pre-import history. The next transition captures the actually known old chain
and notes; missing prior Manager evidence remains unknown. Review markers and
the partial Import contract are unchanged; imported note operations remain rejected.

## Person archival and reactivation

Person `archived` is retained independently of note archival. A status change
never deletes the Person, removes a note document, or drops their referenced
photo. Current indexed photos, structured history, historical captures,
accepted batches and Team review revisions are committed together through the
same save boundary as imports and explicit Manager changes. Failure publishes
none of the proposed status/context changes; local retry/discard and safe-close
state still apply. External refresh loads validated delivered status/history
without recording another closure or implicitly reactivating anyone.

`IApplicationWorkflow.ArchivedPeople` exposes the retained-profile projection,
while `ReportingRoots` always excludes archived people. `EditPersonArchived`
and `EditPersonManager` validate the complete proposed graph and record local
history through the same incremental workflow used by Import apply. Active
reports require active Managers; unknown references and active cycles are
errors. Reports must be explicitly reassigned, cleared or archived by the owner
or a reviewed Import batch before Manager archival is accepted. No inferred
cascade, replacement Manager or permanent-delete operation exists.

Archived profiles expose their last stored Manager for inspection but no
current inherited Team context. Their owned writing remains available. Boolean
omission in an Import record preserves the existing status and its field
confirmation; absence of a Person is also nonmutation. Explicit false is
validated before rejoining the current org. The desktop archived filter is
navigation over retained profiles; Search and favorites also open these profiles.

## Local Search and favorites

`IApplicationWorkflow.Search` performs trimmed, case-insensitive substring
queries over the accepted local Workspace. Empty queries return no matches.
Stored Directory/Preferred names, email, Alias, job title, department,
office/geographic fields, LinkedIn URL, Manager email, photo reference and
Boolean profile values are searchable. Person and owned Team notes include
title and Markdown text, including retained archived writing. Current inherited
Team matches use the same all-ancestor query as profiles; Person notes never
inherit and archived notes never contribute current inheritance.

Each result identifies its Person, source and current/archived/historical
applicability. Notes carry their owning Person and stable document ID; archived
notes are explicitly labeled as not current writing. Historical results identify
the exact immutable capture snapshot and frozen note, retaining former-owner
attribution and review status. Activation rechecks the query against validated
state; it does not dereference a possibly changed live capture or note version.
Former-chain attributes and structured Profile changes' old values are
searchable too. The desktop opens the appropriate profile/note, a specific
read-only frozen capture, or the matching Profile change with its existing
Observation/detection provenance. No live note lookup substitutes for frozen
writing. A historical match on an archived Person carries both distinctions.

`FavoritePeople` is derived from canonical `isFavorite`, includes explicitly
labeled archived profiles, and is sorted by profile heading. `EditFavorite`
marks/unmarks locally through the same atomic profile/history save boundary.
Import omission preserves favorite state and its field evidence; explicit false
removes it after preview/apply. New people default to false. Replay remains a
no-op, including after later local favorite edits. Favorite-only changes do not
manufacture reporting captures or Team-review revisions.

Read-only query snapshots are published only from complete accepted state.
Their sorted population, Reporting tree, report/owner lookups and substring
search entries are rebuilt lazily after validated changes, not on every profile
selection or keystroke. Current-status labels are still refreshed independently.
These private in-memory projections are never stored as authoritative indexes,
and queries do not wait on document/index staging or file decoding. WPF requeries after validated mutations and
external Workspace notifications. Invalid delivery keeps the last valid
results/favorites explicitly **STALE retained**, blocks writes, and never
publishes unvalidated text. Failed favorite saves retain the saved star/list and
offer retry/discard before safe navigation/close. Querying does not write files,
replay imports, manufacture history, fetch remote content, or upload private
information. Search-match Markdown uses the same private native renderer.
