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
No history is manufactured by the initial tracer. Person notes are
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

## Commit and recovery

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

Refresh is read-only: it does not replay accepted imports, invent Observations,
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
