# Person-text and Preferred-name submission

[Testing index](../testing.md)

Read this for Preferred-name defaults, explicit Save/Enter, original bases, stale retries, owner browsing and field-scoped cancellation.

Related guidance: [Whole-profile editing](person-profiles.md#profile-editing-and-cancellation), [independent note writing](note-writing.md), and [required popup/keyboard selections](../testing.md#real-desktop-execution).

## Submission and immutable bases

Preferred-name Save/Enter submits the displayed field's immutable Person base
through `SavePersonTextDraft`; typing, blur, navigation, Settings and generic
update/close draining do not submit it. `DiscardPersonTextDraft` cancels only
that field's unsaved failure, never other fields, profile sessions, quick actions,
notes, Import, local recovery or retained synchronization divergence.
Fresh compact entries display Preferred name, otherwise Directory name, otherwise
empty, without retaining or publishing that default. Explicit Save/Enter captures
the submitted text and original base before asynchronous work begins, so an
untouched default survives pending or rejected submission and owner browsing.
Explicit submissions remain retained even when their text equals the original
Preferred name; only unsubmitted original-value typing is retired as clean.
Save/Enter clears a valid
name equivalent to the current accepted Directory name, ignoring capitalization
and outer whitespace only; distinct text retains its exact storage behavior.
Public Person-text cases cover the matching/missing-name matrix, meaningful
internal spacing/spelling/accent differences, current Directory-name changes and
independent writing. Real-folder cases reopen custom names, equivalent clears,
later Directory headings and private context, and keep original-base failures and
retries stale even when the submitted name would otherwise clear.
Changed-field delivery rejects save/retry;
other-field delivery is retained, and success returns the exact saved base for
newer typing rather than rereading a possibly newer provider revision.

## Older completion and newer writing

The invisible `--verify-preferred-name-retention` check also runs in that existing
profile-refresh selection. It holds the real SQLite commit through the shared
save-boundary observer, drives enabled compiled Save/Cancel and pending A-B-A
Person selection, and requires exact newer writing to survive the older
completion until its own Save or Cancel. Returning before completion must use
the exact accepted base for the next explicit Save, both with and without
earlier Cancel or newer typing during the held write. Independent SQLite reopen
checks the actual saved owner/value and excludes implicit submission of newer
text. A genuine other-client same-field change must still reject the stale
entry and retain its exact writing/error without overwriting external data.
These checks use the existing bounded dispatcher/commit observations and never
show a window or create a native main-window handle; they are not hosted
interaction or latency acceptance.
The untouched-default regression holds the actual local read to check submitted
retention before completion, then browses A-B-A after a same-field rejection.
Both an unset Preferred name and an existing Directory-equivalent Preferred name
retain the explicit submission without retaining an untouched editor on opening.
Exact submitted text, original base and the retained failure remain available
for stale retry until deliberate cancellation; reopening preserves external data.

## Incoming owner changes

`--verify-preferred-name-snapshot-deferral` isolates the full profile-refresh
selection's compiled incoming-archival/Preferred-name deferral case, including
real source/peer snapshot Join and exact retained writing until Cancel. Peer
fixture folders use compact GUID basenames instead of repeating scenario names,
leaving room for native SQLite's nested validation/journal paths without
changing the working directory or global temporary-folder configuration.

## Compiled controls and native popup evidence

The same invisible regression drives the actual Preferred-name Save/Cancel
controls: fresh Preferred/Directory/empty defaults and untouched cancellation
leave saved names unchanged, explicit defaults reopen with the expected clear or
preserved name, and Directory-name changes cannot reseed retained text or its base.
Typing does not publish; failed Save retains
its input/local error across an unrelated Favorite success; Save retry retains
the original base across external delivery, and deliberate cancellation permits
a new entry against the delivered value. Real held profile/note publication
proves that cancellation closes without blocking behind admitted I/O, cannot
revoke it or permit premature close, and newer input waits for another explicit
Save, including when the admitted submission clears an equivalent name.
The Popup is never opened while the window is invisible. Its actual
discovery, focus and physical Enter/Escape/outside-click behavior remain hosted
proof, not an inference from these controls.
The existing native profile journey also focuses the failure Details header,
expands it with one Space key and captures the technical reason inside the same
open entry, requiring retained input, unsafe state and unchanged Workspace bytes.
No second disclosure action or new scenario is used.
An independent invisible-window fixture delivers a valid replacement Workspace
without the Preferred-name owner, requiring retained input/owner, unavailable
feedback, disabled Save and enabled Cancel. Cancellation leaves all delivered
bytes intact. Removing and restoring the owner with a changed Preferred name
must still reject Save against the entry's original base.

These checks use the existing observable People DataContext, actual control
activation/enablement and public workflow refresh notifications, never private
window methods or work-completion fields. The generic update-drain refusal is
proved by the signed update command in `--verify-update-yield`, not by invoking
the private handoff implementation.
