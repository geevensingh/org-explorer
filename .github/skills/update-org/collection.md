# Local collection protocol

The app's [import contract](../../../docs/import-format.md) owns Import semantics.
The [testing strategy](../../../docs/testing.md) owns verification; automated
fixtures use fictitious people. Company data stays in local AppData, never a repo
fixture or CI artifact. WorkIQ's loaded contracts own source APIs and recovery.

## Standing execution policy

On 2026-11-17, the owner explicitly extended the collection, repair, recovery and
photo-omission decisions approved during the first completed run to future
sessions using this skill. Invoking `/update-org` carries these authorizations;
the coordinator records their application through the helper without asking for
the same approval again. They are workflow rules, not approvals tied to employee
identities or an earlier run's files.

| Decision | Standing authorization |
|---|---|
| Delivery | Collect the complete verified reporting subtree and attempt indexed profile/photo enrichment for every retained Person. Publish with explicitly degraded enrichment when optional lookups are unavailable; never silently drop people or reporting relationships. |
| Local defects | Diagnose and repair reproducible producer/capture defects, add fictitious regressions, run the relevant checks, and resume retained work. Preserve source evidence and the import contract. |
| Reporting conflicts | Obtain fresh exact Manager evidence and reconcile only when it confirms an already-sourced competing parent and is at least as recent as every conflicting reporting receipt. Retain original receipts and review flags; unresolved conflicts still block. |
| Execution | Use up to two fresh, disjoint workers concurrently. If workers or worker capture are genuinely unavailable, continue serially with the same WorkIQ source and coverage requirements. Verify descriptor/provenance errors before declaring that limitation. |
| Transient failures | Automatically allow one retry per failed read throughout the run. Failures are per-target recovery work, not a global dispatch freeze. Preserve successful reads and actual invocation-wide retry delays. |
| Known profile failures | Automatically allow one retry for each occurrence of the three exact errors below under the recorded best-effort policy. Earlier strict runs retain append-only matching-error approvals. |
| Photo download 404 | With fresh successful metadata, allow one retry of the exact failed binary download, including `ImageNotFound` or `ErrorNonExistentStorage`. A binary failure is not metadata-confirmed absence. |
| Unavailable enrichment | Indexed profiles, photo metadata and photo downloads are best-effort. After bounded recovery, retain an explicit unavailable outcome and diagnostics, omit unsourced fields/images and preserve saved values. A nonretryable optional failure can settle immediately without probing another query/provider. |
| Capture failures | Recapture the same ticket while its original completion can still arrive. If recovery is genuinely impossible and the original work is confirmed stopped, record claim-bound capture failure and allow one replacement read. Discard unverified values; exhausted optional capture becomes unavailable. |

The approved unclassified profile errors are exactly:

```text
Open '' Failure: Unable to open, Exception Caught.
Open '' Failure: Unable to open, Exception: Status code is .
Profile details couldn't be retrieved for this person right now.
```

The owner approved best-effort enrichment and per-target capture recovery on
2026-11-17. These authorizations preserve the two-total-attempt limit, denial stops,
complete directory identity/reporting coverage and original capture requirements. They do not
authorize alternate clients/accounts/providers, invented evidence, a third
attempt, or treating a failed profile search as a successful no-evidence search.
Unfamiliar non-denied optional failures are reported as unavailable, not new
approval questions. Required directory failures remain resumable blockers;
unrelated work may continue, but publication cannot invent a complete graph.
Ask only for missing/ambiguous inputs or decisions outside this policy.

## Helper commands

Use the [primary workflow](workflow.md) for startup, dispatch, request preparation,
observations and compact progress. The commands below also retain the lower-level
interfaces used for diagnostics and compatible earlier strict runs.

Commands below run from the repository root in PowerShell. Use absolute helper/run
paths in workers. Prerequisites are the repository's .NET SDK and configured
WorkIQ tools; no Python, Graph SDK, credentials file or previous session is needed.

```powershell
$project = Join-Path $PWD '.github\skills\update-org\scripts\OrgExport.csproj'
# Restore locked dependencies only if the build reports missing restore assets.
dotnet build $project --no-restore --verbosity quiet
$tool = Join-Path (Split-Path $project) 'bin\Debug\net10.0\OrgExport.dll'
# Use values actually resolved from the directory and /me, not these variable names.
$started = (& dotnet $tool start $rootDisplayName $rootDirectoryId $connectedUserId) | ConvertFrom-Json
$run = $started.runPath
dotnet $tool plan $run
```

For an existing unsealed run, inspect `source-status` and retain its recorded
policy. Enable best-effort collection before failed reads; a strict run that
already has failures retains its earlier recovery policy rather than silently
changing requirements.
Each new run records its own approval time/reason referencing the standing policy,
not copied approval timestamps or a manually edited ledger.

Nonzero exit means failure. Command-level errors produce a JSON diagnostic on
stderr; `profiles` reports individual failures in its complete stdout result
instead. Do not pipe human progress into helper input. JSON input files and tool
payloads go under `$run` or the host's local temporary output area, not the checkout.

| Command after `dotnet $tool` | Input/output |
|---|---|
| `start LABEL ROOT-ID ACCOUNT-ID [WORKER-LIMIT]` | Atomically creates a versioned private run and records the standing policy. Default two workers; one for genuine serial operation. Returns collector/protocol identity and host limits. |
| `status RUN` | Compact phase, counts, next bounded directory/metadata work, retry deadline, sample-based estimate and collector identity; no source payloads. |
| `next-job RUN WORKER` | Persists one immutable disjoint assignment when a worker slot is free. Repeated claims do not create duplicate jobs. |
| `finish-job RUN WORKER JOB-ID` | Validates source/observation/photo coverage and hashes before completing the current owner's job. |
| `resume-job RUN JOB-ID NEW-WORKER REASON` | Reassigns a confirmed stopped worker's job after affected source claims settle; preserves attempts and appends the handoff. |
| `prepare-next RUN WORKER PHASE TOOL-NAME EVENTS.jsonl [AGENT-ID]` | Selects the next bounded directory/metadata/profile/photo request and creates its exact claim-bound descriptor and capture ticket. Call WorkIQ only when the returned claim is granted. |
| `prepare-source RUN WORKER KIND TARGETS.json TOOL-NAME EVENTS.jsonl [AGENT-ID]` | Advanced explicit-target equivalent of prepared requests; enforces the host's 25/4/1 limits and assigned enrichment scope. |
| `finish-capture RUN TICKET.json` | Captures/finishes one bound structured or binary call, ingesting complete mixed batches or validating successful photo bytes without hand-written response JSON. Profiles retain explicit per-identity outcome association. |
| `record-profile RUN DIRECTORY-ID CAPTURE.json DRAFT.json` | Writes the observation with exact directory email, original captured lookup time and evidence array; fields/reasons remain the worker's grounded interpretation. |
| `upgrade-run RUN REASON` | Explicitly upgrades stopped compatible unversioned state with original byte snapshots and a resumable journal, without resetting policy or attempts. Refuses unknown policy schemas, active work and sealed exports. |
| `init LABEL ROOT-ID ACCOUNT-ID` | Returns a new private staging path below local `OrgExplorer\Imports`. |
| `capture-start RUN EVENTS.jsonl REQUEST.json` | Bookmarks the caller's local session event stream for one exact tool invocation; returns a private capture-ticket path. See below. |
| `capture RUN TICKET.json` | Returns original response/request file references, tool-call identity, source-result counts and outer tool outcome. Incomplete/ambiguous capture fails explicitly. |
| `capture-fail RUN CLAIM-ID TICKET.json REASON` | After bounded local recapture and confirmed-stopped work: records immutable correlated diagnostics, preserves observed denials/delays, and schedules the permitted replacement read or unavailable optional outcome. Rejects a usable capture or missing safely correlated completion diagnostics. |
| `plan RUN` | Returns pending directory paths/errors or job-file paths and fresh photo-metadata paths. |
| `source-status RUN` | Returns the persistent invocation-wide source record, including active claims, failures, deferred first reads, saved result paths and recovery consumption. |
| `source-claim RUN WORKER KIND TARGETS.json` | Claims a source call, or returns `claimId:null` with a blocker. Kinds/target limits are defined below. |
| `source-finish RUN CLAIM-ID RESULTS.json` | Records and returns only this claim's outcomes plus invocation recovery/stop flags; retains the result file for handoff. Use `source-status` for the full ledger. |
| `source-refresh-manager RUN DIRECTORY-ID REASON` | Reopens only a completed Manager confirmation made stale by newer conflicting reporting evidence in `plan.managerRefreshes`; archives the previous observation/attempts and records the reason and required freshness. |
| `source-authorize-best-effort RUN REASON` | Records the standing best-effort policy before failed reads. The helper thereafter schedules retries and unavailable outcomes per target automatically. |
| `source-recover RUN` | Coordinator only: enrolls a newly eligible failed set after all active claims settle, under the run's recorded recovery approvals. |
| `source-authorize-per-read-recovery RUN REASON` | Coordinator only: records this skill's standing per-read recovery approval for the run. |
| `source-authorize-unclassified-recovery RUN KIND TARGET REASON` | Coordinator only, after explicit approval for that failed read: grants its second attempt when the saved failure has no source error status/code. |
| `source-authorize-matching-error-recovery RUN KIND TARGET REASON` | Coordinator only: records an exact saved error/source-kind match covered by the standing policy or a new explicit approval. |
| `source-authorize-not-found-recovery RUN KIND TARGET REASON` | Coordinator only: grants an exact failed read's second attempt under the standing photo-404 policy or a new explicit approval; not confirmed absence. |
| `source-omit-photo RUN DIRECTORY-ID REASON` | Coordinator only: records the standing exhausted-photo omission for that exact ID, retaining failed receipts and omitting `photoPath` to preserve an existing app photo. |
| `source-interrupt RUN CLAIM-ID REASON` | Coordinator only: releases a claim after confirming its worker stopped, preserving uncertain reads as failures. |
| `ingest RUN REQUESTS.json RESPONSE.json` | The request file is an array of 1-50 exact relative paths, in order. The response is the full WorkIQ `fetch` envelope, `{"results":[...]}`. Saves every nested status, including failures. |
| `profile RUN DIRECTORY-ID OBSERVATION.json` | Records a grounded work-profile observation and copies its relevant evidence into the run. |
| `profiles RUN FILES.json` | Processes every observation file path; returns [counts and correlated item outcomes](#profile-batch-outcomes), exiting nonzero if any fail. |
| `cache RUN DIRECTORY-ID` | Reuses photo bytes only with matching fresh metadata, connected-user identity and byte hash. A miss returns `reused:false`. |
| `cached-photos RUN IDS.json` | Runs cache checks for an array of assigned directory IDs. |
| `photo RUN DIRECTORY-ID BLOB.json` | Materializes the full successful `fetch_blob` JSON response, verifying MIME/magic and decoded length. |
| `photos RUN INPUTS.json` | Batch input: `[{"directoryId":"...","responseFile":"..."}]`. |
| `publish RUN` | Checks the actual app preview, copies verified photo cache entries and atomically publishes a new immutable export. |

The other `source-authorize-*`, `source-recover` and `source-omit-photo` commands
remain available for earlier strict runs. New best-effort runs do not need those
per-failure approval/enrollment steps.

Use `plan` paths verbatim. It requires fresh root details, all subtree reporting
pages and leaves, and the ancestor Manager chain. An actual Manager-relationship
404 with `Request_ResourceNotFound` ends that chain only for a separately resolved
directory user; a failed person lookup does not establish a missing Manager.
For mandatory directory reads, empty 200 payloads and unclassified 404s remain
blockers; denials stop the invocation. Continuations must remain
on the returned user's relationship and expected Graph service; preserve tokens.
Use at most 25 structured paths in this host, within WorkIQ's generic 50-path
ceiling. A mixed failure can otherwise expose only one 25-result native chunk.
Prepared requests enforce this bound; batch independent requests rather than
calling one person at a time.
Keep `$select`, `$top` and `$value` literal: use parsed path strings or
single-quoted PowerShell literals, not interpolated query strings.

Fetch any exact Manager paths requested by `plan` under the standing conflict
rule. When `managerRefreshes` identifies a stale completed confirmation, use
`source-refresh-manager` for that exact directory ID before claiming it again.
The helper retains prior attempts and receipts in `previousObservations`, and
rejects an outstanding refresh or a refresh without strictly newer conflicting
evidence. Each newly required confirmation has at most two attempts; this is a
fresh observation, not a way to renew a failed read's budget. Unrelated successful
reads stay completed.

Retain supported agent-user accounts as distinct identities with review
flags rather than excluding them or merging them by name.

The helper never calls M365 itself. Use the shared capture sequence below for
directory, profile-evidence and binary source reads. It transfers original results
without copying JSON or base64 through the model.

### Capture one source call

Use the **calling session's** runtime-provided `events.jsonl` path. A worker with
its own event file uses that file. A native worker sharing its parent's event
stream must also supply its runtime-provided `agentId` (or legacy
`parentToolCallId`) in the request descriptor. Omission selects root-only events,
not any worker. If the host does not expose a complete local event stream and
worker identity, report that prerequisite as a blocker; use no transcript search
or hand-written payload substitute.

1. Claim the source call through the [shared gate](#source-coordination).
   If granted, write a descriptor under your private worker folder, containing only the
   exact configured `toolName`, complete `arguments` you will invoke, the granted
   `claimId`, and optional worker scope. Load tool definitions first. Example shape:
   `{"toolName":"<configured WorkIQ fetch name>","arguments":{"entityUrls":["<pending path>"]},"claimId":"<granted UUID>"}`.
   Binary reads use their exact `path` arguments; grounded profile reads use
   their complete actual arguments. Descriptors contain requests, never responses.
   The helper binds the original ticket and its hash to that claim's attempt.
2. Before the source call, run
   `$ticket = (& dotnet $tool capture-start $run $events $descriptor) | ConvertFrom-Json`.
   Invoke that exact WorkIQ tool once with those arguments. Use separate tickets
   for independent calls; repeated identical calls inside one ticket are ambiguous.
3. Run `$capture = (& dotnet $tool capture $run $ticket) | ConvertFrom-Json`.
   A missing completion can be captured again after it arrives, without repeating
   the source read. Delayed native-event arrival is not proof that worker capture
   is unsupported: verify exact tool name, complete arguments and runtime worker
   scope, then recapture the same ticket when the completion arrives. Changed
   session history, ambiguous identity, unsupported transport and incomplete
   payloads are explicit capture failures. Preserve the ticket and diagnostic,
   then follow the claim-bound capture-failure path below; never repair or
   reconstruct source bytes.
   Capture scans a snapshot of available bytes and parses only newline-terminated
   JSONL records. An unrelated uncommitted final record is deferred until a later
   capture, not parsed as corrupt JSON or allowed to hide an earlier completion.
   Before fan-out, prove this path with the first normal bounded call and retain
   its success; do not repeat it as a separate preflight.
4. Record the captured outcomes with `source-finish` before any further source
   call. Create its result array by local JSON parsing of the captured response,
   preserving ordered results, nested statuses and errors without passing payloads
   through the model. For directory/photo-metadata reads, pass `$capture.requestsFile` and
   `$capture.responseFile` to `ingest`. For photo bytes, pass the response file
   directly to `photo`/`photos`. Profile observations reference the response file
   in `evidenceFiles`. Use a file reader only on the captured evidence needed
   for interpretation; normal coordinator/worker messages contain counts,
   ticket/file references and blockers.

Capture supports original JSON in event `result.content`, native
`structuredContent`, one native text `contents` block, and full `detailedContent`.
It also unwraps MCP `structuredContent` or one text `content` block, including
`isError` wrappers. Native failure messages containing complete JSON, optionally
prefixed `MCP error <integer>: ` or `MCP server '<server>': `, retain mixed
successes and source failures. The same validated prefixes are removed when
inspecting unusable capture diagnostics for denials and retry delays; those
diagnostic bytes are still not accepted as a usable source response.
Host-capped text with the marker
`Output too large to display.\nFull output saved to: <absolute path>` is an explicit
blocker. A path and a fresh modification time cannot establish which invocation
wrote the bytes: another call may have replaced the file. Saved-file support
requires an explicit adapter with a verified immutable per-invocation file
contract or a source-provided digest bound to that completion. A digest computed
after reading the file does not establish that binding. Preserve the completion
diagnostic and report the unsupported host transport; multipart/binary display
blocks, excerpts, missing files and incomplete batches are not reconstructed.

Each ticket retains immutable original start/completion events,
ordered requests and the extracted response. Matching uses the stream bookmark,
exact tool name/arguments, worker scope and tool-call ID, with exclusive claims
preventing two tickets from consuming the same invocation. The completion record
retains all outer errors and nested statuses. `toolSucceeded` describes only the
outer tool execution; `nonSuccessResults` counts non-200/error entries where
source statuses are available (otherwise null). Neither proves authorization
or publication readiness. Ingest mixed batches even after an outer failure so
successes remain available; inspect failures and follow the centralized recovery
and authorization rules before further source reads.

### Profile batch outcomes

`profiles` accepts a JSON array of nonempty observation file paths. `[]` is a
successful no-op: `{"succeeded":0,"failed":0,"items":[]}`. A single input still
returns a one-element `items` array; multiple inputs retain their input order.
Counts are per input, including repeated paths, not distinct People.
Invalid batch JSON, a non-array/null document, or a non-string/null/empty path
rejects the command before processing any members, with a diagnostic on stderr.

Each item has a zero-based `index`, the supplied `inputFile`, `directoryId`
(null if the observation could not be read), `recorded`, and `error`. A recorded
item has null `error`; a failed item has a diagnostic and `recorded:false`.
For example, a valid-failed-valid batch returns:

```json
{
  "succeeded": 2,
  "failed": 1,
  "items": [
    {"index": 0, "inputFile": "first.json", "directoryId": "first-id", "recorded": true, "error": null},
    {"index": 1, "inputFile": "failed.json", "directoryId": "failed-id", "recorded": false, "error": "Source lookup failed."},
    {"index": 2, "inputFile": "last.json", "directoryId": "last-id", "recorded": true, "error": null}
  ]
}
```

This result is on stdout with exit code 1, not a successful command or a rolled-back
batch. Valid observations and their evidence are persisted even after a failure.
Exit code 0 requires every item to succeed. Inspect both exit status and item
outcomes; do not infer success from the presence of stdout.

Use explicit PowerShell arrays at the JSON boundary, including zero/one inputs:

```powershell
[string[]]$observationFiles = @($firstObservation, $secondObservation)
ConvertTo-Json -InputObject $observationFiles | Set-Content -LiteralPath $filesJson -Encoding utf8
$output = & dotnet $tool profiles $run $filesJson
$exitCode = $LASTEXITCODE
$result = ($output -join "`n") | ConvertFrom-Json
# Retain $result.items and report $exitCode plus every failed item to the coordinator.
```

Per-item failures persist under `profile-errors` and block `publish`, including
when a previous successful observation for that Person remains on disk. Correct
and resubmit only failed observations. Failures correlate to the input path and,
only after a verified identity match, the Person and lookup time. An earlier
success cannot resolve a newer failure. A replacement file can resolve matched-Person
failures when its lookup time is at least as recent as their latest failed lookup.
Without a verified identity or lookup time, correct the original input path.
Reusing that path for another Person cannot resolve a failure with a verified identity.
Correcting a mismatched identity resolves that input, not an unrelated Person.
A successful correction preserves other successes and their lookup timestamps/
evidence. Freshness still requires actual evidence from this run, not restamped
old payloads.

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
In best-effort runs, an isolated failure leaves unrelated queued jobs dispatchable;
honor global stop reasons and returned retry deadlines. Earlier strict runs retain
their invocation-wide recovery gate.

Use the [capture sequence](#capture-one-source-call) before `source-finish`. Its input is an
array of `{"target":"exact claimed target","response":{...}}`, one per target.
Structured and photo responses retain the actual status and nested errors. For a
grounded People query, associate each identity's actual result with that ID; wrap
a successful tool completion as `{"statusCode":200,"data":FULL_RESULT}` where
necessary. Preserve nested failures rather than assigning a success wrapper to
an unresolved identity. A call-wide error applies to every affected target.
Record all partial successes and errors, then ingest complete structured batches,
including failed receipts; materialize only successful profile/photo results
through the existing helper commands. A completed search with no evidence
is a success with explicit uncertainty; a failed search cannot become unknown.
Failed binary downloads cannot become `ImageNotFound`.

### Per-target recovery and unavailable outcomes

With `bestEffortEnrichment` recorded, inspect this claim's outcomes from
`source-finish`, or `source-status` after a handoff:

- `completed`: materialize the successful source once; never replay it.
- `failed`, `recoveryEligible:true`: the target is queued for its sole retry.
  Claim only those failed targets, preserving every successful batch member.
- `unavailable`, `accountedFor:true`: an optional lookup has settled without
  usable enrichment. Preserve diagnostics; do not create a success observation
  or a new source call for it.
- `failed`, not retry-eligible/accounted-for: required source work remains blocked.
  Keep unrelated work running, but do not claim a ready complete reporting graph.
- Any `stopReason`: stop all further source dispatch. Denials and invalid returned
  retry delays cannot be settled as unavailable.

The helper automatically retries transient failures and the approved exact
profile errors, and retries binary-photo 404s only with completed successful
metadata, including when that metadata succeeded on its second attempt.
Other nonretryable optional errors settle explicitly as unavailable.
Photo metadata itself can be unavailable; it is not confirmed `ImageNotFound`.
`cached-photos` reports `lookupUnavailable:true` for unavailable metadata/bytes;
this is distinct from confirmed-absence `unavailable:true` and legacy
`omitted:true`. Do not download a photo when its metadata lookup is unavailable.

Only recorded failures count as unavailable coverage. Every retained Person must
still have an attempted profile lookup and photo-metadata lookup, and every
successful non-absent metadata result needs verified bytes or an accounted-for
download outcome. An unavailable binary download does not waive its metadata
lookup; only that exact metadata read's recorded unavailable outcome can do so.
Malformed local observations, identity mismatches and corrupt
artifacts remain explicit validation errors, not permission to hide them as
unavailable source data.

Each target has at most two total attempts. A returned retry deadline applies to
all source calls, even unrelated first reads; wait until the latest recorded
`retryAt`. Otherwise a target's failure does not consume another target's source
slot or freeze queued jobs. Workers may process their helper-granted retries;
the helper, not a worker's judgment, owns eligibility and terminal outcomes.

For a confirmed stopped-worker handoff using `source-interrupt`, the coordinator
uses `source-recover` after active claims settle. Recorded best-effort policy
permits each independent target's eligible second attempt, rather than consuming
one global recovery allowance; it never grants a third attempt.

### Recover an unverifiable capture

First diagnose descriptor/scope errors and bounded late-event arrival by
recapturing the same immutable ticket. Use a bounded local wait (up to 60 seconds
for a missing completion after the source call returned), not a repeated source
call. A known unsupported transport does not need that wait.

If the original invocation is known to have completed, or its worker is confirmed
stopped, use `capture-fail` with its exact claim/ticket and the reason establishing
that fact. A timeout alone does not prove stopped work. The helper checks the
binding and tries capture again: a now-usable result must be finished, not
discarded for a retry. Otherwise it retains failure diagnostics and any correlated
original completion, carries observed denial/delay information into the gate,
and permits only the remaining read attempt. No unverified values are ingested.
If safely correlated completion diagnostics are absent, `capture-fail` leaves the
claim active and authorizes no replacement: absent diagnostics do not establish
the absence of denial or a returned delay. Recapture or repair local inspection
first; stopped-worker handoffs are not a bypass for that check.
After both attempts, optional enrichment becomes unavailable; unverified directory
identity/reporting remains a publication blocker.

The configured WorkIQ tools currently return inline results, not an
invocation-bound file-artifact option. Keep the existing immutable capture
artifacts and request binding; an unbound host temporary file is not a replacement
transport. A future adapter requires an actual upstream per-invocation file/digest
contract, not a path or a digest computed after reading unrelated bytes.

### Earlier strict runs

Explicit error fragments `Status code is <HTTP status name>` are source statuses,
including transient `ServiceUnavailable` and denial `Forbidden`. Recovery checks
both string errors and their supported message-only object wrappers. It checks
the retained original result with the current classifier without rewriting prior
attempts. A newly recognized denial persists the invocation stop.

For a run without `bestEffortEnrichment`, the coordinator pauses dispatch on any `stopReason` or failed read with
`accountedFor:false`, then settles all active claims. Count distinct unfinished
`reads[].attempts[].claimId` values, not active identity rows. Apply the standing
policy through these paths; workers cannot enroll recovery or omissions.

- **Transient failure:** verify `perReadRecovery` is recorded, then run
  `source-recover` for each newly failed eligible set. The generic helper retains
  a one-pass default without that authorization; this skill registers the
  standing exception during initialization.
- **Known unclassified profile error:** on its first occurrence in this run, use
  `source-authorize-matching-error-recovery` with the exact failed target and a
  reason citing the standing policy. Then run `source-recover`, including for
  later occurrences. Keep earlier error approvals; new distinct approved errors
  append rather than replace them. The helper matches source kind and exact
  error content, treating a string and a message-only object as equivalent.
- **Photo download 404:** use `source-authorize-not-found-recovery` for the exact
  failed path, citing the standing policy. This directly grants that read's
  second attempt; it does not need `source-recover`.
- **Other unclassified failure:** preserve diagnostics and obtain a new decision
  before using `source-authorize-unclassified-recovery`. That command also
  directly grants the exact read's second attempt.
- **Exhausted non-denied photo:** use `source-omit-photo` for its exact retained
  directory ID, citing the standing policy; no repeated omission question.
- **Unresolved required read or unsupported capture:** retain progress and report
  the blocker. An exhausted profile/hierarchy read is not an omittable photo.

Authorization commands return their authorization object, not the full ledger.
Inspect `source-status` after recording them; only a granted `source-claim`
permits a call. Preserve approval timestamps/reasons and original attempts.
While failures remain, only eligible recovery reads can be claimed. The latest
actual returned retry delay applies to the whole invocation; inspect attempt
`retryAt` values and honor the latest deadline. Each read has at most two total
attempts. After recovery/omission clears the gate, resume paused jobs and fill
free worker slots; queued targets with zero attempts remain first reads.

For an omission, fresh successful photo metadata and both failed download
receipts remain required. The read stays `failed` with `photoOmission` and
`accountedFor:true`; it is not a success or
confirmed absence. Use `accountedFor` when checking dispatch blockers.
`cached-photos` reports `omitted:true` for that photo. Preserve its omission
warning and count separately from `ImageNotFound`. Other source and enrichment
coverage remains mandatory; denials cannot be waived.

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
completed or explicitly accounted-for optional outcomes, plus verified mandatory
directory receipts and coverage of each profile/photo lookup.
Missing records cannot reset recovery.
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
   reads instead retain errors in `source-finish`. Follow its queued retry or
   unavailable outcome; never submit a failed lookup as an unknown observation.
   Keep unrelated reads running; do not expand providers.
3. Use `profiles` to persist observations in one helper call per batch. Inspect
   the complete [batch outcome](#profile-batch-outcomes), retain its successes,
   and report every failed item for coordinated correction/recovery. Use
   `cached-photos`; download only cache misses whose fresh metadata succeeded.
   Metadata-confirmed `ImageNotFound` needs no binary call and omits `photoPath`. Other 404s,
   nulls, denied reads and service errors are not photo absence.
   A coordinator-authorized `omitted:true` photo needs no further download;
   retain the failed receipts, report its omission separately, and preserve all
   other profile/photo requirements.
   For `lookupUnavailable:true`, preserve the failed-lookup diagnostics and omit
   the unsourced image; do not count it as confirmed absence.
4. Claim and capture remaining `/users/{exact-directory-id}/photo/$value` reads through
   WorkIQ's configured `fetch_blob` tool using the shared capture sequence.
   Keep supplied image resolution; pass captured response files to `photo`/`photos`.
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

`coverage.json` and the publish result distinguish `enrichmentStatus`
(`complete` or `degraded`), `unavailableProfileLookups`, and
`unavailablePhotoLookups`. The existing `unavailablePhotos` count means confirmed
metadata absences; explicit legacy waivers remain `omittedPhotos`.
`unavailable-enrichment.json` retains unavailable read outcomes, original
attempt/result references and capture diagnostics. A degraded import still
requires valid complete reporting, safe photo bytes and the real app preview.
Omitted unsourced fields/images preserve existing Workspace values.

Keep the coordinator's job manifest and source ledger authoritative across
handoffs. Report phase changes, changed blockers and meaningful coverage
milestones, not a separate user-facing handoff for every batch. Distinguish
completed observations, sourced/unknown fields, verified photos, confirmed
absences and approved omissions. A repair, successful retry or completed batch
is a continuation point, not the deliverable: finish publication and return only
the helper's actual `importPath` as the ready import.
