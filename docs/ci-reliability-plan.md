# CI reliability and timing health

**Status:** Implementation integrated on `integration/ci-reliability`; final
complete hosted verification and joint `main` delivery pending under #133.

**Reviewed design baseline:** freshly fetched `main` at
`caec4da7a96a57a5e49b3ea047f29eaabf299835`, verified on 2026-11-25.
Implementation must repeat the [implementation preflight](agents/implementation-preflight.md).
This document does not amend today's acceptance rules by itself.

**Current integration policy:** the [approved versioned policy](ci-timing-policy-proposal.md)
is implemented by #131/#132 as source-bound `ci-timing-v2`: rich prefix
900-second safety/30-minute watchdog, both rich note phases 1,000-ms goal and
1,500-ms inclusive ceiling. Serious-relative blocking additionally requires
five complete comparable executions and both 20%/100-ms product-latency or
20%/30-second whole-duration floors, alongside the approved empirical rule.
Other product, causal, matrix and sample requirements are unchanged.

The original 660-second/1,050-ms observations below retain their original
policies and verdicts. Pre-delivery pilot/manual/diagnostic evidence cannot
become a canonical `main` reference. Automatic default-branch activation and
production history establishment remain pending joint delivery; public isolated
Git fixtures prove those contracts without granting production authority.

## Objective

Make CI dependable and make deterioration visible before it becomes a failure,
without requiring routine manual maintenance of performance baselines.
Cover all existing CI tests, nested stress/concurrency workloads, desktop smoke,
performance/scale runs, installer checks, and release-related validation.
Do not introduce unrelated product features or a separate dashboard.

Separate two questions:

- **Validation outcome:** did every required check execute completely and satisfy
  its correctness and explicit hard performance requirements?
- **Timing health:** how healthy are the measured durations, how much headroom
  remains, and is a meaningful slowdown supported by comparable evidence?

A completed run can pass validation while timing health is yellow. A run killed
before completing its assertions has incomplete validation, not proof that the
product is wrong and not permission to report a pass.

## Starting evidence and original contracts

The initial read-only inventory found these lanes at the reviewed revision.
The audit must verify the full selection beneath each entry, not just workflow
names or green workflow conclusions.

| Area | Existing selection and role |
|---|---|
| Required CI | Core/view models and runner contracts; real filesystem persistence excluding the explicit Performance category; desktop people/workspace partitions; high-resolution reading/context partitions; aggregate completeness checks |
| Nested validation | Compiled-product checks within desktop smoke, targeted concurrent-client/read/write tests, repeated mutations and multi-sample workloads; these are not a dedicated whole-suite soak lane |
| Explicit performance | Complete named 10,000-Person non-UI persistence workload, outside ordinary PR selection |
| Explicit UI scale | Independent prefix and tail workers with a strict union; the tail uses the prefix's actual published logical snapshot |
| Installer | Separate path-filtered/manual installer lifecycle checks with their stated platform and trust limitations |
| Release and renewal | Fresh exact-candidate verification, current-run/attempt artifacts, publication provenance and renewal checks; historical success is not fresh acceptance |

At the reviewed design baseline, CI lane execution ceilings included 300, 660
and 900 seconds. Non-UI
performance and each rich UI-scale worker have a 660-second whole-execution
acceptance budget, including setup and cleanup. A larger Actions job timeout is
reporting/watchdog allowance, not extra passing time. Extended scale observation
is diagnostic and cannot currently qualify for acceptance.

At that baseline, rich note durability had a 1,000-ms goal and an explicitly temporary
1,050-ms enforced ceiling. Other product latency and correctness contracts remain
separate. Preserve raw samples, required sample counts and existing percentile
calculations; a cohort median must not replace a per-run acceptance assertion.

The [failed release run](https://github.com/geevensingh/org-explorer/actions/runs/45527340954)
at `48c86f14bbe0dea9af72fd3b27f14b50f458ed00` exhausted the prefix's shared
660-second budget. Its two note-durability p95 measurements, 789.9 and 969.4 ms,
passed. The tail could not execute and the union rejected missing evidence.
The [following release run](https://github.com/geevensingh/org-explorer/actions/runs/45529573050)
at the reviewed revision completed that prefix in approximately 647 seconds.
The intervening commit did not change the application or rich-scale harness.
This supports timing sensitivity; it does not establish a product, harness or
runner root cause.

Existing sources include [testing](testing.md), [release policy](releasing.md),
historical [performance evidence](performance-validation.md), the workflows
under `.github/workflows/`, and their runner/result contracts under `eng/`.
Run identities, phase/command timings and artifacts already exist, but are not
yet a durable automated comparison system.

## Graded policy

| Timing health | Meaning and delivery behavior |
|---|---|
| Green | Available comparable measurements satisfy the calibrated healthy bands, without a meaningful regression or low-headroom warning |
| Yellow | A slowdown, variability or low-headroom condition warrants attention; yellow alone does not block merge or release |
| Red | An agreed hard performance requirement is violated, or sufficiently evidenced serious deterioration meets a calibrated blocking rule |
| Unbaselined/inconclusive | Comparison evidence is absent, incompatible or insufficient; do not invent a green comparison or assert a proven regression |

Report validation outcome independently. Failed, skipped, cancelled, missing or
unfinished required verification cannot pass, regardless of a timing label.
Likewise, one slow run is not by itself proof of a serious relative regression.
An explicit hard product violation remains actionable without averaging it away.

Missing baseline data is not a new release prerequisite. Existing required
verification must still pass; a missing comparative reference is visible and
nonblocking by itself.

The audit may propose replacing whole-suite cutoffs such as 660 seconds with:

- Healthy-duration and warning bands that expose increasing cost and low headroom.
- Evidence-backed serious-regression rules using comparable measurements and
  observed variation.
- A separate, higher safety timeout that bounds stuck work and resource cost.

Completed 659- and 661-second executions should not receive opposite judgments
merely because of the old cutoff. Their judgment depends on the approved bands
and evidence. Reaching the separate safety timeout still produces incomplete
validation and cannot qualify as a pass.

Do not choose arbitrary percentage bands, pad every timeout, silently convert
product requirements to warnings, or retroactively relabel historical failures.
Before activating changed acceptance, explicitly update the affected runner,
collector, tests and documentation together. Each metric's policy must identify
its goal, warning criteria, blocking criteria and any execution safety timeout.
Concrete values and the serious-regression evidence rule are outputs of the
audit/calibration phase, not invented by this planning document.

## Automated reference collection

For each new `main` revision that passes required CI, automatically collect a
cohort of three independent complete performance/scale executions:

- Each execution has fresh workers and fixtures. Three latency samples inside
  one process do not constitute three independent executions.
- Each UI execution preserves the real prefix-to-tail logical handoff, complete
  scenario/phase selection and strict union. Independent executions must not
  share mutable fixture state or substitute equivalent reseeded handoffs.
- Retain each execution's outcome, raw metric samples, configuration, candidate,
  run/attempt, phase and timing provenance.
- Summarize each selected metric using the cohort median and variability while
  retaining the individual values and all individual acceptance outcomes.
- If otherwise eligible measurements are inconsistent, collect up to five
  executions total. If still inconclusive, retain the previous reference and
  report uncertainty. Do not keep trying until three successful runs appear.

Failed or incomplete executions remain part of the cohort history and cannot
be silently excluded to manufacture eligibility. Additional measurements for
inconsistency are not authority to erase a correctness failure, hard-limit
violation or confirmed blocking regression. Distinguish observed failure from
root-cause attribution; an infrastructure failure is not automatically a product
regression.

Use latest `main` as the initial reference source. The reviewed successful run
alone is not a completed three-execution reference cohort.

### Comparison and promotion

Reference creation and ordinary reference updates require no routine manual
approval after the policy is calibrated.

Pin an already available compatible reference before evaluating a candidate.
Compare before replacement and preserve that comparison result. A candidate's
own measurements cannot become its yardstick for erasing a warning or failure.
If the active reference is already for that candidate, use its recorded eligible
predecessor; otherwise report the comparison as unavailable.

Automatically promote only eligible, complete cohorts. A rejected or inconclusive
cohort leaves the prior eligible reference intact, with its actual revision and
age visible. A slow earlier cohort completing out of order must not overwrite a
newer eligible reference.

Record code revision as provenance, not as a requirement that comparator and
candidate have identical application code. Comparability instead depends on the
selected workload, fixture/size matrix, metric definition, policy and relevant
environment/runtime configuration. The audit must define these matching keys.
Do not compare different selections or silently normalize hardware/configuration
changes into product regressions.

A changed workload or incompatible environment can automatically start a new
comparison series after collecting eligible evidence. Until it has a usable
reference, label it unbaselined. This does not authorize automatic changes to
hard product requirements.

Keep recent trends alongside the automatically refreshed reference. Cumulative
deterioration remains visible, but avoiding routine baseline maintenance takes
priority over maintaining a permanently fixed benchmark.

### Independence from release

Reference collection is not an additional release gate, and release does not
wait for it.

- Preserve required fresh exact-candidate release verification and current
  run/attempt provenance. Historical reference records are comparison inputs,
  never replacement acceptance evidence.
- Release timing comparisons use an already available compatible reference.
  No reference means an explicit unbaselined comparison, not waiting for the
  release candidate to manufacture a reference for itself.
- A later reference cohort can reveal a problem after publication. Preserve and
  surface that finding; do not rewrite the completed release's historical verdict
  or imply it waited for evidence that did not yet exist.
- An already completed verification execution may contribute a reference sample
  only when it satisfies the cohort's independence/configuration/evidence rules.
  Such reuse must not introduce a dependency from release to collection or bypass
  exact-attempt release contracts.

Ordinary PRs retain their current selection, with timing reporting added.
Targeted extra verification may establish an affected workload's suspected
regression; the full repeated performance/scale battery is not automatically
added to every PR. Unexecuted scale work is not green or release evidence.

Bound collection concurrency and estimate hosted-worker cost before enabling
automation. Parallelism can limit waiting time but does not reduce consumed
worker minutes. Keep collection separate from production release concurrency;
never cancel required release verification to refresh references.

## Reporting and history

The first version uses GitHub checks and run summaries, not a separate dashboard.
Show validation outcome and timing health separately, with workload/phase,
measured duration or product latency, reference revision, change, variability,
remaining headroom where applicable, policy version and evidence links.
GitHub's native binary check conclusion need not acquire a yellow state: the
summary carries the graded label and the approved gate determines blocking.

Preserve failed-attempt and cancellation history beside later successes. Report
reliability over all attempts, not just measurements retained for successful
baseline promotion. Include failure counts/rates and enough context to distinguish
flakiness, infrastructure problems, deadline exhaustion and confirmed product
failures without assuming their causes.

Store compact numeric measurements, cohort outcomes, configuration and
run/comparison/promotion provenance in a dedicated GitHub data branch. Do not
require a new metrics service, write routine measurement commits to `main`, or
modify product release metadata for this purpose.

Keep bulky logs, screenshots and raw diagnostic evidence as Actions artifacts
with an explicit retention policy. The compact history must survive artifact
expiry and clearly indicate when its linked raw evidence is no longer available.
Historical metric records still cannot certify fresh release acceptance.
Restrict write authority to trusted collection/promotion automation; treat
retrieved records and artifacts as data, validate their schema and provenance,
and never execute content supplied by a candidate artifact.

## Work plan and checkpoints

1. **Audit completeness and reliability.** Inventory actual tests/selectors,
   nested stress and compiled paths, triggers, environments, timeouts, artifacts,
   retries and requiredness. Review retained successful and unsuccessful attempts
   at latest `main` and relevant history. Identify coverage/evidence gaps,
   phase bottlenecks and root causes without treating a green aggregate as proof
   that optional work executed. Produce a per-lane policy ledger, prioritized
   reliability findings and a hosted-resource estimate.
2. **Establish comparable measurements and history.** Reuse existing telemetry
   where adequate; add only named lane/phase and user-facing metrics that support
   diagnosis or policy. Implement complete attempt records, durable history,
   matching keys and independent reference collection in observation mode.
   Measurement overhead counts honestly; do not subtract harness time to make
   acceptance fit a target.
3. **Fix supported reliability causes.** Address demonstrated product,
   fixture/harness, synchronization, setup or infrastructure-handling defects
   within the audited scope. Preserve required selections, assertions, sample
   counts and real product paths. Do not replace root-cause work with automatic
   retry-to-green, skips, weaker assertions or arbitrary timeout expansion.
4. **Calibrate once.** Use comparable repeated evidence to propose concrete
   healthy/warning/blocking bands, variability rules, reference eligibility and
   separate safety timeouts. Exercise ordinary noise and real or controlled
   regressions at the appropriate seams. Obtain policy approval before activating
   new regression-blocking rules or replacing current acceptance cutoffs.
   This checkpoint is not approval of every subsequent baseline update.
5. **Activate and deliver together.** Apply the approved policy consistently to
   runners, result validators, GitHub reporting and affected PR/release gates.
   Reference collection remains independent of release. Observe the resulting
   reliability and adjust policy deliberately only when evidence requires it;
   ordinary eligible reference promotion stays automatic.

The audit may discover that a requested reliability improvement needs a product
decision or different scope. Report that contradiction before implementation,
rather than quietly weakening an existing guarantee.

## Required behavior checks

The implementation plan must cover these real result shapes and boundaries:

- Complete near-target executions receive consistent graded judgments on both
  sides of an old suite cutoff; a safety-timeout interruption cannot pass.
- Yellow remains nonblocking. A hard product violation or sufficiently evidenced
  red regression blocks the affected verification after policy activation.
- A healthy cohort promotes automatically; inconsistent measurements use at most
  five executions, then retain the prior reference with an inconclusive result.
- Failed, skipped, missing and cancelled executions remain visible and cannot be
  voted away by a cohort median or replaced by successful retries.
- Candidate self-comparison, incompatible workloads, changed environments,
  missing/expired evidence and out-of-order promotion are handled explicitly.
- Release verification can complete before, during or after reference collection
  without waiting for it or consuming stale acceptance evidence.
- No available reference is visible and nonblocking by itself; it does not invent
  green relative health or suppress existing hard checks.
- The complete original scale workers/handoff/union and required PR selections
  remain intact. New metrics do not replace exact correctness/durability proof.

Use focused non-UI contract tests for collection, comparison, promotion, failure
handling and gating where possible. Actual desktop stability and timing require
hosted Windows or an explicitly authorized isolated desktop under [testing](testing.md);
do not run repeated native UI workloads on a shared local desktop.

## Documentation handoff

Publish this approved Markdown as a focused, pushed documentation-only commit
and return the verified immutable source branch, base commit, documentation
commit and exact file list in the producer's handoff record. Do not infer the
documentation commit from a later moving branch tip.

The approved source is `geevensingh-ci-reliability-design`, documentation commit
`4334a254827d450151b536347b52dc20b49fd487`, immediately based on
`caec4da7a96a57a5e49b3ea047f29eaabf299835`, changing exactly
`docs/ci-reliability-plan.md` in `geevensingh/org-explorer`. Integration owner
#123 established `integration/ci-reliability` and incorporated that source commit
by ancestry. Its immutable handoff fields do not move with later implementation
or documentation updates. Each receiver must reverify source/incorporation and
the freshly fetched integration baseline in its own worktree.

Keep this design unmerged into `main` until joint delivery under the
[documentation handoff contract](agents/documentation-handoff.md). The planning
handoff does not create implementation tickets, authorize performance execution,
open a PR or approve merging.
