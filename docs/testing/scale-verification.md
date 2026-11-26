# Explicit scale verification

[Return to Testing](../testing.md).

For full-rich non-UI performance or isolated rich WPF workers, logical handoff, latency policy, deadlines and union results, use this reference. These suites are distinct from [required PR/main smoke](ci-and-artifacts.md#required-prmain-execution). Read [release and manual execution](release-and-manual.md) for hosted dispatch and exact-candidate publication. For standalone causal and cancellation regressions, read [note acknowledgment](note-writing.md#commit-acknowledgment-and-cancellation).

## Separate scale commands and results

The complete workloads are outside required PR smoke, not removed:

| Command | Required selection | Evidence |
|---|---|---|
| `.\eng\Run-Performance.ps1` | Complete `Category=Performance` filesystem workload | `.ci-results\performance\result.json`, unique run directory, complete named TRX and logs |
| `.\eng\Run-UiScale.ps1 -Worker prefix` | `scale-mutation-save-prefix` | Candidate/run/attempt-bound worker result, original rich latency/lifecycle artifacts and complete logical handoff |
| `.\eng\Run-UiScale.ps1 -Worker tail -PrefixResultsDirectory <prefix-artifacts>` | `scale-mutation-delivery-tail`, then `scale-navigation-rendering` | Separate worker result, public Join receipt, ordinary profile samples and complete tail/navigation evidence |
| `.\eng\Assert-UiScaleUnion.ps1` | Both workers; every scenario in `tests\UiScaleScenarios.json` and phase in `tests\UiScalePhases.json` | `.ci-results\ui-scale-union\result.json` and unique run directory containing both complete worker artifacts |

Both rich worker commands require `-ExpectedRevision`, `-RunId` and
`-RunAttempt` (the last two default to the current GitHub Actions environment).
These selectors are not standalone complete-suite commands. The hosted workflow
owns isolated execution, current-attempt artifact transfer and union verification.

Non-UI performance proves genuine 10,000-Person load/reopen, current/historical
search, durable mutations, reviewed 10,000-record imports, original/latest batch
replay, external delivery/recovery and complete retained-data comparisons.
Targeted large-graph/no-import-cap correctness remains in required core coverage.

UI scale builds and drives the **normal WPF executable** on two isolated
interactive Windows workers. The original mutation scenario splits only at its
existing process-reopen boundary, after the combined held-export host has
returned from normal process death:

- `scale-navigation-rendering`: Settings visibly works while real opening is
  pending; expand/collapse, scroll and select a distant realized tree row;
  current/frozen/deep-chain/final-archived-Person search and Manager navigation;
  actual photo pixels and rendered current/frozen Markdown.
- `scale-mutation-save-prefix`: repeated normal-executable usable Team editors,
  Person selection and ordinary durable saves while typing continues, followed
  by actual compiled WPF at the same complete rich population during a held real
  profile Commit and a held complete-snapshot export. Settings and Person
  browsing must finish while those same operations remain admitted, without
  losing the original owner's writing. Both complete 20/42/21 phases, real held
  profile proof, all 21 background profile saves and latest publication stay together.
- `scale-mutation-delivery-tail`: **public FileBacked Join of the first worker's
  actual published complete `.oesnapshot`**, not an equivalent newly seeded
  fixture. It resumes all 21 ordinary normal-EXE profile Save samples, favorite mutation, complete
  reviewed Import, two-local-store public note edits/snapshot delivery/rendering,
  latest publication and durable profile/Manager/favorite/photo reopening.
  Valid incoming adoption cannot require a Settings recovery retry.

The source handoff is captured only after the original held-export flow returns.
Its envelope/payload hashes, exact Workspace identity/head, source/test/runner
trees, workflow blob, candidate/run/attempt/first-worker invocation, full runtime component inventories
and loaded native SQLite version/source/hash are bound to the receiving worker.
Normal product and harness builds map their unique build/checkouts to fixed
debug-source paths so separately built executable/runtime hashes must agree.
The artifact contains only the actual immutable logical snapshot, recorded
preferences and metadata; never a raw database, WAL, SHM, or reseeded note/history
identities. Preferences retain the actual exact selection, ongoing notes and
pane proportions, relocating only the original Workspace path to the receiver's
fresh physical folder. Public Join validates the complete snapshot. Independent
public reopen compares all People, exact notes/owners/GUIDs, profiles/timelines,
current/inherited/frozen contexts and decoded photo bytes/dimensions before the
tail launches the normal executable. The receipt truthfully identifies a new
worker/store/process, not continuation of the first physical process.

`tests\UiScaleWorkers.json` selects every scenario once. Both terminal worker
results, exact ordered original phase begins and successful ends, all required
raw note/profile/lifecycle artifacts and the matching validated handoff receipt
are required by the union. A missing/failed/skipped/duplicate/substituted worker,
stage or payload cannot pass. The tail never calls `ScaleFixture.Build`; only
the separate original navigation scenario retains its own fresh fixture.
The reusable manual/nightly bridge uploads both workers on failure and retains
the existing candidate/run/attempt `ui-scale-...` artifact name for the complete
union. Release gates revalidate both workers, freshness and the same handoff;
they do not relabel their summed wall time as one 660-second execution.

`--verify-ui-scale-handoff` runs a full-rich public-filesystem contract without
showing WPF or using desktop automation. It deletes the original physical
fixture after capture, then verifies actual Join/reopen with the complete rich
population, resources/history and recorded selection/preferences. Its retained
`.ci-results\ui-split-validation\logical-handoff-*\handoff` is contract evidence,
not hosted native acceptance. Focused `UiScaleHandoffTests` cover mismatched
candidate/attempt/native/product hashes, corrupted bytes, different selection
and forbidden raw-file bundles. PowerShell union contracts cover complete and
missing/failed/mismatched/over-budget selections.

The same shared `ScaleFixture` preserves 9,900 active/100 Archived people, the
complete 42-edge chain, 2,500 photo references, 1,000 Person notes, 111 Team notes,
200 frozen contexts and retained Profile evidence. No population/chain truncation,
import cap or non-UI proxy replaces either WPF scenario. The old combined scale
runner is retired; it is not also invoked by smoke or the explicit command.

The rich mutation scenario retains twenty Team-editor, forty-two Person-navigation
and twenty-one ordinary save samples per phase. Healthy samples drive the normal
product EXE. The background-export phase drives the actual product MainWindow in
an owned hosted WPF process, controlling only the public 60-second clock and real
external snapshot publication. The actual held profile transaction separately
proves pending-work usability; its deliberate hold is not a healthy-save latency
sample. The snapshot must remain held through the same complete continued-typing/
browsing workload and independently reopened local writing. Its older publication
cannot acknowledge the later local revision; subsequent Sync now must publish
that exact latest revision.

The controlled host and following normal-EXE reopen inherit the fixture's saved
view, not an empty profile. Their exact expected Person comes from an independent
prelaunch preference snapshot. Missing or unreadable preferences cannot become a
blank-view fallback. `--verify-rich-host-restoration`, also in the full compiled
selection, replays the actual controlled-host construction and Loaded handlers,
exact saved Person/Team-note reading, subsequent Person restoration, grouped
search and normal Closed without creating a native window. It does not replace
the hosted rich lifecycle, change its 180-second readiness observation or extend
the whole 660-second scale budget.

Rich ordinary typing uses six predetermined inputs at nominal 250-ms spacing,
not a new 60-ms stress deadline. Raw native request/observation arrays, nearest-rank
`ceil(0.95 * count)` p95, sample counts, exact candidate/run/attempt, product EXE/
assembly and host hashes, environment and monotonic clock boundaries are retained
in the two `*-sqlite-note-latency-rich-*.json` artifacts and the
`*-sqlite-rich-export-lifecycle.json` artifact. On 2026-11-20 the owner approved
informational editor/navigation timings so functional validation can finish
without further performance work. Their historical 100-ms `budgetMs` references
remain, with `budgetEnforced: false` and `timingUse: informational`; every sample,
percentile and functional usable-state assertion is retained. Ordinary
durable-save p95 retains its 1,000-ms goal. On 2026-11-24 the owner approved a
temporary 1,050-ms **enforced** ceiling only for these rich ordinary note saves
in both `normal-executable` and `background-export`. The note result contract
records `localDurability.goalMs: 1000`, `budgetMs: 1050`,
`budgetEnforced: true`, `temporaryCeiling: true` and `timingUse: acceptance`.
`goalMet` is computed from the original nearest-rank p95 against 1,000 ms; an
accepted p95 above the goal must report `goalMet: false`, not claim one-second
success. The union recomputes p95 from all 21 raw samples and requires this exact
policy metadata in both phases. At 1,050 ms it accepts; above it, it rejects.
All commits must still be observed during continued typing. Profile saves,
small-fixture note/first-commit one-second checks, queue-inclusive three-second
stall, held-adoption capture proof and both 660-second worker budgets are
unchanged. This is temporary acceptance pending later optimization/review, not
informational-only durability or authority to increase another limit. Historical
failed runs retain their original measurements and verdicts.
Missing/incorrect UI, owner, writing or durability remains a failure within the
original observations.

The rich save clock starts before native input and runs through actual WPF
post-Commit acknowledgment for the exact current owner/note/title/request.
The shared observation lifecycle owns request, body and cleanup so an observer
fault cannot replace an initiating input/assertion exception. Secondary faults
remain attached/reported, and an otherwise successful body still fails when its
observer faults or never confirms durability. Request clocks, continued typing
and the original observation window are unchanged.
Pending observation is diagnostic, not mandatory: a fast Commit may finish
before the first observer call. The exact Person/Team saved-success (or Team
saved-and-confirmed) acknowledgment and truthful complete-publication/local-only
success projections can acknowledge the exact request, never generic Ready/Saved
combined with optimistic editor text. The scoped accessible ItemStatus is set
by `OwnedNotePresentation` from VM status. Retained generations
override every core status with Pending; the worker removes a generation only
after accepted `SaveNoteDraft` and only when it is still the latest generation.
That core acknowledgment is returned after actual SQLite Commit.

## Independent reopen and latency boundaries

Request-to-postcommit-acknowledgment latency is a conservative upper bound on
Commit latency, not direct storage-only time. Every rich sample independently reopens real local storage
to verify its request is durably covered under the original owner, never the
selected report. Full-population reopen verification duration is reported
separately: load/validation cost is not substituted for application Commit latency.
Reopen proves data correctness; it is not used to retroactively date Commit.
The two former after-typing reads (prefix backing and final exact-writing
readiness) now share one genuinely fresh public ExistingOnly reopen per check at
the original final UI-readiness point. No harness mutation intervenes; late saves
can settle, so an earlier/in-memory reader is never reused. That one coherent
read checks the original request prefix, exact final title/body, exact owner and
wrong-owner absence, inside the unchanged final observation. Original Ready and
current-owner checks are also retained after the reopen. An earlier durable
prefix with a legitimately later final title/body still pending, or changed UI
readiness, returns false to the same original20-second Await; each later check
reopens freshly, never reuses the earlier reader or submits another save.
Missing/corrupt storage, wrong owner or an uncovered original request prefix
remains an explicit refusal. All 21 successful independent verification
durations remain in
`independentFullReopenVerificationMs`, with
`independentVerificationScope: fresh-final-prefix-owner-and-exact-writing`.
The shared numeric sample validator rejects JSON booleans, including these 21
verification durations in both note phases. Finite nonnegative numeric values,
including zero and fractions, remain valid; counts and p95/policy checks are
unchanged.
They measure the successful final reopen/validation/disposal, never replace the
ACK sample or deduct any pending-check work from the observation. The actual
compiled regression enters this same final predicate when a completed real
work operation exposes Ready/latest editable UI while a later real Commit is
held. It stays pending without success/exception, then proves fresh final owner
data after release with one success record and the original ACK timestamp.
Separate cold pre-Commit negatives and
post-Commit positives still require their distinct real states/readers.
Focused two-Person
20/42/21 samples keep their existing independent-reopen timer, original 20-second
observation, 60-ms continued-input workload and p95 contracts unchanged.
No per-worker budget, functional observation or sample-count change is permitted.

## Execution deadlines

For investigation on an authorized interactive desktop, use the worker selectors
or one scale name with the explicit candidate/run/attempt handoff. Use both
`Run-UiScale.ps1` workers and `Assert-UiScaleUnion.ps1` for complete bounded
candidate-bound acceptance. A one-scenario or one-worker pass is not suite success.
Missing/locked interactive desktops and unavailable pixels are visible failures;
never skip rendering, change shared-machine settings or launch local automation
without separate permission.

Non-UI performance and **each isolated rich UI worker** have their own
**660-second whole-execution pass/fail budget**. The hosted
watchdog allows thirteen minutes so termination, failure reporting and artifact
upload can finish after a failed budget; the extra time cannot make the run pass.
`SCALE_STARTED_AT` begins before checkout/SDK setup;
restore, builds, fixture generation, assertions and cleanup all count. These
are execution bounds, not new latency thresholds or a baseline collection scheme.
Both standalone commands use `eng\ScaleRunner.ps1` for the budget, owned command
lifecycle, fresh reports, exact-candidate checks and cleanup. Workload selection
and complete named-result assertions remain in their separate entry points.
See [performance validation](../performance-validation.md) for fixture detail and
historical measurements, not current command authority.

## Held fixture deadlines

The owner-approved rich export fixture hold is bounded by the remaining original
660-second whole-suite deadline, not a separate 180-second publisher timeout.
The runner passes that exact deadline to owned commands through process-only
`SCALE_DEADLINE_AT` and immediately restores its caller's environment. The rich
host rejects a missing/invalid deadline and an expired hold; no new full-duration
clock starts at publication. Ordinary automatic holds remain 30 seconds, and
all workloads, samples, observation limits and the whole-suite ceiling remain
unchanged. Every held assertion still requires the same unreleased publisher.
The invisible rich-host restoration contract exercises that actual deadline
consumer: released within-budget publication succeeds, an expired deadline
still rejects release, and missing/malformed deadline input fails explicitly.
Its ordinary automatic publisher remains independent of the rich deadline.

## Profile samples and controlled synchronization

Non-UI rich performance also measures twenty-one actual explicit profile Save
requests through the real VM queue and SQLite acceptance, not quick
`EditPersonText` as a proxy for the shipping explicit Save path. Its nearest-rank
p95 retains the one-second target. Same-revision saves still read the real head;
changed/failed heads, stale bases, photos, eligibility, newer generations and
unknown receipt recovery remain covered in focused real-storage suites.
Native rich profile samples repeat that explicit Save path in both normal-EXE
and actual held-export WPF phases, with exact whole-Person and decoded-photo/hash
reopen checks. `*-sqlite-rich-profile-latency-*.json` separates native
request-to-postcommit-acknowledgment from full-reopen verification.
These observations use the real Directory-name editor's automation provider:
visible in Edit and offscreen after Save, alongside the exact owner/value,
enabled Edit action and Ready state. The layout-only `ProfileForm` StackPanel
has no WPF automation peer and cannot be a native UI Automation target.

Only the fixture's synchronization cadence is controlled through its independent
TimeProvider. Actual save-request/latency/three-second clocks remain real for
performance acceptance. The clock-independence real-store regression advances
the synchronization clock during an actually admitted blocked save without
advancing its deadline, then advances the separate save clock by three seconds
and requires Stalled/unknown with browsing available. Non-UI ordinary measurements
admit exports explicitly, without accidental timer exports contaminating the
healthy editing phase. Cleanup stops fixture-owned future scheduling and waits
for already admitted export I/O before deleting its data. Production timer/
Dispose/forced-exit semantics are not changed to make cleanup pass.

## Reports, progress and diagnostics

Each invocation marks a fresh root report `running` before execution and writes
revision, expected revision, dirty/untracked state, selected tests/scenarios,
outcome, timestamps and timings. Successful suite status requires all named work;
missing/duplicate/partial results, failed assertions, skipped/cancelled/timed-out
work, changed candidates, cleanup errors and exhausted budgets fail closed.
Interrupted reports remain incomplete, never an old passing result.
While an owned command is running, atomically replaced reports publish a heartbeat
every five seconds, its active command, and the most recent unmatched phase begin.
Non-UI progress comes from `scale-measurements.log` in the current unique run
directory, not buffered passing-test stdout. The reader ignores unrelated inherited
`SCALE_TIMING_FILE` paths and falls back to command stdout until the current run's
file exists, using the same source for the final progress capture.
Reports distinguish command execution, termination and cleanup, and become terminal
only after cleanup and the unchanged budget check.
Owned-command termination does not depend on successfully publishing its report;
report-write failures still reject verification.

Scale logs retain `SCALE BEGIN`/`SCALE END` markers even when an operation fails.
Fixture generation, current files/indexes, open/validation and independent recovery
are separate phases. The real workflow observer reports capture, validation,
recovery preparation/publication, object writes, reclamation and fresh rechecks,
with completed writes/bytes, durable flushes, publications and deletes.
Storage phases also record process CPU time; reports retain processor/runtime/image
metadata for host comparisons. Nested counters, wall time and process CPU time are
inclusive, so do not sum overlapping phases.
Actual WPF logs also separate Import navigation, preview, apply, return, close and
cleanup. No file contents or Person writing are added to storage-phase diagnostics.
Scale fixture observers retain diagnostic-write/output failures without throwing
through storage callbacks. Fixture disposal reports them as verification failures
after closing clients and cleaning owned fixtures, preserving real save outcomes
and original storage errors rather than silently accepting missing diagnostics.

## Diagnostic-only and tree investigations

The direct scale workflow's `extended_observation` input is explicitly diagnostic.
It requests observation for up to 900 seconds while retaining the original
660-second per-worker verdict and thirteen-minute worker watchdog.
Reports set `diagnosticOnly`, distinguish observed completion from the verdict,
and never report candidate-verification success in this mode, even if observed
completion is under 660 seconds. Its artifact name starts with
`diagnostic-ui-scale-`, not the normal verification prefix. A diagnostic prefix
is never an accepting handoff; diagnostic reports cannot pass the union. Normal release
verification and non-UI execution do not opt into it.
For repeated measurements, freeze one full revision and retain each run's
separate artifacts. Invocations remain independent; no concurrency group
replaces a pending trial.

The scale tree locates its distant row through the reporting branch's UI
Automation item provider, requests realization/scrolling once, then observes
visible geometry and real viewport movement before selecting and checking the
destination Person. Realized-descendant enumeration alone cannot find a
virtualized row. A timeout identifies the target and last observed geometry,
viewport offset and selected email; it retains the existing 180-second bound.

## Pending-work rejection

The scale mutation scenario retains actual in-progress-work rejection while
Settings renders. A rejection check invokes once, then observes the actual rejection
message within the existing deadline while requiring the original Person to
remain selected. A generic pending message or a completed save is not rejection
evidence. The runner's non-UI ordering regression covers pending → rejected
observations and refuses missing feedback, wrong feedback or changed destination;
hosted WPF still supplies the real UI evidence.

## Rich navigation diagnostics

The explicit `--diagnose-rich-navigation` command is a separate, nonaccepting
actual-BAML investigation at the complete rich population: 42 action-to-layout
observations during 21 notes with six nominal 250-ms inputs apiece, raw managed
allocations and picker work, followed by full independent owner/writing/resource
verification. It never replaces native action-to-usable observations, subtracts
observer time or runs as another required scale lane.

The explicit `--diagnose-rich-navigation` diagnostic keeps all 42 selections,
20 Team plus one Person Add, full rich population, per-sample independent
reopens and real 60-second synchronization during sampling. Managed
dispatch/layout, observation milestones, allocations and independent reopen work
are separate raw observations, never additive or subtracted from native timing.
Only fixture-owned future timers stop after sampling; its own publication and
callbacks must finish before actual Closed and cleanup. This is not a required
lane or native acceptance, and changes no editor/navigation/durability target,
deadline, workload or existing scale selection.
