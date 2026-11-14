# Local transactions with independent snapshot synchronization

**Status:** Accepted design; implementation pending.

Use local SQLite outside the synchronized folder as the authoritative workspace
store, and exchange validated, complete logical snapshots through the existing
external folder provider. This replaces readable canonical files and direct
external editing in [ADR 0001](0001-readable-files-external-sync.md), and the
storage/publication architecture and mandatory safe-close veto in
[ADR 0007](0007-incremental-workspace-storage.md). Those ADRs still describe the
current implementation until this decision and its matching behavior land together.

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

See [the approved design](../reliable-nonblocking-saves.md) for failure behavior,
exit choices, retention, and measurable end-to-end acceptance criteria. No
migration is required; recreating the workspace is acceptable, without deleting
the old one. Implementation details such as snapshot packaging and resource
reuse remain open within those criteria; diagnosing the discarded engine is
not an implementation prerequisite.
