# Workspace opening experience

Status: approved design, not implemented. Shared understanding confirmed on
2026-11-27 through `/grill-with-docs`.

## Problem and scope

During opening, the app currently shows normal Explorer chrome alongside an
"Opening Workspace..." notice. Selected controls are disabled, but the screen
still promises browsing that is not available. Essential progress is absent
and the operational Details expander is collapsed.

Replace that in-between presentation with an explicit readiness boundary.
Cover automatic reopening at launch, manual opening and switching, recovery
reopening, and Create, which shares the opening pipeline. Background work in
an already usable Workspace is outside this redesign.

The existing [Workspace](../GLOSSARY.md) definition remains unchanged. This
design does not introduce glossary terms or require an ADR.

## Views and transitions

| Situation | Presentation and outcome |
| --- | --- |
| No remembered Workspace on first launch | Settings with Open/Create guidance; neither loading nor an empty Explorer. |
| Automatic reopening is planned | Loading is present from the first normal app frame; do not flash the empty Explorer first. |
| Opening, switching, recovery reopening, or creating | Dedicated loading view replaces all normal Explorer chrome. |
| Cancellation requested | The same gate remains, with "Cancelling..." and safe-stop progress. |
| Validated local data is ready to browse | Show the normal Explorer, including its ordinary no-Person-selected view when appropriate. |
| Opening fails | Return to Settings with a persistent failure notice and recovery actions. |
| Cancellation is observed and finishes | Return to Settings with a neutral cancellation notice and an action to open the attempted Workspace again. |

Folder selection and existing unsaved-edit decisions happen before the opening
gate begins. Cancelling the folder chooser or refusing those decisions leaves
the current Workspace and view untouched.

Readiness means validated local data and its browsing view are available, not
that every write or synchronization is available. A validated read-only
Workspace enters the normal Explorer with its access limitation clearly
indicated. Background synchronization must not hold the loading view.

## Loading presentation

Use a calm, centered panel on the app's normal background, with an
"Opening workspace" heading and a modest activity indicator. Do not use an
error-colored banner, skeleton People screen, or branded splash animation.

Show the attempted Workspace's identity/location, current human-readable step,
completed steps, and elapsed time without expanding Details. Report actual
operation stages, such as checking the location and format, reading and
validating saved data, and preparing local access. Show creation work only when
performed. Stage completion must reflect real work, not a timer advancing a
decorative checklist.

At 10 seconds of elapsed opening time, add the neutral message "Opening is
taking longer than usual". Continue reporting elapsed time and actual progress.
Do not invent a percentage, completion estimate, timeout, retry, or claim that
the operation is stuck. While cancellation is pending, explain safe-stop
waiting rather than claiming that cancellation is complete.

The loading view exposes Cancel, Details, and Copy diagnostics. Search,
Settings, Workspace switching, editing, and other Explorer controls are absent,
including from keyboard navigation. After Cancel is requested, prevent
duplicate cancellation requests and competing opening operations. Keep the
existing guarded window-close/exit behavior; closing is not instant cancellation.

Technical Details describe the current attempt and its real steps, timings,
outcome, and available error information. Copy diagnostics copies that evidence,
not Person records, notes, or credentials. Essential progress remains outside
Details; this is not a general log-viewer redesign.

## Safe cancellation and outcome arbitration

Introduce cooperative cancellation through the opening pipeline; the current
pipeline has no such contract. Observe requests at safe checkpoints in read and
validation work, including long processing loops. An indivisible operation
already executing may need to finish before a checkpoint can observe Cancel.
Never kill a worker or interrupt an unsafe write to simulate cancellation.

Keep the opening gate until an observed cancellation has stopped the attempt
and settled its attempt-scoped transactions, workers, and resources. Do not
allow a second open to race unfinished cleanup or let stale progress/results
from an old attempt affect another Workspace.

Cancellation preserves data and consistency; it does not promise byte-for-byte
rollback of completed durable work:

- Existing opening may establish SQLite WAL after validation. Let an
  already-started journal-mode change settle safely; do not undo it.
- Create writes a format discriminator before creating the SQLite schema and
  initial metadata. Treat that creation sequence as a safe-stop boundary:
  once started, finish it safely before honoring cancellation, rather than
  introducing a cancellable gap that leaves a discriminator without a store.
- A cancelled attempt may therefore leave a durably created Workspace on disk.
  Settings must explicitly explain that result when creation actually
  succeeded. Do not automatically delete the Workspace or its folder.
- A real I/O, validation, or creation failure remains a failure, even if Cancel
  was also requested. Do not disguise an incomplete creation as a successful
  cancellation or claim that a leftover folder is valid without evidence.

A cancellation request is not itself a terminal outcome. **Prefer success if
opening finishes before cancellation is observed.** In that case show the
ready Workspace normally, even if "Cancelling..." was briefly displayed.
If cancellation is observed first, stop safely and return to Settings instead.
Publish one terminal outcome; a stale Cancel action must not cancel a
Workspace that has already become ready.

For an actually cancelled attempt, retain the remembered-Workspace preference,
show "Opening cancelled" with the attempted location and an open-again action,
and do not automatically reopen the previous Workspace. A successful open
that beats late cancellation follows the ordinary successful-open preference
behavior.

## Failure and recovery in Settings

On failure, Settings shows a persistent notice containing the attempted
Workspace, a plain-language reason, the failed step, Retry, Choose another
workspace, and expandable technical Details. Returning to Settings must not
silently discard the explanation.

Preserve existing storage and switch semantics. Once a switch enters the
workflow, the old in-memory Workspace is unloaded; failure does not restore it.
The user can retry the attempted Workspace or choose another, including
reopening the previous one. Failed or cancelled attempts do not replace the
remembered-Workspace preference.

Preserve [format and compatibility protections](workspace-format.md):
automatic restore remains ExistingOnly; missing, corrupt, incompatible, or
interrupted-creation data is not silently replaced or presented as empty data.
This work does not introduce migration, automatic repair, storage replacement,
or retention of the previous Workspace in memory.

## Acceptance boundaries

The corresponding implementation must demonstrate:

- First-run Settings and first-frame automatic-reopen loading are distinct;
  all opening entry routes replace normal Explorer chrome and prevent
  keyboard or pointer access to unavailable controls.
- Real current/completed steps, Workspace identity, and elapsed time remain
  visible with Details collapsed. The slow message appears at 10 seconds,
  not before; no percentage, estimate, timeout, or automatic retry is fabricated.
- Cancel is observed during long read/validation work. Pending cancellation
  remains gated until safe stopping and cleanup complete, with no competing
  opens or stale attempt updates.
- Cancellation before creation starts avoids creation; cancellation after it
  starts respects the complete creation boundary, retains any valid created
  Workspace, and explains the durable result. Journal-mode changes also
  settle safely. Actual storage failures still produce failure feedback.
- Both late-success and observed-cancellation outcomes are deterministic and
  exclusive. Ready, failure, and cancellation restore the appropriate view,
  actions, and remembered preference without automatically reopening the old
  Workspace after cancellation.
- Failure recovery retains the attempted Workspace and reason in Settings;
  Retry and Choose another workspace actually start the intended new attempt.
- Validated read-only data becomes browsable with an explicit limitation.
  Background synchronization does not prolong opening.
- Existing unsaved-edit guards, chooser cancellation, compatibility rules,
  failed-switch semantics, and guarded exit behavior are preserved.

Use the lowest-cost agreed surfaces in [Testing](testing.md): non-UI workflow
and filesystem cases for progress, arbitration, durability, and preferences;
actual compiled WPF contracts where appropriate; hosted isolated Windows for
first-frame presentation and pointer/keyboard/focus acceptance. Preserve
required smoke lanes and existing explicit scale selection. Local non-UI
results alone do not prove rendered UI or large-population UI acceptance.

## Reconciled baseline and delivery

The design was checked against freshly fetched `origin/main` at
`65f50a43be06087cde7301ac7331c3e668caa2ac`, with no ahead/behind commits or
divergence. The screenshot's partially exposed Explorer matches the current
opening presentation. Existing opening has a generic activity signal rather
than staged progress, no cooperative cancellation, and the creation and WAL
boundaries described above. These are required changes, not capabilities
assumed to exist already.

This design preserves the local-first boundary in
[ADR 0008](adr/0008-local-transactions-independent-sync.md), rather than the
superseded readable-file assumptions in ADR 0007.

Keep the focused design commit pushed and unmerged until delivery with its
corresponding implementation. The producer returns the immutable source/base
SHAs and exact changed file in the
[documentation handoff](agents/documentation-handoff.md). Integration owner
and branch are pending and must be established, with the documentation
incorporated, before implementation or child dispatch.
