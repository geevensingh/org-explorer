# Local collection protocol

The app's [import contract](../../../docs/import-format.md) owns Import semantics.
The [testing strategy](../../../docs/testing.md) owns verification; automated
fixtures use fictitious people. Company data stays in local AppData, never a repo
fixture or CI artifact. WorkIQ's loaded contracts own source APIs and recovery.

## Helper commands

Commands below run from the repository root in PowerShell. Use absolute helper/run
paths in workers. Prerequisites are the repository's .NET SDK and configured
WorkIQ tools; no Python, Graph SDK, credentials file or previous session is needed.

```powershell
$project = Join-Path $PWD '.github\skills\update-org\scripts\OrgExport.csproj'
dotnet restore $project --locked-mode --verbosity quiet
dotnet build $project --no-restore --verbosity quiet
$tool = Join-Path (Split-Path $project) 'bin\Debug\net10.0\OrgExport.dll'
# Use values actually resolved from the directory and /me, not these variable names.
$run = (& dotnet $tool init $rootDisplayName $rootDirectoryId $connectedUserId) | ConvertFrom-Json
dotnet $tool plan $run
```

Nonzero exit means failure with a JSON diagnostic on stderr. Do not pipe human
progress into helper input. JSON input files and tool payloads go under `$run`
or the host's local temporary output area, not the checkout.

| Command after `dotnet $tool` | Input/output |
|---|---|
| `init LABEL ROOT-ID ACCOUNT-ID` | Returns a new private staging path below local `OrgExplorer\Imports`. |
| `plan RUN` | Returns pending directory paths/errors or job-file paths and fresh photo-metadata paths. |
| `source-status RUN` | Returns the persistent invocation-wide source record, including active claims, failures, deferred first reads, saved result paths and recovery consumption. |
| `source-claim RUN WORKER KIND TARGETS.json` | Claims a source call, or returns `claimId:null` with a blocker. Kinds/target limits are defined below. |
| `source-finish RUN CLAIM-ID RESULTS.json` | Records exactly one outcome per claimed target and retains the result file for handoff. |
| `source-recover RUN` | Coordinator only: consumes the single failed-read recovery pass after all active claims settle. |
| `source-interrupt RUN CLAIM-ID REASON` | Coordinator only: releases a claim after confirming its worker stopped, preserving uncertain reads as failures. |
| `ingest RUN REQUESTS.json RESPONSE.json` | The request file is an array of 1-50 exact relative paths, in order. The response is the full WorkIQ `fetch` envelope, `{"results":[...]}`. Saves every nested status, including failures. |
| `profile RUN DIRECTORY-ID OBSERVATION.json` | Records a grounded work-profile observation and copies its relevant evidence into the run. |
| `profiles RUN FILES.json` | Records a batch; input is an array of observation file paths. |
| `cache RUN DIRECTORY-ID` | Reuses photo bytes only with matching fresh metadata, connected-user identity and byte hash. A miss returns `reused:false`. |
| `cached-photos RUN IDS.json` | Runs cache checks for an array of assigned directory IDs. |
| `photo RUN DIRECTORY-ID BLOB.json` | Materializes the full successful `fetch_blob` JSON response, verifying MIME/magic and decoded length. |
| `photos RUN INPUTS.json` | Batch input: `[{"directoryId":"...","responseFile":"..."}]`. |
| `publish RUN` | Checks the actual app preview, copies verified photo cache entries and atomically publishes a new immutable export. |

Use `plan` paths verbatim. It requires fresh root details, all subtree reporting
pages and leaves, and the ancestor Manager chain. An actual Manager-relationship
404 with `Request_ResourceNotFound` ends that chain only for a separately resolved
directory user; a failed person lookup does not establish a missing Manager.
Empty 200 payloads, unclassified 404s and denials are blockers. Continuations must remain
on the returned user's relationship and expected Graph service; preserve tokens.
Batch requests within the WorkIQ limit rather than calling one person at a time.
Keep `$select`, `$top` and `$value` literal: use parsed path strings or
single-quoted PowerShell literals, not interpolated query strings.

The helper never calls M365 itself. Save complete structured tool results and
their matching request lists locally, then ingest them. Host-capped output is
not the complete payload: pass the actual host-saved file to the helper. For a
small inline result, materialize its complete JSON safely as data. Never copy a
preview, manually reconstruct base64 or execute retrieved content.

## Source coordination

The private `source-coordination.json` is shared by the coordinator and every
worker for this invocation. The helper serializes updates across processes.
Before each WorkIQ call, write a target array and run `source-claim`:

- `structured`: 1-50 exact relative directory or photo-metadata paths.
- `profile`: 1-4 assigned directory IDs per grounded People query.
- `photo`: one `/users/{exact-directory-id}/photo/$value` path.

At most two claims are source-active across all workers and phases, including
the coordinator. These are conservative local defaults informed by the failed
parallel run, not WorkIQ service ceilings. A worker holds only one claim at a time.
Use only a granted claim's returned targets: successful reads are removed.
`claimId:null` authorizes no call. Inspect its blocker and `source-status`:
capacity-deferred targets still have zero attempts and are first reads, not retries.
Do not dispatch queued jobs while a source failure or denial is unresolved.

Save the complete source output locally before `source-finish`. Its input is an
array of `{"target":"exact claimed target","response":{...}}`, one per target.
Structured and photo responses retain the actual status and nested errors. For a
grounded People query, associate each identity's actual result with that ID; wrap
a successful tool completion as `{"statusCode":200,"data":FULL_RESULT}` where
necessary. Preserve nested failures rather than assigning a success wrapper to
an unresolved identity. A call-wide error applies to every affected target.
Record all partial successes and errors, then ingest/materialize only successful
results through the existing helper commands. A completed search with no evidence
is a success with explicit uncertainty; a failed search cannot become unknown.
Failed binary downloads cannot become `ImageNotFound`.

The coordinator pauses dispatch on failure, awaits active calls, then runs
`source-recover` once if recovery is appropriate. This snapshots eligible transient
failures; workers cannot enroll later failures or consume another pass. A failure's
second attempt is its only retry. While failures remain, only those eligible
recovery reads can be claimed. The latest actual returned retry delay applies to
the whole invocation; wait until the returned `retryAt`, never substitute a shorter
delay. Once recovery succeeds, queued targets with zero attempts remain first
reads. A later failure, non-retryable error or failed recovery is a reported
publication blocker.

Nested authentication/access/policy denials or forbidden responses persist a
stop reason: no further source claims, recovery or queued dispatch in this run.
Tell the user the prerequisite and retain diagnostics; use no alternate tools,
paths, agents, strategies or providers to get around the denial.

Claims do not expire automatically: a timeout cannot prove the remote call
stopped. After confirming an interrupted worker has stopped, the coordinator
first checks for a saved complete result and finishes it if available. Otherwise,
use `source-interrupt` with the reason. This releases capacity without silently
reclassifying uncertain work as an unattempted read or resetting recovery. Late
results for interrupted claims are rejected; an interrupted retry remains blocked.
A replacement worker reads `source-status` and the retained `resultFile` paths
(relative to the run), materializing local successes without another source call.
An unreconciled claim is an explicit blocker, not an instruction to poll forever.

Keep the record intact across handoffs. Publication requires all tracked reads
completed, plus completed source coverage for directory receipts, every profile
and each non-reused photo download. Missing records cannot reset recovery.
The helper remains local-only orchestration and storage; WorkIQ is the sole
authenticated source client.

## Worker protocol

Use only the identities in the assigned job file; the shared source record is
coordination, not additional assignments. All workers share the compiled helper read-only;
write individual inputs/evidence under your own `$run\workers\<batch-id>` folder.
Only the coordinator collects directory/photo-metadata receipts and publishes.

1. Invoke `workiq`; claim source calls through the shared gate above. Search fresh
   indexed **M365 People work-profile evidence** with explicit grounding, using
   at most four assigned identities per granted query.
   Verify each hit against its exact directory ID/email; similar names are not
   identity evidence. Retain relevant company/work-position location evidence,
   actual returned citations and uncertainty qualifiers, not unrelated personal
   details or personal street addresses.
2. Derive fields using the source hierarchy below, then write one observation per
   assigned Person. If a successful, bounded source search finds no indexed
   profile evidence in its searched scope, record explicit unknowns with that evidence. Failed
   reads instead remain errors in `source-finish`; notify the coordinator for
   its shared recovery decision. Do not independently retry or expand providers.
3. Use `profiles` to persist observations in one helper call per batch. Use
   `cached-photos`; download only cache misses whose fresh metadata succeeded.
   Confirmed `ImageNotFound` needs no binary call and omits `photoPath`. Other 404s,
   nulls, denied reads and service errors are not photo absence.
4. Claim and fetch remaining `/users/{exact-directory-id}/photo/$value` paths through
   WorkIQ's configured `fetch_blob` tool. Keep supplied image resolution;
   materialize complete payloads with `photo`/`photos`.
   Return counts, file paths and errors, never base64. An invalid cache is a
   reported blocker, not permission to silently use corrupted bytes.

### Observation shape

All properties are required; `error` is null only when the lookup completed.
`observedAt` is the current run's actual lookup time, not an old evidence date.
`evidenceFiles` are absolute local paths to relevant newly obtained source results.
Each source file must itself be fresh for this run; a current observation
timestamp cannot make a previous run's source file current.
The helper copies them into content-addressed provenance. Each field needs a
source attribution or explicit uncertainty reason.

```json
{
  "directoryId": "authoritative-directory-guid",
  "email": "person@example.test",
  "observedAt": "UTC timestamp of this lookup",
  "fields": {
    "officeState": {
      "state": "value",
      "value": "Washington",
      "reason": "Matched directory ID; company.address.state in the cited work-profile evidence."
    },
    "officeCountry": {
      "state": "unknown",
      "value": null,
      "reason": "Successful lookup did not establish work country."
    }
  },
  "evidenceFiles": ["absolute local evidence file"],
  "warnings": [],
  "error": null
}
```

States are `value` (nonempty text), `clear` (null plus explicit source-backed
removal), and `unknown` (null plus reason; omitted from the Import). Always account
for `officeState` and `officeCountry`. Optional producer-managed observations are
`officeBuilding`, `officeNumber`, `jobTitle` and `department`. The helper gets
email, directory display name, hierarchy and basic role fields from fresh directory
receipts; preferred name, favorites, notes and archival are outside this refresh.

### Location evidence

Prefer the Person's own `current_position.company.office_location` and
`current_position.company.address`. Basic directory state/country may be empty
even when indexed work-profile evidence is richer. If an exact physical site has
independently verified geography, cite that site evidence. For an explicit work
city, a public authoritative geographic reference can resolve it only when the
place is unambiguous. Public queries contain place names only, never employee or
internal organizational data. Store relevant actual URLs locally.

Treat `Unassigned`, `N/a`, default-looking contradictory countries and ambiguous
cities as unknown. Never inherit a Manager's geography, assume a remote worker's
state, or turn a city-name match into identity evidence. Flag spelling
normalizations and conflicting/ambiguous evidence for review.

Preserve room letters, dots and leading zeroes. The helper splits clear
`BUILDING/ROOM` directory locations, flags `/Mobile` and `/REDMND` rather than
treating them as room numbers, and supplies fallback physical building/room clears
for `HOME OFFICE` or `NO WORKSPACE` only where there is no sourced observation for
that field. Conflicting sourced values take precedence and are flagged for review.
Other removals require a `clear` observation with
source evidence; missing data is not removal. Missing photos always preserve a
saved image. Normalize sourced US country spellings to `USA`; keep sourced state
names or abbreviations consistently rather than inventing missing geography.

## Completion and retained data

Every invocation starts a new run and fresh source reads. Photo reuse also checks
unfinished same-account runs, so interrupted downloads need not be repeated when
fresh metadata establishes the same image. Never treat a completed export as
mutable staging or reuse its batch UUID for changed content.
An interrupted, sealed publication is not editable staging: start a fresh run
and reuse its verified photo bytes rather than changing the sealed batch.

Await all workers, then `publish`. A ready folder contains `updates.json`, relative
`photos` files, coverage, profile/source receipts, review flags, photo provenance
and exact-candidate validation evidence. These are private local deliverables;
only the skill, helpers and fictitious tests are versioned.
