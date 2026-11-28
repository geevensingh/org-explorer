# CI reliability and timing-contract audit

**Result:** #125 is an evidence audit, not a policy activation or a reliability
repair. Required coverage is substantially broader than the workflow names.
Historical failures recur, and the rich prefix has little demonstrated execution
headroom. Its motivating timeout remains unexplained at the causal level.
No acceptance limit, selector, product behavior, workflow or release dependency
was changed. No new numerical warning, regression or variability rule is approved.

## Revision, scope and preflight

Audited on 2026-11-25 for [#125](https://github.com/geevensingh/org-explorer/issues/125)
under [spec #123](https://github.com/geevensingh/org-explorer/issues/123).
Both live issue bodies and comments were read; both had no comments. The native
`125/dependencies/blocked_by` endpoint returned an empty array. Later tickets were
not implemented, dispatched, edited or closed.

| Identity | Verified value |
|---|---|
| Isolated implementation branch | `geevensingh-ci-reliability-audit` |
| Reviewed HEAD and fetched receiving baseline | `4334a254827d450151b536347b52dc20b49fd487` |
| Receiving branch | `origin/integration/ci-reliability`, owned by #123 and coordinating session `8c351f18-fa29-43ff-b4d3-28f3ee307aaa` |
| Fresh main / documentation immediate base | `caec4da7a96a57a5e49b3ea047f29eaabf299835` |
| Approved documentation source | `geevensingh-ci-reliability-design`, commit `4334a254827d450151b536347b52dc20b49fd487`, exactly `docs/ci-reliability-plan.md` |
| Product source tree | `282cee93d5941373304a065008ae5cdae06f22cf` |
| Test tree | `9cba34a7e730fab4d47061f1c1ed657936bbd7f5` |
| Runner tree | `a25d28c1af57cd71196c6141c304685794ec445a` |
| Scale workflow blob | `019d5924de3378d077fac76abc8c04452846d18a` |
| CI workflow blob | `6bca4e7e585fef8e9a8c2b6cbdb6a0562bcb5cb9` |

`Test-ImplementationBaseline.ps1 -BaseBranch integration/ci-reliability` passed
in this worktree on a clean named branch: ahead 0, behind 0, diverged false,
remote baseline mode. Repository/account checks used `geevensingh` and the HTTPS
`geevensingh/org-explorer` origin with the account-specific process token.
`Verify-DocumentationHandoff.ps1` freshly fetched source/main and verified the
recorded parent, exact nonempty Markdown-only delta, source reachability and
absence from main. Incorporation was verified with
`git merge-base --is-ancestor 4334a254827d450151b536347b52dc20b49fd487 HEAD`.
Source SHA equals applied SHA; no cherry-pick approximation was used.

The integration delta from fresh main is only the approved 303-line design.
Current source, tests, runners, SDK configuration, workflow selection and release
contracts therefore agree with main. Domain guidance, GLOSSARY, ADR 0005,
ADR 0008 and the relevant testing/release guidance were read. Material assumptions
reconciled: SQLite/WAL local authority, independent complete-snapshot sync, four
required logical lanes with partitioned native workers, separate explicit scale,
actual prefix-to-tail Join, temporary rich-note ceiling, and fresh release gates.
No material product/spec contradiction was found. The spec's pending ownership
fields are resolved by #125's handoff and the coordinator's explicit destination.
The design's approximate successful-prefix duration is refined below, not a new
acceptance interpretation.

All source links in the inventory refer to this immutable
[reviewed revision](https://github.com/geevensingh/org-explorer/tree/4334a254827d450151b536347b52dc20b49fd487).
This audit is not fresh native acceptance of the documentation-bearing commit:
retained main evidence has identical affected contracts but its own candidate.

## Actual lane and selector inventory

All five repository workflows were inspected, not just the required check.
`windows-latest` is a moving label; checkout/setup use v4 Actions and SDK `10.0.x`,
while `global.json` requests `10.0.100` with `latestFeature` roll-forward and
locked package restores. Each hosted native job has its own disposable desktop.
Nothing below authorizes desktop execution on a shared workstation.

| Lane / entry point | Trigger and actual selection | Proof and completeness boundary |
|---|---|---|
| Core: `Run-CiLane.ps1 core` | Every PR and main push, including docs-only; manual CI too. Unfiltered `OrgExplorer.Core.Tests` plus the required PowerShell companions listed below. | Workflow/view-model domain behavior, real producer CLI and runner contracts; some real-file cases, not exclusively unit tests. Current main TRX has 753 executed/passed, 0 failed/skipped. A command failure propagates to the lane; this count excludes companion executions. |
| Filesystem: `Run-CiLane.ps1 filesystem` | Same CI triggers. `OrgExplorer.Persistence.Tests --filter Category!=Performance`; excludes only the explicitly categorized complete scale test. | Real isolated local stores, snapshot publication/Join/adoption, full retention, writer locks, crash/recovery, photos, update/restart and watcher behavior. Current main TRX has 538 executed/passed, 0 failed/skipped. In-memory or UI success does not replace these assertions. |
| Desktop people: `-DesktopPartition people` / `--desktop-people` | Same triggers; five scenarios: `profile-journey`, `draft-navigation`, `person-note-rendering`, `team-context-history`, `search-favorites`. | Normal WPF EXE, actual UIA, keyboard/mouse, pixels and durable reopen. Includes exact Person/Manager identity, profile Save/Cancel and Preferred-name submission, notes/ownership/current/frozen reading, archive-aware search, Favorites and rejected changes. |
| Desktop workspace: `-DesktopPartition workspace` / `--desktop-workspace` | Four scenarios: `archival-navigation`, `compatibility-settings`, `update-settings`, `external-refresh`; then `--verify-profile-refresh`, `--verify-update-yield`, production-trust publish/checks and output-isolation check. | Real WPF recovery/settings/refresh/update journeys plus nested compiled contracts. Controlled loopback update trust/effects only in the test build; fresh production output must reject them and all test/fixture leakage. |
| High-resolution reading: `-HighResolutionPartition reading` / `--high-resolution-reading` | `responsive-workspace`, required on all CI triggers. | Actual 1920 x 1080 hosted desktop, 96 DPI, advertised/tested/read-back mode, adequate work/track area; complete reading/search/photo/overlay/resize/restore matrices. |
| High-resolution context: `-HighResolutionPartition context` / `--high-resolution-context` | `responsive-draft-navigation`, `responsive-team-history`, required on all CI triggers. | Complete retained-draft/rejection/keyboard reachability and owned/inherited/frozen context matrices. Ordinary native results cannot substitute for these scenarios. |
| `CI` aggregate | `always()` after core/filesystem and both native matrix groups. | Four logical outcomes must all be success. Both current-attempt artifact pairs must match exact revision/run/attempt, exact scenario partition union, successful required companion commands and complete execution metadata. Missing, duplicate, unexpected, stale, cancelled, skipped or partial proof fails closed. |
| Non-UI performance: `Run-Performance.ps1` | Scale schedule `23 10 * * *` UTC, direct manual exact-SHA dispatch, reusable call from release/renewal/rehearsal, or default-off manual CI bridge. `Category=Performance`. | Exactly one named `ScaleWorkflowTests.Ten_thousand_People_keep_complete_durable_current_and_frozen_context_through_mutations_and_delivery` TRX result; exactly one fresh TRX, completed counters, exact selection and pass are required. Ordinary CI does not execute it. |
| Rich prefix: `Run-UiScale.ps1 -Worker prefix` / `--ui-scale-prefix` | Same explicit scale paths. `scale-mutation-save-prefix`. | Both full normal-EXE and held-export note phases, real held profile Commit, all background profile samples, latest publication, normal host death and actual logical snapshot capture remain together. |
| Rich tail: `-Worker tail -PrefixResultsDirectory ...` / `--ui-scale-tail` | Fresh worker after successful prefix. `scale-mutation-delivery-tail`, then `scale-navigation-rendering`. | Public Join of that prefix's actual snapshot/preferences into a new physical store; full public-state/resource comparison; normal-EXE profile samples, favorite/import/delivery/reopen; separate original navigation fixture with real tree realization and rendered evidence. No equivalent reseeding of the mutation handoff. |
| Scale union: `Assert-UiScaleUnion.ps1` | `always()` collector after non-UI/prefix/tail. | All job outcomes, both terminal workers, ordered original phases, raw note/profile/lifecycle evidence and matching candidate/run/attempt/source/product/native/handoff identities. Diagnostic, stale, substituted or over-budget evidence cannot pass. |
| Isolated installer | Path-filtered PR or manual `installer-verification.yml`; not an unconditional main CI lane. Includes `src/**`, desktop smoke, installer/trust tools and relevant configuration/docs. | Deferred-uninstall tests; two real self-contained fixture versions 0.1.0/0.1.1; install/start/authenticated update/automatic reopen/uninstall/reinstall; Workspace preservation and icon checks; production-trust/effects isolation. Hosted Server evidence is not Windows 11 client, publisher reputation/trust, production signing-key provisioning or visual owner acceptance. |
| Product release | Trusted successful main **push** CI completion, product-impact planning, then fresh scale and package/sign/publish. Docs-only no-op can succeed without running scale or publishing. | Revalidates original CI layout at exact source, seven current required physical CI jobs, fresh current-run/attempt scale artifacts, exact product bytes/trust, main/publication baseline and asset provenance. Historical timing is not release approval. |
| Metadata renewal | Daily `17 9 * * *` UTC in production release workflow. Plan may no-op; due renewal calls fresh scale at original **product** SHA. | Separately authenticates current main controller/CI and original product/CI/trust/assets; preserves original app/version/package/bootstrap bytes, signs bounded metadata, rechecks before promotion. No successful due-renewal execution is demonstrated by this audit's sampled logs. |
| Release rehearsal | Default-off manual CI bridge `run_release_verification=true`; reusable `release-verification.yml`. | Fresh exact-candidate scale, release/trust contracts and genuine fixture packaging/signing/renewal with ephemeral keys; named secret-free evidence, no production publication. No package job from this bridge was found in the bounded history. |

Sources:
[ci.yml](https://github.com/geevensingh/org-explorer/blob/4334a254827d450151b536347b52dc20b49fd487/.github/workflows/ci.yml),
[Run-CiLane](https://github.com/geevensingh/org-explorer/blob/4334a254827d450151b536347b52dc20b49fd487/eng/Run-CiLane.ps1),
[DesktopResults](https://github.com/geevensingh/org-explorer/blob/4334a254827d450151b536347b52dc20b49fd487/eng/DesktopResults.ps1),
[scale workflow](https://github.com/geevensingh/org-explorer/blob/4334a254827d450151b536347b52dc20b49fd487/.github/workflows/scale-verification.yml),
[PerformanceSuite](https://github.com/geevensingh/org-explorer/blob/4334a254827d450151b536347b52dc20b49fd487/eng/PerformanceSuite.ps1),
[UiScaleResults](https://github.com/geevensingh/org-explorer/blob/4334a254827d450151b536347b52dc20b49fd487/eng/UiScaleResults.ps1),
[installer workflow](https://github.com/geevensingh/org-explorer/blob/4334a254827d450151b536347b52dc20b49fd487/.github/workflows/installer-verification.yml),
[release policy](https://github.com/geevensingh/org-explorer/blob/4334a254827d450151b536347b52dc20b49fd487/docs/releasing.md).

Repository enforcement is not inferred from prose: the legacy branch-protection
endpoint returned 404, but ruleset **26788996**, "Main requires passing CI PRs",
is active on `refs/heads/main`, with strict `CI` status from integration 15368,
PR/squash and resolved-thread requirements, no bypass actors, and deletion/non-fast-forward
protection. Therefore the 404 does **not** mean main has no CI protection.

### Nested checks, repetition and fixture matrices

Core's mandatory companions, in execution order, are `CiGate.Tests.ps1`,
`CiRunner.Tests.ps1`, `ImplementationBaseline.Tests.ps1`,
`DocumentationHandoff.Tests.ps1`, `DraftDiscard.Tests.ps1`,
`Performance.Tests.ps1`, `DesktopResults.Tests.ps1`, `UiScale.Tests.ps1`,
`Release.Tests.ps1`, `Release.Gates.Tests.ps1`, `Release.Publication.Tests.ps1`,
`Release.Renewal.Tests.ps1`, `Release.Workflow.Tests.ps1`,
`Release.Payload.Tests.ps1`, `Update.TrustLifetime.Tests.ps1`,
`ApplicationIcon.Tests.ps1`. DraftDiscard reruns both named Person/Team public
cases from the already-built Core assembly and refuses partial/skipped evidence.
These contracts exercise real validators, temporary Git stores and ephemeral
cryptography; fake GitHub effects are not hosted publication proof.
Core also includes real filesystem compatibility and runner fixtures, producer
compiled-CLI leasing/serial/two-worker cases, uncapped wide/deep reporting graphs
and the warmed shared-note allocation bound. They are not full UI-scale runs.

Filesystem nested workload examples verified in source:

| Workload | Actual bounded repetition / fault matrix |
|---|---|
| `WorkspaceReadLifetimeTests` | Two long-running refresh-reader variants competing with 20 real note/favorite writes; exact final writing/favorite state and disposal nonnotification. |
| `SqliteWorkspaceTests` | Person/Team continuous typing: 25 inputs spaced 60 ms, first durable progress within one second, at least three observed committed versions, exact final reopen. Separate six-input observation and predetermined 20-input final-writing regressions retain their own timing semantics. |
| `WorkspaceWriteObservationTests`, WAL and public-reader causality | Held real snapshot/Commit boundaries, concurrent independent writers, paired physical read disposal, older coherent reader/newer committed reader and observer-fault nonmutation. |
| `SqliteRecoveryTests`, `SqliteShmRecoveryTests`, snapshot tests | Before/after real process-death, Person/Team/profile and held-receipt/evidence variants; mapped/stale SHM, incomplete/corrupt envelopes, ancestry, resource and recovery-retention cases. |
| `WorkspaceSnapshotTests`, delivery/profile/note suites | Both note scopes and delivery modes; repeated edit generations; same/different-field stale bases; held synchronization with local Save; complete version adoption/rejection and no replay or independent-draft loss. |
| Update settings and external refresh | 128 bounded settings observations; repeated refresh/replay and public restart/cryptographic/transport/process-effect checks. Original behavioral retries test recovery; they are not rerunning failed CI until green. |

`--verify-profile-refresh` is a substantial compiled-product suite, not one
getter assertion. It loads real invisible MainWindow BAML/connectors on STA and
includes exit/startup/process isolation, legacy save feedback, Preferred-name
retention/deferral, cutover publication/note modes/acknowledgment/failure/incoming/
close/recovery, independent-note leave, Import admission and Manager demand,
ordinary fixture state, current search/note demand, functional save/close
preparation, rich host restoration, incoming publication, note readiness/context,
Copy, tree/overlay/report geometry, owned Team-name peers, SQLite creation and
held-note navigation, snapshots/local recovery, profile/notes refresh, selection
restoration and compact search activation. `--verify-update-yield` adds actual
compiled updater/owned exit behavior. These remain required companions even
after all four workspace native scenarios pass. Their final invisible/no-native-handle
assertion means they cannot certify desktop pixels or user keyboard behavior.

The high-resolution matrix retains 700 x 500, 1100 x 900, widths 959/960/961,
maximized and minimum Person allocation, with each journey's original applicable
reading/form/history matrix. Exact restored row identities and positions remain
within one rendered pixel; photo/initials remain 144 DIP; layout limits include
People 240 DIP, Person 420 DIP and prose 600 DIP. These are hard UI assertions,
not timing-health thresholds. Ordinary fixtures remain ordinary (including
the responsive 44-Person graph); no smaller fixture substitutes for rich scale.

All rich selections use 10,000 People: 9,900 active, 100 Archived, a 42-edge chain,
2,500 photo references, 1,000 Person notes, 111 Team notes, 200 initial frozen
contexts and complete retained Profile evidence. Each note phase keeps **20
Team-editor / 42 Person-navigation / 21 durability samples**, six continued inputs
per note at nominal 250 ms. Rich profile keeps 21 Save samples in background-export
and 21 in normal-EXE tail; non-UI has its own 21 explicit profile Save samples,
100 navigation and 100 profile queries, full 10,000-record reviewed imports,
initial/latest replay, delivery/recovery and exact retained-data comparisons.
Samples inside one process do not supply independent cohort executions.

Required prefix phases, exactly in order:
`UI-mutation-open`, `UI-ordinary-save-latency`, `UI-publish-before-held-export`,
`UI-close-before-held-export`, `UI-background-export-independence`,
`UI-profile-mutation`, `UI-handoff-capture`. Profile mutation is nested within
background-export, so successful ends need not share begin order.
Tail phases: `UI-handoff-join`, `UI-ordinary-profile-latency`,
`UI-favorite-mutation`, `UI-import-file`, `UI-import-navigation`,
`UI-validated-refresh`, `UI-import-preview`, `UI-import-apply`,
`UI-import-return`, `UI-external-delivery`, `UI-close-before-reopen`, `UI-reopen`,
`UI-close`, `UI-load`, `UI-tree`, `UI-current-search`, `UI-photo-render`,
`UI-history-search`, `UI-manager-navigation`, `UI-deep-profile`,
`UI-final-person`, `UI-close`. Both distinct `UI-close` occurrences are required.
Manifests, public Join receipt, exact logical snapshot/preferences/resource hashes,
product/harness component inventory and loaded native SQLite binding are checked;
raw DB/WAL/SHM transfer is forbidden.

There is **no dedicated long-duration whole-suite soak lane** in the current
five workflows. There are genuine targeted concurrent-client, repeated-mutation,
multi-sample and process-death checks above. Historical TEMP CrashRecovery
diagnostics are separate observations, not current required lanes.

## Current policy ledger (unchanged)

| Lane / metric | Existing goal or information | Current enforced requirement | Execution safety / watchdog distinction |
|---|---|---|---|
| Core whole execution | 300 s target includes checkout/setup, restore/build/tests/fixtures/cleanup; queue/CD excluded. | 300 s dotnet-command remaining budget; required test/companion success. | Actions 5 min equals the lane ceiling: no separately reserved reporting/upload allowance. In-process companions are not individually timed/budget-killed and the final timing write has no separate elapsed-time refusal. Do not mistake a local timing file for complete candidate proof. |
| Filesystem whole execution | 300 s target. | 660 s remaining budget and required persistence success. | Actions 11 min equals that ceiling; reporting can be interrupted at the job watchdog. |
| Each ordinary/high-resolution native worker | 300 s target, not a new product latency SLA. | 900 s remaining command budget and collector-enforced complete execution <=900 s. | Actions 15 min equals ceiling; not extra passing/reporting time. |
| CI collector | No product/performance target. | All required logical lanes and current-attempt partition evidence. | 2 min Actions job watchdog. |
| Non-UI and each rich worker | Named phase timings are diagnostic unless independently enforced below. | Each has its own 660 s whole-execution acceptance including setup and cleanup. Full named results required. | 13 min Actions watchdog provides reporting/termination/upload allowance, **not** 780 s acceptance. Current safety and acceptance boundaries are otherwise coupled. |
| Direct extended UI observation | Diagnostic only, including observed completion. | Cannot report acceptance or supply an accepting prefix/union, even if completion is <660 s. | Requests up to 900 s while the job watchdog is 780 s; hence 900 s capture is not guaranteed. Neither bound authorizes acceptance. Non-UI/release do not opt into it. |
| Rich note request-to-postcommit acknowledgment | p95 goal 1,000 ms. | Temporary enforced 1,050 ms ceiling in **both** phases; 21 raw finite numeric samples, all committed during typing, exact final owner/prefix/title/body reopen, honest `goalMet`. | Original functional observation remains 20 s; full-reopen verification is separately timed, never deducted or used to backdate Commit. |
| Rich Team-editor / Person navigation | Historical 100 ms reference; `budgetEnforced=false`, `timingUse=informational`. | Exact usable states, complete 20/42 samples and all ownership/lifetime assertions. | Not permission to remove 20/180 s readiness observations. Earlier 100-ms-policy failures stay failed. |
| Profile Save / ordinary small-fixture note | p95 <=1,000 ms profile Save; small-fixture first durable progress <=1 s. | Existing one-second enforcement, queue/scheduling included. Rich note exception does not apply here. | Held Commit is a fault/usability proof, not a healthy-save sample. |
| Application local-save stall / SQLite busy | Explicit real queue-inclusive stall after 3 s; SQLite busy 1 s, WAL/FULL on actual connections. | Stalled/unknown outcome, retained drafts and browsing; no optimistic saved acknowledgment, rollback-journal fallback or forced checkpoint. | Product behavior requirements, not whole-suite timeouts or rules to calibrate away. |
| Held-note/discard admission | Filesystem entry 5 s; Core busy-discard entry temporarily 6 s (approved 2026-11-24). | Both scopes, real held boundary and exact discard/writing assertions. | Test readiness deadlines, not user-facing Save SLA. Recent timeout diagnostics do not prove a scheduling cause was repaired. |
| Ordinary / scale state observations | Ordinary generally 20 s; rich scale generally 180 s, including held boundary observations. | Required UI/owner/data/held-operation state within original observation. | Distinct from whole-worker cutoff; do not pad these to hide a failed state. |
| Installer | Lifecycle/OS/trust coverage reported explicitly; no performance baseline. | Real passed lifecycle and exact preservation/automatic update/cleanup barriers. | Actions 20 min; Sandbox mode separately has 480 s including boot, plus owned exit handling. Hosted mode is not Sandbox acceptance. |
| Product release / renewal / rehearsal | Publication intent/no-op, original source/trust and validity policy, not timing baseline. | Fresh exact-run/attempt scale acceptance and revalidation; 14-day manifests, renewal due at <=7 days, unchanged original app/bytes. | Plan 5 min, package/publish/renew jobs 15 min; production group noncancelling. Reference collection is not a prerequisite. |
| New health / comparison / eligibility | Proposed categories: lane headroom, phase/operation change, variability and trends, explicit unbaselined/inconclusive. | **No new numeric warning/red/noise/promotion gate exists or is selected here.** Existing hard assertions remain blocking. | Separate healthy bands and higher safety bounds require comparable observations and one-time #130 approval before #131 activation. |

Nearest-rank p95 remains the sorted sample at `ceil(0.95 * count)`, one-based.
For 21 samples this is the twentieth; a cohort median cannot replace any
execution's raw p95, hard violation, completeness check or original verdict.
Some diagnostic prose (for example the held-Import result's historical
navigation p95<=100-ms wording) lags the actual informational policy metadata.
Consumers must read the effective numeric/enforcement fields and source policy,
not activate a stale prose threshold.

## Bounded all-attempt history

Discovery covered **2026-11-18T00:00:00Z through 2026-11-25T17:33:48Z**, inclusive
run-creation selection, all events/branches/outcomes, with no success filter.
Two paginated run pages returned 138 runs. Every attempt `1..run_attempt` was
queried individually; every jobs page was checked against `total_count`:
**150 attempts, 762 jobs**. This includes earlier attempts superseded by reruns.
It does not include runs created before the lower boundary and rerun inside it,
deleted runs, inaccessible earlier history or future outcomes. It is not a
randomized reliability experiment or an estimate of healthy-main failure probability.

The durable [evidence snapshot](ci-reliability-audit-evidence.jsonl) retains every
attempt's exact workflow head SHA, branch, event, original conclusion, timestamps,
job IDs/names/outcomes/failed-step names and source URLs. `workflowHeadRevision`
is controller provenance, not automatically the tested product revision.
`observedScaleCandidates` records actual runner-reported revisions when retained
logs were read; otherwise tested-product identity is explicitly unestablished.
In sampled scale logs those candidates matched their recorded workflow heads.

| Workflow | Attempts | Success | Failure | Cancelled | Skipped |
|---|---:|---:|---:|---:|---:|
| Continuous integration | 67 | 33 | 30 | 4 | 0 |
| Isolated Windows installer | 20 | 17 | 3 | 0 | 0 |
| Product release, including renewal planning | 16 | 9 | 6 | 0 | 1 |
| Scale verification | 14 | 3 | 11 | 0 | 0 |
| Historical TEMP CrashRecovery diagnostics | 5 | 1 | 3 | 1 | 0 |
| GitHub Copilot Code Review (auxiliary, not product test proof) | 28 | 28 | 0 | 0 | 0 |
| **All discovered** | **150** | **91** | **53** | **5** | **1** |

For example, CI has 30/67 failed and 4/67 cancelled attempts; these describe a
mixed development/manual/history sample, not an attribution to hosted noise.
The logical `CI` job itself has 35 successes, 31 failures and 1 skip:
some whole-workflow failures were optional scale failures after `CI` passed.
Neither a successful required aggregate nor a successful no-op release claims
unexecuted scale/installer/rehearsal coverage.

| Physical lane name (historical layouts kept separate) | Success | Failure | Cancelled | Skipped |
|---|---:|---:|---:|---:|
| Core and view models | 62 | 4 | 0 | 1 |
| Real filesystem persistence | 58 | 8 | 0 | 1 |
| Ordinary desktop, old unpartitioned | 28 | 1 | 0 | 0 |
| Desktop people | 36 | 1 | 0 | 0 |
| Desktop workspace | 30 | 8 | 0 | 0 |
| High-resolution, old unpartitioned | 18 | 6 | 6 | 0 |
| High-resolution reading | 35 | 1 | 0 | 0 |
| High-resolution context | 36 | 0 | 0 | 0 |

These layout counts are not interchangeable baseline populations. There are 12
split rich-prefix executions across CI bridge/release/direct scale: 4 successes,
8 failures; all 8 failed prefixes leave the tail skipped and union failed.
These downstream outcomes remain recorded but are not 16 additional primary
defects. Older combined rich executions are a different workload series.

All 58 non-success/non-skipped attempt logs were retrieved, plus 3 successful
direct scale attempts, current main CI/release and the latest successful
installer: **64 logs, all retrievals successful**. Failure review distinguished
intentional negative-case messages within passing tests from terminal errors.
Detailed artifact content review was concentrated on the motivating two release
runs and current-main core/filesystem; other failures were reviewed through
retained full logs/job metadata, not every screenshot or snapshot payload.
All 138 run artifact inventories were retrieved: **409 artifacts, none marked
expired**, 966,895,629 compressed bytes in total, including duplicated union
copies. Availability metadata does not mean every archive was downloaded.
Installer explicitly requests seven days; CI/scale use repository-default
retention (the sampled release archives expire after 90 days). No durable
automated numeric history currently survives loss of these artifacts.

### Preserved symptoms and attribution

| Retained evidence | Observed outcome; limits on interpretation |
|---|---|
| [44383352513/1](https://github.com/geevensingh/org-explorer/actions/runs/44383352513/attempts/1), [44591924656/2](https://github.com/geevensingh/org-explorer/actions/runs/44591924656/attempts/2), [45123281436/1](https://github.com/geevensingh/org-explorer/actions/runs/45123281436/attempts/1) | Filesystem `Retaining_newer_typing...` admission refusals at the five-second entry boundary. Later successful attempts remain separate; no timer/thread-pool cause is proved. TEMP diagnostic selections also retain this symptom. |
| [44998243412/1](https://github.com/geevensingh/org-explorer/actions/runs/44998243412/attempts/1), [45136206509/1](https://github.com/geevensingh/org-explorer/actions/runs/45136206509/attempts/1), [45509328822/1](https://github.com/geevensingh/org-explorer/actions/runs/45509328822/attempts/1) | Core busy-discard Team admission failures; latest log retains `NOTE-SAVE-CORE-ENTRY-REFUSAL`. Its later non-atomic snapshot says admission signal completed and available workers remained; that does not prove why the earlier wait timed out. The temporary six-second policy/diagnostics are not a demonstrated root-cause fix. |
| [45517319215/1](https://github.com/geevensingh/org-explorer/actions/runs/45517319215/attempts/1), [45519388019/1](https://github.com/geevensingh/org-explorer/actions/runs/45519388019/attempts/1) | Required compiled profile-refresh companion fails after native scenarios. Stack identifies `VerifyLocalReviewedImportControls`, waiting for Apply enabled after Preview. Different older profile-refresh timeout [44431040950/1](https://github.com/geevensingh/org-explorer/actions/runs/44431040950/attempts/1) was in compact identity; do not merge these into one assumed cause. |
| [45519388019/2](https://github.com/geevensingh/org-explorer/actions/runs/45519388019/attempts/2) | Real stale-SHM process-death case fails accessing an in-use `workspace.sqlite-wal`. Exact actor/lifetime is unknown. Attempt 3 at the **same** `551e977d917169fa8b162310ccebaae445378ec6` succeeds; that is recurrence evidence, not repair. |
| [44967124997/1](https://github.com/geevensingh/org-explorer/actions/runs/44967124997/attempts/1), [45483634095/1](https://github.com/geevensingh/org-explorer/actions/runs/45483634095/attempts/1) | Non-UI test completed, but runner cleanup rejects deletion of mapped/in-use `e_sqlite3.dll`. Cleanup is part of validation: retain failure, not successful reference evidence. Current exact-file owner observation exists; no deletion retry or guessed process termination is warranted. |
| [45081762689/1](https://github.com/geevensingh/org-explorer/actions/runs/45081762689/attempts/1) | Definite historical harness refusal: path-unsafe visual evidence state name. Current source has explicit names; no new patch is needed from this audit. |
| [45107744780/1](https://github.com/geevensingh/org-explorer/actions/runs/45107744780/attempts/1), [45114601837/1](https://github.com/geevensingh/org-explorer/actions/runs/45114601837/attempts/1) and attempt 2 | Installed authenticated reopen fails old all-private-byte preservation. Current contract separately compares complete logical state and allows only exact SQLite DB/WAL/SHM physical differences; other files remain strict. Historical failures are not retrospectively accepted. Latest passed [45519387842/1](https://github.com/geevensingh/org-explorer/actions/runs/45519387842/attempts/1) has its own source/OS/trust limits. |
| [45245513853/1](https://github.com/geevensingh/org-explorer/actions/runs/45245513853/attempts/1) and attempt 2; renewal [45268465975/1](https://github.com/geevensingh/org-explorer/actions/runs/45268465975/attempts/1), [45477625844/1](https://github.com/geevensingh/org-explorer/actions/runs/45477625844/attempts/1) | Release controller demanded obsolete singular desktop CI job despite partitioned source evidence. Current `ReleaseGitHub.ps1` recognizes/pins original layouts and requires current layout, delivered in `39a86ef8a0b38bdf5b02030f5d9441145c8a1a8e`. Do not repair it again or waive missing jobs. |
| [45493909338/1](https://github.com/geevensingh/org-explorer/actions/runs/45493909338/attempts/1) | Required high-resolution initials-presentation timeout retains geometry; not proof of product rendering versus a pixel-observation mismatch. Current failure-only rejected-buffer diagnostics help the next occurrence but are not a fix. |
| [45261308556/1](https://github.com/geevensingh/org-explorer/actions/runs/45261308556/attempts/1) | Rich note p95 1,034.4 ms failed its original 1,000-ms policy. Later temporary 1,050-ms acceptance must not reclassify it. This run also has a separate held-adoption proof failure. |
| Eight retained whole rich execution timeouts | `44208391360/1`, `44374648992/1`, `44594263705/1`, `44835965395/1`, `44998243412/1`, `45052008579/1`, `45463279487/1`, `45527340954/1`. Six are older combined workload, two are split prefix. Several older kills also produce native-DLL cleanup errors; no common product/runner cause follows from identical deadline text. |
| Cancelled attempts | CI `45067287775/1`, `45108796180/1,2`, `45116886885/1` and TEMP `44844418591/1` retain cancellation; six old high-resolution jobs have cancelled outcomes across mixed whole-run conclusions. Generic cancellation text alone cannot distinguish manual cancellation from watchdog/infrastructure interruption. |

Additional older graph/delivery/crash/ordinary-native failures and all successful
reruns remain in the snapshot; the table prioritizes actionable recurring families,
not an exhaustive causal classification. No "runner noise" or "product defect"
label was inferred solely from a timeout. No new product root cause was established
that requires an audit-scope repair.

## Motivating rich-prefix timeout: sharper retained evidence

Compare [failed release 45527340954/1](https://github.com/geevensingh/org-explorer/actions/runs/45527340954/attempts/1)
at `48c86f14bbe0dea9af72fd3b27f14b50f458ed00` with
[successful release 45529573050/1](https://github.com/geevensingh/org-explorer/actions/runs/45529573050/attempts/1)
at `caec4da7a96a57a5e49b3ea047f29eaabf299835`.
The intervening delta is only `docs/releasing.md`, `docs/testing.md`,
`eng/ReleaseGitHub.ps1`, `tests/Release.Publication.Tests.ps1`: no app, rich
fixture, native workload, ScaleRunner or rich acceptance change.

| Actual measurement | Failed attempt | Following successful attempt |
|---|---:|---:|
| Prefix terminal runner seconds, setup/cleanup included | 661.0426542, `timed_out` | **643.0370537**, `success` |
| Prefix native command seconds | 604.9726174, killed exit -1 | 577.2809493, exit 0 |
| Normal note-phase seconds | 209.1102 | 191.5241 |
| Publish-before-held-export seconds | 19.7269 | 3.1872 |
| Held-export phase seconds | Unfinished; 344.2 s from begin to terminal runner report, not a completed duration | 338.4582 |
| Normal note p95 ms | 789.9189 | 604.0955 |
| Held-export note p95 ms | 969.4254 | 955.7867 |
| Held-export profile Save p95 ms | 296.0125 | 378.1751 |
| Non-UI terminal runner seconds | 143.9027042, success | 191.8572332, success |
| Tail terminal runner seconds | Skipped | 188.7244225, success |

The successful runner result is **643.037 s**, not approximately 647 s of
surrounding step/upload wall time. Its current-budget headroom is 16.963 s
(2.57%), a descriptive measurement, **not a selected yellow threshold**.
Both runs report 4 processors, `win25-vs2026` image `20261116.315.1`,
OS `10.0.26100`, PowerShell `7.6.10`, .NET `10.0.15`. Equal advertised metadata
does not certify equal physical CPU/disk load. Non-UI was slower in the successful
release; the data does not support a simple whole-host-fast/slow explanation.

The failed prefix's last unmatched phase is `UI-background-export-independence`.
It completed the earlier phases and nested held-profile proof. Both 21-sample
note artifacts and the 21-sample background-profile artifact exist and pass
their individual latency ceilings. However, no final export lifecycle artifact,
`UI-background-export-independence` end, handoff capture or successful terminal
scenario exists. Empty native stderr and an outer kill do not identify the exact
remaining suboperation. The flow after profile samples includes full public
reopen, old-publication reconciliation, releasing the held exporter, latest
Sync now, capture and normal Close. It could be in any unrecorded portion of that
remainder; claiming a specific blocked Save/Sync/Close would overstate evidence.
The missing tail and union rejection are consequences, not the initial cause.

Useful retained costs: successful normal/held note independent-full-reopen totals
are 21.605/20.869 s; failed totals are 20.799/21.053 s. Successful held-profile
reopens total 22.663 s (failed 21.239 s). These are actual recorded successful
verification reads, not every pending poll, and do not explain all elapsed time.
The successful prefix's harness end record reports 242.197 s inclusive UIA
discovery, 226.397 s native waits and 221.008 s observation, among other counters.
They overlap; adding them or subtracting them from acceptance would be false.
The failed worker lacks that terminal harness record, so the same attribution
cannot be computed for it.

Raw archives read: failed prefix **14983598053**, non-UI **14982582809**, union
**14983962071**; successful prefix **14985320886**, tail **14984917136**, non-UI
**14984286403**, union **14985361423**. Exact names, digests, size/retention and
run bindings are preserved in the snapshot. Failed prefix unique directory is
`run-ca277aa749134914b65f5c139bbd4ede`; successful prefix is
`run-8f754d09650f4368be79e2024207d25d`.
Individual passing measurements do not qualify the failed execution or make the
following release a root-cause repair or a three-execution reference cohort.

## Telemetry and comparison contract recommendations

Reuse existing observable runner/result seams, not private application hooks.
Lane timing files, scenario start/end/error records, command timings, scale
five-second heartbeats/active command/last unmatched phase, storage wall/CPU/effect
counters, native harness observation counters and raw user-operation samples
already supply useful evidence. Named major phases and user-facing Save/navigation/
editor operations suffice; no indiscriminate function-level instrumentation.

| Gap | Bounded recommendation / valid comparison key |
|---|---|
| Core/filesystem timing lacks exact revision/run/attempt/terminal selection binding; companion time is only residual total. | First filesystem slice can emit complete public runner outcomes; other lanes follow. Give each mandatory companion a named outcome/timing, preserve incomplete starts and cleanup/reporting failures. Do not infer success from an existing `timing.json`. |
| Whole Actions duration mixes setup, upload and reporting; skipped jobs can have invalid clocks. | Preserve API timestamps/outcomes. 93 job rows have end-before-start; keep them verbatim with invalid-clock flag and no computed duration. Do not turn a skip into negative work or zero-duration passing verification. Record queue, setup, workload, cleanup, upload separately when actually observed. |
| Rich export remainder is a large opaque interval at interruption. | Add bounded lifecycle stage records around already-executed final reopen, release/end acknowledgment, old/latest publication, capture and Close, through the same runner/result flow. Retain starts without ends and original exceptions; no extra business reads or timing deductions. |
| Configuration changes across history: combined/split rich, native partitions, informational editor/navigation, temporary note ceiling. | Key series by suite/selector/ordered required manifest, full fixture and size/phase/sample matrix, metric name/unit/boundary/percentile and **effective policy version**. Record diagnostic mode and controlled effects/clocks. Old policies keep their verdicts. |
| `windows-latest`, SDK `10.0.x` and cache vary. | Record resolved image/version, OS/build, CPU count/available model information, managed SDK/runtime, PowerShell, architecture, package/loaded native SQLite identity, screen/DPI/work area, cold/warm/cache behavior and setup scope. Missing relevant fields mean incompatible/unbaselined or explicitly uncertain, not assumed comparable. |
| Exact product hashes are mandatory within rich handoff but would reject all cross-revision baselines if used blindly. | Application revision/binary hash are provenance, **not equality keys across candidate/reference**. Preserve strict same-execution native/product/handoff identity; version relevant workload/harness/runtime changes separately. A new compatible eligible series can be established automatically without changing hard limits. |
| Artifact defaults and names do not ensure durable all-attempt history. | Store compact metric inputs, individual original outcomes, selection/configuration/policy, cohort membership, pinned reference/predecessor, comparison and promotion provenance on the dedicated future data branch. Preserve raw-link/expiry state; historical data cannot replace fresh release acceptance. |
| Artifacts are scoped by run; core/filesystem names lack attempt in their identity, while native/scale names include it. | Never attribute an unbound run-level artifact to an arbitrary earlier attempt. Validate schema and exact candidate/run/attempt before using a measurement. The snapshot intentionally keeps run-level artifact inventory separate from attempt records. |
| No eligible three-execution current-main baseline or calibrated relative gate demonstrated. | Collect independent complete observations, retain failures/skips/cancellations/incomplete attempts, compare to a pinned available reference before replacement; unavailable/incompatible evidence remains unbaselined/inconclusive and nonblocking by itself. |

Current main evidence:
[CI 45528329616/1](https://github.com/geevensingh/org-explorer/actions/runs/45528329616/attempts/1)
and the following successful release. Core timing is 150.474 s, filesystem
125.418 s; native job wall durations are people 613 s, workspace 531 s,
reading 594 s, context 392 s. These are different boundaries, not additive
operation latencies or measurements of this audit's documentation-bearing HEAD.
Core's 39.493-s dotnet-test command within 150.474-s total illustrates why
companion/setup attribution matters.

## Complete hosted-cohort cost and concurrency

Estimate the **whole** non-UI + prefix + actual-handoff tail + union, not the
prefix alone. Latest complete release's actual job wall times are non-UI 199 s,
prefix 654 s, tail 198 s, union 13 s: **1,064 s = 17.733 worker-minutes**.
This includes job setup/post steps but excludes queues and separate release
plan/package/publish. Suite result durations sum to 1,023.619 s; that lower
boundary is not billed-worker consumption.

| Complete executions | Fresh jobs | Observed job-wall estimate | Per-job whole-minute rounding estimate | Current watchdog envelope |
|---|---:|---:|---:|---:|
| 3 initial | 12 | 53.2 worker-min | 60 worker-min | 156 worker-min |
| 5 maximum for otherwise eligible inconsistency | 20 | 88.67 worker-min | 100 worker-min | 260 worker-min |

Rounding is `ceil(199/60)+ceil(654/60)+ceil(198/60)+ceil(13/60)=20`
minutes per complete execution, **not a claim about account billing charges or
Windows multipliers/credits**. The watchdog envelope is four 13-minute jobs per
execution, not an expected healthy duration or passing allowance. Four observed
complete split executions cost 997, 1,043, 1,059 and 1,064 job-seconds across
different candidates/policies; this corroborates order of magnitude, not a
calibrated homogeneous timing distribution. Failed attempts consume resources
too and must remain in both budget/history; they cannot be swapped for successes.

At observed durations, one execution's critical path is prefix -> tail -> union:
865 s (14.42 min), excluding queue/transfer gaps. Sequential independent
executions take about 43.25 min for three or 72.08 min for five; fresh non-UI and
prefix jobs can overlap, but tail waits for its own prefix. A bounded initial
configuration worth reviewing is **one active reference cohort, sequential
executions, at most two measurement workers active within it**, in a distinct
noncancelling collection group. This is a resource-control recommendation, not
an activated timing policy. Never share production release concurrency or
cancel/hold release to refresh a reference.

Ordinary required CI already permits six parallel execution workers followed
by its collector; its configured watchdog sum is 78 min and parallel path <=17
min before queues/later CD. Reusing already-completed verification can avoid some
collection work only after independence/configuration/provenance eligibility is
proved. It does not make release wait for three/five observations or authorize
reuse of old acceptance artifacts.

The sampled successful scale archives total about 8.22 MiB compressed, including
both worker evidence and duplicate union copy. Three similarly sized executions
would be about 24.65 MiB, five about 41.08 MiB, before log service storage and
future retention growth. This is one-run storage sizing, not a quota guarantee.
Automatic per-main collection needs an explicit retention/quota review; the
bounded seven-day history already inventories about 922 MiB of archives.

## Diagnostic / repair checkpoints and acceptance completion

Priorities for the coordinator, without adding tickets or performing repairs:

1. **Rich interruption visibility and calibration evidence.** Preserve original
   budgets and all matrices. On hosted Windows, freeze an approved exact
   candidate and run one complete diagnostic/measurement execution first; retain
   actual lifecycle starts/ends and every failure. If that observation produces
   comparable complete evidence, the planned independent three-to-five collection
   can establish variability. Stop at a reproduced defect or material acceptance
   contradiction; do not retry until green. Current extended mode remains
   nonaccepting and its 780-s watchdog can truncate a 900-s request.
2. **Separately scoped non-UI investigations if approved.** One slice for
   held-note/Core admission scheduling evidence at public workflow/result seams;
   another for compiled reviewed-Import readiness; another for owned native-DLL/
   WAL cleanup/lifetime. Preserve exact actors, request/admission/exit and
   original timeout/cleanup errors. Historical path/lifetime refusals alone do
   not authorize fixes, ThreadPool changes, broad refactors or deletion retries.
3. **Proceed with observation-first telemetry/history contracts.** These known
   uncertainties do not block result-schema/history work (#126/#127). They do
   block treating the affected timings as a proven healthy calibration population.
   If diagnostics establish a current defect, propose a context-sized repair and
   its genuine collection/calibration dependency for owner approval before new
   ticket creation or repair implementation.
4. **One-time policy activation decision remains pending.** #130 must specify and
   obtain approval for actual warning/serious-regression/variability/eligibility
   evidence rules and separate safety bounds before #131 changes enforcement.
   This audit does not select percentages or pad timeouts.

All #125 criteria are covered above: fresh preflight/incorporation and current
spec reconciliation; actual complete lane/nested selection; policy ledger;
bounded all-attempt history and attribution uncertainty; useful telemetry/keys/
retention gaps; motivating timeout evidence and bounded diagnostic plan;
durable linked result and full-cohort resource estimate; separately scoped
conditional repairs/checkpoints; preserved product/latency/selection/verdict/
release independence; approved public runner/result seam and hosted-only native
evidence. No behavioral test was fabricated for a documentation/evidence-only
deliverable. Audit data completeness and cited source identities were checked;
no new native tests, workflows, releases or numerical policy were executed.
Ticket closure and receiving-branch merge remain the coordinator's separate
fresh-merger verification responsibility.
