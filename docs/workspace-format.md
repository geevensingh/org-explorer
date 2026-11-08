# Workspace format, version 1

The Workspace is a chosen local folder, not installation data or an import file.
`workspace.json` holds `schemaVersion: 1` and a `generation` identifier.
`generations\<generation>\people\` contains separate indented UTF-8 JSON Person
records with stable email, Directory name, Preferred name, Alias, job title,
department, office building/number/state/country, LinkedIn URL, and explicit
Manager or null. Canonical camel-case properties are `directoryName`,
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
No notes, photos, or history are manufactured by this first tracer.

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
competing state. The UI revalidates the selected import bytes on apply.

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
