## Agent skills

### Issue tracker

Issues and specs live in GitHub Issues for `geevensingh/org-explorer`. Read `docs/agents/issue-tracker.md` before tracker operations.

### Triage labels

Use the five default triage roles. Read `docs/agents/triage-labels.md` before applying triage labels.

### Domain docs

Single-context: root `GLOSSARY.md` and `docs/adr/`. Read `docs/agents/domain.md` before exploring the codebase.

### Documentation handoff

Before handing off design Markdown, publishing its spec/tickets, implementing
an issue with a Documentation handoff, or choosing a PR destination for those
changes, read
`docs/agents/documentation-handoff.md`. It owns source-commit verification,
integration destinations, and joint documentation/implementation delivery.

### Tests

Read `docs/testing.md` before placing, migrating or running tests, changing CI,
or claiming validation. It owns required-smoke versus explicit scale selection
and exact-candidate verification. Default to non-UI local checks; desktop
automation requires hosted Windows or an explicitly authorized isolated desktop.
