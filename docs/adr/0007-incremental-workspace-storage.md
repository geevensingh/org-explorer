# Incremental readable-file workspace storage

Full-copy generations make even a Preferred-name autosave duplicate unrelated
People, photos, accepted Imports, and history. Use copy-on-write documents with an
incremental snapshot index, reusing unchanged documents and atomically publishing
the new state rather than copying the population on every edit. Preserve the
readable-file and provider-owned synchronization contract of
[ADR 0001](0001-readable-files-external-sync.md).

**Status:** Implemented in Workspace version 3 (#41-#47). The normal shipping
workflow creates and opens this format; no opt-in factory or old-generation
writer remains. See the [Workspace format](../workspace-format.md).

The combined [storage safety protocol](../incremental-workspace-storage-protocol.md)
defines incremental roots, current/recovery publication, interruption states and
reclamation safeguards used by the shipping implementation.

## Consequences

- A Preferred-name save writes only affected documents and incremental commit/index
  metadata. It must not create new copies of unrelated Person/history documents,
  photos, or accepted Import data, or rewrite a full-population index per save.
  Initial creation/import can necessarily write the supplied population. This is
  a write/storage goal, not permission to weaken complete-state validation.
- Copy-on-write describes app writes, not a ban on external editing. Valid direct
  edits to currently referenced readable files, including consistent Person/history
  edits and same-path photo replacement, remain supported. Shared references alone
  therefore do not make an older snapshot an independent recovery copy.
- Maintain independent recovery data incrementally too. Recovery documents must
  not share their physical file bytes with externally editable current documents,
  including through hard links. The storage target is an active data set plus an
  independent recovery data set and bounded bookkeeping/recent-version overhead;
  ordinary edits update only affected documents and index metadata in each.
- Keep all recorded Profile changes and Historical team context, not a restorable
  snapshot of every autosave. Genuine retained history and its required resources
  can grow; redundant storage copies should not grow indefinitely. This does not
  introduce edit-version history for Person or Team notes.
- Preserve coherent, all-or-nothing publication, including multi-Person Imports
  and their resources. Flush and validate required data before publication;
  incomplete or invalid external delivery retains the last validated state and
  blocks writes rather than being accepted, repaired, or semantically merged.
  Uncommitted staging files are not automatically adopted.
- Automatically reclaim redundant app-owned versions only when current data,
  recovery, retained history, and interrupted operations no longer need them.
  Do not delete unknown files or provider conflict copies. Publication, recovery
  selection, and reclamation ordering are guarded by durable receipts,
  exact validated roots and inventoried byte digests, including interrupted saves
  and partial/out-of-order synchronization;
  local unreachability alone does not justify immediate deletion.
- Use an explicitly new Workspace format. For this change, reject older formats
  with clear guidance rather than silently converting or overwriting them.
  Migration and cleanup of accumulated legacy generations are out of scope: the
  owner will recreate the sole installed Workspace. Do not delete that Workspace
  as part of implementation.

## Approved publication-conflict amendment (2026-11-12)

The owner approved durable postpublication conflict handling with stated capture
limits and explicit external resolution. This deliberately supersedes #44's
unconditional precommit rejection/preservation promise for a provider racing the
last check and publication; it is not evidence that the old guarantee was met.
Earlier stale-base/draft rejection remains unchanged.

Use expected-presence-aware publication, unique displaced-file backups, durable
source roots and independently sealed bytes actually captured. Positively
published conflicts and indeterminate outcomes remain distinct from unsaved
refusals. They block further mutation/safe close/restart until explicit
revision/conflict-bound external-resolution acknowledgment. Ordinary storage
maintenance and draft discard cannot resolve them or replay the mutation.

This keeps provider independence/direct editing, but does not serialize
noncooperating writers, capture an atomic external graph or preserve unobserved
versions. Windows replacement failures and power-loss namespace durability remain
bounded by the actual OS contracts. Conflicting transactions may retain unbounded
evidence/source resources. The unreleased v3 selector has a required fail-closed
storage-protocol discriminator; record-family/Import versions remain unchanged.

A stronger cooperative-writer transaction would require new restrictions on
editors/providers. That alternative was not selected. See the combined protocol
for the actual publication/evidence/resolution records and limits.

## Considered options

- **Bounded full-copy generations:** limit retained space but still rewrite and
  synchronize unrelated data on each profile save.
- **Copy-on-write documents and an incremental index:** chosen to retain atomic
  pointer publication while eliminating full-population writes. Independent
  incremental recovery addresses the separate risk of external file edits.
- **In-place documents with a transaction journal:** can reduce writes, but makes
  recovery from interrupted local rewrites versus partial provider delivery more
  complicated than retaining the publication model.
- **SQLite:** offers transactions and incremental page writes, but would reopen
  the agreed readable canonical files and external-folder synchronization
  architecture rather than solve this problem within it.
