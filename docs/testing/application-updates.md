# Application-update lifecycle and Workspace preservation

[Return to Testing](../testing.md).

Open this reference when changing signed update work-draining, safe restart, installer handoff or installed-update Workspace preservation.

For retained local failures and receipts, read [storage and recovery](workspace-storage-recovery.md). For close/Exit anyway interaction, read [opening and exit](workspace-opening-exit.md). For Person/profile/note submission rules, use [Testing placement](../testing.md#placement). Required update-settings scenario selection and desktop prerequisites remain in [real desktop execution](../testing.md#real-desktop-execution). For release/manual procedure, use [release verification](../testing.md#active-pre-publication-integration). The [update protocol](../update-protocol.md#controlled-verification) supplies supporting trust and transport rules.

## Actual signed commands, dispatcher yield and final durable bytes

The compiled profile-refresh selection is the subject of the following
note-flush and signed-update observations.

It also exercises the existing public note-flush boundary after a failed save,
successful retry and newer writing while Settings hides the editor. With the
lane's ephemeral trusted-update build, it additionally drives the actual signed
Check for updates command through in-process WPF button providers: failed writing
blocks handoff, retry and newer writing reach the controlled installer effect,
and that effect observes the final file hash. These calls still never show/focus
the window, create its native handle, or use an external UI Automation client;
they do not replace the real hosted update-settings journey.
`--verify-update-yield` is a non-UI compiled-window regression in the required
desktop lane, within its existing budget. It requires the ephemeral
trusted-update/effects build and does not replace a desktop scenario.
It composes the actual window with the real file-backed workflow through an
internal application-construction seam and the same held note publisher used
by persistence tests. Observing the real Import control becoming disabled for
handoff queues one note save at Normal priority for the first dispatcher yield,
and waits for publication to
reach the hold. A ContextIdle observer requires the update to remain pending,
with unchanged saved bytes and no early handoff, before releasing publication.
The same regression repeats with a deliberately queued Preferred-name Save
command and held real profile publication. Merely unconfirmed Preferred-name
typing is separately rejected by the actual signed Check for updates command,
with unchanged Workspace bytes and no installer effect; only explicit Save admits it.
The original single update action must then hand off the final durable bytes
and matching file hash; there is no update retry. The shared work drain
rechecks its gate after dispatcher settling and waits again for an admitted
save, while existing failed-save, profile/draft and final restart guards remain
active. This regression never shows/focuses a window or creates its native
handle.

## Filesystem update effects and unconfirmed restart

The filesystem update tests exercise actual cryptography, staged files, HTTP,
Velopack integrity/explicit launch arguments, real pending Import publication,
failed Person/Team saves, current-format restart, unsupported-data
nonmutation and durable unconfirmed-restart recovery. Only actual external
transport/process effects and clock are controlled. Neither a success flag nor
a per-record repository is an update safety seam.

## Hosted authenticated installation and automatic restart

Actual authenticated install/restart is additionally covered by the hosted-only
`installer-verification.yml` workflow. Its two real self-contained fixture
versions embed ephemeral public trust and a build-time external transport
adapter; the normal verification/workflow and actual Velopack installer are
unchanged. It requires the final running version, automatic same-Workspace
reopen, pre-handoff file hashes, retained final WPF writing, unauthorized-pending
protection, and both owned uninstall-tree/directory-absence barriers. Missing
fixture or automatic-update evidence fails the hosted lifecycle. The final
production build rejects test trust/effects; always-run cleanup removes private
fixture material. This is not production-update/public-key provisioning evidence.

## Versioned installed-update preservation oracle

The installed authenticated-update boundary also captures versioned preservation
evidence before handoff and after the restarted app closes normally. Exact
Workspace identity/local head/folder, complete public Person/Archived/profile/
note/history/frozen-context/photo state and no-op previews of both known fixture
Imports must match. Replay inputs are reconstructed outside the private Workspace;
capture never calls Sync or publishes. Normal close must publish that exact head.
Only the exact SQLite database/WAL/SHM filenames may differ physically across
this application lifecycle; every other private file remains byte-identical.
Original unpackaged safe-point byte checks and nonmutating installer/uninstall
all-file checks remain strict. Both receipts and changed filenames are retained
for diagnosis. This is a controlled two-import fixture oracle, not arbitrary
accepted-import enumeration. Normal production builds exclude these oracle types.

