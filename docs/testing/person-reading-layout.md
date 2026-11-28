# Person reading and responsive layout

[Testing index](../testing.md)

Read this for anchored Person reading, responsive People/Person allocation, search-clear restoration, retained-draft geometry and native versus invisible layout evidence.

Related guidance: [Profile/editor assertions](person-profiles.md), [Person search](person-search-navigation.md), [current/frozen note reading](note-reading-context.md), and [required size matrices](required-desktop-scenarios.md#high-resolution-required-scenarios).

## Browsing restoration

The responsive reading journey also hovers the compact People dismissal
backdrop on the hosted desktop. It samples actual uncovered Person pixels
before opening and after the hover, requiring readable content contrast to
survive the dimming; a uniform opaque button-hover fill fails. The backdrop
has its own translucent template, not ordinary button hover/pressed chrome.
The existing viewport, dismissal, focus and full size-matrix checks remain.

Browsing-restoration acceptance compares every originally visible Person's exact
identity and viewport-relative horizontal/vertical position within one rendered
pixel, together with the existing Favorites, expansion and guarded-destination
checks. A larger viewport may expose additional bottom rows; it cannot move or
replace any original row. Scroll percentages and view sizes remain diagnostics,
not proxies for restored content: a horizontal scrollbar disappearing increases
the vertical viewport and changes the percentage even at an unchanged offset.
The owner approved this assertion correction after the hosted #71 captures.
The original 20-second observation budget and full fixture/size matrix remain.
Fixture preparation still makes one end-measurement request and one interior
scroll request, without replaying either. If virtualization remeasures the extent
after that request, a genuinely interior position establishes the recorded row
baseline; an unchanged endpoint still fails. The later exact-row restoration
check, not the requested percentage, proves that browsing was preserved.

## Automatic selection and deferred reveal

Existing hosted profile/reopen, archival and search/responsive journeys observe
the actual profile identity, selected row's rendered text and clickable point
inside the tree viewport, expanded ancestors and deferred reveal after Import
or search. The search/responsive journeys also deliver a cross-branch candidate
while an archived match is highlighted and Import input remains open, then
observe the matching profile and deferred selected row behind offscreen context.
This accepted-refresh fixture does not create an unapplied Preview: that is
protected pending work and must defer snapshot adoption. The existing invisible
automatic-opening selection also drives these actual compiled 700/1100 refresh
paths and a separate reviewed-Import refusal, checking the exact delivered
revision before matching profile/row assertions. Neither path retries, selects
a corrective destination or discards a review to make refresh succeed.
The original Import journey and full responsive matrices remain intact.
The profile fixture places a qualifying Manager behind offscreen
single-report ancestor context. These assertions do not realize, expand or scroll
the expected row themselves. Existing scenario selections, full responsive
matrices and budgets remain; compiled invisible checks are not hosted evidence.
The clickable target is the rendered header text, not an expanded TreeViewItem's
whole-subtree centre. The latter can lie below the viewport while the entire
selected header is visible. The compiled automatic-opening case exercises this
same late-branch layout and actual named/selected header without a native window;
the hosted check remains responsible for screen coordinates and clickable points.
Logical TreeViewItem `IsOffscreen` metadata is not an additional visibility
requirement for its selected header. The shared observation still requires the
actual selected row, an available named header whose own provider is onscreen,
the original viewport geometry and a real in-viewport clickable point.
Missing/unselected rows, missing/offscreen headers and rejected geometry/points
retain the same bounded failure. The compiled case also exercises this selection
and header observation through actual BAML/provider state, not only a fake identity.
The real-folder compiled opening case also replays failed Apply, Import discard,
local reconciliation, accepted re-preview/Apply and Back to People at the ordinary
viewport width. It compares the remembered browse offset with the settled actual
TreeView offset, alongside the exact profile, selected header and expanded ancestor.
BringIntoView requests are asynchronous: the adapter records browsing at context
idle after queued scroll/layout effects, guarded by the accepted request and owner.
It does not issue a second reveal, restore an earlier offset, change any deadline
or bypass active search/Import or retained writing.
Cold virtualization coverage observes the extent at the one actual reveal request
and compares it with the settled real TreeView extent. The adapter first realizes
and expands the path, then defers that original request to guarded context idle
so pending layout/scroll-extent publication can finish before MakeVisible clamps
the offset. It never repairs a clamped request with a second scroll.
The compiled fixture services the staged request and its existing post-scroll
snapshot before asserting exact selected-header geometry and browsing position.
The compiled opening case also changes the workflow-selected owner after the
single actual reveal request but before its queued browsing snapshot. The
accepted-request guard rejects that stale snapshot, retaining the new owner's
browsing; all three phases share current-Workspace, owner and browsing-context
checks. The original header/extent geometry and one-request assertions remain.
After Import, the search journey deliberately resumes compact People and focuses
the unchanged query before its existing highlight/geometry/focus assertions.
It does not assume that clicking Back to People is a query-field focus gesture.
Reading checks retain the successful text observation for both required and
forbidden writing, rather than making an unguarded second UIA lookup after
readiness. Runner cases preserve the same bounded missing/wrong-text failures.

## Responsive reading and retained drafts

The explicitly selected high-resolution `responsive-workspace` journey measures the adaptive host at
700 x 500 and 1100 x 900 window DIP, maximized, and available host widths
959/960/961 around its 960-DIP breakpoint. It observes unchanged overlay viewport
bounds, all-facts-before-full-width-notes composition, anchored identity during
actual long reading, People 240 / Person allocation 420 minimums,
600-DIP bounded actual note prose, stable long/missing fact slots, unchanged compact typography, mouse/keyboard
People-divider effects, wide/compact/wide and actual reopen proportions, and real
reporting-tree expansion/scroll restoration during persistent and overlay search.
Wide browsing setup expands its fixture branch only if needed. Compact setup
only observes the already-restored expanded state: it never reissues Expand or
repairs lost expansion before measuring scroll/search restoration. Runner
regressions replay the observed external provider's redundant-expansion side
effect and require collapsed restored state to fail, not be silently repaired.
Before capturing each 70% browsing baseline, one explicit scroll to the fixture
end measures cold virtualized rows, followed by the single target scroll.
Neither action is retried, and neither occurs after search/clear restoration.
The cold-cache compiled replay first reproduces percentage drift caused by a
changing measured extent, then exercises this same preparation and the exact-row
restoration criterion [in Browsing restoration](#browsing-restoration). Hosted logs retain unmeasured/measured
view sizes, focus and owner, plus the original/failure diagnostics: calibration
must not conceal later extent drift or change the selected Person.
These are harness-action contracts, not a local reproduction of native WPF focus.
Post-resize collapsed-context assertions wait only for read-only UIA discovery
within the existing bounded wait. An expanded section still fails immediately;
the helper never collapses it, and permanently missing sections still fail.
Runner coverage replays the observed missing-then-present context provider.
People-browsing preparation also observes the query value inside the existing
bounded readiness wait before any conditional Clear or overlay-open action.
Its runner replay covers missing-then-present query discovery after Clear,
without repeating either action; a permanently missing query still fails.
The later exact-row restoration assertion and its 20-second bound are unchanged.
The invisible compiled replay additionally drives the 46-Person/long-note
fixture through real tree scrolling with cold virtualization and recorded
available viewport geometry, grouped-result highlighting/manual clear and wide/compact
restoration, including recycled tree-item load events. Compact replay establishes
a real 39-DIP horizontal offset, then drops the cached measurement and invalidates
the tree's measure while browsing is hidden, retaining all People and expansion.
This reproduces the lost horizontal range that clamps even a correctly retained
offset. The manual-clear fixture preserves the original selected Person while
expanding its Manager, verifies that People is open before recording the baseline,
and invokes the real clear action. Every original row must return to the same horizontal and vertical
position; offset-only restoration goes red, while reserving the captured width
keeps that position reachable. It does not reproduce
native focus or establish that a hosted UIA timeout is fixed. Hosted browsing
failures retain the exact-row/visibility assertion and capture the
before/failure pixels, viewport percentages, bounds and focus without corrective
actions or a longer deadline.
The high-resolution `responsive-draft-navigation` journey additionally browses
through compact People destinations with exact failed Person-note writing retained.
An isolated compiled retained-draft probe records post-resize, settled,
overlay-open and owner-return viewport/notice geometry. A failed note's
already reported guard does not add another global notice line and move the
underlay; new pending work, retries and independent errors remain distinct.
The same real-control probe continues through query entry, highlighted-Person
Enter, accepted query clearing and retained-owner return. Its grouped query can
also match a report's Manager field, so it deliberately highlights the intended
Person instead of assuming they are the first match. It records toolbar,
search/clear desired and arranged bounds, logical focus and the original
absolute Person viewport. Empty-query Clear is hidden while retaining its
measured footprint, so its appearance cannot grow the toolbar or move the
underlay. The native exact-rectangle equality has no pixel tolerance.
The full guard stays available in the local editor, accessible operation status
and Details. Hosted failures retain exact geometry/owner/title/document checks
and add before/after metrics plus a failure frame, without layout repair.
Selection helpers deliberately open compact browsing when needed; they do not
treat a hidden tree or a closed panel as successful navigation. Existing exact
current/archived/frozen destinations and independent draft assertions remain.
Only hosted Windows execution supplies this presentation evidence; the size
matrix belongs to its separate required smoke suite, not a substitute for complete explicit UI-scale work.

## Compiled layout and divider assertions

It also measures/arranges the actual compiled content and drives its real
People splitter DragStarted/DragDelta/DragCompleted events through minimum/maximum
drags, 27% balance recovery and a narrower wide viewport. Facts and notes must
remain full-width and ordered inside the actual ScrollViewer viewport while
held and after completion. A long document must size to content, move with facts
under the main scroll, and leave identity fixed. Compact layout uses fewer fact
columns, and opening its overlay must not move the Person viewport. The actual
clear-search action returns to browsing without replacing the selected Person.
The same compiled layout check saves a genuinely long LinkedIn URL through the
profile form and measures four/three/two-column reading. Each facts row must fit
its own content with a compact 12-DIP inter-row gap; a wrapped URL and its action
in a later row must not enlarge an earlier row. Full URL text and wrapped height
remain intact within the viewport. The check restores the original empty URL.

Paired proportional columns keep both opposing People/Person minimums in WPF's
shared resize constraint. This does not replace
hosted physical gesture/hit-target or rendered-pixel acceptance.
The same real control also receives routed Left/Right keys after the maximum
drag, requiring the exact 10-DIP change to survive layout and preference
publication. Its unshown input source has no rendering target: this tests the
actual key handler, not physical focus or native input delivery. Pane layout
callbacks must not restore the previous ratio during the key's layout/save
transaction.

The larger healthy reading viewport also keeps the Person scroll gutter reserved:
switching to shorter content must not shift labelled fact slots by 17 DIP.
Cold invisible browsing logs retain each requested scroll and its actual
extent/viewport. Exact-row/both-axis assertions and deadlines remain unchanged;
there is no corrective action after restoration.
