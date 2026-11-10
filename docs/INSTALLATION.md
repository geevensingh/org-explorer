# Install Org explorer on Windows

Org explorer targets **Microsoft-supported Windows 11 x64** servicing versions.
The setup enforces the Windows 11 minimum (build 22000) and x64 architecture;
an end-of-support Windows build is not made supported merely because setup runs.
The installer includes the .NET 10 desktop runtime. No separate runtime download,
administrator elevation, company account, or application backend is required.

## Initial trust

This initial installer is **unsigned**. It does not identify a publicly verified
legal publisher, and Windows SmartScreen, antivirus, Smart App Control, or
organization policy may warn about or block it. Do not disable/bypass protections,
install a self-signed trust certificate, or change organization policy to run it.
If your device requires approved signing, stop and obtain an approved installer.
Project-key update authentication is not Windows publisher verification.
Even a signed installer is not a promise of SmartScreen reputation.

## Installation and your Workspace

Run `geevensingh.OrgExplorer-win-Setup.exe` on an appropriately trusted device.
Velopack installs for the current user under
`%LocalAppData%\geevensingh.OrgExplorer`. It creates ordinary shortcuts and an
Apps & features uninstall entry, then launches the real WPF application.
Silent installation does not launch; start the **Org explorer** shortcut or the
stable root launcher Velopack creates.

Choose your private **Workspace** folder separately, for example a folder under
your externally synchronized documents. The app rejects a Workspace inside the
installation, the installation root itself, or a folder containing it (including
resolved folder links). People, notes, photos, and history are never packaging
inputs. Reopen the same chosen folder after reinstalling or replacing the app.
The installer does not select/move that folder and uninstall does not delete it.
The optional portable bundle follows the same independent-Workspace rule.

Close the app normally and resolve any failed saves before manual repair,
replacement, or uninstall. Installer hooks do not flush another running app's
drafts, and forcibly terminating it is not a safe automatic-update policy.

An installation replacement changes app binaries, **not** Workspace format or
Workspace migration history. Installer rollback is not Workspace rollback.
External folder providers still own synchronization and conflicts.

## What is and is not released here

The local package set contains setup, a full update `.nupkg`, the Velopack JSON
feed (`releases.win.json`), generated `assets.win.json` and legacy `RELEASES`
indexes, a portable ZIP, and an unsigned
`package-metadata.json` identity/hash inventory. The channel is `win` and platform
is `win-x64`; the build version is shared by the app, package and feed.
These hashes establish integrity against the supplied inventory, **not**
authorization by a trusted project key.
An automatic product release additionally includes the authenticated
`org-explorer-update.win-x64.json` and exact-source `release-provenance.json`.
Those publication gates and owner prerequisites are described in the repository's
`docs\releasing.md`; local packaging alone is not public publication.
No actual production release has been published yet. Neither controlled installer
verification nor this Windows 11 target is Windows 11 certification.

## Update checks

**Settings → Check for updates** verifies signed project release metadata without
blocking local use. Startup checks default to enabled and can be disabled there.
Offline, rate-limit, invalid-signature and other failures are visible even with
Settings closed; a failed check never means "up to date."

Production builds embed the **owner-provided public verification key**. Private
signing keys and release credentials are not shipped in the app. This configured
trust authenticates project updates, not a Windows publisher or the initial
installer's reputation. The [protocol/provisioning contract](update-protocol.md)
defines that trust; an arbitrary key downloaded alongside a feed is not trusted.

Authenticated automatic download, installation and restart are implemented:
an authorized newer release downloads automatically. Signed length/SHA256,
fresh authorization expiry and Velopack
package integrity must all pass. Pending local saves and Import application
finish before installation/restart; failed writing is retained and blocks exit.
Resolve its visible retry/discard guidance, then check again. External folder
sync does not delay a safe local update.

The app reopens the same independent Workspace and uses its normal supported
automatic conversions/recovery originals. An unsupported Workspace remains
write-blocked with Settings/update access. Download, installer-start and recovery
failures stay visible. A durable restart receipt confirms the running target
version, not merely an installer launch; an unconfirmed old-version restart
pauses automatic retry to prevent a loop. A failed relaunch is also reported
when the app is next opened manually.

Velopack's implicit pending-package apply on startup remains disabled, and no
cached package alone authorizes installation. Standard Windows protections
remain in force. Automatic product-impact publication and signed metadata renewal
are implemented. Renewal refreshes authorization for the unchanged app/package;
it does not invent a newer app version or replace installed public trust. Real
publication still requires matching owner-controlled signing credentials,
successful trusted main CI and fresh exact-candidate scale checks. Public trust
configuration is not evidence that these production publication gates have run.
