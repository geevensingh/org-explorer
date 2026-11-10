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

## Update checks

**Settings → Check for updates** verifies signed project release metadata without
blocking local use. Startup checks default to enabled and can be disabled there.
Offline, rate-limit, invalid-signature and other failures are visible even with
Settings closed; a failed check never means "up to date."

This build deliberately has no production verification keys provisioned.
It reports **update verification not configured / availability unknown** and
does not contact the release host until the owner supplies a trusted app
bootstrap containing public keys. Private keys never belong in the app.
The [owner's protocol/provisioning contract](update-protocol.md) specifies that
separate requirement. Do not trust an arbitrary key downloaded alongside a feed.

Checks do not download packages, install or restart the app yet. No releases or
public CD are configured by this change. Velopack's implicit pending-package
apply on startup remains disabled. The authenticated download/apply and
local-save/restart policy (#18) and publication gates (#19) must be completed
before unattended apply or production publication is enabled.
