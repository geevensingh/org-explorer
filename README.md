# Org explorer

A private, offline-first Windows org explorer with rich person profiles, person
notes, manager-owned team context, and observed reporting history.

The agreed first release targets Windows 11 x64 using .NET 10, WPF, and Velopack.
People, notes, photos, and history live in readable local files. An external
provider such as OneDrive owns synchronization and conflict resolution.

## Specifications

- [Agreed v1 design](docs/design.md)
- [Domain vocabulary](GLOSSARY.md)
- [Import contract](docs/import-format.md)
- [Machine-readable import schema](schemas/import-v1.schema.json)
- [Example import files](docs/examples/)
- [Architectural decisions](docs/adr/README.md)
- [Installer, updater, and signing research](docs/research/installer-updater.md)

## Run locally

On Windows 11 x64 with the .NET 10 SDK:

```powershell
dotnet run --project src\OrgExplorer.Desktop
```

Choose an empty local folder outside the installation, then **Open Workspace**.
Choose a version-1 JSON file, review **Preview import**, and deliberately **Apply
reviewed import**. Expand/collapse the **Imported reporting population**, select
a Person, or navigate to their Manager and imported reports beside the profile.
Report counts describe this selected population, not full-company coverage.
Edit the profile fields; changes are saved locally as you type. Empty editor text
explicitly clears a nullable value;
whitespace-only text is rejected with a field-specific error. Preferred name is
the prominent heading, while Directory name remains visible and independently
editable. Email is a non-editable identity. Reopen the same Workspace to retrieve
saved data. The app does not upload Workspace data.

Profiles also offer multiple private **Person notes**, an autosaved Markdown
editor, and a native rendered **Read** view. Notes show their Person owner and
never inherit to reports. **Archive note** preserves writing; **Show archived
notes** allows reading and restoring it. The app refreshes valid externally
edited note documents and reports save/delivery errors. Failed drafts remain
visible until retry succeeds or you deliberately discard them. Images and raw
HTML never load content; HTTP/HTTPS links open only on deliberate selection.

Profiles display copied local JPEG/PNG photos, with **Choose photo** and
**Clear photo** actions. Imports accept safe relative `photoPath` references or
explicit null to clear; omitted photos stay unchanged. Images are decoded and
validated before a whole batch can apply. The Workspace keeps its own
content-addressed copies, so removing the producer folder does not break saved
photos. Invalid supplied images reject the batch and retain the existing photo.

Textual profiles accept `displayName`, `preferredName`, `alias`, `jobTitle`,
`department`, `officeBuilding`, `officeNumber`, `officeState`, `officeCountry`,
and `linkedInProfileUrl`, alongside `email`, `managerEmail`, and explicit
`archived` in the existing version-1 import envelope. Alias is a short company
handle, not a preferred name
or email. Building/room identifiers and geographic fields remain separate,
unaltered text, including letters and leading zeros.

An office-only import preserves names, links, all other omitted values, and
omitted people. Explicit null clears nullable fields after preview and Apply;
empty/whitespace-only import strings are errors, not clears. Preferred name and
LinkedIn URL have no protected override layer: explicitly importing them replaces
local values. Notes and unknown fields are rejected for the whole batch.

LinkedIn links require a valid absolute HTTPS URL. Displaying/editing the profile
never fetches the URL or other remote content. **Open saved LinkedIn URL in
browser** deliberately hands the saved link to the default browser; browser
opening failures are visible without changing the profile.

Contract fields owned by subsequent slices are still explicitly rejected rather
than discarded. The full import contract remains unchanged.
For example:

```json
{
  "schemaVersion": 1,
  "batchId": "10000000-0000-4000-8000-000000000001",
  "observedAt": "2026-11-01T17:00:00Z",
  "people": [
    { "email": "avery@example.com", "displayName": "Avery Example", "managerEmail": null }
  ]
}
```

Manager references resolve against the complete proposed batch and existing
people, even when a report precedes its Manager. Duplicate keys, unknown
Managers, active reporting cycles, and active reports to Archived managers block
the entire import. Archiving a Manager requires explicit report changes; omitted
people are never reassigned, archived, or removed. Archived profiles remain in
the Workspace but leave the ordinary reporting tree.

Preview lists additions, field changes/clears, reporting moves, archives,
reactivations, and explicit no-change records. Changed Workspace or import bytes
invalidate that review. An accepted batch replay is a visible no-op even after
later local/reporting changes; JSON whitespace, property ordering, Person-array
ordering, and case-equivalent email references do not change its logical identity.
Different data under the same batch ID is rejected.

The selected Person's **Structured Profile timeline** shows accepted old/new
values, imported versus local-edit provenance, source Observation time, and
local recording/application time. Expand the field confirmations and evidence
sections to review explicitly supplied values, including identical
reconfirmations. Omitted fields retain their values and dates: an office update
on November 3 does not reconfirm a Manager last observed on November 1. A
Manager change observed on November 6 can show that November 1–6 evidence
interval, not an invented exact effective date. Older evidence is highlighted
in the preview; accepting it retains the warning in history and uses
detection-only dates when an ordered interval cannot be supported. Creation is
a baseline and does not invent pre-import changes. History reopens with the
Workspace; notes are not given complete edit-version histories.

The shared `IApplicationWorkflow` owns lifecycle, preview/apply, validated edits,
projections, status, and persistence; WPF uses its observable `WorkspaceViewModel`
adapter. File parsing/storage stay behind that seam. See
[Workspace format and recovery](docs/workspace-format.md).

## Validation and CI

```powershell
dotnet restore OrgExplorer.slnx --locked-mode
dotnet build OrgExplorer.slnx -c Release --no-restore
dotnet test OrgExplorer.slnx -c Release --no-build
dotnet run --project tests\OrgExplorer.Desktop.Smoke -c Release --no-build -- src\OrgExplorer.Desktop\bin\Release\net10.0-windows\OrgExplorer.Desktop.exe
.\tests\CiGate.Tests.ps1
```

The smoke runner requires an interactive Windows desktop and drives the actual
WPF controls using Windows UI Automation. It launches the normal app without
test-only flags, previews/applies a five-Person batch with a second-record report
to its fifth-record Manager, expands/collapses the tree, navigates between
Manager/report profiles, rejects an invalid archival batch, edits, closes, and
reopens to verify durability. A combined reporting move, archive, and profile
update followed by replay proves that later edits and unrelated fields survive.
It also edits every textual profile field, checks validation and saved-link
action state, reopens, reviews/applies an office-only import with an explicit clear, and reopens
again to prove unrelated attributes survived. A controlled loopback endpoint
checks that rendering a stored HTTPS link makes no network request; core tests
control the browser-opening boundary without opening production pages.
The desktop history flow also verifies a real persisted timeline, field-specific
dates, observed reporting intervals, identical reconfirmation without a false
change, local clears, stale evidence before Apply, and close/reopen.
It creates/edits/reads multiple Person notes, observes an
external document edit, checks failed-save retry/safe close/conflict recovery and
blocked tree, Manager/report, and Workspace navigation with an unsaved note,
archives writing, and verifies no rendering requests reach a local HTTP listener.
Profile edits and reporting moves/replay retain the selected note and its writing.
Photo smoke checks actual PNG/JPEG pixels, reopening after producer-folder
removal, omission/clear behavior, and corrupt/remote rejection. Photo edits,
reporting moves, and replay preserve private notes and their refresh revisions.
Core checks include a 10,001-Person reporting chain without a hard import cap.
Tests use fictitious people and isolated folders beneath the checkout, cleaned
after each run.

Every PR and main commit (without path filters) runs three independent parallel
jobs: core/view models, real filesystem persistence, and actual Windows desktop
smoke. `CI` runs even after lane failures and accepts only three `success`
results. Cancellation/timeout cannot yield a successful required check.
Dependency lock files support the initial SDK/package cache. Each lane includes
restore/build/test within an eight-minute timeout; the aggregate has two minutes.
Thus the execution critical-path bound is ten minutes and the summed job budget
is 26 minutes, both below 30 (runner queues excluded). Five minutes is the target,
not a claim about unmeasured hosted-runner performance.

Main is protected by an active GitHub **ruleset**, with no bypass actors,
requiring PRs and a strict up-to-date `CI` check from GitHub Actions. Only squash
merges are enabled. `eng\Configure-Repository.ps1` reapplies and verifies those
settings using the explicitly routed account; it does not push or merge code.

Installer, automatic updates, complete imports, Team-note features/frozen historical team context,
and release signing are subsequent slices, not shipped capabilities of this tracer.
