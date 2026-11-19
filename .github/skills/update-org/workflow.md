# Collector workflow

Use this command flow for new runs. The [standing policy](collection.md#standing-execution-policy)
already authorizes bounded recovery, local producer repairs and explicitly
unavailable enrichment. The helper owns policy, source attempts, job assignments
and serialization; WorkIQ remains the only authenticated workplace client.

## Establish the runtime and start once

1. Verify actual local Windows execution, local AppData access and the configured
   WorkIQ connection. An RDP session name is not evidence of cloud execution.
   Load exact configured WorkIQ tool definitions and required source contracts.
   Verify the calling runtime exposes its complete event stream and, for native
   workers, their runtime agent IDs. A missing descriptor field is a local defect,
   not proof that workers or capture are unavailable.
2. Resolve the requested root and connected user's directory IDs serially through
   fresh WorkIQ reads. Accept either `/update-org from <identity>` or
   `/update-org for <identity>`; the preposition is not part of the name.
   Ask for genuine identity ambiguity, not a covered execution-policy decision.
3. Build the bundled producer. Restore locked packages only when dependencies or
   restore assets are missing. Then call:

   ```powershell
   dotnet $tool start $resolvedRootName $rootDirectoryId $connectedUserId
   ```

   This creates one private run with the standing best-effort policy already
   recorded before its first coordinated source read. It returns `runPath`,
   worker limit, host limits and collector/protocol fingerprints. Do not follow
   it with another policy-authorization call. For a genuinely serial runtime,
   pass `1` as the final argument; normal operation permits two workers.

The generic `init` command intentionally remains strict for older callers.
Use `start`, not `init` followed by a remembered authorization step.
Never install tools, switch accounts/providers or infer source permission.

## Prepare one source call

```powershell
dotnet $tool prepare-next $run $worker $phase $exactToolName $eventsFile $runtimeAgentId
```

Omit the final argument only for root/coordinator calls. A serial root caller
uses worker name `coordinator`; native workers use their supplied runtime ID,
not a source-agent override.

| Phase | Caller and preparation |
|---|---|
| `directory` | Coordinator. Selects up to 25 exact pending paths from `plan`. Reconcile any `managerRefreshes` through the documented fresh-Manager gate before recollection. |
| `metadata` | Coordinator, after directory completion. Selects up to 25 retained photo-metadata first reads or permitted retries. |
| `profile` | Assigned worker. Selects up to four unfinished assigned identities and composes one concise People-only grounding query from authoritative IDs/emails. |
| `photo` | Assigned worker. Verifies/materializes local cache hits, then selects one remaining permitted binary lookup. |

Inspect the returned `claim.claimId`. A null claim authorizes no call: honor the
blocker/retry deadline or materialize already captured results. The prepared
`request.toolName` and `request.arguments` are the one exact invocation to copy.
The helper has already stored the descriptor, bound its claim and bookmarked
the event stream in `ticketFile`. Do not retype or add employer qualifiers.

Invoke that configured WorkIQ tool once. Structured and binary calls finish
without rebuilding response JSON:

```powershell
dotnet $tool finish-capture $run $ticketFile
```

This captures the original result, preserves every structured status, finishes
the bound claim and ingests the complete structured batch or validates/materializes
successful photo bytes. Source failures remain failures/unavailable outcomes,
not a rolled-back batch or a fabricated success.

After a local materialization failure, correct the filesystem problem and rerun
`finish-capture` with the same ticket. It resumes the retained completed outcomes
without another source attempt; repeated materialization preserves original
receipt identities and lookup times. Superseded attempts cannot overwrite newer work.

Profile retrieval additionally needs semantic association to each identity:

```powershell
dotnet $tool capture $run $ticketFile
dotnet $tool source-finish $run $claimId $completeOriginalOutcomesFile
```

Parse captured results locally into the documented complete per-target outcomes;
keep every status/error and mixed success. The low-level `ingest` and
`photo`/`photos` remain available for retained-result repair. Neither tool display text nor a host temporary path substitutes
for original evidence.

If event flushing is late, wait briefly and recapture the **same ticket**, bounded
by the [capture contract](collection.md#recover-an-unverifiable-capture).
Never repeat WorkIQ to repair local capture, argument or serialization errors.
If preparation fails before invoking WorkIQ, retain its claim/descriptor and
diagnostic; a local failure does not prove a remote call happened or stopped.

## Dispatch, persist and resume jobs

After all retained photo-metadata reads are accounted for:

```powershell
dotnet $tool next-job $run $freshWorkerName
```

A granted `job` contains its immutable job file, private worker folder, Person
count and job ID. Launch one fresh agent for that assignment and immediately
supply its actual runtime agent ID. A null job is not permission to launch
another worker. Assignments persist across coordinator restarts, honor one
job per worker and the run's one/two-worker limit, and never expire by timeout.

Workers use `prepare-next` for source work. After each successful exact-identity
profile lookup, write only its interpreted fields/reasons to a local draft:

```json
{
  "fields": {
    "officeState": {"state":"unknown","value":null,"reason":"Successful bounded evidence did not establish work state."},
    "officeCountry": {"state":"unknown","value":null,"reason":"Successful bounded evidence did not establish work country."}
  },
  "warnings": []
}
```

Then call:

```powershell
dotnet $tool record-profile $run $directoryId $captureJsonFile $draftFile
```

Use the prepared request's `captureFile` after the capture completes; do not
reconstruct its directory or a run UUID by hand.

The helper supplies the exact directory email, original captured lookup time,
evidence array and stable observation-input path. It requires the latest
completed, claim-bound grounding lookup. Verify source identity and interpret
the evidence before writing the draft; the helper does not invent geography.
Unavailable lookups need no success/unknown observation. Correct only failed
local drafts using retained evidence; preserve earlier successes.

```powershell
dotnet $tool finish-job $run $worker $jobId
```

This verifies assigned profile/metadata coverage, observations, photo outcomes
and byte hashes before freeing the slot. A worker's report alone cannot mark a
job complete. No private dispatch script or hand-edited ledger is needed.

For a confirmed stopped worker, first capture/finish its outstanding source
work or use the existing audited stopped-source handoff. Then:

```powershell
dotnet $tool resume-job $run $jobId $replacementWorker $stoppedWorkerReason
```

Use a distinct fresh worker name. The original job and lookup attempts remain;
late completion by the old owner is rejected. For genuine worker/capture
unavailability, resume under root worker name `coordinator` and proceed serially
with the same WorkIQ tools and coverage. No timeout or missing diagnostics can
authorize replacement source reads.

## Status, estimates and delivery

`status RUN` returns compact phase, job/enrichment counts, next directory/metadata
targets, active claim count, source-wide retry deadline and collector identity.
`source-status` remains the detailed diagnostic interface, not normal progress
output. Never copy a full ledger or employee list into a user progress message.

After directory discovery, report population/scale. After the first finished
sample, use the helper's observed rate and approximate remaining time, clearly
conditional on that rate continuing. Before a sample, report that no estimate
is established. Service waits, blockers and runtime overhead can extend it.

Continue internally after each job; user-facing updates belong at phase changes,
meaningful progress milestones, real blockers and final delivery—not every batch.
Routine optional failures use their recorded retry/unavailable outcome without
another approval question. Denials, unsafe diagnostics and unresolved required
reporting still stop or block as specified in the collection protocol.

After every job finishes, run `publish`. Return the exact immutable `updates.json`
path and complete/degraded counts, distinguish unavailable lookups from confirmed
photo absence, and keep the adjacent `photos` folder. Preview in the app before
applying; the collector never applies a Workspace.

## Versioned compatibility

New runs record collector-state version, worker policy, assembly identity/hash
and bundled protocol hash. Those fingerprints identify the implementation;
they never reset a source budget or retroactively enable best-effort policy.
Do not rebuild or replace a collector casually during a long run.

For a stopped compatible unversioned run, use
`upgrade-run RUN REASON`. It obtains an exclusive lease, retains exact original
run/ledger snapshots and a resumable upgrade journal, and changes only version
metadata. Original attempts, successes, failures and strict/best-effort policy
remain intact. An interrupted replacement resumes only from its recorded hashes.

Unknown future versions and historical parallel-branch policy extensions are
rejected explicitly, without dropping fields or changing files. Use the original
producer for those exceptional older runs rather than guessing a migration.
Sealed/completed exports are immutable and cannot be upgraded. Verified photo
reuse still requires fresh metadata, connected-user identity and intact hashes.
