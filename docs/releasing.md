# Product releases

`release.yml` publishes only after a **successful `ci.yml` push run on main** in
`geevensingh/org-explorer`. It checks the run's source/repository/event and all
three required lanes plus `CI` again through GitHub. PR, fork and manual CI runs
cannot authorize production. Ordinary CI still runs for every PR/main commit,
including repository documentation and tests.

## What causes a release

The comparison is the exact source SHA in the **last successfully published**
app release's `release-provenance.json`, validated against its immutable
lightweight `v{version}` tag. Failed drafts/prereleases are not comparison
baselines. No releases means an explicit first publication, initially `0.1.0`
from the app project. Subsequent versions increment the last published patch.
Already reserved version tags are skipped, never moved/reused; failed late
attempts can therefore leave gaps and unpublished drafts. Patch exhaustion fails
for an owner-reviewed policy change, rather than inventing a fourth component.

Classification includes `src`, assets/schemas, runtime dependencies and locks,
SDK/NuGet/MSBuild inputs, the pinned packager and production release scripts/
workflow. It also reads linked shipped resources from the source projects at
**both** comparison endpoints: Markdown copied into the app counts, as does a
removed shipped resource. Unknown inputs conservatively count as product.
Simple project-property and output-copy resource paths are included; unsupported
dynamic MSBuild resource expressions fail planning instead of silently excluding
their shipped effects.
Repository-only docs, tests, skills and unrelated test tooling normally do not.
Mixed changes count. A failed product attempt followed by a docs-only commit
still includes that pending product effect and gets a new attempt.

App assembly/package/feed versions are generated together. Import schema,
Workspace format and signed-update protocol versions remain independent,
deliberate compatibility contracts; a patch release changes none of them.

## Publication gates and provenance

One non-cancelling production concurrency group covers planning, fresh scale
verification, packaging and publication. Every product attempt calls the same
`scale-verification.yml` used by nightly/manual checks, at the exact candidate:

```powershell
.\eng\Run-Performance.ps1 -ExpectedRevision FULL_40_CHARACTER_CANDIDATE_SHA
.\eng\Run-UiScale.ps1 -ExpectedRevision FULL_40_CHARACTER_CANDIDATE_SHA
```

Both jobs must succeed now. Publication downloads only this run/attempt's named
artifacts, verifies clean exact source and execution times, and revalidates the
complete named TRX/scenario results. Missing, stale, partial, skipped, failed,
cancelled or timed-out work blocks publication. Nightly success is diagnostic
evidence, not a deployment approval. Rerun **all** jobs after a failed attempt;
rerunning only the publisher cannot reuse previous-attempt scale artifacts.

The publisher checks main before planning and again before tag/draft creation
and immediately before promotion. It rechecks the successful-release baseline,
exact tag target, local inventory and GitHub-uploaded SHA256/lengths. An older
finishing candidate cannot deliberately replace a newer release/feed. GitHub
does not offer an atomic compare-main-and-publish API: the last remote check
immediately precedes promotion; the signed payload/tag still pin exact source
and bytes if main advances in that network interval. The serialization applies
to this workflow, not an owner concurrently editing GitHub Releases. Owners
must not manually move tags, replace assets or alter latest during publication.

Only a completely uploaded, verified **draft** becomes public/latest. Assets:

- Windows x64 setup, full `.nupkg`, and portable ZIP;
- `releases.win.json`, `assets.win.json`, `RELEASES`;
- unsigned `package-metadata.json` inventory;
- protocol-1 `org-explorer-update.win-x64.json`;
- `release-provenance.json`, recording source, comparison baseline, CI and fresh
  verification run IDs plus actual artifact SHA256/lengths.

The three feeds must identify the same full package/version. Legacy SHA1 is
checked for index consistency only; it is never authentication. There is no
unsigned feed fallback or second package selection.

Packaging accepts only normal product inputs. Archive checks reject Workspaces,
exports/configuration JSON, fixture/test assemblies, private-key formats and
debug material; compiled trust must byte-match the committed production public
resource and contain no controlled-effects marker. The private key stays in the
signing process, not a file, build property, artifact, log or package.

Tags and assets are never force-updated. An upload or stale-main failure leaves
an unpublished draft/tag; the next successful main attempt skips reserved tags
while still comparing against the last **published** product. Owners may inspect
and remove obsolete drafts after investigation, but automation does not overwrite
or promote them. A legacy/ambiguous public release without valid provenance
blocks planning and needs owner reconciliation, never an adjacent-commit guess.

## Owner prerequisites (not provisioned by this change)

Before real publication, the owner must:

1. Securely generate and retain the production NIST P-256 key outside repository,
   session and client outputs, using the separately supplied owner-run setup.
   This implementation does not generate/acquire a production key.
2. Set Actions secret **`ORG_EXPLORER_UPDATE_PRIVATE_KEY_PKCS8`** to **base64 DER
   PKCS#8** private-key bytes (not PEM, a certificate or public SPKI).
3. Set public Actions variable **`ORG_EXPLORER_UPDATE_KEY_ID`** to the active,
   case-sensitive embedded public-key ID.
4. Commit the matching **public-only** SPKI configuration in
   `src\OrgExplorer.Desktop\update-trust.json`, reviewed as part of the trusted
   bootstrap. Its `keys` array is currently empty. The signer derives public
   SPKI from the supplied private key and rejects absent, malformed, wrong-curve,
   unembedded or mismatched configuration before creating public artifacts.
   Adding the owner's public resource needs no signer-code change.
5. Merge the reviewed workflows to main so GitHub registers `workflow_run` and
   the reusable paths; allow Actions read and publication-job contents-write.
   Existing branch protection/required main CI is not bypassed or reconfigured.
6. Arrange metadata-expiry operations below. Project-key authentication is not
   Windows publisher signing or initial-installer reputation. Never disable
   Windows protections, import a self-signed certificate or acquire a paid
   certificate as a hidden prerequisite.

### Expiry and safe renewal policy

Manifests are valid for **14 days**, within protocol 1's 31-day maximum. There is
**no automatic renewal scheduler in this change**. Monitor the published signed
expiry and arrange renewal before it; if that does not happen, installed clients
correctly report expired/unverified metadata, not "up to date." Local use remains
available. Do not extend validity past 31 days or fabricate docs-only app releases.

Safe no-product renewal must reauthenticate the original full package's
SHA256/length and immutable `v{appVersion}` URL with the owner-held matching key,
fresh issuance and bounded expiry. It must not move that app tag, change package
bytes, introduce downloaded trust, or change the app/data/protocol version.
Use a separately reviewed **immutable metadata-only publication** with an
explicit original-app reference and identical original installer/full/portable/
feed assets if it becomes the GitHub `latest` download surface; never delete and
replace a public signed asset in place. Serialize it with product publication,
recheck the latest product and run both fresh exact-product-candidate suites.
Renewal must not become the product-comparison baseline.

That metadata-only promotion/provenance convention is **not implemented or
enabled here**: the current publisher deliberately rejects non-app public tags
as ambiguous. Do not manually create such a latest release and assume automation
will understand it. Until that separate reviewed operational mechanism exists,
an owner must plan a genuine product release before expiry or accept visible
fail-closed update unavailability. This is an explicit production-operating
limitation, not a credential workaround, silent renewal or rotation.
Old-key bridge/rebootstrap policy remains [unchanged](update-protocol.md#rotation-and-authenticated-automatic-application).

## Safe hosted proof before merge

The existing registered CI workflow has a second **manual-only, default-false**
bridge. On a pushed candidate branch, with the repository-authorized account:

```powershell
gh workflow run ci.yml --repo geevensingh/org-explorer --ref YOUR_PUSHED_BRANCH -f run_release_verification=true
gh run list --repo geevensingh/org-explorer --workflow ci.yml --event workflow_dispatch --limit 5
gh run view YOUR_RUN_ID --repo geevensingh/org-explorer --json headSha,status,conclusion,jobs
gh run watch YOUR_RUN_ID --repo geevensingh/org-explorer --exit-status
```

Do not also set the older scale flag: release verification already calls both
exact commands. Check `headSha`, all three ordinary CI lanes, both fresh scale
jobs and the packaging job. `CI` alone still attests only its three lanes.
The bridge uses no production secrets and has no contents-write permission.
It packages real runtime-included fixture binaries, signs with an in-memory
ephemeral key, validates all feeds/manifests, then rebuilds and checks production
trust isolation. It never installs, downloads a production update, or publishes.
Only named JSON evidence is uploaded; test packages/private material are not.
This is not owner-key provisioning, public release, Windows publisher trust,
Windows 11 client certification or installed lifecycle evidence.

Local non-UI contract checks:

```powershell
.\tests\Release.Tests.ps1
.\tests\Release.Gates.Tests.ps1
.\tests\Release.Publication.Tests.ps1
.\tests\Release.Payload.Tests.ps1
.\tests\Release.Workflow.Tests.ps1
```

Publication-effect tests use controlled fake GitHub responses and bytes, not
real public releases. Actual UI/scale/installer verification remains hosted only.
