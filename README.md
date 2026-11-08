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
- [Installation and unsigned-installer trust limitations](docs/INSTALLATION.md)

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
test-only flags, applies an import, opens a profile, edits, closes, and reopens to
verify durability. Tests use fictitious people and isolated folders beneath the
checkout, cleaned after each run.

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

Authenticated automatic updates, complete imports, reporting/history/notes
features, and production release signing are subsequent slices. The installer
is unsigned; automatic apply and public CD remain disabled.
