# Testing

This is the authoritative test-placement and execution strategy for developers
and agents ([approved spec #21](https://github.com/geevensingh/org-explorer/issues/21)).
Choose the lowest-cost agreed surface that can prove the behavior, not a project
name. A desktop click alone does not establish a successful navigation or save.

## Placement

| Category | Observes | Examples and limits |
|---|---|---|
| Workflow / view model | The real shared `IApplicationWorkflow` and observable `WorkspaceViewModel`, primarily in memory | Identity, every textual field, import omission/null/false, whole graphs, inheritance, frozen context, dates, query permutations, favorite state and safe-close errors. In-memory success is not durability or WPF evidence. |
| Filesystem integration | The same interface backed by real isolated folders | Save/reopen, copied and decoded images, locks, atomic publication, invalid/incomplete delivery, concurrent clients, bounded watcher events, migration originals/recovery and retained context. A projection does not prove rendered pixels. |
| Desktop smoke | Normal WPF executable and actual UI Automation | Representative import/edit/save/reopen journeys; selection/bindings, Manager/report navigation, draft rejection feedback, local photo pixels, Markdown reading, current/Historical attribution, archive/filter/reactivation, Settings and refresh reaching an open client. |
| Non-UI performance | Complete real-file 10,000-Person operations | Load/reopen, search, durable mutations, reviewed imports, refresh and full rich semantic assertions. Targeted large-graph correctness remains in ordinary workflow coverage. |
| UI scale | Real WPF at the full rich population | Realization, large-population search/navigation, rendering and responsive Settings. Neither smaller fixtures nor view-model substitutes prove these risks. |
| Runner / CI contract | Observable execution and saved result contracts | Named results, sequential isolation, failure diagnostics, required selection, deadlines, required-lane failure matrix and product-output isolation. |

`OrgExplorer.Core.Tests` is **not synonymous with isolated unit tests**. Its two
file-backed `WorkspaceCompatibilityTests` carry `Category=Filesystem` and still
run in the core lane; they observe unsupported-version nonmutation and conversion
retry through the view model. `DesktopScenarioRunnerTests` also write real result
and fixture files (`Category=Runner`) without opening WPF. Most other core cases
use in-memory workflow state. Classify dependencies by what a test actually does.

Use independent expected values at the agreed public surfaces. Keep parsing,
storage mechanics and graphs behind the shared workflow; private-method tests,
per-record public repositories and success-only app flags are not migration seams.
Control only genuine external effects (clock, browser/release/installer boundary);
do not mock away the business behavior being asserted.

## Moving coverage

1. Read the actual assertions at both layers before removing a desktop check.
   A similarly named test is not equivalent coverage.
2. Add missing lower-level assertions first and run them. For example, a failed
   Person-note retry must not overwrite externally delivered writing; deliberate
   discard accepts that version, and a subsequent intentional edit reopens.
3. Retain the representative journey and any unique WPF behavior. Exhaustive
   field, query, note-scope, inheritance, graph, transfer, replay, archival,
   migration and delivery permutations belong below WPF; file safety stays
   file-backed. One rendered profile is not proof of every field's business rules.
4. Validate the migrated runner contract locally without UI, then run the real
   scenarios hosted. Keep missing/unexecuted work red. There is no separate
   coverage-map document; tests and these placement rules remain authoritative.

## Required PR/main execution

Every PR and `main` commit, including docs/test-only changes, retains three
independent parallel jobs: workflow/view-model, filesystem, and actual desktop.
The stable aggregate `CI` requires all three successes. Failure, required skip,
cancellation, missing result and timeout cannot pass. Preserve the existing
5/11/11-minute lane and 2-minute aggregate deadlines: 29 minutes summed, at most
13 minutes on the parallel critical path, excluding queues and later CD.
The five-minute target includes setup, restore, build, fixtures, tests and cleanup;
passing a hard deadline does not claim that target or a new latency SLA.

Default local verification is non-UI:

```powershell
.\eng\Run-CiLane.ps1 core
.\eng\Run-CiLane.ps1 filesystem
```

Each runner isolates build artifacts. They may run in independent processes.
The filesystem runner excludes only `Category=Performance`; ordinary persistence,
delivery, recovery and watcher coverage remains required. The core runner also
checks the CI gate, performance selection/results and named desktop-result
contracts without launching WPF.
For a narrow change, use `dotnet test` with combined selectors rather than
rebuilding/rerunning unrelated suites. Contract checks without launching WPF:

```powershell
.\tests\CiGate.Tests.ps1
.\tests\Performance.Tests.ps1
.\tests\DesktopResults.Tests.ps1
dotnet test tests\OrgExplorer.Core.Tests --filter FullyQualifiedName~DesktopScenarioRunnerTests
dotnet build tests\OrgExplorer.Desktop.Smoke -c Release
dotnet build src\OrgExplorer.Desktop -c Release
```

Dependency lock files are authoritative. Restore missing assets with locked mode;
do not install unrelated tooling. A build proves compilation, not a desktop pass.

## Real desktop execution

**Use hosted Windows or a dedicated isolated interactive desktop. Local desktop
automation requires separate explicit permission.** Routine test implementation,
this strategy and a request for CI do not grant permission to open/focus windows on
a shared workstation. An unavailable/locked interactive desktop is a visible
prerequisite failure, not a skipped photo assertion or an invitation to change
shared-machine settings.

On that authorized desktop, the required command is:

```powershell
.\eng\Run-CiLane.ps1 desktop
```

It builds the real product and harness, runs all selected scenarios **sequentially**
in one process, verifies complete named results, then publishes a local
product-check folder to reject leaked fixtures/test assemblies. That publish is
not installer verification or release publication. The runner's actual invocation:

```powershell
dotnet run --project tests\OrgExplorer.Desktop.Smoke -c Release -- `
  src\OrgExplorer.Desktop\bin\Release\net10.0-windows\OrgExplorer.Desktop.exe
```

The required names live in `tests\DesktopScenarios.json`. They cover:

- `profile-journey`: preview/apply/rejection, Manager/report identity, preferred/directory
  binding, representative partial change/clear, save/reopen, actual copied PNG
  and JPEG pixels, photo clear/placeholder and Profile timeline.
- `draft-navigation`: visibly pending autosave versus failed Person/Team drafts,
  explicit blocked actions, retained owner/draft, deliberate discard, then
  separately successful navigation.
- `person-note-rendering`: editing/read tabs, actual Markdown text, loopback
  no-fetch assertion, failed filesystem save, restored tree selection,
  Workspace/close rejection, retry, independent ownership and durable reopening.
- `team-context-history`: explicit Team ownership, all-ancestor attribution,
  review navigation/confirmation and archive/restore, actual current/frozen reading and honest
  historical dates through a representative ancestor move and reopen.
- `search-favorites`: representative profile/note queries, real current,
  archived and frozen destinations, reading, favorites, failed-save rejection
  and retained favorites after reopening.
- `archival-navigation`: rejected Manager archival, failed local save and retry,
  current-tree filtering, retained archived profile, selection protection,
  reactivation and explicit no-Manager UI.
- `compatibility-settings`: global Settings before open, supported conversion,
  unsupported delivered version/write blocking, recoverable conversion failure
  and validated retry.
- `external-refresh`: actual folder delivery changes the open profile, note
  editors, inherited reading/review and same-path photo pixels; invalid graph
  delivery becomes visibly stale, retains last good data and recovers.
- `scale`: the **existing complete 10,000-Person WPF workload**, including
  in-progress save rejection, remains required during this incremental migration.

For investigation, append one name to the harness command, not the app
(`profile-journey`, for example). Older `migration`, `--search-favorites`,
`--person-notes`, `--team-notes`, `--scale` selectors remain aliases.
A targeted pass is not the complete required lane. `DESKTOP_RESULTS_DIRECTORY`
may select a separate result directory for manual runs.

`scenarios.json` is written before execution with all selected names `NotRun`,
then `Running`, then `Passed` or `Failed`. It includes times, fixture locations
and exception diagnostics. Interrupted work remains incomplete, never green.
The required lane validates against the manifest, not just exit code or PASS text.
Failures continue to independent scenarios while budget remains. Each has a fresh
fixture; successful fixtures are removed after workflows/apps stop. Failed small
fixtures stay at their reported path, and `*-ui.log` captures available control
IDs/names before abnormal app termination. The scale fixture retains its own
isolated lifecycle and logs its path. Hosted artifacts include JSON and logs.

Successful navigation waits for meaningful local completion and asserts destination
email/context. Explicit rejection paths invoke once, assert retained owner,
selection/draft and actionable status, and never retry their way into success.
Pending and in-progress messages must explain waiting; failures require
retry/discard. Poll bounded eventual states; keep existing 20-second smoke and
180-second scale limits. Remote content is never fetched just to render notes or
profiles; loopback listeners and actual pixels/text are evidence, not filenames
or image dimensions alone.

## Scale scheduling and migration status

The approved end state separates complete performance workloads from required
PR smoke, preserving the rich 10,000-Person fixture and targeted graph correctness.
The non-UI portion from [#22](https://github.com/geevensingh/org-explorer/issues/22)
now runs separately through `.\eng\Run-Performance.ps1`, selected by
`Category=Performance`, and the nightly/manual `scale-verification.yml` workflow.
Consult
[performance validation](performance-validation.md) for implemented entry points
and historical measurements. Do not equate older timings with current results.
[#24](https://github.com/geevensingh/org-explorer/issues/24) supplies explicit
nightly/manual WPF-scale execution before removing `scale` from the default
desktop manifest. This ticket does **not** make that removal in advance.
No new baseline collection, regression budget or latency thresholds are implied.

The future release-publication workflow must execute the **same separate suite
commands fresh against the exact release-candidate commit**, fail on absent,
failed or timed-out work, and only then publish. Earlier nightly/manual artifacts
are diagnostic evidence, not reusable deployment authorization. This describes
a required future integration, not an already operational publication gate.

## Isolation and remaining blockers

Use fictitious people and local images, never company exports or production
credentials. Keep fixtures/results out of product output. Dispose workflows,
watchers, streams and application processes before cleanup. Preserve version,
data, error and rejected-save semantics while relocating tests.

Test migration neither repairs nor waives the separately parked rare
note-publication failure. If observed, report the exact failure rather than
masking it with retries. Installer verification, updater/signing and unfinished
release work remain separate blockers; test reorganization is not v1 acceptance
or permission to publish.
