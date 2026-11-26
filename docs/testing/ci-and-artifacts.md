# CI, local checks and artifacts

[Return to Testing](../testing.md).

For required lane/partition gates, non-UI contract checks, product-output isolation or cleanup evidence, use this reference. The [Testing entry](../testing.md#required-prmain-execution) keeps the safe default local commands. Read [desktop harness operation](desktop-harness.md) before native or invisible-control execution, and [scale verification](scale-verification.md) for explicitly selected rich suites.

## Required PR/main execution

Every PR and `main` commit, including docs/test-only changes, retains four
required logical lanes: workflow/view-model, filesystem, ordinary desktop,
and high-resolution desktop. Ordinary desktop uses two parallel, isolated
GitHub-hosted Windows jobs (`people` and `workspace`), approved on 2026-11-16.
High-resolution desktop likewise uses two isolated workers (`reading` and
`context`), with owner approval after the complete serial suite exhausted
its existing fifteen-minute limit.
Each native worker has its own disposable desktop; UI automation never runs
concurrently on one desktop.
The stable aggregate `CI` requires all four lanes, including both ordinary
and both high-resolution desktop workers. Failure, required skip,
cancellation, missing result and timeout cannot pass. Preserve the 5-minute
workflow/view-model, 11-minute filesystem, 15-minute limit for each ordinary
desktop worker, 15-minute limit for each high-resolution worker and 2-minute aggregate
deadlines: 78 minutes
summed, at most 17 minutes on the parallel critical path, excluding queues and
later CD. All four native desktop workers enforce matching 900-second whole-execution
budgets, including setup, temporarily raised with owner approval on 2026-11-15.
Both full scenario/size/assertion matrices, including profile-copy and SQLite
save coverage, remain required. Core/filesystem deadlines, separate scale-suite
budgets and save-latency targets are unchanged. If fifteen minutes is insufficient,
investigate rather than automatically extending it again.
The five-minute target includes setup, restore, build, fixtures, tests and cleanup;
passing a hard deadline does not claim that target or a new latency SLA.

`DesktopPartitions.json` assigns every existing ordinary scenario exactly once.
The `people` worker runs profile, draft, Person-note, Team/history and search
journeys. The `workspace` worker runs archival, compatibility, update and external
refresh journeys, followed by every existing compiled profile/note-refresh,
update-yield, release-isolation, release-trust and application-icon check.
Neither partition removes actions, samples, size matrices or assertions.
`HighResolutionPartitions.json` assigns `responsive-workspace` to `reading`
and the complete responsive draft and Team/history journeys to `context`.
Every original scenario, input, check and screen size remains required exactly
once. The original unpartitioned desktop and full high-resolution commands
remain available for an explicitly isolated desktop; explicit scale is unchanged.

Each worker uploads `scenarios.json`, `execution.json`, timing, logs and captures
in an artifact named `desktop-<partition>-attempt-<runAttempt>` or
`high-resolution-<partition>-attempt-<runAttempt>`.
`CI` validates both pairs of current-attempt artifacts against the exact candidate/run,
complete native selections and successful required companion commands. Missing,
duplicate, unexpected, stale or partial results fail closed even when a matrix
job reports success. Rerun each complete partition pair together; an earlier attempt's
successful artifact cannot fill a missing current-attempt result.
This changes execution capacity, not application performance targets or the
unidentified cause of longer UI observation calls.

## Local contract checks

Each runner isolates build artifacts. They may run in independent processes.
The filesystem runner excludes only `Category=Performance`; ordinary persistence,
delivery, recovery and watcher coverage remains required. The core runner also
checks the CI gate, separate-suite selection/results and named desktop-result
contracts without launching WPF.
It also runs `DocumentationHandoff.Tests.ps1` against isolated local Git
repositories and a bare remote, including a source branch that advances beyond
the approved documentation commit. Local path/file fixtures explicitly opt in.
Credential-route cases disable all transports and check rejection of SSH,
plain HTTP, embedded credentials and applicable Authorization headers, while
allowing harmless/unrelated headers without disclosing credential values.
Ref-preservation checks include local-only and remote-only tags with global and
remote pruning enabled. Mixed Markdown/gitlink commits must still be rejected
when clone settings hide submodules. These checks need no network or desktop.
For a narrow change, use `dotnet test` with combined selectors rather than
rebuilding/rerunning unrelated suites. Contract checks without launching WPF:

```powershell
.\tests\CiGate.Tests.ps1
.\tests\CiRunner.Tests.ps1
.\tests\ImplementationBaseline.Tests.ps1
.\tests\DraftDiscard.Tests.ps1
.\tests\Performance.Tests.ps1
.\tests\DesktopResults.Tests.ps1
.\tests\UiScale.Tests.ps1
dotnet test tests\OrgExplorer.Core.Tests --filter 'FullyQualifiedName~DesktopScenario|FullyQualifiedName~NavigationRejectionTests|FullyQualifiedName~ControlReadinessTests|FullyQualifiedName~UninstallProcessTests'
dotnet build tests\OrgExplorer.Desktop.Smoke -c Release
dotnet build src\OrgExplorer.Desktop -c Release
dotnet run --project tests\OrgExplorer.Desktop.Smoke -c Release --no-build -- `
  src\OrgExplorer.Desktop\bin\Release\net10.0-windows\OrgExplorer.Desktop.exe --verify-xaml
```

`ImplementationBaseline.Tests.ps1` runs in the core lane and invokes the real
preflight in child PowerShell processes against isolated local Git repositories
and bare remotes. It checks fetched versus stale local refs, divergence, failed
fetches, explicit remote/local integration baselines and checkout preservation.
Local remotes require the harness's explicit fixture opt-in. Credential-route
cases reject matching configured Authorization headers without contacting GitHub
or printing header values.
It needs no GitHub credentials, network or desktop.

## Product output and native resource contracts

Dependency lock files are authoritative. Restore missing assets with locked mode;
do not install unrelated tooling. A build proves compilation, not a desktop pass.

`tests\ApplicationIcon.Tests.ps1` decodes all ten native ICO frames and checks
transparency and C1 tile/card/Person content. With `-Executable`, it inspects the
real default PE icon group and compares every frame's bytes, without executing
the product. The core lane checks the asset; ordinary desktop product-output
isolation checks the published executable. The invisible compiled-window check
also requires the complete ICO to load through the window's actual `Icon`.
Existing installer-artifact checks inspect setup, full-package app and
`setup.ico`, portable launcher/app resources, and produced product/version
metadata. No installer is launched by these checks.

## Cleanup failures

On build-tree cleanup refusal, `cleanup-failure.json` retains the exact owned
failed path, original error and a bounded read-only Windows owner query through
the existing file-lease helper. The helper is compiled alone, not loaded from
the test binaries being removed. Missing owners and query failures stay unknown;
there is no deletion retry, owner shutdown or passing cleanup fallback.
Observation faults cannot replace the original lane failure. A real held-file
runner contract verifies this evidence and unchanged failing verdict.

## Desktop scenario results

`scenarios.json` is written before execution with all selected names `NotRun`,
then `Running`, then `Passed` or `Failed`. It includes times, fixture locations
and exception diagnostics. Interrupted work remains incomplete, never green.
The required lane validates against the manifest, not just exit code or PASS text.
Failures continue to independent scenarios while budget remains. Each has a fresh
fixture; successful fixtures are removed after workflows/apps stop. Failed small
fixtures stay at their reported path, and `*-ui.log` captures available control
IDs/names before abnormal app termination. Scale fixtures also live at their
reported scenario paths: apps/workflows stop before cleanup, successful fixtures
are removed, and failed fixtures remain for investigation. Hosted artifacts
include result JSON and logs, not the complete fictitious Workspace.
