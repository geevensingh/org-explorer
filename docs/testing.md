# Testing

This is the authoritative test-placement and execution strategy for developers
and agents ([approved spec #21](https://github.com/geevensingh/org-explorer/issues/21)).
Choose the lowest-cost agreed surface that can prove the behavior, not a project
name. A desktop click alone does not establish a successful navigation or save.


For execution work, open [CI and artifacts](testing/ci-and-artifacts.md),
[desktop harness operation](testing/desktop-harness.md),
[required native scenarios](testing/required-desktop-scenarios.md),
[explicit scale verification](testing/scale-verification.md), or
[release/nightly/manual verification](testing/release-and-manual.md).
## Person and writing contracts

- **Profiles, identity and reporting:** read [Person profiles](testing/person-profiles.md) for whole-profile editing, Team names, photos and Copy assertions.
- **Preferred-name Save/Enter:** read [Person-text submission](testing/person-text.md) for defaults, original bases, failed retries and cancellation.
- **Person-note or Team-note edits:** read [note writing](testing/note-writing.md) for autosave, admitted/newer generations and independent failures.
- **Ongoing/Other or current/frozen notes:** read [note reading and context](testing/note-reading-context.md) for exact selection, modes and attribution.
- **Person search or browsing:** read [search and navigation](testing/person-search-navigation.md) for ranking, accepted destinations and retained writing.
- **Responsive reading or editor geometry:** read [Person reading layout](testing/person-reading-layout.md) for restoration, anchored content and evidence limits.

For already-observed pixel capture and its evidence limits, read
[visual evidence](testing/desktop-harness.md#visual-evidence).

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

For fresh batched native observations and responsive discovery, read
[native action and observation](testing/desktop-harness.md#native-action-and-observation).

For file-backed profile sessions and stale saves, read [Person profiles, reporting and editing](testing/person-profiles.md#profile-editing-and-cancellation).

For owned and inherited Team names, read [Person profiles, reporting and editing](testing/person-profiles.md#manager-owned-team-names).

For inherited retained-owner mode preservation, read [Note reading, selection and Team context](testing/note-reading-context.md#read-and-edit-presentation).

Control only genuine external effects (clock, browser/release/installer boundary);
do not mock away the business behavior being asserted.






For the rich held-export deadline consumer and its unchanged whole-suite bound, read
[held fixture deadlines](testing/scale-verification.md#held-fixture-deadlines).
For owned build-tree cleanup refusal and diagnostic evidence, read
[cleanup failures](testing/ci-and-artifacts.md#cleanup-failures).

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

For the separate nonaccepting full-rich compiled investigation, read
[rich navigation diagnostics](testing/scale-verification.md#rich-navigation-diagnostics).

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

For the rich diagnostic workload, clocks and owned cleanup, read
[rich navigation diagnostics](testing/scale-verification.md#rich-navigation-diagnostics).

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

Required-lane overview: workflow/view-model, filesystem, ordinary desktop and
high-resolution desktop.
For every required lane, partition, deadline and current-attempt artifact gate, read
[CI and artifacts](testing/ci-and-artifacts.md#required-prmain-execution).

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

For focused non-UI contract commands and runner isolation, read
[local contract checks](testing/ci-and-artifacts.md#local-contract-checks).

For the `--verify-xaml` invisible build/load contract and its evidence limits, read
[invisible compiled checks](testing/desktop-harness.md#invisible-compiled-checks).
Its domain assertions follow.

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

For the native picker procedure and failure-only diagnostics, read
[native picker operation](testing/desktop-harness.md#native-picker-operation).


For independent ordinary/responsive fixture expectations, read [Person search and browsing](testing/person-search-navigation.md#selection-visibility-and-shared-fixtures).

The failed-note case also opens Settings and attempts Close once. Rejection must
reveal the retained work area instead of leaving Settings over the failed editor,
including after queued focus work settles. Owner, title, Markdown, actionable
failure text and saved file bytes must remain intact. The hosted Person-note
journey retains its original Close action and visible failure/content assertions.

For locked dependencies and product/icon resource checks, read
[product-output contracts](testing/ci-and-artifacts.md#product-output-and-native-resource-contracts).

For native icon and application-monitor captures, read
[visual evidence](testing/desktop-harness.md#visual-evidence).

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

For native setup and harness invocation, read
[desktop harness operation](testing/desktop-harness.md#running-required-desktop-suites).

For every ordinary required name and its complete assertion selection, read
[ordinary required scenarios](testing/required-desktop-scenarios.md#ordinary-required-scenarios).

For both high-resolution selections and their full size/assertion matrices, read
[high-resolution required scenarios](testing/required-desktop-scenarios.md#high-resolution-required-scenarios).

For hosted display preparation and read-only local capacity verification, read
[display prerequisites](testing/desktop-harness.md#display-prerequisites).




For isolated settings, startup-update opt-outs and focused harness selectors, read
[fixture isolation and investigation](testing/desktop-harness.md#fixture-isolation-and-investigation).

For named result states, complete-result validation and failed-fixture retention, read
[desktop scenario results](testing/ci-and-artifacts.md#desktop-scenario-results).

For one-action navigation/rejection and bounded native observation, read
[native action and observation](testing/desktop-harness.md#native-action-and-observation).

For native input preparation, cursor/tooltip discovery and rendered observations, read
[native action and observation](testing/desktop-harness.md#native-action-and-observation).
For the single-action native picker helper, read
[native picker operation](testing/desktop-harness.md#native-picker-operation).

For reserved scroll gutter and unrepaired restoration, read [Person reading and responsive layout](testing/person-reading-layout.md#compiled-layout-and-divider-assertions).

For native leave-save and independent-note Discard, read [Person profiles, reporting and editing](testing/person-profiles.md#profile-editing-and-cancellation).

For external-delivery retry observation and retained drafts, read [Person-note and Team-note writing](testing/note-writing.md#durable-typing-and-save-handoffs).

For scale pending-work rejection and its unchanged native observation, read
[pending-work rejection](testing/scale-verification.md#pending-work-rejection).

For busy and idle scoped discard, read [Person-note and Team-note writing](testing/note-writing.md#immutable-drafts-and-scoped-cancellation).

For current and Archived selection assertions, read [Person search and browsing](testing/person-search-navigation.md#selection-visibility-and-shared-fixtures).

## Separate scale commands and results

For complete scale selections, logical handoff, raw samples and acceptance policy, read
[explicit scale verification](testing/scale-verification.md#separate-scale-commands-and-results).

For postcommit scoped acknowledgment and causal negatives, read [Person-note and Team-note writing](testing/note-writing.md#commit-acknowledgment-and-cancellation).

For rich acknowledgment/reopen boundaries and exact final-writing verification, read
[independent reopen and latency boundaries](testing/scale-verification.md#independent-reopen-and-latency-boundaries).

For pending observation cancellation and owned-child limits, read [Person-note and Team-note writing](testing/note-writing.md#commit-acknowledgment-and-cancellation).

For authorized rich-worker investigation and whole-execution deadlines, read
[execution deadlines](testing/scale-verification.md#execution-deadlines).

For full-rich profile samples and independent fixture/save clocks, read
[profile samples and controlled synchronization](testing/scale-verification.md#profile-samples-and-controlled-synchronization).

For fresh reports, heartbeats, owned cleanup and raw phase diagnostics, read
[reports, progress and diagnostics](testing/scale-verification.md#reports-progress-and-diagnostics).

For extended diagnostic observation and virtualized-tree investigation, read
[diagnostic-only and tree investigations](testing/scale-verification.md#diagnostic-only-and-tree-investigations).

## Nightly and manual execution

For hosted nightly/manual dispatch and workflow registration, read
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
