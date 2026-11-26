# Release, nightly and manual verification

[Return to Testing](../testing.md).

For hosted nightly/manual dispatch, workflow registration, exact-candidate pre-publication verification or release contract checks, use this reference. Read [scale selection and results](scale-verification.md) for the complete worker/union contract and [release operations](../releasing.md) for packaging, signing and publication prerequisites.

## Nightly and manual execution

`.github\workflows\scale-verification.yml` owns the independent non-UI job, the
two isolated rich workers and their strict union collector. Separate invocations
can overlap on the same ref; no scale concurrency group serializes or cancels
them. Each execution job uses its own hosted Windows runner; the tail consumes
its successful prefix's current-attempt handoff. UI scenarios are serial within
their own worker, not parallel competing windows. Each suite records the same
exact candidate SHA in independently named results/artifacts; artifacts upload
even on failure, and missing artifacts fail. These are not additional required
PR checks.

The workflow declares **10:23 UTC nightly**, direct manual dispatch and
`workflow_call` with a required exact candidate. The nightly schedule and new
workflow dispatch registration become active only after the file reaches the
default branch; wiring them on integration does not activate a schedule.

Before that registration, use the existing `ci.yml` **manual-only opt-in bridge**
from a pushed candidate branch, with the repository-authorized account:

```powershell
gh workflow run ci.yml --repo geevensingh/org-explorer --ref YOUR_PUSHED_BRANCH -f run_scale_verification=true
gh run list --repo geevensingh/org-explorer --workflow ci.yml --event workflow_dispatch --limit 5
gh run view YOUR_RUN_ID --repo geevensingh/org-explorer --json headSha,status,conclusion,jobs
gh run watch YOUR_RUN_ID --repo geevensingh/org-explorer --exit-status
```

Verify `headSha` is the intended candidate, both scale jobs succeeded, every named
result passed and both artifacts identify that SHA. The bridge calls the same
local reusable workflow at `github.sha`; it duplicates no suite commands. The
Boolean defaults to false and its guard requires manual dispatch. The ordinary
four lanes still run on a bridge dispatch, but their independent `CI` aggregate
does not attest to scale success: inspect non-UI performance, both rich workers,
the strict union collector and the whole run.
Omit the flag for ordinary focused CI only.

Once GitHub registers the scale workflow, direct manual execution is:

```powershell
gh workflow run scale-verification.yml --repo geevensingh/org-explorer --ref YOUR_PUSHED_BRANCH -f revision=FULL_40_CHARACTER_CANDIDATE_SHA
```

Blank `revision` uses that workflow dispatch's commit; supplied values must be a
full immutable SHA, not a mutable ref. All execution jobs check that checkout equals the
requested SHA before invoking the runner with `-ExpectedRevision`.

## Active pre-publication integration

`release.yml` calls the reusable scale workflow with the exact source SHA after
successful trusted main CI and product-impact planning. Every product publication
uses the complete hosted workflow described above: non-UI performance, both
isolated rich workers and their strict union. Its execution entry points are:

```powershell
.\eng\Run-Performance.ps1 -ExpectedRevision FULL_40_CHARACTER_CANDIDATE_SHA
.\eng\Run-UiScale.ps1 -Worker prefix -ExpectedRevision FULL_40_CHARACTER_CANDIDATE_SHA -RunId RUN_ID -RunAttempt RUN_ATTEMPT
.\eng\Run-UiScale.ps1 -Worker tail -PrefixResultsDirectory PREFIX_ARTIFACTS -ExpectedRevision FULL_40_CHARACTER_CANDIDATE_SHA -RunId RUN_ID -RunAttempt RUN_ATTEMPT
.\eng\Assert-UiScaleUnion.ps1 -PrefixDirectory PREFIX_ARTIFACTS -TailDirectory TAIL_ARTIFACTS -ExpectedRevision FULL_40_CHARACTER_CANDIDATE_SHA -RunId RUN_ID -RunAttempt RUN_ATTEMPT
```

The two rich invocations belong on separate isolated authorized desktops, not
this shared machine. Use the hosted workflow for artifact transfer and complete
candidate verification, not a single rich invocation.
Expected-revision execution refuses a different SHA, tracked changes or
non-ignored untracked files, and rechecks after assertions. Non-UI exploratory
execution without that argument reports modifications honestly; it is not
candidate proof. Rich workers always require explicit candidate/run/attempt binding.
The publisher requires both successful, complete, same-revision suite results
from its own run/attempt, and blocks on failure, absence, required skip,
cancellation or timeout.
Earlier nightly/manual artifacts are diagnostic evidence, **never** reusable
deployment approvals. Wiring is not a successful hosted execution or owner-key
provisioning. Before merge, the registered `ci.yml` default-off
`run_release_verification=true` bridge invokes the same fresh scale workflow,
then genuine packaging/signing with ephemeral keys and production-trust
isolation, with no production publication. See [release operations](../releasing.md)
for the exact safe hosted command, required results and remaining prerequisites.

Core CI also runs the non-UI `Release*.Tests.ps1` contracts: real temporary Git
history, controlled ephemeral cryptography, exact suite evidence, archive
isolation and fake external GitHub publication effects. They never call a real
publication endpoint, open WPF or run an installer.
Publication descriptions are observed through the real `Publish-Release` draft
body at that fake GitHub boundary. Real Git fixtures cover three accumulated
mainline changes (including the earlier durable Workspace-save change), literal
Unicode/Markdown subjects, a candidate older than checkout HEAD, and merged work
without WIP duplicates. They also cover an explicit first publication, unchanged
technical/renewal descriptions, failed/metadata publications retaining the
original app baseline, and pre-tag refusal of missing, shallow, non-ancestor,
off-first-parent, empty or unusable-subject history.
The core runner-category signer interoperability test also invokes the real
PowerShell signer with an ephemeral key and feeds its exact envelope to the real
`UpdateChecker`, checking pinned bytes and mutation rejection without network IO.
Metadata renewal contracts additionally cover due/no-product/no-op evaluation,
original source/trust/provenance, copied bootstrap bytes, unchanged app versions
and stale/failed/concurrent fake GitHub publication. The hosted release bridge
renews its genuine fixture package with ephemeral signing and uploads named
renewal evidence, never a production release.

The trust-verifier lifetime regression compiles a minimal public-resource
assembly, runs both real trust checks, and immediately deletes the fixture tree
in the same PowerShell process. It reproduces the hosted `Assembly.LoadFile`
mapping/cleanup denial without WPF or installation. Byte-based inspection retains
all trust/controlled-effects checks without holding the package DLL mapped.
Cleanup must finish before the hosted release result can report success; there
is no retry, swallowed deletion error or connection to the parked note issue.

Both scale fixtures save the app's ordinary startup-update opt-out before launch,
so a production-trusted historical candidate cannot download a production update
while being tested. The runner's file-backed regression observes both preferences
before scenario actions and verifies update/installed-update selections remain
untouched. No product default, public key, transport effect or scenario assertion
is replaced. Automatic renewal refuses historical candidates predating this
isolation instead of modifying their exact-source test checkout.
