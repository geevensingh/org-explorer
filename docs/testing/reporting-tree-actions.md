# Reporting-tree subtree actions

[Testing index](../testing.md)

Read this for subtree expansion/collapse, cancellation and progress, menu targeting, retained browsing and exact native tree-action evidence.

Related guidance: [Person search and navigation](person-search-navigation.md), [reading layout](person-reading-layout.md), [independent writing](note-writing.md), and [full-rich scale contracts](scale-verification.md).

Reporting-tree Expand subtree, Collapse subtree and Collapse other branches extend the public
`WorkspaceViewModel.GetPeopleTreeActions` / `ExecutePeopleTreeAction` and
`PeopleNavigation` seams with real branching and 1,024-level imported graphs,
hidden remembered expansion, protected subtree/ancestor paths, leaf applicability,
leaf/unavailable no-ops and independent failed Person/Team-note and profile writing.
Expansion also checks all current levels,
archived exclusions, a 1,024-manager/2,048-report graph, observable bounded
progress, cancellation retaining completed branches, resumption and interruption
when the current reporting view changes. It uses the existing action boundary
and public `PeopleTreeExpansion`/continue/cancel outcomes, not private traversal.
The existing invisible tree-presentation
contract exercises the actual row menu's routed command/placement-target
bindings, right-button selection suppression, current/recycled expansion reset
and search restoration. Its actual Expand subtree binding and cancellation
button additionally exercise complete expansion, recycled rows, compact/search
retention and ordinary disclosure interrupting unfinished recursive expansion.
The same compiled contract enters an actual nonempty query during genuinely
unfinished timer-driven expansion, requires progress and cancellation to remain
presented beside search results in wide and compact People, and cancels through
the real button while retaining exact partial expansion and independent profile
writing. Search-clear preserves that partial state and resumability.
Accepted Enter activation of a different Person is also exercised after progress
while search hides the tree, both running and already cancelled. It must retain
all completed branches, cancel outstanding work and leave no later batch to move
back from the accepted destination. Query entry alone remains non-navigating.
An observed real child collection in a previously realized, collapsed unrelated
branch proves that a timer batch does not enumerate its 256 reports. Expansion
returns only the current batch's added identities and updates their tracked
containers; existing Loaded/recycled-row restoration remains responsible for
later realization. The compiled check also requires the affected row to expand
and cancellation to remain available. This is a work-count regression, not a
native timing measurement or a substitute for full-population UI scale.
The compiled search-expansion fixture lets the hierarchy fallback select its
expansion root, settles deferred Person reveal before preparing browsing, and
observes the current row container after search-clear rather than a detached
pre-search container. Logical partial-state, resumability and writing assertions
remain unchanged.
Workspace replacement also clears the operation, pending traversal and feedback,
then clears the timer, panel, text and accessible People status. Refused opening
preserves the existing operation and writing. Public proxy regressions let an
actual worker open another Workspace or apply an Import between graph validation
and batch publication; old identities and completion must not reach the new
view. Observed workflow work advances a generation under the existing destination
gate, without holding that gate across I/O. A batch traverses an immutable pending
stack locally and publishes only against the unchanged generation, operation and
reporting roots. A refused concurrent open leaves the uncommitted traversal
available for the next batch. The compiled contract opens a second real Workspace
after cancellation and checks that no previous counts or status remain.
It also retains exact independent profile/note drafts.
The focused non-UI selector below runs this same contract; it remains included
in the existing full `--verify-profile-refresh` companion check:

```powershell
dotnet run --project tests\OrgExplorer.Desktop.Smoke -c Release --no-build -- `
  src\OrgExplorer.Desktop\bin\Release\net10.0-windows\OrgExplorer.Desktop.exe --verify-tree-presentation
```

The existing responsive reading and inline-profile size matrices additionally
exercise actual right-click, Apps and Shift+F10 targeting, omitted/empty menus,
target visibility, hidden-focus recovery versus preserved unrelated focus,
search-clear retention and compact-overlay preservation. The responsive draft
journey also keeps independent failed Person/Team-note writing during a tree
action with a dirty profile. These are hosted-only native assertions; invisible
compiled checks do not prove popup or keyboard-focus acceptance. Expansion
reuses the reset-manager menu boundaries for real mouse, Apps and
Shift+F10 actions without duplicating the full preparation/retention journeys.

Outside-collapse checks share those same native journeys and their fixture
preparation. A two-level outside Alex branch makes the existing leaf right-click
actionable; additional mouse and Apps checks retain the hidden open profile,
recover hidden-report focus to an initially collapsed target, preserve unrelated
focus and reset nested outside expansion through search-clear/reopening.
The original Collapse subtree inputs, responsive matrices and deadlines remain.
Retention captures expansion/focus alongside the original writing and exact
target bounds in one fresh cached-UIA batch per boundary. The live People
and reporting-row providers are retained only within that menu subjourney.
Each row lookup freshly validates its automation identity and TreeItem type;
an unavailable or recycled identity is rediscovered before input. No cached
identity/state frame is reused across an action, and all original retention, focus and
geometry observations remain fresh. The runner rejects wrong identities without
input replay; the compiled row contract observes a real disclosure transition
without repeating row discovery. This addresses repeated discovery measured in
reading attempt 4 of run 45768355665: all nine menus passed, but consumed about
363 seconds with 1,112 discoveries (about 263 seconds), before the unchanged
900-second lane watchdog fired. Provider reuse is a work-count fix; native
elapsed savings and whole-lane completion still require hosted evidence.
These assertions
still require exact-candidate hosted acceptance; local compiled success does
not establish native popup/focus behavior or whole-lane budget headroom.

Run `45748466369` reached the first dirty-profile leaf right-click with its header
below the 92-pixel tree viewport, after outside-fixture expansion and focus setup
but before any menu command. That initial preparation now conditionally requests
the leaf's ScrollItem provider once, then observes both leaf and ancestor headers
under the original viewport predicate before any mouse input. Refused realization
fails without replaying disclosures, scrolling or clicking. Command/no-op/search
retention still never realizes a lost target. Runner cases replay the captured
509-to-565 header displacement and the narrower/taller viewports; the invisible
compiled check uses the actual data-item ScrollItem and disclosure providers with
retained profile/note writing. Native focus/menu acceptance still requires hosted
evidence; no matrix, predicate or deadline changes.

The reporting-header request replays actual horizontally clipped UI Automation
bounds from run `45688751967` through the shared runner seam. It chooses a
physical pixel inside the visible header content, not the disclosure arrow or
the descendant-inclusive TreeItem box; an offscreen header still fails without
scroll/click replay. Run `45693688714` captured both new menu journeys changing
from Avery/open People to a Manager/closed People at the ordinary external
expansion request boundary. That navigation-capable provider is not a menu
presentation command. Menu fixture preparation now uses the existing disclosure
arrow's identity-bound Toggle provider once only when the fresh branch is
collapsed, including initial setup and final fixture restoration.
Already expanded state is observed without toggling;
unreached state still fails without replaying input. The same full matrices,
menu/writing/focus/visibility assertions and 20-second observation deadline remain.
Runner cases replay the captured external effects through the actual preparation
input-selection seam; the compiled contract also uses the actual non-focusable
disclosure Toggle binding. Ordinary provider behavior is unchanged.
Run `45717484494` later captured a coordinate disclosure request returning with
the same branch still collapsed after focus/geometry changed. The fixture now
resolves the target row's own enabled, visible `Expander` Toggle provider instead
of a physical pointer coordinate; it never falls back to ordinary expansion or
replays failed input. The connected runner case replays that captured refusal,
and the compiled contract resolves the real row-owned disclosure peer. These
checks do not establish the ordering or cause of native focus/scroll events.
The exact command-target viewport predicate remains required at every actual
menu-action, no-op and search-clear boundary. Final ordinary fixture restoration
still requires unchanged profile/writing and an open People panel, but its
focus-induced scrolling is not attributed to the preceding Collapse command.
Run `45698927097` captured this distinction after all command checks passed;
connected runner regressions keep invisible command targets and lost writing
as failures while permitting that later ordinary restoration.
Run `45707651375` additionally captured an initially shown archived list consuming
the entire current-tree viewport before any menu input. The menu subjourney
captures that initial state, conditionally hides the list once, awaits a real
current-tree viewport, and conditionally restores the list once after successful
command checks. These two reconciled fixture inputs preserve the same archived
profile/exact draft and the original archived reading/editing matrices; they are
not input retries or product-layout changes. An unexpected visibility change or
unreached preparation remains a failure, without repair. Runner and actual
compiled CheckBox binding cases cover this setup/restoration; all actual
mouse/keyboard/menu/target checks remain unchanged and require hosted evidence.
`TREE-MENU-EXPANSION` retains bounded fresh row/profile/panel/focus observations,
the request input and provider state, and a failure capture if necessary.
Diagnostic refusal is separate and does not replace the original failure.
These non-atomic observations and invisible checks do not establish native
popup/focus acceptance or a defect inside the ordinary Windows provider.
The same run's reading partition reached its enforced 900-second whole-lane
ceiling after all nine menu states completed and later browsing restoration
passed. Each menu subjourney now records the existing nested harness-runtime
costs, followed by a parent checkpoint so completed-state costs survive a later
watchdog interruption. These measurements are diagnostic, not product latency
or budget deductions; all matrices, assertions and deadlines remain unchanged.
Run `45730328088` measured 376.68 seconds across the nine reading menu calls,
including approximately 252.28 seconds in 1,635 UIA discoveries. Menu retention
now captures its original writing fields, Person identity, panel visibility and
target/viewport geometry in one fresh cached-UIA batch per boundary, rather than
rediscovering each control. The original value/help-text/name snapshot and exact
target predicate are unchanged. A frame is never reused across an input or
later boundary; runner regressions reject repeated capture, stale writing,
changed content and invisible command targets. UIA caches are not atomic native
snapshots, and reduced discovery count is not proof of meeting the hosted budget.

Run `45748466369` failed the rich `UI-tree` menu-open retention check before
the first Collapse subtree input. Its UI dump contains the root Person twice:
the Favorite ListItem precedes the reporting TreeItem. Scale retention now
selects the requested email **and** TreeItem from the same fresh cached batch,
never the first email match or a Favorite fallback. Runner replay and the real
compiled Favorite/reporting controls reproduce that wrong-target geometry.
The profile/target predicates, search/cancellation/full-population assertions,
native inputs and deadlines are unchanged; native scale acceptance still
requires a fresh complete exact-candidate hosted run.

Run `45752557219` reached the final manager-row observation after the native
cancellation/search/partial-retention and resumed 9,900-Person/150-manager
completion checks, then exhausted the unchanged 180-second wait. The same
150-manager loop now retains only its last 12 before/after/final observations:
requested/returned identity and type, expected parent, optional Realize input,
expansion state, row/viewport bounds and current completion text. Direct
element caches never scan the whole window. Unsupported values remain null,
and diagnostic refusals/output failures are separate from the original error.
A failure emits `TREE-SCALE-REALIZATION` and attempts one existing window
screenshot/JSON pair; input count, predicates and 180/660-second budgets remain
unchanged, including diagnostic overhead. This is not a repair or native proof.
