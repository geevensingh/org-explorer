# 10,000-Person validation

## Current execution authority

Read [Testing](testing.md#separate-scale-commands-and-results) for the current
separate commands, selection, revision/results contracts, nightly/manual
activation, hosted manual bridge and future pre-publication requirements.
That is the single execution guide; this document preserves fixture detail and
historical observations. Neither old combined runs nor non-UI passes prove the
new separated WPF suite. No baseline collection or new latency SLA is implied.

## Separation checkpoints (2026-11-10)

The #22 non-UI performance slice passed hosted
[run 42767878245, job 129668552430](https://github.com/geevensingh/org-explorer/actions/runs/42767878245/job/129668552430)
at `e14124f`, reporting **260.8 s**. The #23 independently named desktop-smoke
slice passed all then-required nine scenarios, all three lanes and the aggregate
in [run 42768271599](https://github.com/geevensingh/org-explorer/actions/runs/42768271599)
at `8f241a4fa796b8660f9a80a6026cb12df73c333d`. Its ninth scenario was the old
combined scale flow. These are dependency evidence, not proof of #24's two
separate WPF scenarios or its final required lane, which also retains #17's
authenticated update/settings scenario.

During #22, GitHub rejected direct dispatch of `scale-verification.yml` at
published integration commit `18dcee007e25f453eff2d6a8840c98def7b31885`
with HTTP 404 because it was not registered on the default branch. Supplying
`--ref` did not register it. The already registered CI manual-only bridge solved
that execution prerequisite without changing main or required PR selection.
The current guide retains that bridge for both separate suites.

## Historical acceptance checkpoint (2026-11-09)

Hosted [CI run 42709147493, attempt 2](https://github.com/geevensingh/org-explorer/actions/runs/42709147493/attempts/2)
passed all three required lanes and the fail-closed aggregate at
`845ce1b77a5a4b57dfa4f18a58b926dd38d24b51`. The complete genuine
10,000-Person WPF flow reported an explicit PASS, including actual photo
rendering, history, mutations, refresh and reopen, followed by PASS results for
all remaining desktop flows. This supplies the previously missing complete
desktop acceptance for #15; it is not a new result from the separated suite.

Desktop execution was **368.92 s including setup**: below the existing
660-second ceiling but above the 300-second target. Reported UI-scale profile,
favorite, reviewed refresh, external delivery and reopen operations were
26.906 s, 25.636 s, 29.050 s, 4.254 s and 7.213 s respectively. These are
historical hosted observations, not a baseline, input-latency guarantee or SLA.

The owner has parked the rare lock/publication investigation and authorized
proceeding assuming it fixed for now. A later separate elevated capture
completed 30 runs / 1,740 passed test executions, no failures or lost events;
that is **not causal proof of a repair** and does not erase the failures below.
Explicit safe save errors and all safety assertions remain intact.

The chronological records below retain their original failures, prerequisites
and then-current acceptance statements. In particular, earlier “#15 remains
open” or “no hosted pass” statements are historical, superseded by this
checkpoint, not claims about the current state. Installer/release status in
those records is also historical and is not changed by this test-process work.

## Reference conditions

Measurements are local observations, not an agreed application-latency SLA.
The reference machine is Windows 11 Enterprise x64 (10.0.26300), an AMD EPYC
9V74 with 16 assigned logical processors, 64 GiB RAM, and an NTFS C: volume.
The SDK is .NET 10.0.403; test runtime is .NET 10.0.14. Builds use Release.
NuGet packages are cached. The machine is shared, not a dedicated benchmark
host. Parallel local filesystem/desktop lanes contend on the same volume;
hosted CI jobs use separate runners. Queue time and later CD are excluded.
These reference conditions describe the recorded local measurements below,
not the later hosted checkpoint. No installer/update performance is claimed.

## Population and public seams

`tests\ScaleFixture.cs` constructs **exactly 10,000 fictitious people**, using
the same application workflow for initial import, private notes, transfers,
archival and a later Manager-note edit. It exports the documented readable
version-2 Workspace, including accepted import sources and typed evidence:

- 9,900 active and 100 Archived people;
- multi-level branches and a 42-edge formal Reporting chain;
- 2,500 local photo references to real 128×128 PNGs;
- 1,000 Person notes and 111 manager-owned Team notes;
- 200 immutable Historical team contexts, with old-chain ownership, frozen
  writing and causal source/local-recording evidence;
- structured attributes, preferred/directory names, textual offices and stars.

Filesystem assertions use `IApplicationWorkflow`, including load, reopen,
complete tree/profile queries, current/historical search, favorite/profile/note
saves, validated import preview/apply/replay and external delivery. They verify
counts, copied photos, omitted values, unchanged frozen writing, source identity,
stale retention and refused mutations, not just elapsed time.

The normal WPF executable is driven with Windows UI Automation, never a
success-only app flag. Current explicit UI scale separates navigation/rendering
from mutation/responsiveness into independently reported rich-fixture scenarios;
ordinary smoke keeps representative WPF journeys. Earlier measurements below
used the old combined scale flow inside the then-required desktop lane.
See the authoritative [testing strategy](testing.md) for what runs now.

Tests poll meaningful eventual UI/file state with bounded deadlines. A missing
virtualized/not-yet-realized control is retried only until that deadline; wrong
values, unsaved data, validation errors and unavailable destinations still fail.
There are no arbitrary readiness sleeps, truncated chains/notes, smaller proxy
populations, production credentials or company exports. Fixture generation,
flushes, restore/build and cleanup are counted in execution.

## Optimizations and safety boundaries

Sorted population/tree and owner/report lookups are private rebuildable
projections. Read-only accepted query snapshots keep browsing/search independent
of file decoding/staging. Substring search indexes all accepted current,
archived and frozen sources; it never becomes canonical data. WPF virtualizes
tree, Manager-choice and import-preview rows, preserves realized expansion
state, and discards superseded background search responses.

Loads and version scans reuse bytes only inside one validated read, with fresh
complete before/after fingerprints. Complete byte reads use at most eight
parallel workers; every worker joins before the existing Workspace I/O lifetime
gate releases. The pointer header is checked freshly before version scanning.
Live reads cover the complete pointed-to generation
and note documents, not accumulated recovery generations. Migration backup
fingerprints still cover **all** originals. Repeated photo references decode
once per validation, with safe containment, image content and byte hashes intact.

Staging uses bounded parallel writes and retains unchanged document bytes in
independent flushed files. All workers and source/resource revalidation finish
before atomic publication. No hard links, timestamp-only validation, hidden
history rewrites, provider conflict repair, import caps or save-error suppression
are introduced. The repaired process-local I/O gate and disposal guarantees
remain in place. Watcher quiet intervals are measured from final publication,
so an already queued self-event cannot race immediately into post-save reads.
Events from non-pointed staging/recovery generations do not delay current-data
delivery behind tens of thousands of irrelevant callbacks. Pointer/current
generation/note events still validate the whole state; incomplete delivery keeps
its bounded retry path. Watcher handles are detached under the watcher lock but
disposed outside that lock, while the workflow I/O lifetime gate remains held.

Desktop input coalesces rapid profile/Markdown edits after a 300 ms quiet period.
Drafts remain editable during prior writes, are explicitly pending/not saved,
and block unsafe navigation/closing until saved or deliberately discarded.
Settings and tree expansion remain available. This interval is an implementation
choice, not a latency promise.

## Execution evidence

The original `bba76c3` workflow baseline did not finish the complete scale
exercise after 30 minutes (stopped at 30m45s); it was not reported as a passing
CI run. An intermediate parallel run exhausted the former eight-minute lane
budget in both real filesystem and desktop lanes. Those failures prompted
retained-byte staging, source revalidation, query snapshots and bounded
parallel fixture cleanup; they were not replaced by smaller or skipped tests.

Per-lane JSON/log/TRX results are emitted under `.ci-results\`. Hosted timing
starts before checkout/SDK setup. Each meaningful lane has one shared execution
budget including setup/restore/build/test/fixtures, not a fresh budget per
command: five minutes for core and eleven each for filesystem/desktop. The
aggregate has two minutes. These recorded three-lane results used a
**29-minute summed execution ceiling** and a
thirteen-minute critical-path ceiling, excluding queues and later CD.
The five-minute optimization target remains distinct from these hard ceilings.
The subsequent required high-resolution smoke split adds a separate 11-minute
job without changing those lane deadlines: current CI totals 40 minutes summed
and retains the 13-minute parallel critical-path ceiling. See `testing.md` for
the current four-lane contract; historical timings below are not new acceptance.

Fixture source/resources are linked only into test projects. The desktop lane
also validates an actual local product publish folder and rejects test
assemblies/fixtures in it. No installer, updater or CD implementation is included.

### Recorded local results (2026-11-08)

The complete filesystem lane ran concurrently with core on this shared machine.
Its last passing execution includes restore, compilation, all 158 real-file
tests, construction and cleanup of the 10,000-Person fixture. The final core
rerun and final desktop attempt include their own restore/build work.

| Validation | Result | Execution |
| --- | --- | ---: |
| Core and CI gate | 139 passed, no skips; strict aggregate/budget checks passed | 7.981 s |
| Complete filesystem lane | 158 passed, no skips | 299.314 s |
| Complete desktop lane | Failed at migration photo rendering: desktop pixels unavailable | 40.411 s |
| Focused actual search/favorites UI | Passed current/frozen/archived reading, notes, mutations, failed-save safety, external refresh and reopen | 270.035 s |
| Separate actual product publish/isolation check | Passed; 8 product files, no test assemblies or fixtures | 1.438 s |

The filesystem lane's restore took 1.678 s; compilation, tests, fixtures and
cleanup took 297.583 s. Its completed scale-operation measurements were:

| Public workflow operation | Elapsed |
| --- | ---: |
| Representative fixture construction/export | 47.110 s |
| Initial Workspace load | 8.739 s |
| 100 Manager-navigation queries | 0.0032 s total |
| 100 note/inherited/historical profile queries | 0.0010 s total |
| First current-context search, including index construction | 0.1343 s |
| Frozen-context search | 0.0120 s |
| Favorite mutation and durable generation publication | 53.240 s |
| Private Person-note mutation | 8.712 s |
| Textual office mutation and durable generation publication | 53.994 s |
| Reviewed Manager-change preview | 0.0216 s |
| Reviewed Manager-change apply | 59.726 s |
| Reopen | 7.953 s |
| Complete reopened population/note/history assertions | 0.0451 s |
| Latest accepted-batch replay | 5.344 s |
| Original 10,000-record batch replay preview | 0.2925 s |
| Original batch replay apply, verified no-op | 5.437 s |
| Unchanged refresh | 2.276 s |
| Validated external Markdown delivery | 10.050 s |

These are whole synchronous workflow operations, not an input-latency SLA.
The reviewed Manager-change batch changes one Person in the genuine
10,000-Person Workspace; the original-batch replay contains all 10,000 records.
Ordinary durable generation mutations remain expensive despite bounded I/O:
the accepted-query snapshot and asynchronous desktop work preserve access to
the last accepted data while publication is outstanding.

A separate real scale UI attempt constructed its fixture in 39.374 s, opened
the normal executable and exercised Settings during load in 17.126 s, expanded/
collapsed the actual tree in 6.339 s, and searched/selected the target profile
in 8.700 s. These UI numbers include Automation traversal and eventual-state
polling. It then failed the mandatory real photo-pixel assertion. They are
partial measurements, **not** a passing desktop or full scale UI result.

### Acceptance checkpoint and remaining blocker

The final desktop host returned `GetPixel = FFFFFFFF` at valid, non-offscreen
96×96 image bounds, with no foreground window. A test-owned native window
capture probe also stalled and was stopped; its code and diagnostics were
removed. No shared desktop configuration was changed. Smoke now reports this
missing prerequisite explicitly, rather than eventually reporting a generic
timeout. Actual pixel assertions remain mandatory; no image-name/dimension-only
replacement, skipped flow or success-only executable mode was introduced.

**#15 is not fully accepted yet.** The complete actual desktop lane and remaining
scale UI historical search, mutations, 10,000-record reviewed refresh, external
delivery and reopen still require an interactive, unlocked Windows desktop:

```powershell
.\eng\Run-CiLane.ps1 desktop
```

The complete filesystem lane narrowly achieved the local five-minute target;
core also achieved it. A passing five-minute end-to-end/desktop result has
**not** been demonstrated. The independent CI jobs retain their 29-minute
summed hard ceiling, but no passing hosted CI run is claimed. The actual
product publish isolation check passed separately after the desktop failure;
it is not a substitute for that failed lane. Installer #16, updater and CD
remain outside this ticket.

## Review-fix validation (2026-11-09)

Review fixes were implemented in an isolated worktree on
`implement/review-fixes`, based on integration `6bb1f762b8b71b5e6bfcb84e72af8b5701b47321`.
The integration checkout was neither switched nor edited.

The integration evidence initially reported 154/158 filesystem tests, with two
note-save/replacement failures and two watcher-delivery failures. The isolated
baseline reproduced the two watcher failures: **156 passed, 2 failed, no skips**,
401.190 s including restore/build/fixtures. All 41 selected note/history/archival
tests passed in isolation, as did twelve repetitions of the four originally
failing tests. The note-replacement errors were not independently reproduced;
they are not claimed as separately diagnosed.

A real-file overlap first reproduced the review defect: saving note A allowed
delivery of note B to be fingerprinted as current while queries still returned
`Provider original`, not `Provider delivered`. The retained regression uses the
public workflow and a narrow operating-system publication boundary that writes
real files, controlling delivery between the final check and atomic replacement
without sleeps or oversized documents. Nine cases cover Person/Team publication,
generation publication, either delivered note kind, invalid ownership, refresh,
reopen and retained failed drafts. Reinstating the original live-capture
acceptance made **all nine fail**; expected-byte acceptance made **all nine pass**.
The red/green TRX files are in `.ci-results\review-focused\`.

Targeted watcher tracing measured callbacks blocked behind busy workflow gates
for more than ten seconds during concurrent fixture construction. A minimized
same-process fixture-load run reproduced delivery failures. Nonblocking gate
deferral then passed all 167 cases in that diagnostic run (including its
temporary fixture-load probe), without extending eventual assertions. The probe
and all debug instrumentation were removed. The permanent suite retains its full
10,000-Person fixture and adds the nine publication cases.

The review also consolidated the duplicated atomic Markdown transaction,
import-envelope/record scalar validation and composed desktop note-draft safety.
Person versus Team metadata, review/inheritance UI, proposed graph validation
and resource checks remain independent.

A follow-up red test exposed a concrete remaining validator drift:
`schemaVersion: 1.0` passed import preview/apply but failed reopening with
`accepted batch source.schemaVersion: expected an integer`. The shared import
version rule now also serves pre-write version inspection and accepted-source
reopening, preserving preview's supported numeric representation and
unsupported-version upgrade blocking. This adds one further public-workflow/
real-filesystem regression, bringing the permanent filesystem suite to 168.

The complete desktop attempt on this date did obtain actual rendered photo
pixels, unlike the earlier recorded host attempt. Its mandatory lane still
failed closed at **660.457 s**, exhausting the unchanged execution budget during
scale flows after search/favorites and actual photo rendering; measured profile
and favorite mutations were 90.300 s and 105.081 s under concurrent validation.
This is not a complete desktop pass, and the previous pixel prerequisite must
not be represented as the failure of this newer attempt. No desktop/login
configuration or pixel assertion was changed.

After committing the code fixes (`c0b84d36e5fb9013c9902d91059944dd9a8e0917`),
the integration tip was merged again (already up to date), and combined
validation was rerun on that state. All runs used the existing SDK/tooling on the
same shared Windows host; these are local reference results, not hosted CI
claims.

| Validation | Result | Execution |
| --- | --- | ---: |
| Core plus fail-closed CI gate checks | 139 passed, no skips; aggregate/budget rejection checks passed | 92.279 s |
| Complete filesystem lane, first combined run | 167 passed, no skips, including genuine 10,000-Person operations | 500.423 s |
| Five concurrent-load repetitions of note/history/archival/read-lifetime/overlap cases | 57 passed in each repetition, no failures or skips | TRX results retained per repetition |
| Separate actual product publish/isolation check | Passed; 8 product files, no fixtures or test assemblies | 3.623 s |

The first combined filesystem run remained below the eleven-minute hard
ceiling, but **did not achieve the five-minute optimization target**. Its durable
favorite, profile and reviewed-apply operations measured 67.485 s, 74.845 s and
85.397 s respectively. No practical desktop-latency acceptance is inferred from
these timings.

The second full repetition on that intermediate state was **not stable**:
166/167 passed and one unchanged five-second invalid-delivery assertion timed
out, in 573.473 s. This result is retained in
`.ci-results\review-validation\filesystem-2-failed\`; it is not counted as a
passing repetition. Watcher callbacks now defer at the process-local Workspace
I/O gate as well as their own workflow gate, rather than blocking pool workers
behind sibling clients' synchronous publication. Explicit refresh/read
coordination still waits normally. The complete non-scale suite then passed
167/167, including the new version regression, in 13 s.

### Final refinement results

The final production-code refinement is
`b5948ff5c722ccb9e29869424209180ac45f6932`. Subsequent
`39cf2f715b68dcb9bece4c95e2a8d28ada7b4525` adds observable autosave-completion
barriers to the real Team-note smoke flow. That flow initially hit an
Automation enabled-check/`SetValue` race when an outstanding note autosave
disabled profile controls. The fix waits on the existing Ready/Pending UI
states before dependent operations; it adds no sleeps, exception retries,
skips, weakened assertions or longer timeouts.

| Validation | Result | Execution |
| --- | --- | ---: |
| Final core and fail-closed CI gate checks | 139 passed, no skips; rejection/budget checks passed | 13.827 s |
| Complete final filesystem lane | 168 passed, no skips | 304.850 s |
| Complete final filesystem repetition, after refreshing integration ancestry | 168 passed, no skips | 286.283 s |
| Five final concurrent-load note/history/archival/read-lifetime/overlap repetitions | 58 passed each, no failures or skips | Per-run TRX retained |
| Actual WPF Team-note flow | Passed ownership, inheritance, review, failed-save/retry, moves, private rendering and reopen | 50.222 s |
| Actual WPF Person-note flow | Passed edit/read, real photos, external delivery, failed saves, retry/discard, navigation/close guards and reopen | 52.013 s |
| Final rebuilt product publish/isolation | Passed; 8 product-only files | 4.028 s |

The 304.850-second filesystem result still misses the five-minute target when
restore/build are included, despite its test-only duration being under five
minutes. The 286.283-second full repetition did achieve that local filesystem
target. No rounding-down or test-only timing is used, and neither run establishes
an application-latency SLA or a passing five-minute complete desktop result.

The second integrated **complete desktop lane** failed closed at 660.213 s
before the final I/O-gate refinement. Unlike the prior pixel-blocked attempt,
it passed real photo rendering, search/favorites and the **entire genuine
10,000-Person UI flow**, including historical search, navigation, mutations,
validated refresh, external delivery and reopen. Its log contains that flow's
explicit PASS; it does not contain a complete desktop-executable PASS. Scale UI
profile/favorite mutations measured 81.676/87.619 s, the reviewed refresh
122.102 s, external delivery 21.533 s and reopen 35.818 s.

Finally, the complete desktop lane was rerun on **all committed fixes**
(`39cf2f715b68dcb9bece4c95e2a8d28ada7b4525`) after another integration ancestry
check and cleanup of stopped synthetic fixtures. It failed closed in
**154.583 s** at the mandatory scale photo assertion:

```text
Actual UI pixels are unavailable. Desktop smoke requires an interactive,
unlocked Windows desktop; image assertions cannot be skipped.
```

That latest attempt passed migration and search/favorites before reaching scale
load/tree/current search, but is not a complete scale or desktop pass. The host
therefore supplied real pixels in earlier runs and lost that prerequisite in the
final run; neither outcome is inferred from older evidence. No OS/login
configuration or pixel assertion was changed. The current final logs/timing are
in `.ci-results\desktop\`; the previous budget failure is retained in
`.ci-results\review-validation\desktop-2-budget\`.

The focused note flows and product publish are separate successful
checks, not a substitute for the failed complete desktop lane. **#15 remains
acceptance-open** pending a passing complete desktop lane and its remaining
end-to-end budget/latency verification. #16 remains unmerged and
verification-blocked; #17–#19 remain blocked. No push, pull request, issue
closure, installer merge, desktop/login change or pixel-assertion bypass was
performed. The three CI lanes and their 29-minute summed fail-closed ceiling
are unchanged.

## Post-merge sharing failure follow-up — unresolved (2026-11-09)

The parent merged the review work as
`591b6ecbd6ae37e206d7fbfc6cd2d2ffeac25107`. Its exact 58-case selection passed
once, then failed 57/58 at
`LocalPublicationDeliveryTests.A_local_publication_accepts_only_its_validated_base_and_own_changes`
(`person`, Person-note delivery, valid delivery), at the initial local-save
assertion. The reported error was `Unable to remove the file to be replaced.`
The remaining parent repetitions were not run.

The isolated branch was fast-forwarded to that integration tip. Investigation
did **not** establish a root cause or a justified product/test repair. In
particular, a sharing exception is not proof of an in-process read-coordination
bug: a deliberately restrictive external read correctly refuses publication,
retains the saved note, and surfaces an explicit save failure. The corresponding
permissive read/delete-sharing control succeeds. These controls distinguish the
contracts, but do **not** identify the owner of the sporadic reported lock.

Every repetition below used the parent's original filter, not the earlier
similarly sized selection containing Profile-history tests:

```text
FullyQualifiedName~PersonNotePersistenceTests|FullyQualifiedName~TeamNotePersistenceTests|FullyQualifiedName~HistoricalTeamContextPersistenceTests|FullyQualifiedName~PersonArchivalPersistenceTests|FullyQualifiedName~WorkspaceReadLifetimeTests|FullyQualifiedName~LocalPublicationDeliveryTests
```

All scheduled iterations in each batch were completed and retained, including
failures; none was discarded or rerun to replace its result.

| Follow-up batch | Complete 58-case runs | Passed test executions | Failed test executions |
| --- | ---: | ---: | ---: |
| Uninstrumented baseline | 11 | 636 | 2 |
| Temporary native lock-owner diagnostics | 10 | 580 | 0 |
| Failure-only native owner diagnostics | 20 | 1,160 | 0 |
| Paired-process/read-lifetime diagnostics | 20 | 1,160 | 0 |
| First-pass exception diagnostics | 20 | 1,159 | 1 |
| Final uninstrumented, unchanged original selection | 20 | 1,160 | 0 |
| **All follow-up batches** | **101** | **5,855** | **3** |

No tests were skipped. The baseline failures were the concurrent read/favorite
save assertion and a `batches.json` cleanup sharing exception in
`WorkspaceReadLifetimeTests`. The first-pass diagnostic batch failed while
`PersonArchivalPersistenceTests.Seed` deleted `producer.png` after import.
Neither is silently treated as a successful reproduction or repair of the
parent's exact initial Person-note publication failure.

Minimization/observation attempts also retained:

- 200 isolated repetitions of the exact reported Person-note scenario passed.
  A further instrumented 200 repetitions and 100 concurrent read/favorite
  scenarios passed; these are workflow iterations inside two diagnostic tests,
  not 300 additional discovered test cases.
- Two raw-filesystem probes, with no application workflow or watcher, each
  completed 10,000 stage/flush/provider-write/replace/delete sequences. One used
  unique names and JSON-front-matter content. Both passed.
- Read-only Windows Restart Manager lock-owner observation was calibrated
  against a deliberately held handle. No unexpected publication exception
  recurred while that observation was active, so **no culprit process or
  in-process handle was identified**.
- All diagnostic callbacks, reader tracking and throwaway probe source were
  removed. `src\` and `tests\` were confirmed byte-for-byte unchanged from the
  merged integration state before final validation. No writer retries,
  catch-all fallback, assertion relaxation, filesystem-indexing exclusion, OS
  change or pixel-assertion bypass was introduced.

Final validation on that unchanged code:

| Validation | Result | Execution |
| --- | --- | ---: |
| Full core plus fail-closed CI-gate checks | 139 passed, no skips | 13.614 s |
| Full real-filesystem lane | 168 passed, no skips | 360.909 s |
| Original 58-case selection, complete final batch | 20/20 runs passed; 1,160/1,160 test executions | All per-run TRX retained |
| Actual Team-note UI | Passed | 149.637 s |
| Actual Person-note UI | Failed closed at mandatory photo pixels: interactive/unlocked desktop prerequisite unavailable | 31.192 s |

Complete per-run outcomes, errors, traces and validation timing are retained in
`.ci-results\publication-repair\followup-results.json` and the adjacent TRX/logs.
The later all-green batch is **not evidence of a causal fix**: it cannot erase
the initial/intermediate sharing failures. Publication/concurrency acceptance
therefore remains unresolved. A file-I/O ownership capture during an actual
uninstrumented failure is still needed before attributing the lock to app
readers, test resource lifetime, or another process and changing policy.
The prior validated-revision, shared-writer, validator and draft-safety fixes
remain intact; this follow-up does not claim that their complete concurrency
verification is finished. #15 stays acceptance-open, #16 stays unmerged, and
#17–#19 stay blocked. No push or issue/PR closure was performed.
