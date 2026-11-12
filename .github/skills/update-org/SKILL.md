---
name: update-org
description: Build a local, photo-enriched reporting-subtree import for Org Explorer.
disable-model-invocation: true
---

# Update org

Run `/update-org from <full name or email>` in a fresh **local Windows**
org-explorer session. Produce an import path; the user imports it in the app.
This is a refresh, not archival reconciliation or an automatic Workspace change.

## 1. Establish the source and local run

Verify the execution context is local Windows before collecting workplace data.
If remote/cloud execution or the source connection is unavailable, stop and
explain the prerequisite; do not install tools, change accounts or switch providers.

Read [the collection protocol](collection.md). Invoke `workiq` and follow its
current people, structured-read, grounding, binary-download and recovery contracts.
Resolve the exact directory root and connected user's directory ID; ask for
selection when identity is ambiguous. A missing argument requires a root.
Resolve these prerequisites serially. A failed prerequisite lookup stops here;
do not perform an unrecorded recovery before the shared run exists.

Build the bundled helper, then initialize the private staging run. Completion:
one unambiguous root and a helper-created local run path, outside Git/cloud sync.
Use current source results, not any previous conversation or employee fixture.

## 2. Establish the complete reporting subtree

Use helper-generated pending paths and ingest complete structured WorkIQ results.
Claim and finish every source call through the shared
[source coordination gate](collection.md#source-coordination), including the
coordinator's directory reads.
Traverse every direct-report page, including leaves and excluded accounts.
Follow the root's Manager chain to its sourced endpoint; ancestors supply context,
not permission to enumerate their other reports.

Keep distinct emails distinct. Exclude only the explicitly labeled
`(NON EA SC ALT)` accounts; retain contractor primary accounts. Other suspected
duplicates remain separate and are flagged, not merged by name. A retained report
whose Manager would be excluded is a blocker, not permission to invent a move.

Completion: `plan` has no pending directory paths or errors, and produces
disjoint local job files. Never silently truncate the requested subtree.

## 3. Enrich in bounded parallel batches

Fetch fresh photo metadata for all retained people, at most 50 paths per
structured-read call, and ingest every individual result. Dispatch the job files
to **up to two concurrent subagents**, each with one disjoint batch. Use a fresh
agent for the next independent batch, not a reusable message-driven worker pool.
If subagents are unavailable, explain this before collection and ask whether to
continue serially.

Give each worker the original requested root, its job path, run path, helper path,
and [worker protocol](collection.md#worker-protocol). Workers query fresh indexed
M365 work-profile evidence, preserve source identity, record field uncertainty,
and materialize or verifiably reuse photos locally. They return only job identity,
counts, local paths and errors. Neither image payloads nor employee lists belong
in coordinator messages, Git, issues, Libraries or shared attachments.

Completion: every job finishes, every retained person has a fresh accounted-for
profile observation, and each photo has bytes or a confirmed `ImageNotFound`.

## 4. Recover or stop

Use `source-status` to settle every active claim before the coordinator consumes
`source-recover` once. The gate owns the invocation-wide allowance and returned
retry delays; follow its granted targets rather than re-running whole batches.
Retain local successes and deferred first reads. An interrupted worker requires
the explicit stopped-worker handoff in the collection protocol.
Authentication/access/policy denial stops this invocation without alternate
paths, tools, agents, strategies or providers. Notify the coordinator immediately;
it stops active workers' further source calls and queued dispatch. Report the
authorization prerequisite rather than attempting recovery or resetting the run
to evade it.

An uncompleted lookup is not "no photo" or "unknown geography." Unresolved service
errors or incomplete hierarchy block publication. Retain local progress and
diagnostics; report the blocker rather than a ready import path. A new invocation
queries hierarchy, profile evidence and photo metadata anew; verified photo bytes
from an interrupted run can still be reused.

## 5. Publish once

After all workers stop, run `publish`. The helper checks coverage, field semantics,
the complete proposed graph and photo bytes through the real app's non-UI Import
preview. It does not open or apply changes to the user's Workspace.
The user's app preview remains authoritative for existing Workspace state,
including previously Archived Managers; standalone validation cannot inspect it.

Completion: the helper atomically moves the validated run to an immutable local
export folder and returns `importPath`. Keep earlier exports unchanged. Return
that exact `updates.json` path, Person/photo counts and material review flags.
Explain that its adjacent `photos` folder must stay in place. Do not claim a ready
export when the helper fails, or run the full real org as an automated test.
