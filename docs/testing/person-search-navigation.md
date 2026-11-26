# Person search and browsing

[Testing index](../testing.md)

Read this for Person-level search, ranking and attributed evidence, accepted/rejected destinations, browsing bookmarks and exact retained owner writing.

Related guidance: [Exact note destinations](note-reading-context.md), [browsing restoration and layout](person-reading-layout.md), and [required native search selections](required-desktop-scenarios.md).

## Browsing bookmarks and destinations

Adaptive People navigation is observed through `WorkspaceViewModel.PeopleNavigation`
and its navigation/query/browsing operations. Focused cases preserve an exact
secondary note and frozen search destination, and retain owner/editor, expansion,
scroll position, overlay and actionable feedback on rejected navigation.
The in-session browsing bookmark captures horizontal as well as vertical offset;
neither belongs to synchronized Workspace data or a preference schema change.
It also retains the measured content width. Root reporting rows reserve that
width while unseen wide labels are virtualized, keeping the bookmarked horizontal
offset reachable instead of clamping it to zero. This minimum applies only to
root containers, not recursively to descendants or the outer People pane.
Existing accepted/rejected navigation tests retain both axes and measured width.
`WorkspaceStartupTests` uses real settings/files to prove percentage reopen,
canonical per-Workspace identity, interleaved designation/startup writes, unchanged
Workspace bytes and locked/corrupt/invalid-version/invalid-percentage rejection.
These do not prove WPF geometry.

## Grouped Person search and retained writing

The bounded shared-Team-note excerpt regression measures allocations through
real in-memory `Search` after warming its index: 64 reports share a long note,
and one request must allocate less than 2 MiB instead of normalizing the body
per result. It also checks exact owners/documents, different query excerpts and
fresh writing after an edit. This is not either 10,000-Person scale suite.

The #107 search slice groups the existing `Search` evidence through `SearchPeople`
at the shared workflow/view-model seam. Cases retain every source and attribution,
equal-name/different-email identities, archived eligibility and
exact unsaved writing, including accepted same-Person browsing without rebasing.
Clean ordinary activation uses the ongoing-note Read view; an exactly selected
unsaved secondary note retains its owner/Edit/identity/base and writing.
Exact Other notes and legacy remembered-note
restoration remain independently covered.
The invisible compiled replay observes grouped rows, rendered email bindings,
distinct-Person counts, non-activating row selection and guarded explicit result
Enter. Current/frozen owner-correct documents remain available through their normal
disclosures. It retains the full cold two-axis manual-clear browsing replay and
exact-row criterion; logical focus without a native window is not keyboard evidence.
Existing normal/responsive search and scale callers explicitly select and activate
a Person rather than relying on selection to navigate. Hosted search checks observe
ordinary profile/note Read state, with independent Other notes and History access,
not matched-field focus or a duplicate generic inspector. The existing
responsive draft journey also browses through valid search and returns to the
exact failed owner editor without changing its Person viewport. Invalid/stale
destination rejection still retains the active query and highlight.
Unique visual captures cover
grouped results, mouse activation, exact/empty Other notes, current/frozen attribution
and rejected writing within those existing journeys. No scenario, population,
deadline, required lane or explicit-scale selection is removed. These captures
and hosted assertions must run for each complete candidate.
The #108 view-model cases observe initial/query-reset/empty highlighting,
clamped navigation without leaving exact reading or unsaved profiles, and
accepted versus rejected current/archived transitions. Manual clear retains
both browsing axes and measured width; accepted activation instead expands the
chosen path and reveals its row, never restoring an old offset over that choice.
Query input defers matching to background publication; highlight movement reuses
that published result list instead of rebuilding excerpts. Bounded allocation
cases use a warmed long-note fixture, and publication cases reject older query,
refresh and Workspace requests. The compiled replay also sends Enter immediately
after query input, before pumping publication, to retain single-Enter acceptance.
Archived-to-current search also replays a delayed tree-selection echo: People
stays open for the already accepted Person, while explicit same-Person search
activation and different-Person tree navigation still dismiss the overlay.
The SQLite integration additionally holds an actual explicit profile transaction
while single-Enter search navigates away and back, distinguishing its submitted
durable value from newer unsaved writing and the original immutable base. Both
note scopes retain an unsaved secondary selection through same-owner search.
Only the existing ordinary-local-save classification and existing local-Import
kind permit search during foreground work; Workspace replacement cannot inherit
that permission. The obsolete query/refresh/Workspace publication and stale
eligibility negatives remain. `--verify-current-search-storage` and the narrower
`--verify-person-search-integration` expose these existing public/BAML checks
without running unrelated suites or claiming native input/focus acceptance.
The compact-identity regression additionally gates real public search returns
after an actual profile Save produces immediate and deferred same-query refreshes.
One selected-row ListBox Enter must use the existing fresh-result activation,
not disappear because the displayed request was superseded. Same-query refreshes
are not new query intent; text changes (including away-and-back), Workspace
replacement and intervening Manager/report browsing invalidate delayed activation.
A separate no-selection Enter still waits for pending result publication.
These actual-BAML/real-SQLite cases observe the routed async handler's completion
through a forwarding synchronization context, so rejection is not inferred from
a sleep or pending task. They are in the existing profile-refresh selection;
`--verify-compact-identity` exposes them plus the unchanged original caller and
full compact identity/editor matrix. They do not prove which identity or refresh
overlap occurred in the historical hosted timeout, physical keyboard behavior,
native/rich acceptance or performance. Existing workload/scenario counts, clocks
and budgets are unchanged.

## Identity and context ranking

The #111 own-identity slice replaces first-match order at that same `SearchPeople`
seam: complete Preferred/Directory names, Alias and email precede literal name
words/phrases, word prefixes and incidental own-identity substrings. Cases cover
punctuation/whitespace boundaries, multiple occurrences, archived parity,
heading/email ties, stable same-tier evidence, strongest primary excerpts and
unchanged literal membership. The Sachin regression publishes names before
Kiran's incidental email and Manager-only matches, preserves secondary-note and
historical attribution, and exercises ranked acceptance while retaining exact
owner-scoped unsaved writing. Stale-result guards remain. Referenced Managers
must still exist; the existing invalid-reference rejection is unchanged, not
bypassed for an unreachable fixture.
The #112 context slice completes the seven-tier policy at this same boundary:
other owned current profile/Person-note/Team-note matches precede current
Manager/inherited Team-note matches, and all historical evidence follows.
Small mixed-source cases retain exact former names/Aliases, former Manager
identity and frozen note versions below weaker-text current evidence. They
check archived/current owned-context parity, same-heading email ties, one
strong hit versus repeated inherited mentions, title-only primary excerpts,
stable same-tier evidence order and complete owner-correct evidence retention.
The existing no-identity-hit publication and bounded-allocation cases now
highlight the owned Team-note result before its reports; ordinary first-result
acceptance opens that owner without targeting a matched note. These non-UI cases
do not establish native query focus, viewport visibility or mouse/keyboard
acceptance; the existing required hosted journeys remain required on the complete
candidate, with no new desktop scenario or explicit-scale execution.
The native manual-clear replay waits read-only for the queried Person's current
Person-note evidence before selecting once; hidden browsing controls alone do
not establish result readiness. The existing bounded wait and exact row/pixel
restoration assertion are unchanged.
The existing search journeys now type through the focused query field with real
keyboard input, retain typing focus during Up/Down, observe each highlighted row
inside the results viewport, and check first/last clamping, continued typing,
first/chosen-result Enter, no-result Enter and mouse equivalence.
Target discovery snapshots attribution and ordered result identities inside the
same bounded read-only observation, without an unguarded second lookup of the
result list. Each subsequent highlight still requires fresh native visibility,
selection and query focus; no input is repeated to repair discovery.
Population readiness uses the rendered distinct-Person count, not the number
of simultaneously realized UI Automation rows. The complete-candidate hosted
failure exposed only seven/nine realized rows for forty matches at the two
viewport sizes. Every native Down step must still reveal the exact next
Person, selected and wholly inside the results viewport with query focus;
neither a displayed total nor invisible logical focus proves that traversal.
Accepted current and archived rows must be selected and visible, with their
leading edge, full header height and clickable point inside the viewport.
Reserved blank reporting-row extent can remain wider than that viewport.
Wide mode requires actual row keyboard focus. Compact current/archived acceptance must
dismiss People and focus its button, then retain the revealed selection on
reopening. Archived access must show the retained list without reactivation or
current-tree insertion. An accepted-search timeout captures the actual failure
frame and reports query, owner, profile readiness, navigation visibility and the
actual keyboard-focus owner without retrying input or repairing focus. The same
20-second assertion and current/archived matrix remain unchanged.
Accepted-search layout discovery shares that bounded read-only observation:
a transiently missing People entry must not freeze the wide-layout branch for
the entire wait. A runner regression replays an initial layout observation that
differs from the settled mode. Native query/profile, mode-specific visibility
and focus, reopened selection and exact viewport assertions remain unchanged;
the replay does not establish hosted focus or acceptance.
The responsive retained-note journey uses the same
query-field input and preserves exact failed writing and geometry on owner return;
accepted activation clears search, while a separately unaccepted query retains
its highlight and overlay. No scenario, responsive matrix, deadline or scale contract changes.
The complete #107/#108 candidate requires exact-candidate hosted execution;
authored assertions and invisible replays are not native keyboard/layout acceptance.

## Selection visibility and shared fixtures

Person-selection assertions always require the current tree and also require
the Archived people list when its filter is on. A collapsed Archived list is not
a missing-control failure. Headless replay of the shared assertion covers that
absence and still rejects wrong selections or missing required visible lists;
hosted WPF supplies the real selection/visibility evidence.

The ordinary (43-Person), responsive and invisible replay (44-Person) browsing
paths share only their fixture records and long-note paragraph inputs.
Expected identities, exact row positions and document/owner assertions remain
independently literal; the full populations, actions and deadlines are unchanged.

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

For the lifecycle prerequisites, read [Workspace startup restoration](workspace-opening-exit.md#startup-preferences-and-exact-remembered-destinations).
