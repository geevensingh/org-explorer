# Workspace format, version 2 (supported conversion from version 1)

The Workspace is a chosen local folder, not installation data or an import file.
`workspace.json` now holds `schemaVersion: 2`, a `generation` identifier, the
relative generation `files` list, and required `recordVersions`. These are data
versions, independent of the app build, import contract, or future update protocol.

```json
{
  "schemaVersion": 2,
  "generation": "14000000000000000000000000000000",
  "files": ["batches.json"],
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

This illustrates a pointer, not a populated Workspace. Each existing generation
Person and Profile-history document additionally requires its own
`schemaVersion: 2`. Version 1 used implicit versions for those records. The new
headers and mandatory family declarations are a **breaking storage conversion**:
version-1-only clients refuse the pointer instead of writing the new format.
Person/Team-note front matter and Historical-team-context headers retain version
1; accepted Import envelopes retain import version 1. JPEG/PNG bytes, accepted
batch maps, and Team-context revision maps retain their version-1 encoding, now
explicitly declared by the pointer. Observations and typed Profile changes belong
to the versioned Profile-history document; frozen profiles/notes/evidence belong
to the independently versioned Historical-team-context document.

Versions are checked both on open and validated external refresh, and again before
any write/creation of a writer lease. Unknown Workspace versions, family names,
family versions, or individual record versions are upgrade blocks, not conversion
candidates. Missing/invalid metadata is unavailable data, not permission to guess
an older encoding. Every declared
file must arrive before a generation is accepted, so a delayed Person, Team context, or history
document cannot silently look like a complete smaller population or a legacy
history-free Workspace. The list describes locations, not content hashes;
valid same-path external edits remain possible. Older version-1 pointers without
the list remain readable with their documented legacy unknown-evidence semantics.

**Settings and upgrade guidance** is globally accessible with no Workspace,
unsupported data, or conversion failure. `IApplicationWorkflow.Compatibility`
reports NotOpened, Compatible, Unsupported, RecoveryRequired, or Unavailable,
the supported Workspace version, observed data version when known, guidance,
and a validated originals path when available. WPF shows upgrade/recovery
guidance and offers retry validation/conversion without enabling blocked edits.
The authenticated updater consumes this global status without requiring a
compatible loaded Workspace. Before handoff, the desktop flushes pending local
drafts and `IApplicationWorkflow.RestartWhenSafe` waits on the existing local
work gate, rejects failed saves, and supplies the actual opened Workspace path.
The restart receipt is outside the Workspace. Reopening after an update uses
these same conversion/recovery rules; installer rollback is not data rollback.
See [the update protocol](update-protocol.md).

`generations\<generation>\people\` contains separate indented UTF-8 JSON Person
records with stable email, Directory name, Preferred name, Alias, job title,
department, office building/number/state/country, LinkedIn URL, and explicit
Manager or null, plus archival and favorite state. Archived records retain their data but are
excluded from the ordinary reporting tree. Canonical camel-case properties are `directoryName`,
`preferredName`, `alias`, `jobTitle`, `department`, `officeBuilding`,
`officeNumber`, `officeState`, `officeCountry`, and `linkedInProfileUrl`.
The import contract's `displayName` maps to canonical `directoryName`.
Canonical `isFavorite` is a Boolean; omission in older version-1 Person records
means false. It is saved alongside the profile, not in a second favorite store.
Office identifiers stay text and missing optional attributes in existing
version-1 records remain unknown after conversion; adding headers does not
replace identity or reconstruct values. All textual fields are validated when loading as well as when edited
or imported. Filenames are SHA-256 hashes of case-normalized emails, avoiding Windows
filename restrictions. Email inside the record remains authoritative. The same
generation has `batches.json`, recording accepted batch IDs, logical hashes,
original validated source envelopes (including Observation times and producer),
and local application times. Source evidence is retained for subsequent slices
rather than discarded when the initial tracer does not yet display history.
The same generation's `profile-history\` contains separate readable per-Person
history documents, with the same email-hash filenames. Each version-2 document contains
`schemaVersion`, `email`, `changes`, and `observations`. A structured change retains `field`,
typed JSON `oldValue`/`newValue`, `previousEvidence` (or null when unknown), and
accepted `evidence`. Evidence retains the explicitly supplied field/value,
`source` (`0` = import, `1` = local edit), source `observedAt` (null for local
edits), local `recordedAt`, and optional `batchId`/`producer`. Initial creation
records supplied evidence but is a baseline, not a discovered change.
Identical explicit imports record evidence without adding a change; omitted
fields do not record evidence. Notes do not acquire an edit-version history.

Older version-1 generations without `profile-history\` remain readable. Their
unknown previous field confirmations are not reconstructed from file dates,
accepted envelopes, or guessed local edits. Subsequent changes retain null
prior evidence and show detection information rather than invented intervals.
External Person changes with recorded confirmations must deliver consistent
Profile history too; inconsistent partial delivery is stale, never repaired by
inventing evidence. Automatic refresh loads delivered history without recording
another local edit or reapplying an accepted batch. History is committed in the same generation as profiles, accepted batches, and
other Workspace resources, so a failed save publishes none of the change.
Photo changes use the canonical content-addressed photo reference as their
old/new value, not a producer path; local photo edits and clears share the same
history recording and atomic-save boundary.
Person notes are
separate documents described below, not imported Person fields.

Person `photoPath` is null or an app-created hash-named filename (SHA-256 of the initially copied
image bytes plus `.png` or `.jpg`), resolved in the same generation's `photos\`
directory. These are ordinary copied JPEG/PNG files, not producer paths, remote
URLs, or embedded base64 in Person JSON. Opening validates containment, image
content; damaged supplied or Workspace images are
errors, never successful placeholder substitutions. A generation carries the
currently referenced images forward, so later edits and export-folder removal
do not break profiles. Older generations keep recoverable originals when a
current photo is cleared or replaced.

The filename is a local location, not an immutable integrity claim: external
tools/providers may replace valid image bytes at that same location. The open
client validates and decodes the replacement, fingerprints the actual bytes,
and replaces its displayed image even when the path and timestamps did not
change. Import-batch identity still includes the producer image's exact SHA-256;
changing producer bytes under a previously accepted batch ID is still rejected.

All supported fields live in the canonical readable files. Derived profile
headings are not another identity or override layer. Import version, Workspace
version, and SDK/app version are independent concerns.

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

### Automatic supported conversion

The only supported older Workspace version is **1 → 2**. Conversion runs
automatically, without a separate approval prompt, on open or complete validated
external delivery. It first validates the complete original state, including the
reporting graph, accepted envelopes, note ownership/dates, decoded photos, Profile
changes/Observations, review revisions, and frozen chain/note/evidence consistency.
Incomplete provider delivery, malformed records, conflict copies, unknown fields,
and unsupported versions are **not** rewritten as migrations or repairs.

After acquiring the short local writer lease and checking the source byte
revision, conversion durably copies original files and empty directories to
`recovery\<attempt>\original\`. The copy includes the original pointer, generation
files, Person and Team Markdown, photos, history, and existing prior generations.
App-owned recovery attempts and the ephemeral writer lease are excluded from
recursive copies. The copied originals are revalidated and their fingerprint must
match the original revision before they are advertised as recoverable.

A separate staged Workspace at `recovery\<attempt>\converted\` gets a fresh
generation, version-2 Person/history headers and a version-2 pointer. All other
fields and payloads are preserved: names/preferences, archival/favorite state,
note IDs/text/dates/archival, image bytes/references, accepted batch identity,
Observations, typed changes/provenance, review markers, and immutable frozen
Historical team context. Missing legacy evidence stays unknown. Migration does
not replay imports, reconfirm fields/notes, record a transfer, or invent dates.
Notes keep their current version-1 encoding and live locations; conversion does
not rewrite those mutable originals.

The **actual normal Workspace reader** validates staged output, not just its
version headers. Only then is the fresh generation copied/flushed into the live
folder, where the normal reader validates the actual copied generation and live
notes once more before publication. The source fingerprint is checked again, excluding that app-owned staged
generation, before atomically replacing the pointer. The previous pointer and
generations remain. A failure before publication never replaces the original
pointer or advertises the staged state as loaded; app-created orphans are not
automatically adopted. Recoverable originals are never edited by the app.

Conversion failure shows recovery guidance and the validated originals path when
available. Resolve a local access/locking failure and retry opening, automatic
refresh, or **Settings → Retry Workspace validation / conversion**. If backup
creation itself failed, original live files still remain authoritative; an
incomplete copy is not advertised as a validated backup. For deliberate rollback,
close the app, back up the entire folder, copy a verified `original` snapshot to a
separate restore folder, and use a compatible client. Do not modify the recovery
snapshot in place. Resolving provider-created competing files remains external.
**Installer rollback does not restore Workspace data.**

Recovery artifacts are not canonical current data and are excluded from refresh
fingerprints; no retention deletion currently runs. Preserve them with the
Workspace when managing storage externally.

### Ordinary local saves

A mutation writes a new immutable generation and flushes every file to local
storage before atomically replacing `workspace.json`. The previous pointer is
preserved as `workspace.previous.json`; earlier generations remain recoverable.
No retention cleanup runs yet. A short exclusive `.workspace.lock` coordinates
local app writers, not external folder providers. Fingerprints detect Workspace
changes since opening; stale previews and saves are refused instead of merging
competing state. The UI revalidates the selected import bytes and every
referenced image's bytes on apply. Accepted-batch logical fingerprints include
image SHA-256 values, so unchanged path strings do not authorize changed images. Local
mutations also invalidate prior previews. A graph batch publishes one coherent
new generation, never intermediate Manager/report updates.

Live validation fingerprints the pointer, **the complete pointed-to generation**
(including its declared history/photos), and current note documents, not every
recoverable or uncommitted generation accumulated by earlier saves. Earlier
generations remain untouched and recoverable; changing recovery-only files does
not invalidate review of unchanged current data. Migration-original preservation
explicitly fingerprints and copies **all** generations, as before.

A validated read captures file bytes once for parsing/version/history/note
validation and computes a fresh full byte fingerprint afterwards. The cache
exists only inside that read; it is not a timestamp shortcut or an authoritative
index. Every unique supplied/loaded photo path is still contained, decoded and
hashed; repeated references do not repeatedly decode the same image during a
single validation. Apply revalidates those resources. Generation-file staging
uses bounded parallel I/O, but every write is flushed and all workers finish
before pointer publication. An I/O failure still publishes none of the mutation.
Unchanged Person/history documents retain their validated original bytes in
independent, flushed files in the new generation, avoiding repeated parsing and
format expansion. No hard links or shared mutable originals are used. The live
Workspace fingerprint and supplied import-photo resources are checked again
after staging, immediately before publication; intervening delivery invalidates
the operation instead of overwriting it.

After local publication, the accepted revision is computed from that validated
base and the exact bytes the writer flushed, not from a new unvalidated scan of
live files. This applies both to complete generations and to the shared atomic
Person/Team Markdown writer. Delivery of another note during the final
check/publication interval therefore remains a revision difference: refresh
must load and validate it before it can become current. Invalid delivery retains
the last validated state and blocks writes without repairing provider files.
The read snapshot never falls back to live bytes for a file absent from that
snapshot. A narrow `IWorkspaceFilePublisher` operating-system boundary permits
controlled delivery at publication in real-filesystem workflow tests.

Import preview and accepted-source reopening share envelope/property allowlists
and scalar validation, including real UTC calendar timestamps. Reopening source
evidence does not reapply a partial batch against today's graph or reread old
producer resources; proposed-state graph and import-resource checks remain
separate. Import version validation is also shared with pre-write version
inspection: a numeric representation of import version 1 accepted by preview
(such as `1.0`) must still reopen and replay. Workspace/record version-header
rules are unchanged, and unsupported source versions still block writes with
upgrade guidance.

Validated Workspace reads use short-lived handles that permit external writes
and deletion/replacement. Opening a reader must not lock a provider's same-path
delivery or another client's atomic publication. Complete before/after byte
fingerprints and data validation still reject changing, incomplete, or invalid
deliveries; permissive handle sharing does not authorize unvalidated content.
Producer-photo containment and preview/resource revalidation remain unchanged.

File-backed workflows within one process also coordinate complete filesystem
reads, version inspection/conversion, and local publication by normalized
Workspace path. One client's open read handles finish before a sibling client's
atomic replacement starts; readers likewise cannot inspect an in-process
half-written generation. This gate is process-local I/O lifetime coordination,
not provider synchronization, a semantic merge, or an on-disk read lease.
External delivery still requires whole-state fingerprints/validation, and the
exclusive local writer lease still refuses actual competing writers and access
failures. Coordination adds no catch-all recovery or write retries.

Debounced watcher callbacks defer when either their workflow gate or the
process-local Workspace I/O gate is busy rather than occupying thread-pool
workers behind a synchronous local mutation. Explicit
refresh still coordinates complete reads, and a deferred callback rechecks the
latest files; this changes scheduling, not validation or failed-save policy.

The app's `workspace.next` and UUID-named `.next` files directly beneath `notes\`
and `team-notes\` are uncommitted staging, not canonical data. Refresh traversal
does not inspect or fingerprint them: Windows replacement source files must stay
unopened during publication, even with delete-sharing handles. The recovery-only
`workspace.previous.json` is also excluded from live reads because Windows must
remove/overwrite that backup during the next publication. A partial/locked stage
or backup leaves the committed pointer and notes authoritative. Other files,
canonical links/conflict copies, and required generation manifests keep their
existing validation; this is not conflict repair or a general ignored-file rule.

A save error remains visible and does not publish unsaved attributes as the saved
profile. Retry after resolving local access failures, or deliberately discard
unsaved local edits. `CanSafelyClose` and `LocalSaveError` on the shared workflow
retain failed-save state across refreshes and unrelated successful operations;
successful retry of that operation clears its error. Refreshing or reopening
does not silently acknowledge a failed save.
Invalid files, unsupported versions, and unexpected canonical properties block
opening/writes rather than being repaired or downgraded. Back up the entire
folder before manually resolving external provider conflicts. If a local write
was interrupted before the pointer commit, the previous committed generation
remains authoritative; staged/orphan files are not automatically applied.

If the committed pointer is damaged, close the app, make a full folder backup,
inspect the readable previous pointer and referenced generation, and restore a
known-good pointer externally before reopening. The app does not silently pick
a conflicting generation or implement sync-provider conflict resolution.
Installation/upgrades must not remove this chosen folder.

Disposal stops the watcher under the same workflow gate used for reads, so an
active filesystem read finishes before closing returns and queued watcher reads
cannot reenter. Explicit refresh requests after disposal are refused without
filesystem access or new notifications. Callers must dispose every file-backed
workflow before removing its Workspace or releasing fixture ownership.

## Private Person notes

`notes\<note-id>.md` holds one UTF-8 Markdown Person note. Notes are outside
Person generations so editing or archiving a note replaces only that document;
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
or write generations/history. Own-save event bursts match the accepted byte
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
The zero revision is the initial baseline. The version-1 Team-note encoding
needs no conversion of its own; Workspace 1 → 2 preserves existing Person/Team
documents unchanged.

The committed generation's `team-context.json` maps Person emails to opaque
context revision UUIDs. A new revision is committed atomically with a role
(job title/department), archival, or complete Reporting-chain change. All affected
descendants' context revisions change for an ancestor move; ordinary office/name
changes and exact batch retries do not. Notes whose confirmation differs from
their owner's current revision remain visible but need review, even after a
change is reverted. These markers are canonical review state, not reconstructed
history. Missing markers in earlier version-1 generations mean the initial
baseline; later generation commits preserve them.

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

The committed generation's `historical-team-context\<email-hash>.json` contains
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
`team-context.json` independently. Reversion and subsequent moves preserve
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

Captures publish in the same atomic generation as profiles, Profile history,
accepted batches, and Team-context revisions. A failed commit publishes none of
them. The generation manifest declares historical documents, so withheld files
are incomplete delivery, not silently missing history. Opening/automatic refresh
validates frozen identities, chain coherence, note attribution/content versions,
and causal evidence before accepting the complete byte revision. Invalid
delivery retains the last valid current/history views with the common STALE
warning and blocked writes. Compatible-version refresh is read-only: it loads delivered captures
without another transition, note confirmation, or replay.

Older version-1 generations without this directory remain readable with no
fabricated pre-import history. Their next reporting transition captures the
actually known old chain and notes; missing prior Manager evidence remains
unknown. Existing `team-context.json` baselines/review markers and the original
partial import contract are unchanged; imported note operations remain rejected.

## Person archival and reactivation

Person `archived` is retained independently of note archival. A status change
never deletes the Person, removes a note document, or drops their referenced
photo. Current-generation photos, structured history, historical captures,
accepted batches and Team review revisions are committed together through the
same save boundary as imports and explicit Manager changes. Failure publishes
none of the proposed status/context changes; local retry/discard and safe-close
state still apply. External refresh loads validated delivered status/history
without recording another closure or implicitly reactivating anyone.

`IApplicationWorkflow.ArchivedPeople` exposes the retained-profile projection,
while `ReportingRoots` always excludes archived people. `EditPersonArchived`
and `EditPersonManager` validate the complete proposed graph and record local
history through the same generation workflow used by Import apply. Active
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
and queries do not wait on generation staging or file decoding. WPF requeries after validated mutations and
external Workspace notifications. Invalid delivery keeps the last valid
results/favorites explicitly **STALE retained**, blocks writes, and never
publishes unvalidated text. Failed favorite saves retain the saved star/list and
offer retry/discard before safe navigation/close. Querying does not write files,
replay imports, manufacture history, fetch remote content, or upload private
information. Search-match Markdown uses the same private native renderer.
