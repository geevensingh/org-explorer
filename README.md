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

## Run the first tracer

On Windows 11 x64 with the .NET 10 SDK:

```powershell
dotnet run --project src\OrgExplorer.Desktop
```

Choose an empty local folder outside the installation, then **Open Workspace**.
Choose a version-1 JSON file, review **Preview import**, and deliberately **Apply
reviewed import**. Select the Person and edit Preferred name; changes are saved
locally as you type. Empty clears it. Directory name remains visible. Reopen the
same Workspace to retrieve saved data. The app does not upload Workspace data.

Profiles also offer multiple private **Person notes**, an autosaved Markdown
editor, and a native rendered **Read** view. Notes show their Person owner and
never inherit to reports. **Archive note** preserves writing; **Show archived
notes** allows reading and restoring it. The app refreshes valid externally
edited note documents and reports save/delivery errors. Failed drafts remain
visible until retry succeeds or you deliberately discard them. Images and raw
HTML never load content; HTTP/HTTPS links open only on deliberate selection.

This first slice accepts `email`, `displayName`, `preferredName`, and
`managerEmail` in the existing import envelope. Other fields, including valid
contract fields not implemented yet, are explicitly rejected for the whole batch,
never discarded. The full import contract remains unchanged for later slices.
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
test-only flags, applies an import, opens a profile, edits, closes, and reopens to
verify durability. It also creates/edits/reads multiple Person notes, observes an
external document edit, checks failed-save retry/safe close/conflict recovery,
archives writing, and verifies no rendering requests reach a local HTTP listener.
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

Installer, automatic updates, complete imports, reporting/history/Team-note features,
and release signing are subsequent slices, not shipped capabilities of this tracer.
