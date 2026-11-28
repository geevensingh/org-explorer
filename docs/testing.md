# Testing

This is the authoritative test-placement and execution strategy for developers
and agents ([approved spec #21](https://github.com/geevensingh/org-explorer/issues/21)).
Choose the lowest-cost agreed surface that can prove the behavior, not a project
name. A desktop click alone does not establish a successful navigation or save.

## Placement

| Category | Observes | Examples and limits |
|---|---|---|
| Workflow / view model | The real shared `IApplicationWorkflow` and observable `WorkspaceViewModel`, primarily in memory | Identity, every textual field, import omission/null/false, whole graphs, inheritance, frozen context, dates, query permutations, favorite state and safe-close errors. In-memory success is not durability or WPF evidence. |
| Filesystem integration | The same interface backed by real isolated folders | Save/reopen, copied and decoded images, locks, atomic publication, invalid/incomplete delivery, concurrent clients, bounded watcher events, version refusal/recovery and retained context. A projection does not prove rendered pixels. |
| Desktop smoke | Normal WPF executable and actual UI Automation | Representative import/edit/save/reopen journeys; selection/bindings, Manager/report navigation, draft rejection feedback, local photo pixels, Markdown reading, current/Historical attribution, archive/filter/reactivation, Settings and refresh reaching an open client. |
| High-resolution smoke | Normal WPF executable on a separate disposable hosted 1920 x 1080 desktop | Required responsive geometry/type/width/divider/overlay/search/rejection and Team/history matrices; ordinary fixtures, not the 10,000-Person UI-scale suite. |
| Non-UI performance | Complete real-file 10,000-Person operations | Load/reopen, search, durable mutations, reviewed imports, refresh and full rich semantic assertions. Targeted large-graph correctness remains in ordinary workflow coverage. |
| UI scale | Real WPF at the full rich population | Tree realization/scrolling, large-population search/navigation, actual photo/Markdown rendering and responsive Settings while real load/mutation is pending. Neither smaller fixtures nor view-model substitutes prove these risks. |
| Runner / CI contract | Observable execution and saved result contracts | Named results, sequential isolation, failure diagnostics, required selection, deadlines, required-lane failure matrix and product-output isolation. |

`OrgExplorer.Core.Tests` is **not synonymous with isolated unit tests**. Its two
file-backed `WorkspaceCompatibilityTests` carry `Category=Filesystem` and still
run in the core lane; they observe unsupported-version nonmutation and validation
retry through the view model. `DesktopScenarioRunnerTests` also write real result
and fixture files (`Category=Runner`) without opening WPF. Most other core cases
use in-memory workflow state. Classify dependencies by what a test actually does.

Use independent expected values at the agreed public surfaces. Keep parsing,
storage mechanics and graphs behind the shared workflow; private-method tests,
per-record public repositories and success-only app flags are not migration seams.

Control only genuine external effects (clock, browser/release/installer boundary);
do not mock away the business behavior being asserted.

### Find the owning contract

| When changing or verifying | Open |
|---|---|
| Profile editing, identity, reporting, Team names, photos or Copy | [Person profiles](testing/person-profiles.md) |
| Preferred-name defaults, Save/Enter, cancellation or stale retries | [Person-text submission](testing/person-text.md) |
| Person-note/Team-note autosave, immutable drafts, retained writing or independent failures | [Note writing](testing/note-writing.md) |
| Ongoing/Other selections, Read/Edit, inherited ownership or current/frozen context | [Note reading and context](testing/note-reading-context.md) |
| Search ranking, accepted destinations, automatic Person selection or startup restoration | [Person search and navigation](testing/person-search-navigation.md) |
| Responsive reading, exact-row restoration, editor geometry or dividers | [Person reading layout](testing/person-reading-layout.md) |
| Workspace Create/Open, remembered views, Settings, close or exit | [Workspace opening and exit](testing/workspace-opening-exit.md) |
| Local SQLite Commit, WAL readers, receipts, compatibility or crash recovery | [Workspace storage and recovery](testing/workspace-storage-recovery.md) |
| Snapshot publication/adoption, periodic synchronization or whole-version choice | [Workspace synchronization](testing/workspace-synchronization.md) |
| Reviewed Import Apply/reentry, collector policy or captured producer evidence | [Import and producer verification](testing/import-and-producer.md) |
| Signed update drain, safe restart or installed Workspace preservation | [Application updates](testing/application-updates.md) |
| Required lane/partition gates, focused local checks, artifacts or product-output isolation | [CI and artifacts](testing/ci-and-artifacts.md) |
| Native interaction, invisible compiled controls, fixture isolation or visual evidence | [Desktop harness operation](testing/desktop-harness.md) |
| Ordinary/high-resolution smoke names and complete assertion/size matrices | [Required desktop scenarios](testing/required-desktop-scenarios.md) |
| Explicit full-rich performance/UI scale, latency policy, deadlines or union results | [Scale verification](testing/scale-verification.md) |
| Exact-candidate release gates, nightly/manual dispatch or publication prerequisites | [Release and manual verification](testing/release-and-manual.md) |

Add specialised guidance to its owning reference. Keep this entry a router;
the offline [documentation contract](testing/ci-and-artifacts.md#testing-documentation-contract)
checks its 300-physical-line budget and local navigation.

### Retired-engine coverage reconciliation (#100)

For retired protocol coverage and its current semantic replacements, read
[storage and recovery](testing/workspace-storage-recovery.md#retired-engine-coverage-reconciliation-100).

## Moving coverage

1. Read the actual assertions at both layers before removing a desktop check.
   A similarly named test is not equivalent coverage.
2. Add missing lower-level assertions first and run them. For example, a failed
   Person-note retry must not overwrite externally delivered writing; deliberate
   discard accepts that version, and a subsequent intentional edit reopens.
3. Retain the representative journey and any unique WPF behavior. Exhaustive
   field, query, note-scope, inheritance, graph, transfer, replay, archival,
   compatibility and delivery permutations belong below WPF; file safety stays
   file-backed. One rendered profile is not proof of every field's business rules.
4. Validate the migrated runner contract locally without UI, then run the real
   scenarios hosted. Keep missing/unexecuted work red. There is no separate
   coverage-map document; tests and these placement rules remain authoritative.

## Required PR/main execution

Default local verification is non-UI:

```powershell
.\eng\Run-CiLane.ps1 core
.\eng\Run-CiLane.ps1 filesystem
```

Every PR/main candidate retains workflow/view-model, filesystem, ordinary desktop
and high-resolution desktop lanes. Both native partition pairs and their complete
current-attempt artifacts remain required by `CI`; a local or focused pass is not
the complete candidate. For all selections, deadlines and failure/skip gates, read
[required CI and artifacts](testing/ci-and-artifacts.md#required-prmain-execution).
For a narrow non-UI change, choose the combined selectors or contract commands in
[local contract checks](testing/ci-and-artifacts.md#local-contract-checks).

## Real desktop execution

**Use hosted Windows or a dedicated isolated interactive desktop. Local desktop
automation requires separate explicit permission.** Routine test implementation,
this strategy and a request for CI do not grant permission to open/focus windows on
a shared workstation. An unavailable/locked interactive desktop is a visible
prerequisite failure, not a skipped photo assertion or an invitation to change
shared-machine settings.

Before native execution, read [harness setup](testing/desktop-harness.md#running-required-desktop-suites)
and [complete required selections](testing/required-desktop-scenarios.md).
For non-UI actual-BAML loading and its evidence limits, read
[invisible compiled checks](testing/desktop-harness.md#invisible-compiled-checks);
invisible checks do not establish native navigation, rendered pixels or acceptance.

## Separate scale commands and results

For explicitly selected full-rich workloads, clocks, acceptance qualifications and
worker/union results, read [scale contracts](testing/scale-verification.md#separate-scale-commands-and-results).
These are separate from required smoke, not a default local command.

## Nightly and manual execution

For hosted dispatch and workflow registration, read
[nightly and manual execution](testing/release-and-manual.md#nightly-and-manual-execution).

## Active pre-publication integration

For exact-candidate scale/release gates and non-UI release contracts, read
[pre-publication integration](testing/release-and-manual.md#active-pre-publication-integration).

## Isolation and remaining blockers

Use fictitious people and local images, never company exports or production
credentials. Keep fixtures/results out of product output. Dispose workflows,
watchers, streams and application processes before cleanup. Preserve version,
data, error and rejected-save semantics while relocating tests.

Test migration neither repairs nor waives the separately parked rare
note-publication failure. If observed, report the exact failure rather than
masking it with retries. Installer verification, owner signing/trust provisioning and release operating
prerequisites remain separate evidence; test reorganization is not v1 acceptance
or permission to publish.
