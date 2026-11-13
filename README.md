# Org explorer

A private, offline-first Windows org explorer with rich person profiles, person
notes, manager-owned team context, and observed reporting history.

The agreed first release targets Windows 11 x64 using .NET 10, WPF, and Velopack.
People, notes, photos, and history live in readable local files. An external
provider such as OneDrive owns synchronization and conflict resolution.

## Specifications

- [Agreed v1 design](docs/design.md)
- [Agreed adaptive UI redesign (delivery in progress)](docs/ui-design.md)
- [Domain vocabulary](GLOSSARY.md)
- [Import contract](docs/import-format.md)
- [Workspace versions and recovery](docs/workspace-format.md)
- [Testing strategy and safe execution](docs/testing.md)
- [10,000-Person validation and reference timings](docs/performance-validation.md)
- [Machine-readable import schema](schemas/import-v1.schema.json)
- [Example import files](docs/examples/)
- [Architectural decisions](docs/adr/README.md)
- [Installer, updater, and signing research](docs/research/installer-updater.md)
- [Installation and unsigned-installer trust limitations](docs/INSTALLATION.md)
- [Project-authorized release protocol and owner key provisioning](docs/update-protocol.md)

## Run locally

On Windows 11 x64 with the .NET 10 SDK:

```powershell
dotnet run --project src\OrgExplorer.Desktop
```

Everyday chrome keeps local search, **Settings**, and a quiet Workspace-name
label. In **Settings**, choose an empty local folder outside the installation,
then **Open Workspace**. The full folder path and folder-opening controls live
there rather than in the everyday toolbar.
Choose **Settings → Import...** without entering Edit profile, choose a version-1
JSON file, review **Preview import**, and deliberately **Apply reviewed import**.
**Back to people** returns to reading; file selection, preview and Apply controls
are shown only in the dedicated Import view. Changing the file path or returning
to people requires a fresh preview before Apply.
If Apply fails, retry from Import or choose **Discard failed Import (not other
drafts)** to abandon that batch. This does not discard notes, Preferred-name
changes or a profile draft; any other unsaved writing still blocks close/restart.
Expand/collapse the **Imported reporting population**, select
a Person, or navigate to their Manager and imported reports beside the profile.
Report counts describe this selected population, not full-company coverage.
Profiles open in **View** mode with photo, identity and principal actions together.
Role, department, Manager, location and email are immediately readable.
**More details** reveals populated Alias, office number and LinkedIn URL fields;
the saved LinkedIn link still opens only through an explicit action.
Right-click a value to copy it, or Tab to it and use the context-menu key / Shift+F10.
Empty optional rows are omitted. **Edit profile** reveals all
ordinary fields and stages text, Manager and photo changes together.
**Save profile** validates and publishes the complete change; **Cancel**
discards it without changing saved data or history. Empty editor text explicitly
clears a nullable value; whitespace-only text is rejected. Preferred name is
the prominent heading, while Directory name remains visible and independently
editable. Email is a non-editable identity. Reopen the same Workspace to retrieve
saved data. The app does not upload Workspace data.

After a successful open, the app remembers that Workspace in app-local settings
and reopens it on the next launch through the same version-3 validation flow.
First launch stays empty with folder-opening controls available in **Settings**.
If the previous folder cannot be reopened, the app stays empty, explains the failure, and offers
**Retry previous Workspace** or **Choose another folder**. Automatic reopening
never creates a missing replacement folder; intentional manual open/create
remains available. Failed opens never replace the remembered successful folder.
Preference read/save errors are visible, and the startup-update preference is
independent. After validated reopening, the last valid Person and exact last-viewed
note reopen in Read mode, including a secondary Person or owned Team note.
Renaming a note does not change its identity. An unavailable or archived target
retains the saved preference and writing, explains the problem, and shows a
purposeful search/Favorites/People start instead of opening replacement writing.
No unsaved edit buffer is restored.

Preferred name normally appears as readable text. **Edit** (or **Add preferred
name** when unset) reveals its independent autosaving inline editor; **Done**
returns to reading after the save succeeds. Pending or failed writing stays in
the editor with the existing retry/discard protections. **Mark favorite** remains
an independent one-click saved action.

Loading, validation and local writes run off the desktop dispatcher. Rapid
Preferred-name/Markdown typing is coalesced after a short quiet interval; the visible
draft stays editable while a previous write completes. Pending/newer drafts are
explicitly **not yet saved**, and navigation/closing cannot discard them. A
failed write still requires retry or deliberate discard. Preferred name,
Favorite and notes remain independent of profile Save/Cancel. Leaving a dirty
profile through navigation, Import, archival, workspace switching or closing
offers **Save / Discard / Stay**. A failed Save retains its owner and draft.
A profile changed externally since entering Edit cannot be overwritten by Save
or retry; discard that draft before editing the delivered version. Changes
already incorporated before editing and changes only to other people do not
cause this conflict. Automatic update restarts never save or discard an unsaved
profile. Tree expansion and
Settings remain usable while local work is outstanding. Rejected navigation is
explained in the main status: pending edits or an in-progress save ask you to
wait; a failed quick/note save asks you to retry or discard. Note-specific feedback also
appears beside the affected note. Desktop smoke waits for saves and verifies
the destination Person before continuing a successful navigation. Search runs
locally in the background and ignores superseded query responses. None of this changes
the import review checkpoint, atomic selector publication, or data formats.

Workspaces use **version 3**, with changed-document/index writes, independent
committed recovery and guarded bounded reclamation. Older versions (including
1 and 2) and future versions are rejected without changing their bytes or
directories. There is no migration or legacy cleanup: preserve the old folder
and explicitly choose a new empty folder. Import version 1 and existing record
families remain unchanged. Incomplete external delivery is unavailable data,
never permission to repair it or accept a smaller population.

**Settings and upgrade guidance** is always accessible, including before choosing
a Workspace or after incompatible/unavailable data. **Retry Workspace
validation / storage maintenance** retries local loading and committed maintenance
without converting incompatible data or replaying an edit.
**Check for updates** verifies project-key signed releases, and startup checks
default to enabled. Availability and offline/rate-limit/verification errors are
explicit and do not block local work. The owner-provided **public verification
key is configured** for production builds; no private key or credential is
included. Authenticated automatic download, safe installation/restart and signed
metadata renewal are implemented. No actual production release has been
published yet; configured public trust and controlled verification are not
publication evidence, Windows publisher signing/reputation, or Windows 11
certification. See the [protocol/provisioning contract](docs/update-protocol.md)
and [release operations](docs/releasing.md).
Installer rollback does not restore Workspace data; follow the
[Workspace recovery guidance](docs/workspace-format.md#restoration)
before restoring files externally.

Profiles offer a direct ongoing **Person note**, with **Other notes** retaining
all separately titled documents, including empty-body and archived notes.
When several active notes exist without a recorded choice, select one through
Other notes and **Use as ongoing**; the app never guesses from title, recency or
list order. A sole active note can be read directly without recording a choice.
The designation remains changeable and never edits, merges or deletes writing.
Search and Other notes open their exact target; **Open ongoing note** returns
deliberately to the continuing document. Existing notes open in
native rendered **Read** mode; one-click **Edit** reveals their autosaved title
and Markdown editors, and **Read** hides both editors again. **New Person note**
opens directly for writing. Empty note sections retain a message and New action,
and notes with empty bodies remain readable and editable. Notes show their Person owner and
never inherit to reports. **Archive note** preserves writing; **Show archived
notes** inside Other notes allows reading and restoring it.

Ongoing choices are device-local, per Workspace, in the existing app-local
`workspace-settings.json` lifecycle, not in synchronized Workspace documents.
The version-1 startup format accepts an optional `workspaces` extension keyed by
canonical folder (case-insensitive on Windows), note scope, stable owner email
and existing note GUID. Renaming a note does not change the choice. Other
Workspace entries and the successful startup folder are preserved when a choice
changes. Missing/archived ongoing targets retain the recorded choice and show
explicit recovery/re-selection guidance; no replacement is created or chosen.
Invalid/unsupported preferences are not overwritten, and read/write failures
remain visible without claiming that a new choice was remembered.
The same optional per-Workspace entry stores `lastView`, using `personEmail`,
the note's `source`, `noteId` and `noteOwnerEmail`, plus `historicalContextId`
for frozen Team writing. A Person-only view needs only the email. Inherited
and frozen note destinations retain their actual context, not an owned ongoing
substitute. Exact-view, ongoing-choice, pane-percentage and startup-folder writes
preserve each other and other Workspaces, using the existing atomic publication.
Failed view writes leave the in-session choice visible with an explicit
**NOT remembered** warning and retain the prior durable view. Deliberately
selecting the destination again retries the preference write.

The app watches local folder events and automatically reflects valid external
Person, Person/Team Markdown, Team context metadata, and photo changes, including new image bytes at the same
location. Whole-Workspace/graph validation precedes acceptance. Incomplete,
unsupported, or invalid deliveries visibly mark all retained information
**STALE / unavailable**, block writes, and never create false roots or repair
files. Complete valid delivery recovers automatically. A draft retains the note
version shown when editing began, including before its first autosave. External
changes cannot be overwritten by that draft or its retry: the local writing
stays visible, while the delivered document stays unchanged until you explicitly
discard the draft and edit the accepted version. Ordinary I/O failures remain
retryable. Images and raw
HTML never load content; HTTP/HTTPS links open only on deliberate selection.

**New Team note** explicitly opts into reporting-subtree scope. Writing a
**Person note** about a Manager never opts into inheritance. The profile separates
owned Team notes from **Current inherited Team context**, which includes notes
from every formal management ancestor and identifies each owning Person by name
and email. Informal leads/project membership are not reporting relationships.
Team scope is still private to the Workspace owner, not sharing.

Person writing stays first. Owned Team writing uses the same direct ongoing note,
explicit ambiguous choice, secondary **Other notes**, exact search and Read/Edit
interaction, with independent autosave/retry/discard. Team choices share the
device-local settings lifecycle above and preserve pane proportions. Every active
Team note still contributes inherited context, including secondary notes.
Inherited details start collapsed; attributed **Needs review** warnings stay
outside the collapsed section. Following a note opens its exact owner's review
workflow, subject to all existing writing/navigation protections.

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

Secondary **History** access retains the structured Profile timeline,
field-specific confirmations, source observations and read-only **Historical team
context**. Direct transfers and ancestor reorganizations capture the coherent
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

Read the [testing strategy](docs/testing.md) before placing, migrating or running
tests. It is the authoritative guide to test categories, commands, scenario
results, scale scheduling and future pre-publication requirements.
Default local verification is non-UI:

```powershell
.\eng\Run-CiLane.ps1 core
.\eng\Run-CiLane.ps1 filesystem
```

The complete real-file 10,000-Person non-UI workload runs separately with
`.\eng\Run-Performance.ps1`. Genuine WPF scale uses `.\eng\Run-UiScale.ps1` on
an authorized interactive desktop. Both reuse the nightly/manual `Scale
verification` workflow; the testing guide explains default-branch registration,
the existing CI manual bridge, exact-candidate results and future release reuse.
The ordinary filesystem lane excludes only `Category=Performance`; its durable
write, delivery, recovery and watcher checks remain required.

Actual WPF verification defaults to hosted Windows or a dedicated isolated
interactive desktop. **Local desktop automation requires separate explicit
permission.** On an authorized desktop run `.\eng\Run-CiLane.ps1 desktop`.
Named scenarios run sequentially with independent fixtures, JSON results and
failure diagnostics; missing/unexecuted work cannot pass. They retain real
photo pixels, Markdown rendering, draft protection, navigation, current/Historical
context and external refresh rather than repeating every business permutation.
Required smoke contains nine ordinary 1024 x 768 scenarios, including authenticated
update/settings, plus three explicitly selected responsive scenarios on a separate
required disposable hosted 1920 x 1080 runner. The high-resolution job verifies
an advertised/tested native mode before launching the app; it never changes a
local or shared display. Neither smoke suite uses the complete scale fixture.
The explicit UI-scale command runs two independent rich 10,000-Person scenarios
for realization/navigation/rendering and pending-mutation responsiveness.
See [performance validation](docs/performance-validation.md) for fixture detail
and historical evidence, not current execution instructions.

Every PR and main commit, including docs/test-only changes, retains four
parallel required lanes and a fail-closed aggregate `CI`. Deadlines and the
five-minute target are unchanged; the added UI lane has the same 11-minute ceiling
as ordinary desktop (40 minutes summed, at most 13 on the parallel critical path).
Core includes file-backed compatibility cases;
its project name does not make all tests isolated units. Filesystem coverage
proves durable writes, publication/delivery safety and recovery at the same
workflow interface. Product-check publication rejects leaked fixtures and test
assemblies; it is not installer or release verification.

Main is protected by an active GitHub **ruleset**, with no bypass actors,
requiring PRs and a strict up-to-date `CI` check from GitHub Actions. Only squash
merges are enabled. `eng\Configure-Repository.ps1` reapplies and verifies those
settings using the explicitly routed account; it does not push or merge code.

## Build the local Windows installer

Use PowerShell 7 on Windows x64 with the .NET 10 SDK:

```powershell
.\eng\Package-Windows.ps1 -Version 0.1.0
.\eng\Package-Windows.ps1 -Version 0.1.1
.\eng\Test-WindowsInstaller.ps1 -Version 0.1.0 -UpgradeVersion 0.1.1
```

Packaging uses the repository-local pinned `vpk` tool (1.2.161, matching the
Velopack client), a fresh self-contained WPF publish, and bootstrap validation.
Outputs are under `.artifacts\installer\0.1.0\releases`; this command does **not**
publish anything. See the shipped [installation guide](docs/INSTALLATION.md)
for the exact artifact set, metadata, Workspace separation and trust limitations.
Existing output is rejected rather than mixed with stale files. A custom
`-NuGetConfig` can explicitly select your approved package cache/source without
changing repository or global settings. By default normal repository/user NuGet
configuration inheritance is preserved, including configured transport settings;
locked restore and vulnerability auditing are not disabled.

The default `Test-WindowsInstaller` mode requires **Windows Sandbox already enabled**. It maps only
the built installer, self-contained test executables, and a dedicated results
folder, disables networking/clipboard/audio input/printer redirection, and drives
the real installed desktop. It checks default per-user installation/automatic
launch, local editing, a real two-version installation upgrade,
uninstall/reinstall and byte-for-byte unchanged external Workspace data.
Profiles, photos, Person notes, Profile changes and frozen former Team context
are also reopened through the real UI. No .NET runtime is installed separately.
No PowerShell execution policy or Windows trust protection is bypassed.

If Sandbox is unavailable, the command prepares the offline `.wsb` payload,
then **fails with an explicit verification blocker**. `-PrepareOnly` prepares it
without claiming a passed install test. Never run setup on a shared host merely
to make verification pass. `--installto` and environment overrides alone do not
isolate Velopack's user-registry and shortcut effects.

### Isolated hosted Windows verification

The separate **Isolated Windows installer** workflow runs on relevant PR changes
or manual dispatch. It does not replace the four required CI lanes.
It builds fixture versions `0.1.0` and `0.1.1`, tests all four fast bootstrap
hooks, then runs the actual setup/WPF/upgrade/uninstall lifecycle on a disposable
GitHub-hosted Windows runner:

```powershell
.\eng\Test-WindowsInstaller.ps1 -Version 0.1.0 -UpgradeVersion 0.1.1 -Mode Hosted
```

**Do not run that command on a shared machine.** Both the script's native harness
and its negative guard tests refuse ordinary host, self-hosted runner,
foreign-repository and out-of-checkout execution. Hosted mode requires the
GitHub Actions `github-hosted` Windows runner context for this repository.
`-PrepareOnly` stages the real inputs without installing or claiming success;
`tests\Installer.Preparation.Tests.ps1` exercises this safe preparation path.

The hosted test observes loaded `coreclr.dll` and WPF native module paths beneath
the installed app, proving bundled-runtime use even when the runner has SDKs.
It stages a real newer package and verifies that startup does **not** apply it
without authorization, before explicitly running the newer setup. The manual
fixture upgrade is not an authenticated automatic-update test.

Uninstall completion includes Velopack's deferred self-delete child, not merely
`Update.exe` exiting or the WPF executable disappearing. The harness assigns the
suspended uninstaller to an owned Windows job, waits for the entire job to become
quiescent, and requires the installation directory to be absent before reinstall.
The existing 150-second process/seven-minute lifecycle budgets remain unchanged;
there is no sleep, retry, or discarded failure to conceal overlapping cleanup.
Non-UI `UninstallProcessTests` exercise real parent/cleanup subprocesses, gated
cleanup, paths with spaces, and fail-closed timeout without running setup or WPF.

`result.json` records actual OS description/build/product type, runner image,
source commit, versions, uninstall completion barriers and lifecycle results. **`windows-latest` commonly runs
Windows Server, not Windows 11.** A passing server lifecycle is useful isolated
Windows x64 evidence, not Windows 11 client/servicing, SmartScreen, or corporate
policy certification. The product still targets supported Windows 11 x64.
No hosted result is claimed merely because packaging or preparation succeeds.

Only allowlisted result JSON, test/installer logs and unsigned artifact
inventories are retained for seven days. The workflow never uploads the
Workspace, build staging, executables/packages, credentials or signing material,
and does not publish releases or enable production CD. Networking is disabled
in Sandbox; hosted mode uses local fixtures but does not claim network isolation.
No production secrets are needed.

Installer verification, automatic updates and CD/release signing remain
separately evidenced slices; scale validation does not implement them.
Hosted installer evidence must be collected from the workflow, not inferred from
local build results. The installer remains unsigned.
