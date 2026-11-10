# Person update import format, version 1

This is the producer-facing contract for org explorer. Another agent, session, or
tool produces these files; the app does not authenticate to or extract from a GAL.
The normative structural schema is
[import-v1.schema.json](../schemas/import-v1.schema.json).

Validators must enable format assertions for `uuid`, `email`, `date-time`, and
`uri`; Draft 2020-12 implementations may otherwise treat `format` as annotation
only. Schema validation does not replace the state-dependent and filesystem
checks below.

This contract specifies person updates, not direct writes into the workspace.
**Notes cannot be imported.** Unknown properties, including note payloads or note
operations, are errors rather than silently ignored extensions.

## Files to produce

Produce a UTF-8 JSON file and, when referenced, nearby JPEG or PNG image files:

```text
import-folder\
  updates.json
  photos\
    priya.png
```

`updates.json` is a conventional filename, not an identity requirement. Select
the JSON file in the app; referenced images resolve relative to its directory.
JSON does not permit comments.

Use a new batch ID for a new logical batch. Keep it unchanged for retries of the
same batch. The concrete version-1 choices are a UUID batch ID, UTC observation
time, explicit partial person records, and local photo references.

## Envelope

```json
{
  "schemaVersion": 1,
  "batchId": "10000000-0000-4000-8000-000000000001",
  "observedAt": "2026-11-01T17:00:00Z",
  "producer": "example-exporter",
  "people": [
    {
      "email": "priya@example.com",
      "displayName": "Priyanka Rao",
      "managerEmail": "morgan@example.com"
    },
    {
      "email": "morgan@example.com",
      "displayName": "Morgan Example",
      "managerEmail": null
    }
  ]
}
```

| Property | Required | Meaning |
|---|---|---|
| `schemaVersion` | Yes | Integer `1`. Reject unsupported versions and explain that an app upgrade may be needed. |
| `batchId` | Yes | UUID identifying this logical batch, not a person. |
| `observedAt` | Yes | UTC source observation time, using `YYYY-MM-DDTHH:MM:SSZ` or up to seven fractional-second digits before `Z`. |
| `producer` | No | Nonblank descriptive producer name; no identity matching or authentication is inferred from it. |
| `people` | Yes | Nonempty array of partial person records, with one record per email. |

`observedAt` describes only supplied values. It does not assert that omitted
people or fields were observed, refreshed, cleared, or removed. The app separately
records when it applied the batch. File modification times are not reliable
observation or transfer dates.

Use real Gregorian calendar dates in years 0001-9999 and ordinary UTC times,
without leap-second notation. The receiver must parse the timestamp, not merely
match its printed shape: schema regexes and some library format checks do not
reject every impossible calendar date.

Exact replay of an already accepted logical batch is a visible no-op, not another
set of history events. Reusing its ID with a different semantic payload or
different referenced image bytes is an error. JSON whitespace and person-array
order do not change the logical payload.

## Person fields

Emails are plain mailbox identifiers, not display-name addresses or `mailto:`
URLs. Match and resolve person/manager email keys case-insensitively; preserve
other supplied text rather than guessing names or identities.

Every record requires `email`. A newly created email additionally requires a
non-null `displayName` and an explicit `managerEmail`, either an email or null.
These creation rules depend on existing workspace state and therefore need
semantic validation in addition to JSON Schema validation.

| Property | Value when supplied | Meaning |
|---|---|---|
| `email` | Nonblank email; never null | Stable, unique identity and update target. |
| `displayName` | Nonblank string or null | Directory display name. Creation requires a non-null value. |
| `preferredName` | Nonblank string or null | Preferred heading/reference name; explicitly importable. |
| `alias` | Nonblank string or null | Short company handle, not another email or preferred name. |
| `jobTitle` | Nonblank string or null | Job title. |
| `department` | Nonblank string or null | Department. |
| `officeBuilding` | Nonblank string or null | Building name or number; text, not an integer. |
| `officeNumber` | Nonblank string or null | Office/room identifier; preserve leading zeros and letters. |
| `officeState` | Nonblank string or null | State, province, or comparable administrative region. |
| `officeCountry` | Nonblank string or null | Country text; no inferred geocoding or automatic conversion. |
| `linkedInProfileUrl` | HTTPS URI or null | Explicit LinkedIn profile link; no automatic scraping/fetching. |
| `managerEmail` | Email or null | Formal manager. Null explicitly means no manager relationship. |
| `photoPath` | Safe relative file path or null | Local image to copy, or explicit clearing of the existing photo. |
| `archived` | Boolean | True archives; false explicitly activates/reactivates. |
| `isFavorite` | Boolean | True stars the person; false removes the star. |

Strings cannot be empty or whitespace-only. Use null to clear nullable text,
URL, manager, and photo fields. Boolean fields are cleared by explicitly setting
false, not null. Initial `archived` and `isFavorite` values default to false when
no prior record exists.

There is no `notes`, `teamNotes`, `personNotes`, `directReports`, arbitrary
`attributes`, or replacement-record property. Reports and inherited context are
derived from stored relationships and local notes, not imported duplicates.

## Omission, null, and explicit values

For an existing person:

| Input | Effect |
|---|---|
| Person absent from `people` | Leave their record and status unchanged. |
| Field absent from a person record | Leave that field and its observation information unchanged. |
| Nullable field explicitly null | Clear that field after review. |
| Field explicitly given a value | Set that value after review, even if normally edited in-app. |
| `archived: true` | Archive without destroying information, photos, notes, or history. |
| Ordinary update to an archived person | Keep them archived unless `archived: false` is explicit. |

An existing-person email-only record can be reported as having no field changes;
it does not reconfirm all their other values. Optional fields absent during
creation remain unknown. There is no special import-protected preferred name or
LinkedIn layer; notes are the sole import exclusion.

An explicit same-value observation can reconfirm that supplied field without
creating a false profile-change event. An office-only observation must not
advance the last observation of the manager. Warn when older evidence is being
reapplied, retain source/application dates, and never infer a backwards transfer
interval from stale observations.

## Manager references and batch validation

Validation uses the current workspace plus the **complete proposed batch**.
Never require a referenced manager to appear earlier in the array.

Before applying, require:

1. Every email appears only once in the batch, including case-equivalent keys.
2. Every new identity has the required creation fields.
3. Every non-null manager resolves to an existing or same-batch person.
4. An active person does not reference an archived manager in the proposed state.
5. The active reporting graph has no self-reporting or cycles.
6. Every supplied photo passes path, existence, and image-content checks.
7. The format is supported and the batch ID has not been reused for a different
   logical payload.

Do not create placeholder manager records. A manager can be supplied later in the
same batch, and an archived manager can be explicitly reactivated in that batch.
If a manager is archived, their active reports must be explicitly reassigned,
cleared, or archived as necessary to leave a valid graph. Do not infer changes to
those reports.

Use dependency-aware staging and, where needed, manager-before-report application
order. Compare the old graph to the complete proposed graph for historical
captures, not intermediate writes.

An accepted active-to-archived change closes the Person's old context even if
`managerEmail` is omitted. It freezes the old chain and locally known active Team
writing without deleting profile fields, copied photos, Person/owned Team notes
or earlier history. A combined reporting move/archive captures once from the
coherent old state, not from intermediate record order. New archived identities
are baselines; explicit reactivation resumes current context without rewriting
the frozen captures.

For example, a Manager/report archive can supply both explicit status changes:

```json
[
  { "email": "morgan@example.com", "archived": true },
  { "email": "priya@example.com", "archived": true }
]
```

This is the `people` array within a complete version-1 envelope, not a standalone
Import file. Omitted active reports still block Manager archival. Replacing the
report's status change with `"managerEmail": null` or an explicitly valid active
Manager is another deliberate resolution, not something the app infers.
Reactivation can explicitly supply false for both report and Manager in one
new batch, independent of array order; false for only a report whose Manager is
still archived is rejected.

An invalid record blocks the whole batch. Display concrete record/email/field
errors; do not silently skip records. Show a change preview and require explicit
apply. If the workspace or referenced resources change after preview, revalidate
and refresh the preview instead of applying against a silently changed base.

## Photos

Example JSON syntax for a Windows relative path:

```json
{
  "email": "priya@example.com",
  "photoPath": "photos\\priya.png"
}
```

The referenced file must actually exist and contain a decodable JPEG or PNG.
Inspect content, not just the extension. Resolve paths against the import JSON's
directory, including filesystem links, and require the resolved file to remain
inside that directory. Drive-rooted paths, UNC paths, URLs, parent traversal,
and escaping symlinks/junctions are invalid. Schema path checks do not replace
filesystem containment checks.

The Windows receiver checks the opened file's resolved handle for containment,
validates complete PNG chunks/checksums or JPEG segments/end marker, and decodes
all image pixels with Windows Imaging Component. A renamed extension does not
turn supported content into an error or unsupported content into an image.
Image hashes are part of preview revalidation and accepted-batch identity.

Copy valid supplied images into the workspace; do not retain a dependency on the
export folder or any remote image URL.

- Omitted `photoPath` preserves an existing photo.
- Omitted `photoPath` during creation allows a clearly visible placeholder.
- Explicit null clears the current photo.
- A supplied missing, escaping, unsupported, or invalid image is an import error,
  not a successful placeholder substitution.

The JSON examples below omit photos so they require no nonexistent example image
files. The snippet above is illustrative and requires the producer to supply its
image.

## Example sequence

All people and addresses are fictitious. `example.com` is an example domain.

| Order | File | Demonstration |
|---|---|---|
| 1 | [initial-import.json](examples/initial-import.json) | Five new people. Priya is second and references Morgan, who is fifth. |
| 2 | [profile-update.json](examples/profile-update.json) | Partial office/preferred-name/LinkedIn/favorite changes and an explicit department clear; no manager reconfirmation. |
| 3 | [reporting-transfer.json](examples/reporting-transfer.json) | Priya moves from Morgan to Alex; the previous locally known team context is frozen in history. |
| 4 | [archive-person.json](examples/archive-person.json) | Jordan is explicitly archived without deleting their record or notes. |

No example imports notes, claims complete directory coverage, or uses actual
employee data.
