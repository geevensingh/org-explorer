# 10,000-Person validation

## Reference conditions

Measurements are local observations, not an agreed application-latency SLA.
The reference machine is Windows 11 Enterprise x64 (10.0.26300), an AMD EPYC
9V74 with 16 assigned logical processors, 64 GiB RAM, and an NTFS C: volume.
The SDK is .NET 10.0.403; test runtime is .NET 10.0.14. Builds use Release.
NuGet packages are cached. The machine is shared, not a dedicated benchmark
host. Parallel local filesystem/desktop lanes contend on the same volume;
hosted CI jobs use separate runners. Queue time and later CD are excluded.
No hosted-runner timings or installer/update performance are claimed here.

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

The normal WPF executable is driven with Windows UI Automation. The default
desktop lane includes scale, not a success-only app flag. It opens the Workspace,
uses Settings while loading, selects profiles/search destinations, navigates
Managers/deep branches, checks **actual rendered photo pixels**, edits, reviews
an actual 10,000-record partial batch, applies it, observes validated external
writing and reopens durable results. Ordinary smoke flows additionally retain
full import, notes, historical/archival, migration and failure coverage.

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
aggregate has two minutes: **29 minutes summed execution ceiling** and a
thirteen-minute critical-path ceiling, excluding queues and later CD.
The five-minute optimization target remains distinct from these hard ceilings.

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
