# Local transactions with independent snapshot synchronization

**Status:** Implemented as the normal Create/Open path (#91-#100).
Final joint-delivery acceptance requires fresh normal and full rich-scale hosted
verification of the exact integration candidate; implementation is not that evidence.

Use local SQLite outside the synchronized folder as the authoritative workspace
store, and exchange validated, complete logical snapshots through the existing
external folder provider. This replaces readable canonical files and direct
external editing in [ADR 0001](0001-readable-files-external-sync.md), and the
storage/publication architecture and mandatory safe-close veto in
[ADR 0007](0007-incremental-workspace-storage.md). Those ADRs retain historical
rationale only; there is no hidden legacy authority or direct-file writer.

Local durability must not depend on synchronization, and navigation must not
depend on either. Local failures or unresolved divergence pause edits, not
browsing; synchronization outages alone do not pause edits. Normal exit waits
for local commit and publication, but an informed Exit anyway always exits.
Preserve both divergent versions for explicit selection instead of merging or
using last-writer-wins.

The trade-off is giving up directly editable canonical files in favor of a
transactional local commit boundary and a separate, eventually consistent
interchange protocol. SQLite does not itself solve synchronization or UI
responsiveness, and its live database must not be folder-synchronized. Complete
snapshots simplify sequential cross-computer use but require validated adoption,
explicit ancestry, resource-safe retention, and separate publication status.
Per-edit operation replication was not selected.

Local SQLite uses WAL with `synchronous=FULL` on every actual connection, verified
before a writer starts. A compatible format-3 store is fully validated before
establishing WAL; unavailable negotiation is explicit, never a rollback-journal
fallback or a logical-format migration. A real independent reader can retain
its coherent older snapshot while a later note generation commits and a fresh
reader observes it. One-writer serialization, the one-second SQLite busy setting,
the queue-inclusive three-second application stall and explicit recovery remain.

The trade-off is local `-wal`/`-shm` state and checkpoint ownership. SQLite's
default automatic checkpoints and last-connection cleanup are retained; neither
normal exit nor Exit anyway adds a blocking checkpoint. A long reader can delay
checkpointing and grow the WAL without making a successful Commit unpublished
or unsaved locally. The main database alone is not a complete backup: never copy
it as an interchange artifact or manually delete a WAL. Complete logical
snapshots include committed, uncheckpointed content and every required resource.
The bundled native SQLite is verified as 3.53.4, including the
[WAL-reset fix](https://www.sqlite.org/wal.html#the_wal_reset_bug).

See [the approved design](../reliable-nonblocking-saves.md) for failure behavior,
exit choices, retention, and measurable end-to-end acceptance criteria, and the
[implemented format/protocol](../workspace-format.md) for SQLite format 3 and
self-contained interchange-1 `.oesnapshot` artifacts. No
migration is required; recreating the workspace is acceptable, without deleting
the old one. Snapshots own every required resource byte; no live database or
journal is synchronized. Diagnosing the discarded engine is not an
implementation prerequisite.
