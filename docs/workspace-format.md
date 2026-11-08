# Workspace format, version 1

The Workspace is a chosen local folder, not installation data or an import file.
`workspace.json` holds `schemaVersion: 1` and a `generation` identifier.
`generations\<generation>\people\` contains separate indented UTF-8 JSON Person
records with stable email, Directory name, Preferred name, Alias, job title,
department, office building/number/state/country, LinkedIn URL, and explicit
Manager or null, plus archival state. Archived records retain their data but are
excluded from the ordinary reporting tree. Canonical camel-case properties are `directoryName`,
`preferredName`, `alias`, `jobTitle`, `department`, `officeBuilding`,
`officeNumber`, `officeState`, `officeCountry`, and `linkedInProfileUrl`.
The import contract's `displayName` maps to canonical `directoryName`.
Office identifiers stay text and missing optional attributes in existing
version-1 records load as unknown; no migration or identity replacement is needed
to add them. All textual fields are validated when loading as well as when edited
or imported. Filenames are SHA-256 hashes of case-normalized emails, avoiding Windows
filename restrictions. Email inside the record remains authoritative. The same
generation has `batches.json`, recording accepted batch IDs, logical hashes,
original validated source envelopes (including Observation times and producer),
and local application times. Source evidence is retained for subsequent slices
rather than discarded when the initial tracer does not yet display history.
The same generation's `profile-history\` contains separate readable per-Person
history documents, with the same email-hash filenames. Each document contains
`email`, `changes`, and `observations`. A structured change retains `field`,
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
History is committed in the same generation as profiles, accepted batches, and
other Workspace resources, so a failed save publishes none of the change.
Person notes are
separate documents described below, not imported Person fields.

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

A mutation writes a new immutable generation and flushes every file to local
storage before atomically replacing `workspace.json`. The previous pointer is
preserved as `workspace.previous.json`; earlier generations remain recoverable.
No retention cleanup runs yet. A short exclusive `.workspace.lock` coordinates
local app writers, not external folder providers. Fingerprints detect Workspace
changes since opening; stale previews and saves are refused instead of merging
competing state. The UI revalidates the selected import bytes on apply. Local
mutations also invalidate prior previews. A graph batch publishes one coherent
new generation, never intermediate Manager/report updates.

A save error remains visible and does not publish unsaved attributes as the saved
profile. Reopen the Workspace after resolving access or external-change errors.
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

The open profile checks note files for external changes, validates a complete
note read before refreshing, and retains the last valid text on invalid/incomplete
delivery. A simultaneous change to other Workspace files requires reopening the
Workspace rather than acknowledging stale Person state. Local edits invalidate
old import previews. Save errors keep the draft visible and explicitly **NOT
saved**; leaving/closing is blocked until retry succeeds or the owner deliberately
chooses **Discard unsaved draft**. Resolving competing versions remains the
external provider/owner's responsibility; the app does not merge them.

The reading view renders CommonMark headings, emphasis, lists, quotations, code,
and links as native WPF text. Markdown images are labeled **Image not loaded**,
including local references; raw HTML is inert text. No image, stylesheet, embedded
object, or remote document is fetched. Only a deliberately clicked HTTP/HTTPS
link launches the user's external handler; other URI schemes are inert.
