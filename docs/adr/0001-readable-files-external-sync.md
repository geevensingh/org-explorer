# Readable files under external synchronization

**Status:** Superseded by [ADR 0008](0008-local-transactions-independent-sync.md).
The normal shipping path uses local SQLite and independent complete-snapshot
synchronization. The readable-file engine and direct-external-editing path are
retired; the original decision below is historical rationale, not a second authority.

Canonical people, separate notes, photos, and history live in readable local
workspace files, not an app-hosted database, encrypted app-only store, or one
monolithic data file. External folder providers own synchronization and conflict
resolution; clients save locally and react to externally changed files without
implementing semantic merges. This favors offline ownership, inspectability, and
provider independence while accepting that provider conflict copies or competing
versions may need resolution outside the app.
