# Person-note and Team-note writing

[Testing index](../testing.md)

Read this for immutable note drafts, autosave, admitted/newer generations, failed saves, scoped discard and independent durable writing.

Related guidance: [Exact note reading and context](note-reading-context.md), [Person-text cancellation](person-text.md), [Workspace-led recovery and publication](../testing.md#placement), and [separate rich-scale contracts](../testing.md#separate-scale-commands-and-results).

## Immutable drafts and scoped cancellation

`BeginNoteDraft` captures an immutable Person/Team document base (or expected
absence) at the workflow/view-model seam. Editors display that snapshot and
`RetainNoteDraft` marks it dirty without waiting behind in-flight Workspace I/O.
Close/restart stays blocked through first autosave and retries; `SaveNoteDraft`
returns the next base on success, retained again for newer typing. Real-store
tests cover incoming adoption deferral before first save, stable pending
creations, stale bases, deliberate discard/intentional edits and ordinary I/O
recovery/retry. Complete snapshot validation replaces retired frontmatter/parser
protocols; Team confirmation and format checks stay explicit.
The existing two-scope held-note regression keeps a real SQLite Commit barrier
and five-second entry deadline. An entry refusal appends
`NOTE-SAVE-ENTRY-REFUSAL` JSON with monotonic request/admission/exit times,
task outcome, workflow status/errors and available/min/max worker snapshots.
Its existing file observer retains at most 24 storage phase callbacks plus
completed-effect counts, without note bodies or progress file paths. These
observations are non-atomic and can include concurrent workflow callbacks;
missing phases are not proof that validation never started.
A failed entry does not wait indefinitely or dispose an active writer: resource
cleanup follows the existing save's completion, and late task/cancellation or
cleanup faults remain visible under `NOTE-SAVE-TASK-OUTCOME` or
`NOTE-SAVE-CLEANUP-FAULT`. Scheduling, assertions and storage behavior are unchanged.
The separate in-memory two-scope busy-discard case uses its own six-second
`SaveNoteDraft` admission wait. On 2026-11-24 the owner approved this temporary
20% test-only increase from five seconds for both scopes and the shared delayed
variants, deferring investigation. It does not claim a timing cause was fixed
or relabel earlier failed runs as passing. Other tests' deadlines and production
save, synchronization and latency policies are unchanged.
Only a timeout emits `NOTE-SAVE-CORE-ENTRY-REFUSAL`:
request/create/update/observation monotonic timestamps, actual synchronization
context/thread and worker snapshots, admission-signal state, and at most 32
existing public-proxy entry/return/fault observations. Naturally returned local
save statuses are recorded without additional workflow getters, note writing,
file paths or private autosave-task inspection. These non-atomic observations
cannot identify a missed timer or scheduling cause. Reporting faults remain
separate on the original timeout; its type, stack, deadline and assertions stay
unchanged. This is capture instrumentation, not a fix or exoneration of the
historical hosted admission refusal.
This busy-discard contract controls only its view-model request clock so a
delayed test continuation cannot turn the deliberately held writer into an
unrelated real-time stall. Both scopes also run with a continuation delayed past
three wall-clock seconds; the six-second admission wait and all discard
assertions stay intact. Production clocks and separate exact three-second
queue/stall/unknown-outcome regressions remain unchanged.

The same normal SQLite format-3 authority retains Person/Team note identities,
ownership, complete Markdown, timestamps, archival and Team confirmation/context
revisions. Earlier staged SQLite1/2 and every readable-file format are refused
without mutation and with recreate/upgrade guidance; no schema upgrade or retired
engine fallback remains. Complete-snapshot synchronization is the shipping path.
The observable view model owns pending creations and latest drafts independently
of displayed controls. Its first-edit 250-ms cadence cannot be postponed by typing;
one worker serializes captured revisions, retains newer generations after completion
and makes failed writing retryable without another creation identity. Local Person
and exact-note browsing do not use the legacy draft veto. Safe close still requires
all retained writing to commit. The focused stalled/recovery and informed-exit
[Workspace-led contracts](../testing.md#placement) cover the remaining failure/close behavior.

The combined hosted follow-up also runs focused actual compiled
`--verify-cutover-recovery-copy`, `--verify-independent-note-leave`,
`--verify-import-recovery-admission`, `--verify-manager-choice-demand` and
`--verify-cutover-ordinary-fixture-state` checks in that full selection.
Healthy recovered storage becomes quiet once no unsaved writing remains;
clipboard retry cannot dismiss an independent fault. Profile-only Discard does
not authorize a protected mutation/replacement past an independent note error.
The shared guard is tested through actual nonmodal replacement controls and
real SQLite, without invoking private product methods or opening native dialogs.
Browse-only navigation still bypasses that mutation guard.

The ordinary fixture-state loop distinguishes a discarded uncommitted creation
from an existing note: restore assertions first independently establish the
exact saved owner/id/title/body. Paused failed Edit retains readable writing
with disabled inputs/mutations; healthy Edit still requires writable inputs.
After failed-profile owner browsing, a clean note returns Read and its actual
Edit action realizes the Value provider before inspecting writing. Active search
selection is checked through its own exact selected-Person result, not a hidden
archived toggle or an inherited note owner's incidental email; query, overlay,
viewport and complete native size matrices remain.

Headless draft-discard coverage compiles the actual note coordinator with only
its timer substituted. Busy retries must refuse discard before any refresh or
draft mutation; idle explicit discard remains available.

## Durable typing and save handoffs

`SqliteWorkspaceTests` proves both scopes' durable creation/edit/archive/restore,
Ongoing selection, inherited Team context, actual Person switches during a real
SQLite writer lock, newer typing, failure/retry identity and scoped discard. Continued
typing must produce multiple independently reopened committed values before idle,
including a first ordinary small-fixture commit within one second.
Public workflow wrappers also hold an actual successful SQLite SaveNoteDraft
return after commit in both scopes. Typing in that gap must not reinstall the
old base; the coordinator installs its accepted successor before retaining newer
writing, which must then commit/reopen under the same owner/identity. While a
save is admitted, typing updates its generation without reclaiming workflow-base
retention from the writer. These cases keep safe close unavailable and browsing
usable through the gap. Separate admitted-call gates allow a real second local
workflow to commit/deliver a different base: that save must still be rejected,
retaining both the other committed writing and recoverable local draft. No
private-table assertions, production-only hooks, unconditional base replacement
or storage-I/O locks around UI draft updates are used.
The existing invisible `--verify-profile-refresh` selection drives both compiled
editors before a held commit and actual selection events, retaining exact newer
writing and owner through late completion without showing/focusing a native window.
Its held-note navigation oracle requires the same admitted save/base to remain
saving without error before and after each real selection/layout, with the
actual SQLite writer still held and safe close still unavailable. Independent
reopen must find that creation uncommitted before deliberate release; afterwards
the exact newer owner writing must commit and reopen. A once-only 150-ms
selection-handler pause makes unrelated slow dispatch explicit. Bounded
`DIAGNOSTIC: compiled held-note navigation` records unadjusted dispatch/layout
time and real pending/error/transaction state, not a single-sample latency SLA.
Waiting until the held write fails cannot satisfy this structural proof.
The actual ordinary native navigation workload retains informational p95;
this invisible oracle does not replace it. Deliberately held
profile-save navigation has the [structural, diagnostic-only policy](person-profiles.md#durable-saves-and-held-navigation).
The existing hosted `compatibility-settings` tail additionally exercises twenty
distinct Team creations and one Person creation, continuous writing and immediate
report/Manager switches. It records informational p95 from actual native interaction
to a new writable, visible, keyboard-focused editor or usable selected Person, and
requires one second from ordinary typing to independently reopened committed writing covering
that request while typing continues. Its `*-sqlite-note-latency.json` records exact
candidate/run/attempt/product hash, environment, samples, boundaries and nearest-rank
percentiles; named editing/reopened captures retain actual desktop evidence.
These ordinary two-Person observations are not #100's combined rich 10,000-Person
and background-export proof. Required scenario selection, assertions and lane budgets
are unchanged; no hosted interaction result is claimed by compiling this tail.

The #92 latency observer refreshes native UIA caches after each request: one
filtered note-surface snapshot requests only the selected scope's surface,
note identity, title, Markdown and Edit tab, not unrelated reading/list controls.
It retains exact identity, enabled/writable title and body, selected Edit mode,
title visibility and real keyboard focus; permanent native identity elements retain
the selected email, heading and viewport check. It does not repeatedly search
the whole window inside a timing sample, reuse pre-request property values, or
focus/enable controls to make readiness pass. Native pattern discovery is
preparation; every sample still starts before the actual Invoke/Select/Set request
and includes dispatch, layout and observation until the required state is observed.
No measured overhead is subtracted.
`addTeamNote.observationNodes` records each successful post-action snapshot's
node count alongside all existing raw durations and poll counts. The invisible
compiled replay drives both scopes and repeated Add/Person switches, requiring
the observation view to contain only the live current editor controls and retain
exact workflow identity/writing/writable mode. Invisible peers cannot claim
native visibility or keyboard focus. This red/green query-work regression is
not a substitute for hosted p95.

`NoteCommitObservation` independently reopens the real Workspace through the
workflow while native navigation and twenty predetermined continued edits
proceed at nominal 60-ms spacing. This includes all original first six edits;
the sequence never stops early or changes cadence based on save observation.
Every native Markdown request has start/completion timing, and typing ends at
the final actual SetValue completion, not after a trailing sleep or idle wait.
The first-to-last continued edit must span the full one-second observation
window. The former subsecond six-edit burst could end before an otherwise
in-budget complete reopen, so it did not establish the sustained-writing setup.
This workload correction does not change any native sample count or latency
budget. The observer timestamps
the first coherent committed writing covering the original request instead of
waiting for unrelated UIA assertions before beginning observation. A real-storage
regression makes the first actual reader deliberately slow, continues both
scopes' typing/switching, waits for the independently completed coherent
observation, then delays collection by more than one second. Collection must
return that exact immutable observed value, not a newly stamped collection
time. This is a deterministic timestamp-independence oracle, not a hard
per-case one-second SLA; separate healthy-storage progress checks and all
native p95 budgets remain unchanged. Its bounded `NOTE-OBSERVATION-TIMING`
records admission/read/observed/collection timing and status without note
contents. Cancellation
of a pending real-write observation is explicitly not a saved result.
The latency JSON retains all original raw arrays, p95, budgets and boundaries
(20 editor, 42 navigation and 21 commit samples), and adds native-request durations,
observation poll/read counts, cumulative reopen-read time, typing-completion
timestamps and observed-during-typing flags. All commits must be observed before
the continued-typing sequence completes.
`localDurability.continuedEditsPerNote` (20), `nominalEditSpacingMs` (60),
`continuedEditRequestStartedMs` and `continuedEditRequestCompletedMs` retain the
fixed plan and all 21 by 20 actual action timings. Each original
`typingCompletedMs` equals the corresponding final request completion.
The shared fixed sequence also drives a real-storage regression in both scopes.
Under the owner-approved non-UI policy correction (2026-11-16), its controlled
450-ms first-reader delay and fixed 20 edits/60-ms spacing remain, but the
independent observer must find the exact final intended title/writing, note
identity, scope and Person owner within its existing 20-second observation
window. The same polling loop defers the independent reopen until the fixed
input sequence ends and public VM safe-close/local-save status reports no
pending requests, retained drafts, paused edits or unknown outcome. This is
readiness only: success still requires the actual independent reopen and exact
final data/ownership, including wrong-owner absence. An attempted open failure
remains an explicit error. No added wait or Flush makes the observation succeed;
the existing final draft drain remains after observation.
`NOTE-SUSTAINED-TYPING-REOPEN` retains raw action/observation times and whether
observation happened before typing ended as diagnostics only, not an additional
per-case deadline. The historical late-observation failure remains causally
unattributed. This non-UI correctness policy does not alter native p95 budgets,
request-start boundaries or during-typing sample/flag requirements above.
Optional `localDurability.observerAdmittedMs`, `firstReadStartedMs` and
`successfulReadStartedMs` retain actual worker/reader timing beside the original
21 raw totals. Queueing and read/validation are included, never deducted; these
are observation boundaries, not a database-only commit timestamp or proof of
a slow-write root cause. Neither delayed collection nor instrumentation can
satisfy the native durability/during-typing assertions. Small-fixture note p95
remains enforced at 1,000 ms; only the rich normal/held-export note phases use
the temporarily enforced [1,050-ms ceiling](../testing.md#separate-scale-commands-and-results), retaining the
1,000-ms goal.
The invisible compiled selection also records twenty actual note Add handlers,
layout, Person selection and independently reopened commits under a clearly
labelled `DIAGNOSTIC` marker. Those timings decompose local product work; they
remain distinct from native focus/pixels and hosted acceptance.

Another real-file fixture rejects Person/Team-note creation with the publication
lock, then delivers an unrelated valid profile change. Note feedback keeps the
retained creation error without a refresh-success prefix; distinct current
failures remain alongside it, and deliberate resolution restores quiet feedback.

External-delivery retries use the same distinction for profile and Person/Team
note drafts: settled main-work controls plus concrete rejection and pending
writing, followed by unchanged owner/editor and delivered-byte assertions.
Before invoking Retry once, subscribe to actual activity changes and require that
retry's saving activity followed by retained-writing Pending completion. The
preceding rejection or an intermediate Ready alone cannot satisfy observation;
headless runner replay covers queued and fast retries and missing transitions
within the existing observation budget.

## Commit acknowledgment and cancellation

The rich save clock starts before native input and runs through actual WPF
post-Commit acknowledgment for the exact current owner/note/title/request.
The shared observation lifecycle owns request, body and cleanup so an observer
fault cannot replace an initiating input/assertion exception. Secondary faults
remain attached/reported, and an otherwise successful body still fails when its
observer faults or never confirms durability. Request clocks, continued typing
and the original observation window are unchanged.
Pending observation is diagnostic, not mandatory: a fast Commit may finish
before the first observer call. The exact Person/Team saved-success (or Team
saved-and-confirmed) acknowledgment and truthful complete-publication/local-only
success projections can acknowledge the exact request, never generic Ready/Saved
combined with optimistic editor text. The scoped accessible ItemStatus is set
by `OwnedNotePresentation` from VM status. Retained generations
override every core status with Pending; the worker removes a generation only
after accepted `SaveNoteDraft` and only when it is still the latest generation.
That core acknowledgment is returned after actual SQLite Commit.

The existing invisible compiled selection includes one bounded negative at the
same real public-storage/actual-BAML seams (`NOTE-COMMIT-ACK-CAUSAL`): the exact
new request cannot acknowledge while a real transaction is held before Commit;
an independent reopen still sees the original writing. Release must permit both
the exact acknowledgment and independently reopened owner writing to advance.
Existing failed/unknown/no-stale-generation cases remain. This is causal
contract evidence, not native timing acceptance.
The actual compiled input-provider regression also first observes only after
Commit (without seeing Pending), then performs real Sync now and checks the same
exact committed writing again. Publication feedback remains unchanged. The final
independent full-writing check requires exact owner, note identity/title/body,
Ready state and usable scoped editor; unrelated global English word order is
not another success gate. Returning to a clean note explicitly selects its
actual Edit tab before continued typing, without changing the request clock.

The pending-observation cancellation fixture holds the actual admitted app
transaction before Commit through cancellation; a competing SQLite writer
would instead end the app attempt at the unchanged one-second busy limit and
make a later IsSaving poll miss that transient state. Its original five-second
admission and exact cancellation exception remain, including a delayed-body
case. Exit cancellation is observed through the caller's cancellation-aware
Task for the entire operation, including a completed-await/queued-continuation
gap. An owned non-UI child constrains only its own managed workers and holds a
real Commit plus the external completion-post effect; the unchanged one-second
cancellation, three-second stall, retained draft/fault/browsing and original
compatible exception assertions remain. The shared test host/desktop is never
reconfigured by that child.
