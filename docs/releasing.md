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
6. Enable and monitor the daily metadata-renewal schedule below. Project-key authentication is not
   Windows publisher signing or initial-installer reputation. Never disable
   Windows protections, import a self-signed certificate or acquire a paid
   certificate as a hidden prerequisite.

### Expiry and safe renewal policy

Manifests are valid for **14 days**, within protocol 1's 31-day maximum.
`release.yml` evaluates renewal **daily at 09:17 UTC** once registered on main.
With no published app it does nothing: it cannot invent the first product release.
With more than seven days of authenticated validity remaining it also does
nothing. At seven days or less, including recovery after expiry, it renews
signed metadata without changing the app, Import, Workspace or protocol version.
Repeat evaluation of a successful renewal is a no-op until its next due window.

Renewal creates an immutable `metadata-v{appVersion}-{runId}-{attempt}` release
and lightweight tag pointing at the **original app source**, never current-main
binaries. The new signed payload still says `v{appVersion}` and pins the original
full package's exact versioned URL, filename, length and SHA256. Only issuance,
expiry and a signer already trusted by that original app may change. The original
app tag, package and all published assets remain untouched.

The latest-download bootstrap remains useful: setup, full package, portable ZIP,
all three Velopack indexes and `package-metadata.json` are copied byte-for-byte
from the original `v{appVersion}` release. Only the envelope and
`release-provenance.json` are new. Renewal provenance explicitly links the
original release ID/provenance SHA256, app source/version/tag/CI, current trusted
main controller/CI, preceding publication and fresh verification run.
Product-effect comparisons and generated app versions always use the last actual
app release, not intervening metadata releases.

The same non-cancelling production concurrency group covers product publication
and renewal, including fresh gates. Every due attempt freshly executes **both**
scale suites at the **original product SHA**; earlier release/nightly artifacts
cannot approve renewal. The controller must itself be current main with
successful required push CI. Original successful CI, tag/provenance, all remote
asset lengths/digests, signed original/current metadata and GitHub latest are
revalidated. Immediately before promotion it checks the controller and original/
latest snapshot again. Main moving, a competing product/renewal, missing/failed/
skipped scale work or partial upload leaves latest unchanged and any draft
unpublished. New daily runs/whole-run attempts allocate distinct metadata tags;
no force update, asset replacement or collision recovery overwrites anything.

The signer is checked against the public resource at the **original app commit**,
not a newer main resource. The downloaded original package's actual embedded
trust must match that source configuration (ignoring JSON formatting/line endings),
with no controlled-effects marker. The selected owner secret must derive a public
SPKI already embedded there. Current-main keys cannot substitute downloaded trust;
an unknown/new active key fails actionably and requires the ordinary bridge/
rebootstrap policy, not automatic rotation.

Scale fixtures persist an ordinary isolated `checkOnStartup: false` preference
before either scale scenario can launch a normal app. This prevents a historical,
production-trusted app from checking/downloading production updates during CI.
It changes no product default, runtime trust or test success path. The required
`update-settings` and installed-update scenarios are unchanged. Historical product
candidates lacking this runner regression are refused; ship a genuine bridge
product release rather than patching an historical candidate or weakening
exact-source verification.

Monitor schedule failures and signed expiry. GitHub schedules can be delayed or
disabled by repository inactivity/Actions policy; missing keys, unavailable
original provenance/assets, stale main or failed gates remain visible failures.
If renewal cannot complete before expiry, clients correctly report unverified
availability while local use remains available. Do not extend validity past
31 days, fabricate a docs-only app version, replace a public manifest in place,
or waive gates to hide that outage.
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
ephemeral key, validates all feeds/manifests and performs a genuine metadata-only
renewal of that exact package with identical bootstrap bytes and unchanged app
version. It verifies repeat evaluation is a no-op, then rebuilds and checks production
trust isolation. It never installs, downloads a production update, or publishes.
Only named JSON evidence is uploaded; test packages/private material are not.
This is not owner-key provisioning, public release, Windows publisher trust,
Windows 11 client certification or installed lifecycle evidence.

Local non-UI contract checks:

```powershell
.\tests\Release.Tests.ps1
.\tests\Release.Gates.Tests.ps1
.\tests\Release.Publication.Tests.ps1
.\tests\Release.Renewal.Tests.ps1
.\tests\Release.Payload.Tests.ps1
.\tests\Release.Workflow.Tests.ps1
.\tests\Update.TrustLifetime.Tests.ps1
```

Publication-effect tests use controlled fake GitHub responses and bytes, not
real public releases. Actual UI/scale/installer verification remains hosted only.
