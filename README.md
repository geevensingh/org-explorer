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
- [Workspace versions and recovery](docs/workspace-format.md)
- [10,000-Person validation and reference timings](docs/performance-validation.md)
- [Machine-readable import schema](schemas/import-v1.schema.json)
- [Example import files](docs/examples/)
- [Architectural decisions](docs/adr/README.md)
- [Installer, updater, and signing research](docs/research/installer-updater.md)
- [Installation and unsigned-installer trust limitations](docs/INSTALLATION.md)

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

Loading, validation and local writes run off the desktop dispatcher. Rapid
profile/Markdown typing is coalesced after a short quiet interval; the visible
draft stays editable while a previous write completes. Pending/newer drafts are
explicitly **not yet saved**, and navigation/closing cannot discard them. A
failed write still requires retry or deliberate discard. Tree expansion and
Settings remain usable while local work is outstanding. Rejected navigation is
explained in the main status: pending edits or an in-progress save ask you to
wait; a failed save asks you to retry or discard. Note-specific feedback also
appears beside the affected note. Desktop smoke waits for saves and verifies
the destination Person before continuing a successful navigation. Search runs
locally in the background and ignores superseded query responses. None of this changes
the import review checkpoint, atomic generation publication, or data formats.

Known version-1 Workspaces open through automatic validated **1 → 2 conversion**,
including the breaking Person/history record-version headers. Conversion
preserves the complete profile, note/photo, Observation and frozen-history
family, with durably copied and validated originals under `recovery\<attempt>\original\`.
No migration approval is required. Unsupported versions immediately suggest an
app upgrade and block all Workspace writes; incomplete external delivery is
unavailable data, never an app-owned repair. Conversion failure keeps the
original pointer authoritative and reports recoverable originals, when available.
Resolve the local failure and retry; do not treat staged output as committed.

**Settings and upgrade guidance** is always accessible, including before choosing
a Workspace or after incompatible data/conversion failure. **Retry Workspace
validation / conversion** retries local loading without enabling invalid edits.
Automatic updates are **not implemented yet**: installer verification still
blocks the later updater, and Settings does not pretend to have checked update
availability. Installer rollback does not restore Workspace data; follow the
[Workspace recovery guidance](docs/workspace-format.md#automatic-supported-conversion)
before restoring files externally.

Profiles also offer multiple private **Person notes**, an autosaved Markdown
editor, and a native rendered **Read** view. Notes show their Person owner and
never inherit to reports. **Archive note** preserves writing; **Show archived
notes** allows reading and restoring it. The app watches local folder events and automatically reflects valid external
Person, Person/Team Markdown, Team context metadata, and photo changes, including new image bytes at the same
location. Whole-Workspace/graph validation precedes acceptance. Incomplete,
unsupported, or invalid deliveries visibly mark all retained information
**STALE / unavailable**, block writes, and never create false roots or repair
files. Complete valid delivery recovers automatically. Failed drafts remain
visible until retry succeeds or you deliberately discard them. Images and raw
HTML never load content; HTTP/HTTPS links open only on deliberate selection.

**New Team note** explicitly opts into reporting-subtree scope. Writing a
**Person note** about a Manager never opts into inheritance. The profile separates
owned Team notes from **Current inherited Team context**, which includes notes
from every formal management ancestor and identifies each owning Person by name
and email. Informal leads/project membership are not reporting relationships.
Team scope is still private to the Workspace owner, not sharing.

Team notes stay with the Person on whom they were written. A reporting move
replaces the current inherited context with the new chain without relocating
former Managers' notes or losing direct Person notes. Changes to the owner's job
title, department, archival state, or complete Reporting chain mark Team notes
**Needs review**, including ancestor moves with an unchanged direct Manager.
Warnings remain visible in inherited context and import review, even if a role
change is later reverted. Review on the owner's profile: **Confirm still
applies**, edit the autosaved Markdown (which confirms current applicability), or
**Archive Team note** to retain but stop inheriting the writing. Restoring an
archived note does not silently confirm it. Failed review saves retain the warning
and saved writing; unsaved drafts block navigation/close and offer retry/discard.

**Historical team context** is a separate read-only section on the Person's
profile. Direct transfers and ancestor reorganizations capture the coherent
former Reporting chain and attributed, locally known active Team-note text and
versions. Current inheritance follows only the final new chain. Later edits,
review actions, note archival, or Manager renaming do not rewrite those captures.
Creation is a baseline, not a discovered earlier transfer; accepted replay adds
no duplicate captures. Historical writing reopens with the Workspace.

History labels each causal Manager change with its source Observation and local
processing provenance. Ordered prior/new evidence can show an observed interval;
missing or older evidence shows detection information and an unknown effective
date. The frozen writing was **locally known at processing time**, not guaranteed
writing from the unknown actual transfer instant. Direct Person notes stay intact
and never enter inherited or Historical team context.

**Archive Person** explicitly removes the selected Person from the ordinary
current reporting tree while keeping their profile, copied photo, Person notes,
owned Team notes, and timelines. **Show archived people** opens a separate
retained-profile list; select a Person there to inspect/edit their information
and frozen context, including after closing/reopening. Archival is not deletion
or an assertion of departure. The profile labels its last stored Manager
separately from current inheritance, which is empty while archived.

Archival closes the old Reporting chain and freezes locally known Team writing
even when the Manager did not change, including an empty chain for a root.
Later Manager-note edits cannot rewrite the capture. Team notes stay with their
owner and are marked for review by status changes, rather than moved or erased.
**Reactivate Person** explicitly restores a valid current profile; it uses
current Manager writing without reopening or rewriting old captures.

An active Person must have an active Manager. Archiving a Manager with active
reports rejects the whole change and identifies the affected report: no cascade
or reassignment occurs. On each affected profile, choose a Manager (or explicit
no-Manager) and **Apply Manager change**, or explicitly archive that report.
Alternatively preview/apply all explicit report and status changes in one Import
batch. A report may appear before a Manager being reactivated in that batch.
Omitted people/Boolean fields stay unchanged; ordinary edits/imports of archived
profiles never reactivate them. Only explicit `archived: false` does so, after
whole-graph validation. Failed status/Manager saves retain the committed profile,
photo, notes, and history; retry or discard the unsaved local action before leaving
or closing.

**Local search** finds stored profile attributes and Person/Team note titles and
Markdown text without a network connection. Search is case-insensitive and uses
the entered text as a substring. Results identify current information, archived
profiles/notes, and historical matches separately, including note owners and
Team-review warnings. Select a result to open its profile and appropriate note
or read-only **Selected search context**. Historical matches show the exact frozen
writing/former chain or old Profile value with its existing provenance, never
later live writing mislabeled as history. Close that context to return to normal
profile reading. Archived writing stays findable without joining the current org.

**Mark favorite** / **Unmark favorite** saves the selected Person's star locally.
The **Favorites** list returns to active or clearly labeled archived profiles,
including after reopening. Reviewed imports may explicitly supply `isFavorite`;
omission preserves the star and false removes it. Favorite-only edits do not
create reporting history. Queries and favorite views rebuild from validated
Workspace state after local/external changes, not a canonical index or hosted
backend. Stale delivery is labeled; failed favorite saves retain the saved state
and require retry/discard before navigation or close.

Profiles display copied local JPEG/PNG photos, with **Choose photo** and
**Clear photo** actions. Imports accept safe relative `photoPath` references or
explicit null to clear; omitted photos stay unchanged. Images are decoded and
validated before a whole batch can apply. The Workspace keeps its own
content-addressed copies, so removing the producer folder does not break saved
photos. Invalid supplied images reject the batch and retain the existing photo.

Textual profiles accept `displayName`, `preferredName`, `alias`, `jobTitle`,
`department`, `officeBuilding`, `officeNumber`, `officeState`, `officeCountry`,
and `linkedInProfileUrl`, alongside `email`, `managerEmail`, and explicit
`archived` and `isFavorite` in the existing version-1 import envelope. Alias is a short company
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

The full version-1 import contract remains unchanged.
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

## Build the local Windows installer

Use PowerShell 7 on Windows x64 with the .NET 10 SDK:

```powershell
.\eng\Package-Windows.ps1 -Version 0.1.0
.\eng\Test-WindowsInstaller.ps1 -Version 0.1.0
```

Packaging uses the repository-local pinned `vpk` tool (1.2.161, matching the
Velopack client), a fresh self-contained WPF publish, and bootstrap validation.
Outputs are under `.artifacts\installer\0.1.0\releases`; this command does **not**
publish anything. See the shipped [installation guide](docs/INSTALLATION.md)
for the exact artifact set, metadata, Workspace separation and trust limitations.
Existing output is rejected rather than mixed with stale files. A custom
`-NuGetConfig` can select your approved package cache/source without changing
repository or global settings.

`Test-WindowsInstaller` requires **Windows Sandbox already enabled**. It maps only
the built installer, self-contained test executables, and a dedicated results
folder, disables networking/clipboard/audio input/printer redirection, and drives
the real installed desktop. It checks default per-user installation/automatic
launch, local editing, replacement, uninstall/reinstall and unchanged external
Workspace data. No .NET runtime is installed separately. Its native harness
refuses to run outside the disposable Sandbox account; no PowerShell execution
policy or Windows trust protection is bypassed.

If Sandbox is unavailable, the command prepares the offline `.wsb` payload,
then **fails with an explicit verification blocker**. `-PrepareOnly` prepares it
without claiming a passed install test. Never run setup on a shared host merely
to make verification pass. `--installto` and environment overrides alone do not
isolate Velopack's user-registry and shortcut effects.

CI additionally builds the artifacts, checks all four fast bootstrap hooks, and
runs real WPF flows from the self-contained portable payload with an independent
fictitious Workspace beneath the checkout. Replacing all app files preserves
that Workspace. A controlled newer local package does not trigger implicit apply.
These host checks do not install setup/registry entries/shortcuts and are **not**
a substitute for the isolated install/uninstall test. Velopack's normal per-user
diagnostic logger remains active during host desktop checks; private test data and
staging locations are isolated beneath the checkout.

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
Team-note smoke exercises explicit opt-in, every ancestor and owner attribution,
private rendered reading, visible review warnings, confirm/edit/archive, failed
confirmation and draft-navigation safety, replacement/move scope, and reopen.
Historical-context smoke drives actual ancestor and direct transfers, separate
current/frozen Markdown reading, honest evidence dates, later former-owner Team
edits and renaming, repeated transfers/replay, and close/reopen. Core and real
filesystem tests also cover immutable versions, same-batch late Manager creation,
atomic save failures, legacy unknown evidence, invalid/missing historical
deliveries, and validated watcher refresh without duplicate captures.
Archival smoke drives actual in-app archive/filter/reopen/reactivate with retained
photo pixels, Person/owned Team notes, frozen all-ancestor writing and timelines.
It rejects inconsistent Manager archival/reactivation, explicitly changes a
Manager relationship, checks failed-save retry/safe close, preserves status on
ordinary imports, accepts complete report/Manager status batches, and replays
without duplicate captures. Core and filesystem checks cover null/false/omission,
all explicit report-resolution choices, archived cycles, root/combined archival,
immutable capture validation, atomic failed status saves and coherent folder-event
delivery/reopening.
Search/favorite smoke queries every textual profile attribute through the actual
WPF search box, opens Person/owned/inherited Team writing with attribution,
reads frozen and archived destinations, changes stars, validates import
omission/false, exercises failed-save navigation/close safety, observes external
profile/note/star delivery, and reopens persisted results. For only that real
desktop flow, append `--search-favorites` to the smoke-runner command above
(never to the app). Core and filesystem tests cover all-ancestor review flags,
independent frozen note versions, old Profile values, legacy favorite omission,
nonmutating queries and invalid-delivery stale retention.
Coupled refresh checks cover externally delivered owned/inherited Team writing
and review metadata, with whole-Workspace stale retention and recovery.
Real folder-event tests and the actual UI also observe profile/Markdown changes,
same-path photo pixels, invalid graph/stale state, and recovery using bounded
eventual assertions. Own saves cause no recursive refresh/save or batch replay;
failed saves remain visible to safe-close coordination until retry/discard.
Core checks include a 10,001-Person reporting chain without a hard import cap.
Filesystem and default desktop lanes also construct an actual 10,000-Person
Workspace with 2,500 local photo references, 1,000 Person notes, 111 Team notes,
200 frozen contexts, typed Profile history, 100 Archived people, and a 42-edge
Reporting chain. They exercise load/reopen, current and historical search,
navigation, mutations, validated external delivery, and stale/error retention.
The actual desktop also checks rendered photo pixels and Settings access during
loading. Append `--scale` to the smoke-runner command for just that complete
real scale flow, never to the app. See the [reference measurements](docs/performance-validation.md).
Tests use fictitious people and isolated folders beneath the checkout, cleaned
after each run.
Filesystem regressions also stress a refreshing second client against repeated
note/favorite autosaves, prove locked uncommitted stages and the recovery pointer do not replace or invalidate
committed data, and ensure closed workflows cannot reenter refresh. Fixture
owners dispose all workflows before removing their folders. Reader sharing and
staging traversal and process-local Workspace I/O lifetimes are corrected at the workflow rather than masked by save
retries, suppressed I/O failures, or disabled tests.

Every PR and main commit (without path filters) runs three independent parallel
jobs: core/view models, real filesystem persistence, and actual Windows desktop
smoke. `CI` runs even after lane failures and accepts only three `success`
results. Cancellation/timeout cannot yield a successful required check.
Dependency lock files support the SDK/package cache. `eng\Run-CiLane.ps1` uses
separate build artifacts for each lane, so the same meaningful lanes can also
run in independent local PowerShell processes without shared build-output races.
Each lane includes restore/build/test/fixture work within one shared timeout:
five minutes for core, eleven each for filesystem/desktop, and two for the aggregate.
Thus the execution critical-path bound is thirteen minutes and the summed job budget
is 29 minutes, both below 30 (runner queues excluded). Five minutes is the target,
not a claim about unmeasured hosted-runner performance. Hosted lane timing starts
before checkout/SDK setup, and remaining command deadlines share that budget.
CI preserves timing JSON, logs and TRX evidence rather than using empty smoke
commands. Desktop validation publishes a local product-check folder and rejects
test assemblies/fixtures in it; this is not an installer or CD publication.

Main is protected by an active GitHub **ruleset**, with no bypass actors,
requiring PRs and a strict up-to-date `CI` check from GitHub Actions. Only squash
merges are enabled. `eng\Configure-Repository.ps1` reapplies and verifies those
settings using the explicitly routed account; it does not push or merge code.

Installer verification, automatic updates and CD/release signing remain
blocked subsequent slices; scale validation does not implement them.
