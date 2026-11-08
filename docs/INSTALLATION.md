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
Future project-key update authentication is not Windows publisher verification.
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

This installer slice does not publish releases, configure public CD, embed
credentials/private keys, fetch an update feed, or apply updates unattended.
Velopack's implicit pending-package apply on startup is disabled. Authenticated
update manifests and the local-save/restart policy must be completed before any
unattended apply or production publication is enabled.
