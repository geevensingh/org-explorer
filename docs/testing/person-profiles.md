# Person profiles, reporting and editing

[Testing index](../testing.md)

Read this for Person category/reporting facts, whole-profile editing, Manager-owned Team names, photos and profile Copy assertions.

Related guidance: [Person-text submission](person-text.md), [independent note writing](note-writing.md), [reading layout](person-reading-layout.md), and [required native selections](../testing.md#real-desktop-execution).

## Profile editing and cancellation

Ordinary profiles use `BeginProfileEdit`, staged text/Manager/photo operations,
and explicit `SaveProfileEdit` / `CancelProfileEdit`. `ProfileEdit` exposes the
person-scoped immutable baseline and complete draft. `TryLeaveProfileEdit`
observes Save / Discard / Stay for actions abandoning a profile. Person browsing
instead retains each owner's complete draft. Profile Cancel clears only that session, never independent note or
quick-action failures. Unsaved profiles block safe close and updater restart.
Person More actions require confirmation before the same profile leave decision
and apply archival/reactivation immediately through `EditPersonArchived`, not as
profile-draft fields. Cancellation preserves the draft and saved history; Stay
and rejected leave-save retain the owner. Profile Discard never clears note drafts.

File-backed profile-session cases cover complete publication and reopening,
Cancel nonmutation, real locks and missing photos, Manager/history consistency,
independent quick changes, same-record/different-field stale saves and retries,
previously incorporated delivery and unrelated-person delivery. Save revalidates
persisted data even without an explicit view refresh.

Rejected profile Save observes the actual typed `Failed/NotCommitted` local
state and visible noncommit error, while retaining owner/draft/destination and
available-browsing assertions. Successful Import discard observes the removed
discard action and disabled Apply, not a global success phrase masked by an
independent local-storage fault. That fault must remain until explicit recovery.

The profile navigation scenario enters Edit, stages a value, and exercises the
real Save / Discard / Stay decision, including a locked-file leave-save failure.
Completed rejection requires concrete failure feedback, a closed leave dialog,
enabled profile Save controls, retained owner/value, and pending unsaved-draft
activity. It must not require Ready while that rejected draft is still retained.
Accepted profile Discard refreshes both the displayed values and status/activity
before successful navigation: Ready only when no independent writing remains.
Discarding a profile while a Person/Team note failed must retain pending activity
and that note's owner/editor until its own deliberate discard.

Ordinary reading does not construct or clear the hidden whole-population Manager
picker. Actual profile Edit and retained-owner restore still build all current
choices with the exact current draft/base; visible editing continues through
the existing refresh path, without a stale cross-head cache. Staging and Cancel
retain their original relationship/history semantics.

## Categories and reporting

Person-category cases use the real workflow through
`WorkspaceViewModel.GetRelationships`, including leading-prefix/fallback rules,
unrelated names/titles, accepted navigation, archival/reactivation and rejection.
Real-folder cases reopen categories and accept delivered People while retaining
private context and note writing. The existing invisible profile-refresh contract
checks actual Employee/Vendor badge text, accessible labels, report destinations
and anchored geometry across its size matrix. Existing hosted profile, archival,
external-refresh and responsive journeys assert rendered badges; invisible checks
do not establish hosted presentation or keyboard evidence.
Reporting-summary cases use that same public profile query for independent
12/9/3 mixed graphs with Employee and Vendor roots, explicit single-category
zeros, accepted moves/imports, rejection, archival/reactivation and visible-subset
independence. The 12,000-report wide and 1,024-level deep cases prove uncapped
counts, not either separate scale suite or a latency SLA. Real-folder cases
reopen current counts and refresh selected profiles/ancestors while retaining
private context and note writing. The existing invisible profile-refresh contract
observes the ordinary zero-inclusive fact, its real read-only selectable
TextPattern provider and final-category character layout, accessible full text,
placement, size matrix, delivered counts and eligibility changes without a
native window. The prior plain TextBlock had no TextPattern; source text and
an outer control box alone cannot establish final-category glyph readability.
Existing hosted profile, archival, external-refresh and responsive journeys
observe every final count/category character's actual rendered text-range
rectangles, fact text, wrapping, Team-first order and report destinations;
those presentation and keyboard assertions require the hosted desktop lanes.

An independent invisible-window fixture checks reports and their count are
hidden when empty and during wide tree browsing, but visible when populated in
compact mode or search. Report selection still opens its exact Person; opening
the temporary People overlay does not change the report bounds or content extent.
The native profile journey uses the tree for wide report navigation, while the
note journey retains visible report-list navigation during search.
The independent tree fixture measures the real collapsed/expanded chevron and
requires identical toggle bounds and label indentation. Real selection providers
must open only the accepted Person's immediate reports, preserving other branches
and deliberate collapse. Search/clear reveals the accepted selected branch;
failed note writing rejects another selection without expanding that destination.
These are compiled wiring/measurement checks, not rendered acceptance.

## Manager-owned Team names

Owned Team-name cases extend these same profile-session seams: explicit set,
rename, clear and Cancel; current-direct-report eligibility and commit-time
rechecking; dormant/reappearing labels; private Import rejection/preservation;
and exclusion from Profile changes and frozen Team context. Real folders cover
visible/dormant reopening, archival restoration, direct delivery, stale saves,
actual writer/selector locks and retained independent note writing. The ordinary
profile/archival and responsive reading journeys observe the always-present first
scrolling fact below anchored Manager, owner attribution on hover/focus and owner-only editing. They remain
hosted-only interaction evidence, distinct from invisible compiled checks.
The first label's placement accounts for the existing one-DIP border, eight-DIP
padding and four-DIP slot margin; actual compiled layout must pass that same
predicate while a later row or column must fail.
The existing invisible profile-refresh check also loads the compiled owned-name
template, verifies its accessible text/owner and placement-target tooltip binding,
and drives eligible Save plus ineligible `Not set` through real compiled controls.
It never opens that tooltip or shows/focuses a native window.
The archival fixture settles its preceding Team-note autosave before entering
profile edit; otherwise that later save can disable the new focus destination.
Its invisible replay observes the real timer/save and enabled logical entry.
Team-name reading discovers its segment inside the existing bounded observation,
including regeneration after Cancel/resize; missing or wrong values still fail.
The compiled replay uses the actual 700 x 500 editor, focuses/brings its owned
input into view, then cancels and lays out at 1100 x 900. Its segment peer must
belong to the current visual subtree: a cached peer for a detached TextBlock
cannot pass. Team-name updates retain generated segment controls and update
their attributed values in place, including when a neighbour is renamed,
cleared or restored, instead of disconnecting cached peers during a partial
path change or no-change refresh.
Inherited-name cases assert exact ordered labels and independent owners through
the same workflow and real-folder seams: duplicate names, unnamed gaps, dormant
owners retaining ancestor context, final imported graphs, refresh with retained
writing, and unchanged frozen Team-note evidence. The existing `profile-journey`
and `responsive-workspace` journeys require actual ` > ` separators, duplicate
mouse/keyboard attribution, segment-only Copy through mouse and keyboard context
menus, inherited read-only context and owner-only editing.
The responsive long-duplicate-name checks retain the full original size matrix
and restore the original 44-Person graph before the remaining reading checks.
Invisible compiled checks separately cover wrapping, exact segment/separator
order and live attributed peers through rename, clear, restore and Cancel/resize;
they are not rendered interaction acceptance.

Team-name reading discovers the live positive-size ProfileScroll and its real
pattern inside the existing bounded scroll-settlement observation, with at most
one scroll request. Label discovery remains inside the existing reading
observation. Temporary missing/detached discovery cannot bypass those waits;
permanent missing/wrong values and provider failures still fail without new
deadlines, repeated actions or changes to profile Save/Cancel/read-only behavior.

## Durable saves and held navigation

The #93 path adds format-3 Person/profile/Preferred-name/favorite/archival/photo
and Manager saves through those same seams. `SqliteWorkspaceTests` checks exact
durable reopen, complete rejection for missing photos and eligibility loss,
independent writing, duplicate Team-name owners/unnamed gaps/dormant labels,
frozen former-chain notes and retained resources. Actual SQLite writer/file
locks cover browsing during a pending profile and failed-save retry. Separate
owner drafts and newer generations remain unsubmitted until explicit Save;
Cancel is owner-scoped. No private-table assertions or synchronization proof
are used.

The existing invisible `--verify-profile-refresh` selection also drives compiled
profile/Preferred-name editing, real Person switches and a held explicit Save,
then reopens exact values and decoded photos without showing a native window.
Its actual compiled Preferred-name/browse/Save/favorite replay also requires
both note surfaces' accessible ItemStatus to agree with the real idle/safe-close
state. Same-owner local browsing refreshes only feedback, not note editors:
completed independent saves cannot leave stale global Pending text, while
genuine unsubmitted writing remains retained and pending after favorite success.
The `compatibility-settings` hosted tail retains all original note samples and
adds native profile draft/Preferred-name navigation, Save/Cancel, photo pixels,
favorite/archival/reactivation, Manager/frozen history and process reopening.
Capture details must remain separately collapsed, read-only and fully copyable;
later current note writing cannot rewrite frozen reading.
Its `*-sqlite-person-saves.json` schema 3 binds revision/run/attempt/product
hash, editing/reopened processes, exact Person values/photo hash/capture/note
version, twenty native Save-to-independent-reopen samples (nearest-rank p95
at most 1000 ms), and a native Person-selection sample while a real SQLite
writer holds the profile. The single held sample is explicitly diagnostic,
not a per-case 100-ms release deadline or a p95 estimate. Like held-Import
browsing, `heldSaveNavigation` retains unadjusted `samplesMs`, marks
`timingUse` as `diagnostic` and has no `budgetMs` field. Read-only Save-pattern
preparation precedes the held interval. The single original Save/Select requests
retain their submitted owner, real physical hold before/after selection, and
exact usable visible destination. Independent readers must see the exact
original profile/no photo while held and the exact intended profile/photo after
release and the existing completion observation. A failed/missing commit,
released writer, different submitted owner or wrong destination fails.
UI activity/status text is diagnostic presentation, never save identity or an
additional pending-operation criterion. The invisible compiled loop exercises
the same physical-hold/old-state/new-state proof without claiming native
visibility; runner negatives reject incorrect lifecycle/storage outcomes.
`*-sqlite-person-held-navigation.json` schema 1 and `HELD-PROFILE-NAVIGATION`
retain stage, expected/submitted save owner, last actually observed destination
email/heading/visibility, actual before/after activity/status/hold,
single raw sample, invocation/destination/original-state/committed-state
outcomes and exception type/HResult, bound to candidate/run/attempt/process/
executable hash. Metadata text is bounded to 512 characters. This partial
evidence is written even when the later Person manifest is not reached; it
does not claim the full journey passed. Evidence-write failure cannot replace
an existing primary exception, and a failure on an otherwise successful path
remains an error. The actual owned PID/executable hash is captured before held
work; final sidecar emission performs no process/window lookup, so an already
exited app cannot replace the original failure or prevent writing its evidence.
A real owned no-window child regression verifies that lifetime boundary and
primary exception identity. Historical missing fields are not reconstructed.
The ordinary 42-sample navigation workload retains every raw timing and reports
informational p95; exact usable destinations remain required.
Named retained/committed/history/
reopened captures carry actual desktop pixels and matching metadata.
Each explicit Save pattern is prepared through the existing bounded
`ControlReadiness` wait, tolerating transient missing/disabled providers without
repeating the request. Preparation remains before the original native
Save-to-reopen clock; no time is deducted from its samples or deadline.
These focused two-Person checks do not establish rich-scale/background-export,
stalled-save orchestration or hosted acceptance before the required lane runs.

## Compiled profile and Copy assertions

It also checks that the ten fixed profile Copy targets accept focus and Tab
navigation and expose an accessible copy action. Absent values disable their
Copy value action and expose neither the hand cursor nor a copy hint.

The separate `--verify-profile-refresh` option runs that load contract and then
drives the actual compiled controls' open/select/Save events on a bounded STA
dispatcher with isolated real files, still without showing a window or creating
its native handle. It observes note content at profile Save completion, before a
later refresh could hide stale same-owner reading, and proves both clean
delivery refresh and dirty-note retention. Completion uses the actual submitted
owner's View/controls and independently reopened whole profile, not a global
English success message: a retained invalid note must keep its own Pending/error
feedback even when the independent profile committed. Owned Team creation likewise
requires exact committed note/review state rather than a retired writer's success
phrase. `--verify-profile-note-refresh` focuses these same actual compiled/public
real-store regressions for diagnosis; they remain included in the complete
`--verify-profile-refresh` selection. It does not run Loaded startup/update
handlers or UI Automation. The desktop lane runs this additional regression
within its existing budget. Actual Tab/context-menu Copy and Import-only discard
remain in the representative hosted profile journey.
The same invisible-control regression exercises all ordinary facts, labelled
missing-value slots, long identity/facts, matching photo/initials footprints, full editing of an empty field and Cancel,
and Settings-to-Import rejection retaining independent failed Person-note writing
and delivered bytes. It is not rendered-layout or actual keyboard evidence.
Its profile-copy fixture substitutes only the Windows clipboard write boundary.
Actual compiled primary/key/menu connections retain exact multiline text and
leading zeroes, segment-only duplicate Team names and owner/full-value hints.
Missing/cleared values leave a sentinel untouched, while a real dash value still
copies. The transient, non-hit-testable accessible confirmation expires without
clearing independent errors. A rejected clipboard write shows no success and
retains the selected Person, profile/Person-note drafts and exact Workspace bytes.
No native window, tooltip or real local clipboard is opened/written by this
fixture. These are compiled-control observations, not physical input or rendered
confirmation acceptance.
Representative identity, ordinary-fact and Team-name gesture checks start with
the previous confirmation expired, then require newly visible success. Exhaustive
eligible-value payload checks remain; expiry is not added to every matrix gesture.

It also exercises labelled Manager and Office in the anchored header, joined
building/room values and partial/missing Office data, with Department in a
labelled facts slot. Header context fits the existing photo height at
minimum/ordinary/wide sizes while saved-Manager navigation remains anchored
during profile editing. Full building/room Copy and profile-search targets remain
separate; Department follows the facts grid's stable missing-value policy.
The same real-control check saves multiline header values, bounds their visible
height while retaining full text/tooltips/automation names, and requires Office
automation-peer traversal to follow building then room. Hosted adaptive reading
also checks that Office traversal order.
Compact-state fixtures also cover line breaks in saved Directory/Preferred names
and populated archived profiles. Both the invisible native-client allocation
check and hosted minimum-window keyboard journey retain full saved names, the
visible Archived Person marker, existing actions and usable dirty editors.
Edit-only whitespace is reclaimed rather than shrinking controls; this includes
the taller Directory-name editor when its saved value contains a line break.
These stored-relationship fixtures also require Manager's tooltip to retain the
full caption and email, including `Last stored:` and an archived-manager qualifier.
Archived reading also covers the 408-DIP minimum Person allocation, retaining
the full visible marker beside Email and all identity actions without increasing
the photo-height header. The real compiled divider and hosted drag both exercise
this reading state; Email retains its full Copy/tooltip value when ellipsized.
Its inline profile check measures paired fields at wide widths and stacked fields
at the minimum Person allocation, then covers 700 x 500, 1100 x 900 and wide
content. It requires enabled, Tab-accessible, readable-width fields without
horizontal page overflow, notes below the form, and identity/Save/Cancel remaining
anchored during scrolling. Logical entry/Cancel focus, routed field Enter and
retained staged text are also checked without creating a native window handle.
Physical focus, Tab scrolling, Enter delivery and appearance still require hosted
WPF; these compiled observations cannot substitute for them.
It also dirties the profile at 700 x 500 with the real pending notice visible and
requires room for a complete labelled editor. The existing Choose/Clear buttons
share the inline edit bar rather than growing the photo column. The hosted
minimum-size profile journey repeats full keyboard traversal while that draft is
dirty. Its invisible counterpart also measures the 684 x 461 client allocation
observed inside the hosted 700 x 500 window and requires every focused editor
to fit without clipping; nominal content sizing alone is not native-window proof.
The edit bar's compiled automation-peer and hosted UIA order must follow
Choose/Clear then Cancel/Save. Wrapped photo commands must not stretch the
Cancel/Save buttons beyond their natural height.
Square, landscape, portrait and small-source compiled photo layouts must
keep the Image geometry at 144 x 144; hosted automation and pixels remain the
accessibility and visible-crop evidence.
