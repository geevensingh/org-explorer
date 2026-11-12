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
| `capture-start RUN EVENTS.jsonl REQUEST.json` | Bookmarks the caller's local session event stream for one exact tool invocation; returns a private capture-ticket path. See below. |
| `capture RUN TICKET.json` | Returns original response/request file references, tool-call identity, source-result counts and outer tool outcome. Incomplete/ambiguous capture fails explicitly. |
| `plan RUN` | Returns pending directory paths/errors or job-file paths and fresh photo-metadata paths. |
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

1. Write a descriptor under your private worker folder, containing only the
   exact configured `toolName`, complete `arguments` you will invoke, and optional
   worker scope. Load tool definitions first. Example shape:
   `{"toolName":"<configured WorkIQ fetch name>","arguments":{"entityUrls":["<pending path>"]}}`.
   Binary reads use their exact `path` arguments; grounded profile reads use
   their complete actual arguments. Descriptors contain requests, never responses.
2. Before the source call, run
   `$ticket = (& dotnet $tool capture-start $run $events $descriptor) | ConvertFrom-Json`.
   Invoke that exact WorkIQ tool once with those arguments. Use separate tickets
   for independent calls; repeated identical calls inside one ticket are ambiguous.
3. Run `$capture = (& dotnet $tool capture $run $ticket) | ConvertFrom-Json`.
   A missing completion can be captured again after it arrives, without repeating
   the source read. Changed session history, ambiguous identity, unsupported
   transport and incomplete payloads are explicit blockers. Preserve the ticket
   and diagnostic locally; never repair or reconstruct source bytes.
4. For directory/photo-metadata reads, pass `$capture.requestsFile` and
   `$capture.responseFile` to `ingest`. For photo bytes, pass the response file
   directly to `photo`/`photos`. Profile observations reference the response file
   in `evidenceFiles`. Use a file reader only on the captured evidence needed
   for interpretation; normal coordinator/worker messages contain counts,
   ticket/file references and blockers.

Capture supports original JSON in event `result.content`, native
`structuredContent`, one native text `contents` block, and full `detailedContent`.
It also unwraps MCP `structuredContent` or one text `content` block, including
`isError` wrappers. Native failure messages containing complete JSON, optionally
prefixed `MCP error <integer>: `, retain mixed successes and source failures.
Host-capped text with the exact marker
`Output too large to display.\nFull output saved to: <absolute path>` reads and
retains the original fresh local file, not its preview. Other host transports
need an explicit supported adapter before use; multipart/binary display blocks,
excerpts, missing files and incomplete batches are not reconstructed.

Each ticket retains immutable original start/completion events, host-saved bytes,
ordered requests and the extracted response. Matching uses the stream bookmark,
exact tool name/arguments, worker scope and tool-call ID, with exclusive claims
preventing two tickets from consuming the same invocation. The completion record
retains all outer errors and nested statuses. `toolSucceeded` describes only the
outer tool execution; `nonSuccessResults` counts non-200/error entries where
source statuses are available (otherwise null). Neither proves authorization
or publication readiness. Ingest mixed batches even after an outer failure so
successes remain available; inspect failures and follow the centralized recovery
and authorization rules before further source reads.

## Worker protocol

Read only the assigned job file. All workers share the compiled helper read-only;
write individual inputs/evidence under your own `$run\workers\<batch-id>` folder.
Only the coordinator collects directory/photo-metadata receipts and publishes.

1. Invoke `workiq`; search fresh indexed **M365 People work-profile evidence** for
   the assigned identities with explicit grounding. Batch known names/emails.
   Verify each hit against its exact directory ID/email; similar names are not
   identity evidence. Retain relevant company/work-position location evidence,
   actual returned citations and uncertainty qualifiers, not unrelated personal
   details or personal street addresses.
2. Derive fields using the source hierarchy below, then write one observation per
   assigned Person. If a successful, bounded source search finds no indexed
   profile evidence in its searched scope, record explicit unknowns with that evidence. Failed
   reads instead remain errors; send failed paths/delays to the coordinator for
   its shared recovery decision. Do not independently retry or expand providers.
3. Use `profiles` to persist observations in one helper call per batch. Use
   `cached-photos`; download only cache misses whose fresh metadata succeeded.
   Confirmed `ImageNotFound` needs no binary call and omits `photoPath`. Other 404s,
   nulls, denied reads and service errors are not photo absence.
4. Capture remaining `/users/{exact-directory-id}/photo/$value` reads through
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
