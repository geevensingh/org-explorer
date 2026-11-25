# Testing

This is the authoritative test-placement and execution strategy for developers
and agents ([approved spec #21](https://github.com/geevensingh/org-explorer/issues/21)).
Choose the lowest-cost agreed surface that can prove the behavior, not a project
name. A desktop click alone does not establish a successful navigation or save.

## Person and writing contracts

- **Profiles, identity and reporting:** read [Person profiles](testing/person-profiles.md) for whole-profile editing, Team names, photos and Copy assertions.
- **Preferred-name Save/Enter:** read [Person-text submission](testing/person-text.md) for defaults, original bases, failed retries and cancellation.
- **Person-note or Team-note edits:** read [note writing](testing/note-writing.md) for autosave, admitted/newer generations and independent failures.
- **Ongoing/Other or current/frozen notes:** read [note reading and context](testing/note-reading-context.md) for exact selection, modes and attribution.
- **Person search or browsing:** read [search and navigation](testing/person-search-navigation.md) for ranking, accepted destinations and retained writing.
- **Responsive reading or editor geometry:** read [Person reading layout](testing/person-reading-layout.md) for restoration, anchored content and evidence limits.

Visual-refresh journeys can capture named, already-observed states through the
shared `CaptureVisualEvidence` harness helper. It reads actual desktop pixels
without opening, focusing, resizing or repairing a view. The existing hosted
desktop lanes retain these PNGs beside JSON recording the source revision,
run/attempt, executable hash, DPI and captured bounds. Calls require unique
state names within a scenario and never overwrite an earlier observation.
Optional application-monitor capture retains the full actual monitor, including
the taskbar region, and labels its scope separately from a window-bounds capture.
Captures follow the same desktop-isolation rules below; they are comparison
material, not behavioral proof or owner visual acceptance.

For compact backdrop contrast and exact-row browsing restoration, read [Person reading and responsive layout](testing/person-reading-layout.md#browsing-restoration).

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

**Workspace, Import and update contracts:**

- When changing Create/Open, startup restoration, Settings or exit, read [opening and exit](testing/workspace-opening-exit.md).
- When changing local SQLite, WAL readers, receipts or crash reconciliation, read [storage and recovery](testing/workspace-storage-recovery.md).
- When changing publication, incoming adoption, periodic sync or whole-version recovery, read [synchronization](testing/workspace-synchronization.md).
- When changing reviewed Apply/reentry, collector policy or captured producer evidence, read [Import and producer verification](testing/import-and-producer.md).
- When changing signed update drain, safe restart or installer preservation, read [application updates](testing/application-updates.md).

`OrgExplorer.Core.Tests` is **not synonymous with isolated unit tests**. Its two
file-backed `WorkspaceCompatibilityTests` carry `Category=Filesystem` and still
run in the core lane; they observe unsupported-version nonmutation and validation
retry through the view model. `DesktopScenarioRunnerTests` also write real result
and fixture files (`Category=Runner`) without opening WPF. Most other core cases
use in-memory workflow state. Classify dependencies by what a test actually does.



Use independent expected values at the agreed public surfaces. Keep parsing,
storage mechanics and graphs behind the shared workflow; private-method tests,
per-record public repositories and success-only app flags are not migration seams.

For bounded shared-Team-note excerpts, read [Person search and browsing](testing/person-search-navigation.md#grouped-person-search-and-retained-writing).

For note bases, held entry and temporary busy-discard allowance, read [Person-note and Team-note writing](testing/note-writing.md#immutable-drafts-and-scoped-cancellation).

For Preferred-name submission, defaults and original bases, read [Person-text and Preferred-name submission](testing/person-text.md#submission-and-immutable-bases).

For profile Save/Cancel and leave decisions, read [Person profiles, reporting and editing](testing/person-profiles.md#profile-editing-and-cancellation).

For Person categories and reporting summaries, read [Person profiles, reporting and editing](testing/person-profiles.md#categories-and-reporting).

Read-only multi-control native checks batch their requested IDs and properties
in one fresh UI Automation observation, avoiding a full-window search for each
field. Each readiness poll and post-action check captures again; observations
do not survive navigation, focus/scroll changes or updates. Text-pattern, pixel,
selection and keyboard assertions remain actual provider/desktop observations,
with the same complete scenario selection, size matrices and execution budgets.
Responsive editor checks acquire the displayed identity inside the existing
readiness poll, so a transient post-resize absence cannot bypass that wait.

For file-backed profile sessions and stale saves, read [Person profiles, reporting and editing](testing/person-profiles.md#profile-editing-and-cancellation).

For owned and inherited Team names, read [Person profiles, reporting and editing](testing/person-profiles.md#manager-owned-team-names).

For inherited retained-owner mode preservation, read [Note reading, selection and Team context](testing/note-reading-context.md#read-and-edit-presentation).

Control only genuine external effects (clock, browser/release/installer boundary);
do not mock away the business behavior being asserted.






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
On build-tree cleanup refusal, `cleanup-failure.json` retains the exact owned
failed path, original error and a bounded read-only Windows owner query through
the existing file-lease helper. The helper is compiled alone, not loaded from
the test binaries being removed. Missing owners and query failures stay unknown;
there is no deletion retry, owner shutdown or passing cleanup fallback.
Observation faults cannot replace the original lane failure. A real held-file
runner contract verifies this evidence and unchanged failing verdict.

For exact secondary selection and held-export Edit preparation, read [Note reading, selection and Team context](testing/note-reading-context.md#read-and-edit-presentation).



For format-qualified note authority, first-edit cadence and retained generations, read [Person-note and Team-note writing](testing/note-writing.md#immutable-drafts-and-scoped-cancellation).

For durable note generations, held navigation and native latency, read [Person-note and Team-note writing](testing/note-writing.md#durable-typing-and-save-handoffs).

For SQLite profile saves and held-navigation evidence, read [Person profiles, reporting and editing](testing/person-profiles.md#durable-saves-and-held-navigation).












For native note latency observation and invisible query-work evidence, read [Person-note and Team-note writing](testing/note-writing.md#durable-typing-and-save-handoffs).

For bounded live Team-name discovery, read [Person profiles, reporting and editing](testing/person-profiles.md#manager-owned-team-names).

For continued-typing workload, immutable observation time and policy history, read [Person-note and Team-note writing](testing/note-writing.md#durable-typing-and-save-handoffs).





The #100 hosted-failure regressions also run in that full selection. Focused
`--verify-cutover-publication`, `--verify-cutover-note-modes`,
`--verify-cutover-acknowledgment`, `--verify-cutover-failure-feedback`,
`--verify-cutover-incoming` and `--verify-cutover-close-publication` selectors
load actual BAML without showing a window or creating a native handle.
They use public SQLite creation and complete
snapshot transport, not canonical-file edits. The folder adapter creates the
actual synchronization directory before the picker can select it. Clean note
owner returns reopen Read; pending/failed writing returns Edit with its exact
owner, identity, draft and error. The native journeys retain both-scope reading,
historical context and independently reopened writing assertions.

For rejected profile Save and independent Import failure, read [Person profiles, reporting and editing](testing/person-profiles.md#profile-editing-and-cancellation).


For independent note-leave guard and recovery-copy checks, read [Person-note and Team-note writing](testing/note-writing.md#immutable-drafts-and-scoped-cancellation).


For saved note restoration and paused versus writable Edit, read [Person-note and Team-note writing](testing/note-writing.md#immutable-drafts-and-scoped-cancellation).

For demand-driven Manager choices, read [Person profiles, reporting and editing](testing/person-profiles.md#profile-editing-and-cancellation).

The explicit `--diagnose-rich-navigation` command is a separate, nonaccepting
actual-BAML investigation at the complete rich population: 42 action-to-layout
observations during 21 notes with six nominal 250-ms inputs apiece, raw managed
allocations and picker work, followed by full independent owner/writing/resource
verification. It never replaces native action-to-usable observations, subtracts
observer time or runs as another required scale lane.

For Preferred-name held-save retention and stale retries, read [Person-text and Preferred-name submission](testing/person-text.md#older-completion-and-newer-writing).

For Preferred-name incoming archival deferral, read [Person-text and Preferred-name submission](testing/person-text.md#incoming-owner-changes).

### Retired-engine coverage reconciliation (#100)

For retired protocol coverage and its current semantic replacements, read
[local storage and recovery](testing/workspace-storage-recovery.md#retired-engine-coverage-reconciliation-100).



For Person/Team ongoing choices, preference failures and empty invitations, read [Note reading, selection and Team context](testing/note-reading-context.md#ongoing-and-exact-selections).

For hosted long current/frozen reading and historical pending acceptance, read [Note reading, selection and Team context](testing/note-reading-context.md#current-and-frozen-reading-evidence).

For People navigation, two-axis bookmarks and settings limitations, read [Person search and browsing](testing/person-search-navigation.md#browsing-bookmarks-and-destinations).

For grouped search, publication guards and retained owner drafts, read [Person search and browsing](testing/person-search-navigation.md#grouped-person-search-and-retained-writing).

For Read-demand, functional and archival note readiness, read [Note reading, selection and Team context](testing/note-reading-context.md#read-and-edit-presentation).

The explicit `--diagnose-rich-navigation` diagnostic keeps all 42 selections,
20 Team plus one Person Add, full rich population, per-sample independent
reopens and real 60-second synchronization during sampling. Managed
dispatch/layout, observation milestones, allocations and independent reopen work
are separate raw observations, never additive or subtracted from native timing.
Only fixture-owned future timers stop after sampling; its own publication and
callbacks must finish before actual Closed and cleanup. This is not a required
lane or native acceptance, and changes no editor/navigation/durability target,
deadline, workload or existing scale selection.

For identity/context ranking and native keyboard/viewport acceptance, read [Person search and browsing](testing/person-search-navigation.md#identity-and-context-ranking).


For Team/history Read preparation and empty-viewport readiness, read [Note reading, selection and Team context](testing/note-reading-context.md#read-and-edit-presentation).

For secondary-note exact reopen and responsive matrix, read [Note reading, selection and Team context](testing/note-reading-context.md#ongoing-and-exact-selections).

Normal/responsive search journeys observe archived-target startup recovery,
unchanged Workspace bytes and deliberate archived access with reopened ratios.
No additional whole journeys, manifest changes or deadline increases are needed.
Startup helpers require the exact Workspace, completed activity, current
validated data, expected Person and absence of visible Workspace/view recovery
errors. No-selection expectations remain for fixtures without saved valid views.
Their runner regression rejects an earlier open-success observation until the
saved destination is actually present. The full UI-scale mutation journey now
waits for that validated startup restoration instead of issuing a redundant
manual open whose completion could clear its new query. Its 10,000-Person fixture,
180-second waits and final durable profile/Manager/favorite/photo checks remain.
Local helper/file/control checks do not prove hosted reopen or UI-scale execution.

For adaptive reading, cold virtualization and retained-draft geometry, read [Person reading and responsive layout](testing/person-reading-layout.md#responsive-reading-and-retained-drafts).

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

Default local verification is non-UI:

```powershell
.\eng\Run-CiLane.ps1 core
.\eng\Run-CiLane.ps1 filesystem
```

`DraftDiscard.Tests.ps1` now runs both named Person/Team public workflow/view-model
cases from the already-built Core test assembly (the core lane supplies its
isolated assembly path). It refuses missing/skipped/partial results. The actual
shared draft coordinator, not a compiled retired `NoteDraftState` with a fake
dispatcher timer, proves admitted-discard refusal, exact owner writing and
scoped idle discard preserving independent unsubmitted input. Filesystem cases
remain responsible for SQLite durability and actual effect barriers.

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

`--verify-xaml` is a separate non-UI build/load contract, not a desktop scenario.
It constructs the actual compiled MainWindow BAML and event connectors on STA
with isolated settings, requires the window to remain invisible with no native
main-window handle, then closes it. It never calls Show or runs the application
dispatcher; startup restore/update Loaded handlers and an external UI Automation
client are not run.
This catches XAML connection failures that compilation alone misses. It does not
prove rendered controls, navigation, dialog discovery or any smoke/scale result.

For compiled Copy focus and absent values, read [Person profiles, reporting and editing](testing/person-profiles.md#compiled-profile-and-copy-assertions).

For distinct compiled note identities and collapsed context, read [Note reading, selection and Team context](testing/note-reading-context.md#ongoing-and-exact-selections).

For compiled frozen capture disclosure and attribution, read [Note reading, selection and Team context](testing/note-reading-context.md#current-and-frozen-reading-evidence).


For compiled profile refresh and Copy payloads, read [Person profiles, reporting and editing](testing/person-profiles.md#compiled-profile-and-copy-assertions).

Hosted Workspace-path primary-click nonmutation observes the subsequent physical
right-click opening its Copy path menu before reading the sentinel, without invoking
Copy or reseeding the clipboard. That ordered input proves WPF processed the primary
click, rather than sampling early.

For anchored facts, archived identity, editor and photo geometry, read [Person profiles, reporting and editing](testing/person-profiles.md#compiled-profile-and-copy-assertions).

For compiled splitters, main reading and independent fact-row heights, read [Person reading and responsive layout](testing/person-reading-layout.md#compiled-layout-and-divider-assertions).

For report visibility and accepted tree selection, read [Person profiles, reporting and editing](testing/person-profiles.md#categories-and-reporting).

For Preferred-name controls, cancellation and missing owners, read [Person-text and Preferred-name submission](testing/person-text.md#compiled-controls-and-native-popup-evidence).

For retained creation failure after independent profile delivery, read [Person-note and Team-note writing](testing/note-writing.md#durable-typing-and-save-handoffs).

For public control seams and separate update-drain proof, read [Person-text and Preferred-name submission](testing/person-text.md#compiled-controls-and-native-popup-evidence).

For proportional minimums and routed divider keys, read [Person reading and responsive layout](testing/person-reading-layout.md#compiled-layout-and-divider-assertions).

For compiled Team designation, archive, delivery and preference failures, read [Note reading, selection and Team context](testing/note-reading-context.md#ongoing-and-exact-selections).

For bounded prose, note timeout diagnostics and body ink, read [Note reading, selection and Team context](testing/note-reading-context.md#current-and-frozen-reading-evidence).

The native picker helper targets the observed Folder Edit field (ID 1152), not
the file-dialog entry or a Search box. Select Folder/Cancel match both their
native automation ID and Button role before obtaining InvokePattern: shell
folder items can reuse numeric IDs 1/2 and are not dialog actions. Exactly one
action is invoked before the unchanged close observation; no second click or
action retry repairs a still-open picker. Cancellation compares actual before/after
orientation, displayed path, preference bytes and untouched Workspace contents;
HelpText need not be empty because WPF can supply useful tooltip fallback.
Installed-flow failure diagnostics only read the existing automation tree and
retain the original exception; they never call the maximize/open-window helper.


For independent ordinary/responsive fixture expectations, read [Person search and browsing](testing/person-search-navigation.md#selection-visibility-and-shared-fixtures).

The failed-note case also opens Settings and attempts Close once. Rejection must
reveal the retained work area instead of leaving Settings over the failed editor,
including after queued focus work settles. Owner, title, Markdown, actionable
failure text and saved file bytes must remain intact. The hosted Person-note
journey retains its original Close action and visible failure/content assertions.

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

The existing profile and installed desktop journeys compare the live window's
small/large `WM_GETICON` pixels with the matching produced native frames, saving
unscaled PNGs and a light/dark compositing plate with executable hash and candidate
metadata. This is native-resource evidence, not a screenshot of the taskbar or
Start menu. Alongside the existing window screenshot, those journeys also save
actual full application-monitor pixels with the same candidate/executable
identity. Review the monitor PNG for the visible taskbar and app button; neither
registrations nor the HICON plate proves those shell pixels. Capture does not
open Start, reveal an auto-hidden taskbar, change theme/focus, or replay setup.
Start-menu and setup-dialog appearance therefore remains unproven by this path.
The isolated installed lifecycle also resolves the real Desktop/Start
shortcuts and Installed Apps icon target after install, upgrade and reinstall.
These checks do not replace actual Windows small-size/surface review or the
whole-pass owner review. They must not be run on the shared local desktop.

Installer-runner process tests use a separate non-UI .NET fixture, never the
installed app or setup. Its parent exits with application files already removed
while a named-event-gated cleanup child still owns the fixture installation.
Reinstall must remain blocked until that child exits; a held child must consume
the original remaining deadline, fail, and be terminated with its owned job.
These process-boundary regressions do not replace hosted installation/Workspace
preservation verification.

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
not installer verification or release publication. After the lane prepares the
ephemeral embedded update trust and fixture environment, the runner's invocation
has this form (the lane uses its isolated artifact paths):

```powershell
dotnet run --project tests\OrgExplorer.Desktop.Smoke -c Release -- `
  src\OrgExplorer.Desktop\bin\Release\net10.0-windows\OrgExplorer.Desktop.exe
```

The nine ordinary required names live in `tests\DesktopScenarios.json`. The lane
verifies native current-mode and monitor bounds at 1024 x 768 / 32 bpp / 96 DPI
without changing its display. They cover:

- `profile-journey`: preview/apply/rejection, Manager/report identity, preferred/directory
  binding, readable Preferred name and one-click Add/Edit, exact popup defaults
  and focus, physical Save of the unchanged Directory default and physical Enter
  clearing an equivalent existing name, retained inline validation failure,
  Cancel/Escape/outside-click of untouched defaults and distinct writing
  dismissal without mutation, popup discovery/focus and uniquely named normal,
  failure and dismissal captures,
  quiet chrome and Settings keyboard entry/focus, anchored photo/identity bounds,
  all ordinary facts with stable labelled missing-value slots and no More details gate,
  readable ordinary values and idle state after profile Cancel,
  primary copying of every eligible value, representative Enter/Space and the
  retained mouse/keyboard Copy value menus, exact multiline/leading-zero payloads,
  actual hand cursors/full-value/owner hints and rendered transient confirmations.
  Clipboard-sentinel checks protect absent values, Heading/Manager navigation,
  editor Enter and Workspace-path primary clicks. A genuinely held OS clipboard
  rejects copying without success or loss of staged profile writing; explicit
  retry returns to quiet reading. The responsive journey repeats the eligible
  property set, long/truncated/wrapped values and duplicate Team-name activation
  throughout its unchanged size matrix. Both remain hosted-only evidence,
  representative partial change/clear, save/reopen, actual copied PNG
  and JPEG pixels, photo clear/placeholder and Profile timeline. Its marked
  300 x 300, 300 x 150, 150 x 300, 72 x 72 and 72 x 36 PNGs prove centered
  reduction/enlargement, preserved square-marker proportions, independently
  expected pre-mask fill sizes (144 x 144, 288 x 144, 144 x 288, 144 x 144,
  288 x 144 DIP), axis-band positions and intentional circular exclusion.
  UIA requires both the existing photo viewport and accessible Image to stay at
  144 x 144 DIP. Centered fill happens inside a square drawing using the decoded
  bitmap; the independent pre-mask expectations are checked through source-band
  positions and square-marker proportions in actual pixels, not oversized
  accessible bounds. Bounds and pixels use effective Windows scaling. Initials retain
  their accessible text and blue/white colors; UIA text geometry and rendered ink
  prove proportional enlargement, and pixel spans prove the matching 144-DIP
  circle. Choosing/clearing stays staged until explicit Save; Cancel and
  Person switching retain the correct saved photo/initials footprint.
  An original initials timeout also writes failure-only
  `photo-rejection-<scenario>-ui.png` / `.json` diagnostics. The PNG is the last
  already-captured rejected `ReadPhotoPixels` buffer, retained before its GDI
  disposal, never a later screenshot. JSON carries its original acquisition
  interval, crop/viewport/text/previous-photo coordinates, DPI, the first false
  guard or probe and observed RGB, computed glyph bounds and circle-edge offsets.
  Unreached stages stay unobserved. Only one rejected buffer (up to 2 MiB) and
  bounded probe records are retained; a later pre-pixel refusal is labelled
  separately from that buffer. Native window/PID binding observations have
  their own timestamp, distinct from pixel acquisition. Source/run/attempt,
  app/workflow/harness and native hashes are bound after the original failure.
  PNG/JSON are emitted only after the unchanged twenty-second failure; output
  faults remain attached to the primary exception and separately reported.
  No extra desktop acquisition, GetPixel, UIA or business query is made, no
  predicate is replayed, and no capture/serialization time is deducted. All
  geometry, colour tolerance, expected values, focus and cursor behavior remain.
  Synthetic managed-raster recorder/PNG/refusal tests run in Core; they do not
  claim actual desktop pixels or fix app rendering. These diagnostics do not
  replace normal visual evidence or any required acceptance check.
  Clear is invoked once and its visible staged-clear notice must appear before
  asserting the unchanged saved photo or cancelling; pixels already present
  before the click do not establish that the handler ran.
  Import is opened
  explicitly, replaces the people view, and returns to reading with Import
  controls absent. Choosing/previewing retains the saved population; changing the
  path invalidates Apply until reviewed again.
  Inline editing additionally requires initial Directory-name focus, Tab to Alias,
  actual field Enter followed by Tab without submission, unchanged saved office
  data before explicit Save, and focus returning to Edit profile after Cancel.
  Named captures retain the field-Enter draft and staged photo clear.
- `draft-navigation`: retained profile/Preferred-name and Person/Team writing across
  real tree/search/Manager browsing, plus explicit dirty-profile Save / Discard / Stay, failed
  leave-save retention and Import protection versus independent failed Person/Team drafts,
  protected Import/Workspace replacement, retained owner/draft and deliberate discard.
  Opening the dedicated Import view exercises
  all three profile decisions and a locked-file leave-save rejection, then checks
  reading values after returning; independent failed notes still block Import.
- `person-note-rendering`: Read-first existing notes with both editors hidden,
  one-click editing, empty-section actions, direct New-note editing and empty-body
  accessibility, actual Markdown text, loopback
  no-fetch assertion, failed local transaction, usable retained-draft browsing,
  Workspace replacement/close decision, retry, independent ownership and durable reopening.
- `team-context-history`: explicit Team ownership, all-ancestor attribution,
  ongoing/Other designation, grouped Person search and independent exact secondary access, Read-first owned notes,
  direct New-note editing and empty-body accessibility, visible preference/save
  failures and collapsed attributed warnings at the ordinary desktop size,
  exact owner review navigation/confirmation with retained writing and archive/restore,
  secondary History with confirmations/observations, actual current/frozen
  reading and honest historical dates through a representative
  ancestor move, historical-source Person search, ordinary History access and reopen.
  Capture details additionally exposes complete transition/note evidence
  separately from frozen Markdown, with keyboard expansion/collapse and
  nonmutation checks. Named actual-pixel states retain the collapsed capture
  entry, expanded evidence and intermediate frozen-note boundary, including a
  note needing review when captured. The responsive journey repeats these states
  at 700 x 500 and 1100 x 900 alongside its complete long current/frozen reading.
- `search-favorites`: representative profile/note queries, real current,
  archived and frozen destinations, reading, favorites, failed-save rejection
  and retained favorites after reopening.
- `archival-navigation`: Person More/confirmation cancellation, dirty-profile
  Save/Discard/Stay and independent-note protection, rejected Manager archival, failed local save and retry,
  current-tree filtering, retained archived profile, selection protection,
  reactivation and explicit no-Manager UI.
- `compatibility-settings`: global Settings before open, current-format reopening,
  older/future-version nonmutation and write blocking, validation retry and an
  explicitly chosen supported alternative. Startup coverage also checks first-run controls, automatic
  same-Workspace reopening without a selected Person, an honest empty recovery
  state with Retry/Choose actions and no replacement creation, successful Retry,
  failed manual attempts retaining the old default, and alternate-folder reopen.
- `update-settings`: actual default-enabled startup and manual checks against a
  controlled loopback feed with ephemeral embedded public trust, responsive local
  editing/saving during a pending check, signed availability, persisted opt-out
  and reopen, unknown signer/tampered signature/unsupported channel/rate-limit/
  offline rejection, and a visible signature failure on a later startup.
  Preference changes wait for the app's accepted-setting guidance as well as the
  checkbox state before closing; a visually toggled checkbox alone is not a save.
  Authorized results are consumed automatically: signed invalid package bytes
  fail Velopack verification. A valid controlled signed package then exercises
  real locked-note save rejection, retained writing, explicit retry, and final
  draft durability before the external installer boundary. The unpackaged
  boundary reports a deliberate installation failure; it is not an install pass.
  The lane prepares this normal-product test build and later verifies a fresh
  production-trust publish; see [the protocol contract](update-protocol.md#controlled-verification).
- `external-refresh`: actual folder delivery changes the open profile, note
  editors, inherited reading/review and same-path photo pixels; invalid graph
  delivery becomes visibly stale, retains last good data and recovers. Person
  and Team typing across delivery also verifies first-autosave/retry rejection,
  retained editor/delivered writing and deliberate discard through real WPF.

The fourth required lane uses
`.\eng\Run-CiLane.ps1 high-resolution -HighResolutionPartition reading` and
`-HighResolutionPartition context` on separate disposable GitHub-hosted Windows
VMs. Its complete selection remains `tests\HighResolutionScenarios.json`;
`HighResolutionPartitions.json` supplies the exact disjoint split through
`--high-resolution-reading` and `--high-resolution-context`. The original
`--high-resolution` selector still requests the whole suite:

- `responsive-workspace`: the search/Favorites journey plus the complete
  700 x 500, 1100 x 900, host-width 959/960/961 and maximized matrix, actual
  Segoe UI text, People 240 / Person allocation 420 minimums, 600-DIP prose cap,
  all-facts ordering and long/missing text, anchored main scrolling, overlay bounds/focus,
  mouse/keyboard People resizing, percentage resize/reopen and search/clear position.
  At each reading size and the minimum Person allocation, the same real photo
  and a Manager's initials retain their 144-DIP circle without a footprint
  change on selection. Identity actions remain visible, focusable and beside
  the photo; the photo stays anchored during the existing long-note scroll.
- `responsive-draft-navigation`: the complete draft-protection journey plus
  actual compact overlay rejection retaining the exact owner/editor and feedback.
  Its inline form also traverses every ordinary field and Manager by Tab at
  700 x 500, 1100 x 900, host widths 959/960/961, maximized and the minimum
  Person allocation. Each focused control must fit the actual scroll viewport
  without moving identity or Save/Cancel; stable email stays read-only and notes
  stay below the draft. Named top/Manager and failed-leave-save captures support
  final whole-pass comparison, not automatic visual acceptance.
- `responsive-team-history`: the complete owned/inherited Team, independent
  draft and frozen-history journey plus the 700/1100 rendered warning/history
  matrices. All ordinary domain/ownership/history assertions remain in the
  ordinary journey as well.

The split is by explicit scenario name, never a conditional screen-size skip.
The two smoke manifests and explicit UI-scale manifest are nonempty, unique and
disjoint; each worker requires its own complete named `scenarios.json`. Ordinary
results cannot stand in for the high-resolution suite.

Before any app launch, the high-resolution fixture requires GitHub Actions,
`RUNNER_ENVIRONMENT=github-hosted`, Windows and 96 DPI. It enumerates the actual
primary display's monitor-compatible modes, requires an advertised 1920 x 1080
32-bpp mode, tests it with `CDS_TEST`, and applies it dynamically exactly once
with no registry/persistence, unsafe-mode, primary-device or DPI changes.
It then observes the exact selected mode, actual 1920 x 1080 monitor bounds,
96 DPI, work area >=1800 x 1000 and track capacity >=1100 x 900 within the normal
bounded observation. Driver rejection, restart requests, missing modes, wrong
read-back or inadequate capacity fail the lane; there is no synthetic fallback.
`display.json` records actual native prerequisites, not rendered acceptance.
The job VM is disposable, so no restoration step is needed. This is not
permission to change any local, self-hosted or shared display. Explicit UI-scale
execution reuses the same preparation on hosted VMs; an already-authorized
isolated local desktop receives read-only capacity verification, never a change.




Both required desktop suites use ordinary fixtures. They do not construct the
complete scale population; all genuine UI-scale work has explicit execution below.
Every scenario opts out of startup update checks before launching the app,
except the controlled `update-settings` and `installer-update` scenarios.
This includes ordinary smoke, manual normal-build execution and installed
create/reopen flows; they must never fetch or apply a production release.
Every scenario also supplies its own `ORG_EXPLORER_SETTINGS_DIRECTORY`, initially
without a remembered Workspace. Launch fails closed without an explicit absolute
settings directory, so no fixture falls back to the workstation owner's actual
preferences or Workspace. Repeated launches within one scenario deliberately
share only that scenario's settings to exercise durable reopening.

For investigation, append one name to the harness command, not the app
(`profile-journey`, for example). Older `migration`, `--search-favorites`,
`--person-notes`, `--team-notes` selectors remain aliases.
The obsolete combined `scale` / `--scale` / `--ui-scale` selectors are rejected;
use the isolated `--ui-scale-prefix` and `--ui-scale-tail` selectors with their
candidate-bound logical handoff. A single explicit scale scenario is investigative,
not complete scale acceptance. `--high-resolution` selects only the
three required high-resolution scenarios and is never part of default ordinary smoke.
A targeted pass is not the complete required lane. `DESKTOP_RESULTS_DIRECTORY`
may select a separate result directory for manual runs.
The full selection and `update-settings` require the prepared update fixture and
matching test-trust build; a production-trust build cannot substitute for that
controlled evidence. Other focused scenarios may use a normal build.

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

Successful navigation waits for meaningful local completion and asserts destination
email/context. Explicit rejection paths invoke once, assert retained owner,
selection/draft and actionable status, and never retry their way into success.
Pending and in-progress messages must explain waiting; failures require
retry/discard. Poll bounded eventual states; keep existing 20-second smoke and
180-second scale limits. Photo/initials visibility and geometry poll within that
existing smoke bound; persistent mismatches report the last observed geometry.
Missing desktop pixels or failed capture still fail explicitly rather than
being retried as layout. Remote content is never fetched just to render notes or
profiles; loopback listeners and actual pixels/text are evidence, not filenames
or image dimensions alone.

UI Automation invocation can return before WPF processes the queued button
handler. Click and value helpers acquire an enabled control and its required
pattern within the existing bounded observation wait, then invoke or set once
without a second control lookup. Only preparation is retried, never the action.
Raw Click/Set and idle/startup observations never open or close Settings.
Physical copy click/hover preparation likewise captures a visible usable point
within that wait, then sends input once. Eligible targets must be enabled;
deliberate missing-value checks retain their disabled automation peers.
Copy/owner hovers send actual absolute mouse movement on the verified primary
display, not position-only `SetCursorPos`, which left the hosted cursor suppressed
despite hitting the correct value. The native visible-hand/non-hand assertion remains.
Hosted cursor observation samples twice to retain its repeated-read regression.
Every read reinitializes `CURSORINFO.cbSize`, which the API can clear on return;
reusing that output made later reads fail with Win32 87. Invisible local contracts
never query the interactive cursor, which can be inaccessible on a locked desktop.
Tooltip discovery checks native top-level popup children before the same descendant
query, avoiding full reading-document traversal when the popup is top-level. Exact
tooltip content, cursor, clipboard and rendered-feedback predicates are unchanged.
Copy readiness failures retain the target's enabled/offscreen/focus state and bounds.
Named physical-click targets are located inside that same readiness wait, so queued
input cannot race an unguarded control lookup before preparation begins.
The ordinary copy fixture scrolls its reading surface to the top once after profile
Save, before acquiring any fact target, as the responsive copy fixture already does.
UIA's offscreen flag alone can be false even when retained edit scrolling clips the
fact above that viewport; acquiring an actual clickable point remains required.
Rendered contrast uses one actual desktop bitmap capture, then the original six-pixel
sample grid and luminance threshold against its pixels, rather than repeating desktop
readback for every sample. This is neither offscreen rendering nor a reduced predicate.
Feedback reads request name, offscreen state and bounds together in a fresh UIA cache
snapshot per observation; no metadata is reused across gestures, and the same actual
desktop-pixel check still proves rendering.
Exact clipboard-copy observation uses the existing bounded STA dispatcher pump,
servicing the harness's own OLE clipboard-owner messages while another process copies.
The real clipboard equality and 20-second bound remain; no copied-text effect is faked.
Only a read-side `COMException` with `0x800401D0 (CLIPBRD_E_CANT_OPEN)` remains
pending within that same wait. It never substitutes empty text, repeats a copy
gesture/write, or restarts the deadline. Timeout records the last read's availability
or text mismatch and preserves both the wait failure and latest cannot-open exception;
unexpected read errors propagate. Runner-category `ClipboardObservationTests` drive
the same tooling observer with a controlled external reader and bounded wait, without
accessing the local clipboard or opening a window. Actual native proof stays hosted.
Bounded `DIAGNOSTIC: harness-runtime` JSON lines investigate ordinary-lane
runtime without changing the workload or acceptance clocks. Every native scenario
and compiled profile-refresh/update invocation emits a start/end record; compiled
copy and SQLite, native copy and SQLite note/profile work have named child phases.
Copy starts before its first existing PASS, with fixed payload/Team/missing-value
checkpoints; SQLite checkpoints separate held navigation and repeated dispatch.
An unmatched start/checkpoint identifies interrupted work, not success.
Each phase carries the exact `GITHUB_SHA`, run and attempt (null when unbound
locally), its parent/context and outcome. Fixed-size, thread-safe in-memory
counters retain started/completed counts, total and maximum elapsed observation
cost for existing discovery, shared native requests, waits/predicates, filtered
note snapshots, capture/pixels and compiled copy/SQLite layout. Capture measures
screenshot acquisition, PNG encoding and metadata writing;
standalone path-safe state validation records no capture operations. The existing
invisible capture-name regression verifies that distinction without taking screenshots.
These diagnostics add no per-poll writes, UIA reads/cache warming, actions or retries. Existing
direct native latency requests and samples remain unchanged. Nested/inclusive
measurements overlap and concurrent snapshots are non-atomic; these are neither
database commit times nor replacement product latency, and nothing is subtracted
from the raw acceptance samples or lane budget. Expected diagnostic output failures
cannot replace an action's result/exception. No note/profile values or exception text
are recorded by these diagnostics. Harness regressions cover bound early
failure/interruption records, unchanged outcomes on output failure and bounded
parallel aggregation. These probes are investigation, not a root-cause fix.
Expected serialization/output failures retain process-wide bounded counts and
last exception type/HResult, never exception messages. They emit the fixed stderr
marker `DIAGNOSTIC: harness-runtime-output-failure investigationIncomplete=true`,
without retrying the failed writer recursively. If stderr also fails, its
count/type/HResult remain observable through `HarnessRuntime.OutputFailures`.
Later records carry `diagnosticsComplete=false` and `outputFailures`; action
outcome is separate from evidence completeness. Missing boundaries or any output
failure make investigation evidence incomplete, even when actions passed.
Unexpected diagnostic programming errors are not silently caught.
`AwaitLocalWorkIdle` preserves its original exact `Ready` activity and ordinal
non-`Pending` status guards before requesting one fresh, four-ID filtered native
snapshot per eligible poll. The snapshot contains only Email, WorkspaceStart and
the two note surfaces, never elements/properties retained across polls or owners.
Absent Email still requires the actual visible starting view; empty Email retains
its original optional-note behavior. A selected Person still requires Person then
Team feedback without `Pending`, including offscreen note reading. Required-control
absence and provider failures are not readiness, and no local-save phase/status is
normalized. The existing twenty-second observer/catches remain unchanged.
Runner regressions pin short-circuit order, startup/required-control failures,
per-poll query count, changed owners/feedback and unavailable providers. These are
observation contracts, not rendered usability, local durability or latency claims.
Journeys prepare Settings explicitly before folder, Import or update actions,
and deliberately return to People before subsequent writing. Rejected-close
observations require Settings to be closed by the app before retrying the
retained note; input helpers cannot repair a hidden editor and mask that failure.
The headless readiness regression scripts missing/disabled controls and transient
pattern discovery at that shared helper; hosted WPF remains the actual UI proof.
The native-picker helper invokes the visible action once, fills the native folder
entry and accepts or cancels once, then observes dialog closure. It never creates
folders, repairs Settings, retries Open or substitutes an app-side path setter.
Empty folders and malformed selectable Workspaces are explicit scenario fixtures;
the rejected-open checks retain their complete byte/preference assertions.

For reserved scroll gutter and unrepaired restoration, read [Person reading and responsive layout](testing/person-reading-layout.md#compiled-layout-and-divider-assertions).

For native leave-save and independent-note Discard, read [Person profiles, reporting and editing](testing/person-profiles.md#profile-editing-and-cancellation).

For external-delivery retry observation and retained drafts, read [Person-note and Team-note writing](testing/note-writing.md#durable-typing-and-save-handoffs).

The scale mutation scenario retains actual in-progress-work rejection while
Settings renders. A rejection check invokes once, then observes the actual rejection
message within the existing deadline while requiring the original Person to
remain selected. A generic pending message or a completed save is not rejection
evidence. The runner's non-UI ordering regression covers pending → rejected
observations and refuses missing feedback, wrong feedback or changed destination;
hosted WPF still supplies the real UI evidence.

For busy and idle scoped discard, read [Person-note and Team-note writing](testing/note-writing.md#immutable-drafts-and-scoped-cancellation).

For current and Archived selection assertions, read [Person search and browsing](testing/person-search-navigation.md#selection-visibility-and-shared-fixtures).

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

For postcommit scoped acknowledgment and causal negatives, read [Person-note and Team-note writing](testing/note-writing.md#commit-acknowledgment-and-cancellation).

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

For pending observation cancellation and owned-child limits, read [Person-note and Team-note writing](testing/note-writing.md#commit-acknowledgment-and-cancellation).

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
See [performance validation](performance-validation.md) for fixture detail and
historical measurements, not current command authority.

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

## Nightly and manual execution

`.github\workflows\scale-verification.yml` owns the independent non-UI job, the
two isolated rich workers and their strict union collector. Separate invocations
can overlap on the same ref; no scale concurrency group serializes or cancels
them. Each execution job uses its own hosted Windows runner; the tail consumes
its successful prefix's current-attempt handoff. UI scenarios are serial within
their own worker, not parallel competing windows. Each suite records the same
exact candidate SHA in independently named results/artifacts; artifacts upload
even on failure, and missing artifacts fail. These are not additional required
PR checks.

The workflow declares **10:23 UTC nightly**, direct manual dispatch and
`workflow_call` with a required exact candidate. The nightly schedule and new
workflow dispatch registration become active only after the file reaches the
default branch; wiring them on integration does not activate a schedule.

Before that registration, use the existing `ci.yml` **manual-only opt-in bridge**
from a pushed candidate branch, with the repository-authorized account:

```powershell
gh workflow run ci.yml --repo geevensingh/org-explorer --ref YOUR_PUSHED_BRANCH -f run_scale_verification=true
gh run list --repo geevensingh/org-explorer --workflow ci.yml --event workflow_dispatch --limit 5
gh run view YOUR_RUN_ID --repo geevensingh/org-explorer --json headSha,status,conclusion,jobs
gh run watch YOUR_RUN_ID --repo geevensingh/org-explorer --exit-status
```

Verify `headSha` is the intended candidate, both scale jobs succeeded, every named
result passed and both artifacts identify that SHA. The bridge calls the same
local reusable workflow at `github.sha`; it duplicates no suite commands. The
Boolean defaults to false and its guard requires manual dispatch. The ordinary
four lanes still run on a bridge dispatch, but their independent `CI` aggregate
does not attest to scale success: inspect non-UI performance, both rich workers,
the strict union collector and the whole run.
Omit the flag for ordinary focused CI only.

Once GitHub registers the scale workflow, direct manual execution is:

```powershell
gh workflow run scale-verification.yml --repo geevensingh/org-explorer --ref YOUR_PUSHED_BRANCH -f revision=FULL_40_CHARACTER_CANDIDATE_SHA
```

Blank `revision` uses that workflow dispatch's commit; supplied values must be a
full immutable SHA, not a mutable ref. All execution jobs check that checkout equals the
requested SHA before invoking the runner with `-ExpectedRevision`.

## Active pre-publication integration

`release.yml` calls the reusable scale workflow with the exact source SHA after
successful trusted main CI and product-impact planning. Every product publication
uses the complete hosted workflow described above: non-UI performance, both
isolated rich workers and their strict union. Its execution entry points are:

```powershell
.\eng\Run-Performance.ps1 -ExpectedRevision FULL_40_CHARACTER_CANDIDATE_SHA
.\eng\Run-UiScale.ps1 -Worker prefix -ExpectedRevision FULL_40_CHARACTER_CANDIDATE_SHA -RunId RUN_ID -RunAttempt RUN_ATTEMPT
.\eng\Run-UiScale.ps1 -Worker tail -PrefixResultsDirectory PREFIX_ARTIFACTS -ExpectedRevision FULL_40_CHARACTER_CANDIDATE_SHA -RunId RUN_ID -RunAttempt RUN_ATTEMPT
.\eng\Assert-UiScaleUnion.ps1 -PrefixDirectory PREFIX_ARTIFACTS -TailDirectory TAIL_ARTIFACTS -ExpectedRevision FULL_40_CHARACTER_CANDIDATE_SHA -RunId RUN_ID -RunAttempt RUN_ATTEMPT
```

The two rich invocations belong on separate isolated authorized desktops, not
this shared machine. Use the hosted workflow for artifact transfer and complete
candidate verification, not a single rich invocation.
Expected-revision execution refuses a different SHA, tracked changes or
non-ignored untracked files, and rechecks after assertions. Non-UI exploratory
execution without that argument reports modifications honestly; it is not
candidate proof. Rich workers always require explicit candidate/run/attempt binding.
The publisher requires both successful, complete, same-revision suite results
from its own run/attempt, and blocks on failure, absence, required skip,
cancellation or timeout.
Earlier nightly/manual artifacts are diagnostic evidence, **never** reusable
deployment approvals. Wiring is not a successful hosted execution or owner-key
provisioning. Before merge, the registered `ci.yml` default-off
`run_release_verification=true` bridge invokes the same fresh scale workflow,
then genuine packaging/signing with ephemeral keys and production-trust
isolation, with no production publication. See [release operations](releasing.md)
for the exact safe hosted command, required results and remaining prerequisites.

Core CI also runs the non-UI `Release*.Tests.ps1` contracts: real temporary Git
history, controlled ephemeral cryptography, exact suite evidence, archive
isolation and fake external GitHub publication effects. They never call a real
publication endpoint, open WPF or run an installer.
Publication descriptions are observed through the real `Publish-Release` draft
body at that fake GitHub boundary. Real Git fixtures cover three accumulated
mainline changes (including the earlier durable Workspace-save change), literal
Unicode/Markdown subjects, a candidate older than checkout HEAD, and merged work
without WIP duplicates. They also cover an explicit first publication, unchanged
technical/renewal descriptions, failed/metadata publications retaining the
original app baseline, and pre-tag refusal of missing, shallow, non-ancestor,
off-first-parent, empty or unusable-subject history.
The core runner-category signer interoperability test also invokes the real
PowerShell signer with an ephemeral key and feeds its exact envelope to the real
`UpdateChecker`, checking pinned bytes and mutation rejection without network IO.
Metadata renewal contracts additionally cover due/no-product/no-op evaluation,
original source/trust/provenance, copied bootstrap bytes, unchanged app versions
and stale/failed/concurrent fake GitHub publication. The hosted release bridge
renews its genuine fixture package with ephemeral signing and uploads named
renewal evidence, never a production release.

The trust-verifier lifetime regression compiles a minimal public-resource
assembly, runs both real trust checks, and immediately deletes the fixture tree
in the same PowerShell process. It reproduces the hosted `Assembly.LoadFile`
mapping/cleanup denial without WPF or installation. Byte-based inspection retains
all trust/controlled-effects checks without holding the package DLL mapped.
Cleanup must finish before the hosted release result can report success; there
is no retry, swallowed deletion error or connection to the parked note issue.

Both scale fixtures save the app's ordinary startup-update opt-out before launch,
so a production-trusted historical candidate cannot download a production update
while being tested. The runner's file-backed regression observes both preferences
before scenario actions and verifies update/installed-update selections remain
untouched. No product default, public key, transport effect or scenario assertion
is replaced. Automatic renewal refuses historical candidates predating this
isolation instead of modifying their exact-source test checkout.

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
