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
