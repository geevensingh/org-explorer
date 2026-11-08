# Windows installer, updater, and signing research

Research used primary documentation and official source during the design
interview. These findings inform a selected approach; no package was installed,
key provisioned, release published, or OS trust setting changed.

## Selected approach

Use Velopack with .NET 10/WPF and a public GitHub Releases feed. The documented
.NET integration supports startup/button checks and download/apply/restart, while
the Windows setup model supports per-user installation. The framework does not
own private workspace files or their migrations.

- [Velopack C# integration](https://docs.velopack.io/getting-started/csharp)
- [Windows packaging](https://docs.velopack.io/packaging/operating-systems/windows)
- [Update integration and restart APIs](https://docs.velopack.io/integrating/overview)
- [Lifecycle hooks and their bounded execution windows](https://docs.velopack.io/integrating/hooks)
- [GitHub Actions publishing](https://docs.velopack.io/distributing/github-actions)

The app must finish local saves/import application before requesting an update
apply. Framework hooks are not permission to kill a process with unsaved data.
Any implicit pending-update application behavior must remain behind the app's
authentication and restart-safety policy.

## GitHub feed

Velopack documents `GithubSource` for GitHub hosting, including public access
without client credentials. Publishing requires its feed/index and package
assets, not just a setup executable. Pin packaging and client versions together
and verify the actual artifact set during implementation.

- [Update sources](https://docs.velopack.io/integrating/update-sources)
- [Distribution and release indexes](https://docs.velopack.io/distributing/overview)
- [Channels](https://docs.velopack.io/packaging/channels)

Unauthenticated GitHub REST requests have a shared per-IP limit, which matters
behind a corporate NAT. Do not embed a personal access token in a public client.
Cache/coalesce checks, handle rate limits visibly, and keep offline app use
independent of feed availability.

- [GitHub REST rate limits](https://docs.github.com/en/rest/using-the-rest-api/rate-limits-for-the-rest-api)

`SimpleWebSource` can mechanically construct static requests through GitHub's
`releases/latest/download` links without REST API discovery, but a changing
"latest" label can race feed and version-specific asset requests. It is not the
documented GitHub source configuration and does not by itself provide access to
arbitrary older release assets or prerelease channels. Do not switch to it merely
to claim an unlimited or fully reliable update service.

- [GitHub release permalinks](https://docs.github.com/en/repositories/releasing-projects-on-github/linking-to-releases)
- [Velopack SimpleWebSource source](https://github.com/velopack/velopack/blob/a0345e08/src/lib-csharp/Sources/SimpleWebSource.cs)

## Integrity is not publisher authentication

The reviewed Velopack runtime verifies package size and feed-declared SHA256 when
available, otherwise SHA1. That does not establish the identity of the publisher:
the reviewed update path does not perform an approved-publisher Authenticode check.
Treat these source-level findings as version-specific and reverify the pinned
runtime during implementation, not as an eternal promise.

- [Velopack C# UpdateManager source](https://github.com/velopack/velopack/blob/a0345e08/src/lib-csharp/UpdateManager.cs)
- [Velopack Rust update manager source](https://github.com/velopack/velopack/blob/a0345e08/src/lib-rust/src/manager.rs)

The selected project-key manifest adds a separate authorization layer. Standard
asymmetric signatures establish authorization by the key the app already trusts;
they do not verify a legal publisher name to Windows. Verify the signed manifest,
asset binding, and downloaded bytes before allowing a framework apply path.
Installer rollback does not restore the externally synchronized workspace.

## Publicly trusted Windows signing alternatives

MSIX requires a signed package trusted by the target device. App Installer also
adds deployment/update integration and WPF restart responsibilities. This was not
selected over the Velopack model for v1.

- [MSIX signing requirements](https://learn.microsoft.com/windows/msix/package/signing-package-overview)
- [Non-Store update and restart integration](https://learn.microsoft.com/windows/msix/non-store-developer-updates)

Azure Artifact Signing is a paid public-trust route with identity and geographic
eligibility requirements. Basic pricing was discussed as roughly USD 10/month;
the live regional price was not independently verified and is not a project
budget commitment.

- [Official pricing](https://azure.microsoft.com/en-us/pricing/details/artifact-signing/)
- [Identity validation and eligibility](https://learn.microsoft.com/en-us/azure/artifact-signing/quickstart)

SignPath Foundation offers free signing to approved qualifying open-source
projects, with licensing, project/security, and signing approval conditions. A
public repository alone is not eligibility, and approval requirements may
conflict with fully unattended per-product-commit publication. No application or
approval was obtained.

- [SignPath Foundation terms](https://signpath.org/terms.html)

Unsigned or low-reputation executables may be warned about or blocked by Windows
policy. Even signing does not guarantee absence of reputation warnings. This
design introduces no warning bypass, self-signed trust-store installation, or OS
trust weakening.

- [Windows signing and SmartScreen reputation](https://learn.microsoft.com/windows/apps/package-and-deploy/smartscreen-reputation)
