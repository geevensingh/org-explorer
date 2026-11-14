# Readable files under external synchronization

**Planned replacement:** [ADR 0008](0008-local-transactions-independent-sync.md)
supersedes the readable-canonical-file and direct-external-editing requirements
with local SQLite and independent snapshot synchronization. The original decision
below still describes the current implementation; deliver the replacement
documentation and behavior together.

Canonical people, separate notes, photos, and history live in readable local
workspace files, not an app-hosted database, encrypted app-only store, or one
monolithic data file. External folder providers own synchronization and conflict
resolution; clients save locally and react to externally changed files without
implementing semantic merges. This favors offline ownership, inspectability, and
provider independence while accepting that provider conflict copies or competing
versions may need resolution outside the app.
