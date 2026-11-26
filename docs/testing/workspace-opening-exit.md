# Opening, restoring and exiting a Workspace

[Return to Testing](../testing.md).

Open this reference when changing Create/Open, compatibility, startup preferences, Settings feedback, normal close or Exit anyway.

For real local Commit, reader locks and unknown outcomes, read [storage and recovery](workspace-storage-recovery.md). For publication, adoption or divergent versions, read [synchronization](workspace-synchronization.md). For safe updater handoff, read [application updates](application-updates.md). For retained writing, read [Person profiles](person-profiles.md) and [note writing](note-writing.md).

## Create/Open and current-format compatibility

`SqliteWorkspaceTests` observes the normal shipping path through the same real-storage
`IApplicationWorkflow` / `WorkspaceViewModel` seams. It covers ordinary Create/Open,
reviewed People-only Import, all supplied attributes, exact accepted evidence and
replay, startup reopening, stale review, actual file-access failure, unavailable
or corrupt local storage, explicit unsupported actions and preservation of an
existing retired Workspace. It does not inspect private tables as a business seam or substitute an
in-memory success. The existing `compatibility-settings` hosted journey also
creates the app-managed local format without an opt-in, previews/applies, browses rendered Person facts and reporting
navigation, closes and reopens the same population, and checks the old Workspace
bytes. All prior scenario steps remain.
Compiling this journey or passing filesystem cases does not establish hosted
WPF interaction evidence or any 10,000-Person responsiveness claim.
The existing invisible `--verify-profile-refresh` contract also drives the
compiled ordinary Open Workspace and SQLite Create actions, requiring both to
return from Settings to the browsing/start view. It then invokes normal Settings
Import and previews real initial People without accepting them before Apply.
It never sets presentation state to bypass navigation or shows a native window.
This guards the hosted helper's required starting view; the unchanged hosted
journey still establishes actual create/import/browse/close/reopen interaction.

## Startup preferences and exact remembered destinations

`WorkspaceStartupTests` uses app-local `WorkspaceSettings` and the real
workflow/view-model with isolated folders. It covers first run, preference/app
reopen, missing-folder noncreation and Retry, locked/invalid Workspace rejection,
alternate-folder recovery, intentional manual creation, failed attempts retaining
the previous default, and visible corrupt/unwritable preference errors. These
cases also prove that both manual and existing-only settings opens retain a
dirty profile, independent quick changes, saved bytes and the successful
preference until an explicit leave decision. These are filesystem/settings
assertions, not WPF evidence or a second profile-mode policy.

Exact startup view uses the same observable People/owned-note state and real
settings/files in `WorkspaceStartupTests`. Cases close/reopen secondary Person
and owned Team notes by GUID after rename, retain Read mode and ongoing choices,
preserve same-Person Team selection and interleaved per-Workspace percentages,
and resolve inherited/frozen note ownership without substituting current writing.
Unavailable/archived Persons/notes, failed Workspace validation and missing
folders preserve preferences and writing. Corrupt/unsupported/invalid preferences
and locked publication are explicit; a failed view write retains valid in-session
reading without creating a designation. Settings-driven existing-only reopen
cannot replace retained independent drafts or enable unsafe close/restart.

## Compiled startup lifecycle and readiness evidence

The exit host's one preparation Open waits for the actual compiled Ready activity
and enabled Open control inside its original bounded preparation observation.
Product Loaded performs asynchronous startup/update recovery even with startup
update checks opted out; a second Loaded handler cannot assume that work has
finished. `--verify-exit-host-startup`, also included in the existing compiled
profile/exit checks, replays both real Loaded handlers without Show or a native
handle. It requires observed pending startup, exactly one subsequent Ready Open,
the real populated/selected Workspace and actual owned process death/reopen.
It does not establish hosted startup/exit acceptance.
Native exit cases retain bounded `*-sqlite-exit-<case>-readiness.json` sidecars
even when window discovery or fixture readiness fails. They record the case,
parent/child stages, actual process exit/code, available child stderr/error and
observation-read failures; unknown live-host outcomes remain explicit. Child
stages are capped at 24 and error text at 8192 characters with paths redacted;
these observations contain no editor bodies or backing-store paths. Diagnostic
write/report failures cannot replace the original case failure.

Startup restore observation likewise accepts an absent/offscreen recovery label
only after the requested folder, Ready activity and current validation agree;
no selected Person or visible recovery error is accepted. Its headless replay
covers the collapsed-label hosted failure and rejects wrong-folder, pending and
stale observations without retrying Open. Failed-startup smoke still requires
the actual visible error and enabled Retry/Choose recovery controls.
The exact opened folder is read from the quiet Workspace label's accessibility
description; the full copyable path is display text in Settings. Restore checks
still require the exact folder and live current-data/owner/recovery observations
within the same bounded wait, not just a successful folder-name match.

## Settings, truthful feedback and picker assertion boundaries

Hosted Workspace-path primary-click nonmutation observes the subsequent physical
right-click opening its Copy path menu before reading the sentinel, without invoking
Copy or reseeding the clipboard. That ordered input proves WPF processed the primary
click, rather than sampling early.

The compiled chrome contract keeps folder management and Import inside Settings,
the quiet Workspace name non-interactive, and search outside the People-only view.
Settings occupies the bounded main work area rather than growing above it;
operational feedback and startup recovery stay outside that scrolling view.
It requires one Open Workspace action and a focusable, copyable displayed path,
without a pending-path TextBox or separate Choose action. Healthy reading collapses
the operational notice rather than keeping Ready/validated/saved furniture.
The Window's accessible ItemStatus and HelpText expose the actual existing
workflow activity and operation feedback; the Workspace label's ItemStatus exposes
actual validation/freshness. These are bound to the same detailed status, not
test-only readiness or success flags.
Notice classification uses the workflow's operation error and actual retained
local-save/recovery state, not English success-message lists or maintenance substrings.
Preferred-name cancellation and successful profile/note saves leave healthy
reading quiet. Both note sections collapse routine operation feedback while
exposing that same feedback through their surface's accessible ItemStatus.
Pending/failed drafts remain visible; independent save and preference errors,
unavailable local data, synchronization divergence and committed cleanup remain actionable.
An idle failed operation is summarized once, without an additional generic
pending row or a second copy of its technical rejection. Real in-flight work
and distinct failures still appear. Preferred-name failure keeps a short local
retained-entry message, full diagnostic HelpText and a keyboard-accessible
Details disclosure inside the same popup; reading Details does not require
an outside click. The compiled check activates that real disclosure and retains
the failed input, original saved bytes and unsafe-close state.
Hosted note waits read the real accessible feedback rather than requiring a
permanent success TextBlock. Discard checks the restored saved reading and bytes.

It also drives the actual Settings button through the WPF dispatcher and checks
logical focus on Open Workspace, including closure before a queued focus request.
The focus request waits behind layout and the current input event; the unchanged
hosted Space-key/`HasKeyboardFocus` assertion remains the physical-focus proof.
The existing internal application-construction seam substitutes only the external
folder-picker result in invisible checks. Accept/cancel, rejected invalid delivery,
retained note writing, startup-preference bytes and explicit Import-path Enter
preview all run through the actual compiled handlers and real file-backed workflow.
This does not establish native picker discovery, input, selection or cancellation.
The ordinary profile and Person-note journeys use the actual native dialog once,
with explicit selectable-folder setup, exact accepted/cancelled outcomes and
unchanged preference/writing assertions. Installed, responsive and scale callers
use that same visible picker, never a hidden direct-path adapter.

Quiet status is read through the real accessible properties above, without
expanding Details as a hidden prerequisite. Retry observation subscribes to the
same actual activity on the Window, preserving saving-to-Pending completion.

## Normal close, Cancel and informed Exit anyway

The #99 close cases use that same real-storage workflow/observable view-model
seam: both note scopes commit/reopen current writing during an older held
publication, and close readiness requires the latest actual published revision.
Cancel preserves unsubmitted profile/Preferred-name drafts, selection, exact
stalled owner writing and the original local fault without waiting for the
admitted transaction/export. Missing folders and failed exports remain distinct
from local loss; a fresh close retries publication without mutation replay.
The existing exit-publication read observer also holds that actual provider read
while the same public Workspace saves and independently reopens newer note
writing. The old publication cannot acknowledge the new head. The blocked-folder
cases count calls at the public workflow proxy and require no repeated expensive
exit verification while publication is already known to be unavailable.
The invisible compiled `--verify-exit` check (also in the existing required
`--verify-profile-refresh` command) drives the real BAML close/Cancel controls,
repeated/canceled intents, newer writing and late exports. Nine owned non-UI
child processes invoke the compiled Exit anyway action with unconfigured/failed/
hung publication, pending/stalled precommit writes, postcommit lost acknowledgment,
failed local saving, an active explicit profile Save and real divergence.
The existing pending-save case also holds a real same-process public SQLite
reader at its known-held snapshot point; actual Exit anyway must terminate both
it and the admitted writer without waiting for either disposal.
It requires actual process death without returning through secondary Closed/
disposal guards, then independently reopens and reconciles committed versus
incomplete data, retained conflict versions and unpublished work. This never
shows a window, creates a native handle or substitutes a success flag for exit.
The existing ephemeral signed/effects `--verify-update-yield` command also requires
an owned process to terminate through Exit anyway while the real signed update
command waits at its safe point behind an admitted, held SQLite transaction.
Its ordinary safe-drain and unsubmitted-Preferred-name refusal proofs remain.

The existing hosted `compatibility-settings` journey adds the native counterpart
using those actual compiled WPF processes and real SQLite/snapshot artifacts.
It observes visible enabled exit choices, exact Cancel/owner writing and Person
browsing, then actual Exit anyway process death for the complete matrix.
A separate native signed-update case requires the same forced termination
without changing the updater's preexisting safe-point/edit-disable contract.
A separate held-export journey cancels normal close, enters newer native note
writing, independently reopens it before releasing the old export, requires the
canceled process to remain open/unpublished, then requests normal close once and
requires latest publication plus normal process death. Its candidate/run/attempt/
product-hash-bound `*-sqlite-exit-lifecycle.json` and named `sqlite-exit-*` captures
are hosted-only evidence. The normal creation/reopen fixture explicitly
chooses the saved-but-unpublished exit risk before folder configuration; it still
requires all local writing to finish first. Existing rejected-close journeys now
Cancel the decision before their original retry, preserving drafts/bytes and
profile navigation Save/Discard/Stay without leaving a latent close intent.
No scenario, sample, previous assertion scope, lane budget or latency target is
removed or extended. Local compiled/process success is not hosted UI acceptance.
The existing invisible Configure/Sync contract also queues both Configure and
Join at the public workflow boundary, replaces the Workspace through actual
compiled Open controls, and requires rejection without configuring the new
Workspace or creating the captured folder. The Manager-choice and independent
note-leave contracts pump through actual `Closed`; its normal handler owns model
disposal, so a canceled close cannot be reported as completed cleanup.
The compiled close contract also holds accepted folder configuration at that
queue boundary. Cached publication cannot close the window before the operation
finishes and its resulting folder is published; Exit anyway still bypasses waits.
Waiting for window-level update/configuration/version work does not repeatedly
read or hash its already published snapshot.

## Exact publication preconditions and normal cleanup close

Fixtures expecting ordinary process exit first establish its real publication
precondition through the existing Settings Configure/Sync controls and an
independently reopened exact local/published revision. Opening Settings for this
preparation must preserve an underlying Import view and its selected owner.
Publication/close preparation preserves an already configured transport by
default; the smoke sibling folder is only for an unconfigured Workspace.
Strict normal-close preparation additionally rejects a changed configured
folder, not just a changed head. Its two-store compiled close/reopen replay
performs both normal closes on the original incoming folder and observes a
delivered continuation entering/completing the actual adoption Commit phase.
The rich normal-note phase publishes to its existing isolated transport after
the unchanged latency samples and before its original 10-second close check;
the publication stage still counts toward the whole suite budget. A pending
publication is not a missing-folder error, and neither permits substituting
Exit anyway or bypassing the normal close guard. The compiled
Rich normal-close preparation avoids a redundant Sync now only when a fresh
independent Open confirms the exact current revision is published in the intended
folder, no unpublished changes remain, and safe close is available. Otherwise
the original synchronization and 20-second observation remain. The existing
before/after identity, revision, publication and configured-folder checks are
unchanged; general incoming synchronization is never skipped by this helper.
The compiled close-preparation contract covers both already-published nonmutation
and a genuinely newer unpublished revision that still synchronizes exactly once. The compiled
`--verify-normal-close-publication-preparation` regression drives
actual Configure/Sync, a genuinely
newer Import and normal Closed with an independent complete-snapshot receiver.
It remains in the full compiled selection. The complete later rich background
export and profile phases and all original hosted exit cases remain required.
After verifying and capturing the latest rich publication in Settings, the
driver returns to browsing before the unchanged note-idle/normal-close checks.
Those checks require the actual browsing note surfaces, not a Settings-only
automation subtree. Neither local-idle criteria nor normal exit is bypassed.

The compiled local-recovery fixture uses a separate synchronization scheduling
clock, releases and joins its own admitted local save, then publishes through
the shared real lifecycle and waits for actual normal Closed before returning.
`--verify-local-recovery-cleanup` and the full selection reject a leftover logical
window/exit intent. A bare Close on an unpublished fixture is not cleanup: normal
exit correctly remains pending and can keep reading its local head during
directory removal. Production timer/disposal and forced nonjoining exit semantics
are unchanged; actual save-request and three-second clocks remain real.
The close-publication selector drives actual Closing/Closed and decision/Cancel
controls. Fresh exact published heads avoid candidate-validation/export replay;
real sibling-advanced heads reload and publish, with independent joined-data
checks. Unavailable publication and persisted known divergence remain vetoed,
without clearing retained bytes or review authorization. The existing older
export/newer writing/repeated/canceled-intent compiled regression remains.
Public real-store tests additionally cover changed receipt bytes, locked/corrupt
artifacts, unavailable local storage and an actual pre-Commit sibling transaction
whose unresolved receipt must refuse cached Ready until explicit recovery.
