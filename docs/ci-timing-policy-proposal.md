# Timing policy proposal: ci-timing-preview-v1

**Status: one-time maintainer approval pending; observation only.** This is
#130's proposal, not #131 gate activation, #132 canonical promotion, or #133
final verification. Existing acceptance and historical verdicts remain enforced.
Approval of the original design or the staged diagnostic is not approval of
this numerical proposal. No additional measurement executions are requested
by the preview code.

## Verified baseline and diagnostic

The isolated worktree passed the shared fetched-base gate against pushed
`integration/ci-reliability` at `4fcb58d84237589bf840fa832fbfbd5dfb83344a`.
The source handoff verifier freshly checked source
`geevensingh-ci-reliability-design`, immediate base
`caec4da7a96a57a5e49b3ea047f29eaabf299835`, documentation
`4334a254827d450151b536347b52dc20b49fd487`, and exactly
`docs/ci-reliability-plan.md`. Incorporation is by ancestry. Observed newer
main `87c6adeacaef6de440fcf344900e8f23a8571f10` was not merged.
The integration's product, full rich matrices, original hard limits and staged
#127/#129/#154 dispositions were reconciled against live specs/comments.

Exactly **one** separately authorized prefix-only diagnostic was dispatched:
[45714583109/1](https://github.com/geevensingh/org-explorer/actions/runs/45714583109/attempts/1),
immutable source `5563bc50c189d1adb32aed4f37d0cb10b6a69405`,
prefix job `139557263857`. Its registered Scale verification dispatch selected
`prefix_only_diagnostic=true`, with 900-second observation and 17-minute
watchdog. The held publisher inherited the observation deadline, not a new
product requirement. Non-UI, cohort, tail and union were skipped; their skipped
job records remain visible. The latter two have reversed API timestamps and
must not acquire computed negative durations.

| Retained result | Actual value |
|---|---|
| Whole prefix including setup/cleanup | **683.0593558 s** |
| Original acceptance / observed work | **timed_out / success**, diagnosticOnly=true |
| Native command | 624.2439535 s, exit 0 |
| Original deadline / observation deadline | 22:46:56.0700518Z / 22:50:56.0700518Z |
| Completed scenario | scale-mutation-save-prefix, 622.8407859 s, Passed |
| Normal / held note nearest-rank p95 | 874.6613 / **1030.138 ms** |
| Held profile Save p95 | 294.464 ms |
| Held note policy | Goal1000 ms missed; temporary enforced1050 ms passed |
| Normal / held note phases | 209632.2 / 352787.5 ms |
| Handoff capture | 8593.9 ms, completed after the original acceptance deadline |
| Lifecycle | Passed, original real hold/release/latest-publication/normal-exit proof |
| Artifact | 15068794414, diagnostic-ui-scale-prefix-5563bc50c189d1adb32aed4f37d0cb10b6a69405-45714583109-1 |
| Artifact digest / expiry | sha256:1b34d065c02c0c0a6d8855d6b102d4930f4d654d75deb4ba76c044b06064c45e / 2026-12-25T22:47:19Z |
| Captured handoff SHA256 | 1C0EF42FD67BF230E63094E0B0390AAC46C1F0E72B6F8C526DF32534468B13D6 |

This captured the intended missing completion interval. It does **not** prove
why the original pilot was slower, a cleanup repair, a complete scale union,
three eligible full samples, or an eligible baseline. There was no rerun,
diagnostic tail/union, release, or canonical write.

## Evidence population and limitations

The [audit](ci-reliability-audit.md) and
[all-attempt snapshot](ci-reliability-audit-evidence.jsonl) remain the complete
bounded inventory: 150 attempts across 138 discovered runs, 762 jobs, including
53 failures, five cancellations and one skipped workflow. The inventory's
selection interval and historical workload/policy distinctions remain intact.
These development/manual observations are not a randomized healthy-main
population or a calibrated failure probability.

Retained raw result/configuration records, phase logs, raw sample vectors,
nearest-rank percentiles, lifecycle records and original artifacts were read,
not just the following summaries.

| Population | Individual observations (s) | Disposition |
|---|---|---|
| Non-UI pilot45683358483/1, source8cfd266b81fdcdd782ac542fa1db5f66bd0a7863 | 163.5375819, 201.0297724, 196.450717 | Rejected: third owned native-DLL cleanup failed after passed named TRX |
| Non-UI guard pilot45692949637/1, sourcea67ee73f1bb10b4e566cdb47e1490d53b0984506 | 197.6465113, 211.6883753, 154.04058 | Three non-UI observations only; pilot authority, no healthy canonical baseline |
| Full pilot45704991010/1, source5078960e12bef092a8a5fccc181943dabc0ac668: non-UI | 190.2177153, 170.2749818, 185.2966182 | All three non-UI workers complete; full cohort still rejected |
| Same full pilot: prefix | 403.0340036, 393.511577, 660.8815275 | Two complete; third censored at660, not a completed660.8815275s workload |
| Same full pilot: tail | 181.5505583, 221.8307915, unavailable | Third skipped; union failed |
| New prefix diagnostic45714583109/1 | 683.0593558 | Complete diagnostic observation, original acceptance timed_out, baseline-ineligible |

The non-UI guard's median197.6465113 and range57.6477953 differ from the
full pilot's non-UI median185.2966182 and range19.9427335. The two completed
full prefixes span9.5224266 s; the two tails span40.2802332 s. These are
**descriptions, not healthy/noise ceilings**. Discarding the third full sample
would conceal the important uncertainty. Its normal/held note p95 values
995.097/853.6043 ms and lifecycle passed; handoff began only0.4414483 s before
the old deadline and had no end. It must not be treated as a measured completion.

The audit's matched-contract release prefix643.0370537 s versus censored
661.0426542 s likewise demonstrates sensitivity, not a healthy distribution.
The diagnostic's683.0593558 s demonstrates one real completion beyond660.
None supplies the missing three eligible independent full executions.

Configuration is retained, not normalized away: hosted image
win25-vs2026/20261116.315.1, OS10.0.26100, four processors, SDK10.0.404,
PowerShell7.6.10, resolved runtimes including .NET10.0.15; actual rich display
1920x1080, work area1920x1032, 96DPI. Loaded SQLite3.54.1 source/hash is recorded
where available. The failed full prefix lacks its native handoff binding.
Non-UI fixtureTree5486612b3265a53e28c58c53be350ada26571b27 in the earlier
pilots differs from730ba5c8e50f45ef816bb5b3c1233f67ec60ce51 in the full pilot.
Rich fixtureTreeb57fa2fcd0aad24b2f44fc9f3acdb4bc0a070eab differs from diagnostic
8d27f768833b58ff48515275b0de36feb53860f2, and diagnostic observation mode differs.
These populations cannot silently become one comparator. Equal advertised host
metadata does not establish equal physical load.

#154's [accepted inconclusive disposition](https://github.com/geevensingh/org-explorer/issues/154#issuecomment-7174479991)
remains binding: historical native cleanup holder/cause unknown; no repair claim.
The local f327 core record's753 checks/22 steps and raw exit0 took667.605643 s,
exceeding original300 s: failure/ineligible, not hosted healthy timing.
Its raw local archive was not available in this checkout; its disposition is
retained from #127, not represented as newly inspected raw measurement.
Earlier failed artifacts, reanalysis and TLS-unavailable **read** attempts remain
distinct evidence events; a later successful read is not another workload
execution or grounds to erase a failed/incomplete/cancelled run.

The remote ci-health-data ref was absent when freshly queried. No canonical
reference or hosted history rollout is claimed. Read-only public previews of
the actual full pilot retain2 complete/1 failed, all original values and no
reference. Actual CI45528329616/1 preserves original successful jobs:
core/filesystem lack newly attempt-bound artifacts; native whole timings
604.4830649/520.4250956/582.1248981/381.4721127 s lack resolved comparison
configuration. Those missing measurements are not zero or retroactive failure
of the originally successful CI. Optional scale/rehearsal stays skipped.

## Versioned per-metric rules proposed for approval

No defensible fixed healthy-duration, percentage-regression or universal noise
band can be estimated from this evidence. Their absence is explicit, not filled
with padded limits. Version1 instead proposes an **empirical-envelope rule**
whose numerical bands are derived from a pinned eligible comparable cohort.
There is no claim of calibrated statistical confidence.

For each selected metric retain the independent per-execution values, count,
minimum L, maximum U, median M and full observed range R=U-L. A user's21 raw
latency samples still produce that execution's original nearest-rank p95;
three p95 values from three workers are not63 independent executions.

| Metric/workload | Healthy / warning proposal | Blocking requirement / proposed safety |
|---|---|---|
| Core whole duration | Empirical envelope; existing300s target remains descriptive | Preserve300s policy/safety; no evidence justifies padding this lane |
| Filesystem whole duration | Empirical envelope; existing300s target descriptive | Preserve660s safety and full real-storage selection |
| Each desktop/high-resolution partition | Empirical envelope, per actual partition/companions | Preserve900s safety, full UI/state/size matrices |
| Non-UI whole duration | Empirical envelope, no healthy band inferred from mixed pilots | Preserve660s safety; observed complete workers are far below it |
| Rich prefix whole duration | Empirical envelope; unavailable reference means unbaselined | **Propose900s safety instead of660s whole-suite acceptance**, with17m watchdog, only after approval/consistent activation |
| Rich tail whole duration | Separate empirical envelope; never summed with prefix/non-UI | Preserve660s safety; only two complete pilot tails, so healthy band pending |
| Rich normal/held note p95 | Goal<=1000ms; goal miss within enforcement is yellow; otherwise envelope | Each original p95<=1050ms, all21 during-typing/final-writing checks; no product-limit change |
| Non-UI/rich/ordinary profile p95; ordinary note durability | Empirical comparison plus existing one-second requirement | Preserve original1000ms limits and all raw/per-run assertions; no rich exception for ordinary notes |
| Editor/navigation timings, named phases/commands/scenarios | Envelope observations for diagnosis; no invented latency SLA | Remain informational; future relative gates must select whole duration/product-latency metrics explicitly |
| Nested locks/readers/process-death/admission/storage behavior | Attribute named test/phase results, not inferred host noise | Preserve original5s/temporary6s admissions,3s stall,1s busy,20s/180s state observations and actual causal assertions |
| Installer/release/renewal/rehearsal/controllers | Timing comparison pending compatible complete evidence; no healthy absolute band | Preserve20m installer,480s Sandbox scope,2m CI collector,5m plans/15m publication watchdogs and all platform/trust/fresh-candidate requirements |

900 seconds is a **bounded safety proposal**, not a predicted healthy p95:
it reuses the already authorized observation horizon, which captured683.0593558s
with216.9406442s remaining. The diagnostic alone cannot establish its future
success rate. No other workload receives a blanket extension. Watchdog reserve
is reporting/termination/upload allowance, never additional accepting time.
Activation must preserve measurement/setup/cleanup cost rather than subtract it.

For a positive observed reference range:

- Green comparison: complete measured inputs; candidate maximum<=U and
  candidate range<=R, goals met and remaining applicable headroom>R.
- Yellow: maximum>U, range>R, missed existing goal, or headroom<=R. One observed
  range of headroom means an already observed fluctuation could exhaust it.
  Yellow alone never blocks merge/release.
- Serious-relative red proposal: **five** complete eligible comparable candidate
  executions, every value>U+R, candidate range<=R, and median increase>R.
  This requires separation beyond the full observed noise envelope, not an
  arbitrary percentage or one slow worker. It is a conservative deterministic
  evidence rule proposed for approval, not an experimentally calibrated
  probability. Phase/informational grades do not authorize product gates.
- A zero reference range cannot establish a calibrated noise floor: comparative
  health is inconclusive, not a threshold where any epsilon is a serious
  regression. Hard requirements and absolute goals still apply.
- Any individual hard violation remains red/actionable under **current** policy,
  even if the cohort median is below its limit. Missing/censored metrics and
  interrupted required work cannot supply green comparison or acceptance.

A completed prefix's measured duration can receive the same comparison grade
at659 and661 seconds while its original acceptance remains success/failure.
This is a duration observation, not authority to qualify a failed cohort.
Diagnostic records remain nonaccepting regardless of duration.

## Eligibility, comparison series and future automatic promotion

The following is the approval proposal for #132, **not activated behavior**.
Current previews keep promotion disabled and eligibility pending/rejected.

Start with exactly three independent fresh complete executions, each owning
its required non-UI, actual prefix snapshot, fresh Join/tail and strict union.
All must pass the policy that was actually enforced, share comparison keys and
retain exact candidate/run/attempt/job/runner/invocation provenance. No failed,
skipped, cancelled, missing, unrequested or reused execution can be removed or
replaced to obtain three greens. Pilot/manual/fork/local/diagnostic observations
are never canonical inputs.

With a compatible reference, otherwise complete yellow/out-of-envelope or wider
range evidence proposes two additional fresh executions, numbered4 and5. Stop
at five. Persistent wider range or low headroom is inconclusive and retains
the prior reference. A complete consistent five-execution yellow cohort may
promote after comparison if it is neither a hard failure nor serious-relative
red; warning history survives. No failure authorizes this extension.

For the first eligible main cohort of a new series, compare unavailable before
bootstrap; never claim comparative green. Retain its actual median/range as an
initial empirical envelope. If its measured range spans remaining safety/hard
headroom, extend otherwise eligible evidence to five; unresolved ambiguity
retains no reference. Fixed healthy/noise bands for unsupported workloads remain
pending, not automatic requirements that release wait for collection.

Pin the available reference and history tip **before** evaluation. Exclude
the candidate itself; use its recorded eligible predecessor if already active.
Preserve comparison and prior revision/age before any replacement. Use Git
main ancestry/order, not completion time, for promotion: an older cohort finishing
late cannot replace a newer eligible one. Unknown ancestry or publication
failure is explicit and retains the previous reference. The preview's eligible
prior observation is expressly not a canonical active-reference pointer.

Keys include workload/selector, ordered scenarios/phases/occurrences, full rich
population/size/sample matrices, metric unit/boundary/percentile/effective policy,
fixture/harness/test identity, resolved runtime/SDK/native binding, OS/architecture,
image/version/processors, actual display/DPI and setup/cache/controlled-clock/
observation scope. Missing relevant identity means incompatible/inconclusive.
Existing conservative Get-HealthSeriesKey hashing is reused; an eng/fixture
change may conservatively start a new series. Do not normalize that away.
Application commit/binary hashes remain provenance across revisions, not equality
keys; the stricter exact identities within each real rich handoff stay enforced.

Changed compatible workload/environment definitions may bootstrap a new eligible
series automatically once this algorithm is approved, never auto-change hard
product requirements. Keep all-attempt trends and prior references, including
age and raw artifact expiry. Compact validated numeric history survives expiry;
it never certifies fresh release acceptance. Release compares only an already
available reference and never waits for or self-baselines against collection.

## Bounded cost and orchestration proposal

Keep one active noncancelling reference cohort, sequential independent samples,
and at most two measurement workers within a sample (non-UI plus prefix).
Tail waits only for its own handoff; union follows it. This changes concurrency
from the currently independent collector and belongs to activation, not this
ticket. It must not share production-release cancellation/concurrency.

The original full pilot consumed approximately775/786 worker-seconds in its two
complete samples and at least846s in its failed third (runner sums, not billing).
The audit's fuller job-wall estimate remains17.733 worker-minutes per complete
execution:53.2 for three,88.67 for five, or60/100 minutes under per-job rounding.
With only the prefix watchdog proposed from13 to17 minutes, a conservative
four-job watchdog envelope is56 minutes/sample,168/280 for3/5 samples.
These are consumed-worker envelopes, not Windows billing multipliers or healthy
duration predictions. Failed executions consume budget and remain recorded.
The one diagnostic used693 job-wall seconds, at most12 rounded worker-minutes,
within its17-minute watchdog; its4,133,480-byte artifact expires after30 days.
No repeated full battery, production release, new cost ceiling extension or
additional diagnostic is authorized by this proposal.

## Public preview and approval handoff

Both existing observer CLIs accept TimingPolicyPreview for read-only output:
Invoke-NonUiCohort.ps1 -Stage Summarize and Invoke-LaneHealth.ps1.
They use the existing real result/union/schema validators and real Git history;
preview plus any history publication is rejected. Summarizing already retained
five-sample evidence is permitted, but planning/dispatch is not. Saved original
records, original policy versions, canonical pointers and release exits remain
unchanged. New grades are nested policyPreview data plus GitHub-compatible
summary Markdown, with version, current verdict, reference/envelope, headroom
and explicit unavailable states.

Public contracts cover no reference, pinned non-self/incompatible comparison,
normal variation, one slight slowdown, low headroom, old659/661 boundary,
five separated slow values, continued five-sample variation, zero-range
uncertainty, hard individual violations and cancelled/skipped/incomplete work.
Existing raw percentile/matrix/union rejection and immutable mixed-Git history
checks remain. Only external GitHub/hosted responses are faked. Controlled
fixture previews are not measured hosted speed or fresh release acceptance.

GitHub-visible conformance examples from that public flow (synthetic values,
not timing evidence):

| Input / pinned reference | Preview | Original validation |
|---|---|---|
| No compatible predecessor | Unbaselined; no reference invented | Unchanged |
| 111/120/129 s; reference110/120/130 | Green | Success |
| 111/120/131 s; same reference | Yellow, no serious regression;4/5 suggested only | Success |
| 639/640/641 s; same reference,660s safety | Yellow,19s headroom versus20s observed range | Success |
| Complete659s and661s prefixes; reference620/630/640 | **Both yellow**,900s proposed safety, no gate activation | Success / rejected respectively under original660 |
| Five200/201/202/203/204 s; reference110/120/130 | Serious-relative red, observation only | Success |
| Five111/120/129/160/100 s; same reference | Yellow/inconclusive, stop at five | Success |
| Individual profile p95=1000.1 ms while cohort median=800 | Hard red; no replacement samples | Rejected |
| Zero-range reference | Inconclusive, no noise-floor fiction | Unchanged |
| Cancelled/skipped/unfinished required work | Inconclusive; all attempts retained, no extension | Rejected |

Read-only real-evidence reproduction after repository/account verification:

```powershell
.\eng\Invoke-NonUiCohort.ps1 -Stage Summarize -Pilot -RichScale `
  -TimingPolicyPreview -ReadHistory `
  -Revision 5078960e12bef092a8a5fccc181943dabc0ac668 `
  -CiRunId 45528329616 -CiRunAttempt 1 `
  -CohortRunId 45704991010 -CohortRunAttempt 1 `
  -WorkingDirectory <new-isolated-output-directory>
```

This downloads retained evidence and emits record.json/summary.md. It runs
**no** workload, dispatch, release, history write or promotion. Real pilot
authority remains baseline-ineligible; absent data-branch history is visible.

**Maintainer decision required:** approve or revise ci-timing-preview-v1's
empirical envelope, five-execution separation/eligibility/bootstrap rules,
selected future gates, prefix900s/17m safety, unchanged remaining limits and
bounded concurrency/cost. Unsupported fixed bands remain pending. Record exact
approval/version on #130 before closing it or permitting #131/#132 activation.
#131 must update runners, deadline propagation, result validators, collectors,
gates and documentation consistently; #132 must implement trusted promotion/
series/ancestry semantics. #133 must demonstrate three eligible complete full
samples under the approved policy, with the unresolved #154 limitation intact.
