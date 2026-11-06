# Issue tracker: GitHub

Issues and specs live in GitHub Issues for `geevensingh/org-explorer`. Use the `gh` CLI for tracker operations.

## Account and repository routing

Run commands from this clone. Before GitHub operations, clear `GH_TOKEN` and `GITHUB_TOKEN`, then obtain the account-specific token with `gh auth token --user geevensingh` and set it as `GH_TOKEN` for that process only. Never print the token. Verify `gh api user --jq .login` returns `geevensingh` and `gh repo view --json nameWithOwner` returns `geevensingh/org-explorer`; stop if either differs.

Pass `--repo geevensingh/org-explorer` to issue and label commands. File issues only in this repository, not the upstream skills repository.

## Conventions

- Create: `gh issue create --repo geevensingh/org-explorer --title "..." --body-file <path>`. Use a UTF-8 body file for multiline text.
- Read: `gh issue view <number> --repo geevensingh/org-explorer --json number,title,body,labels,comments`.
- List: `gh issue list --repo geevensingh/org-explorer --state open --json number,title,body,labels,comments`; add label or state filters as needed.
- Comment: `gh issue comment <number> --repo geevensingh/org-explorer --body-file <path>`.
- Labels: `gh issue edit <number> --repo geevensingh/org-explorer --add-label "..."` or `--remove-label "..."`. Read `docs/agents/triage-labels.md` for triage roles.
- Close: post the explanation with `gh issue comment`, then `gh issue close <number> --repo geevensingh/org-explorer`. Comment separately even if the issue is already closed.

## Sub-issues and blockers

Use native GitHub sub-issues and issue dependencies when supported. Check the installed CLI's help before using version-dependent flags.

- Link a child: `gh api --method POST repos/geevensingh/org-explorer/issues/<parent>/sub_issues -F sub_issue_id=<child-db-id>`.
- Add a blocker: `gh api --method POST repos/geevensingh/org-explorer/issues/<child>/dependencies/blocked_by -F issue_id=<blocker-db-id>`.
- Resolve a database ID: `gh api repos/geevensingh/org-explorer/issues/<number> --jq .id`. API payloads above require this numeric database ID, not the issue number or node ID.
- Count open blockers: `gh api repos/geevensingh/org-explorer/issues/<number> --jq .issue_dependencies_summary.blocked_by`.

If native relationships are unavailable, put `Part of #<parent>` or `Blocked by: #<number>, #<number>` at the top of the child body, and list children in the parent's task list. A ticket is startable only when every blocker is closed. Surface API failures before using a fallback.

## Pull requests as a triage surface

**PRs as a request surface: no.** Set to `yes` to include external PRs in triage.

When enabled, read PR bodies and comments separately with `gh pr view` and `gh pr view --comments`, and inspect changes with `gh pr diff`. Use `gh pr` equivalents for comments, labels, and closing. Identify external authors by `author_association` outside OWNER, MEMBER, and COLLABORATOR. GitHub shares issue and PR numbers; resolve the item type before acting.

## Skill operations

When a skill says "publish to the issue tracker", create a GitHub issue. When it says "fetch the relevant ticket", read the issue and comments.

## Wayfinding operations

- Map: a single issue labelled `wayfinder:map`, with Notes, Decisions-so-far, and Fog sections.
- Child: link a sub-issue to the map, or use the fallback above. Label it `wayfinder:<type>`, where type is research, prototype, grilling, or task.
- Frontier: inspect the map's open children in map order, excluding assigned tickets and tickets with open blockers; choose the first remaining ticket.
- Claim: `gh issue edit <number> --repo geevensingh/org-explorer --add-assignee @me` as the session's first write.
- Resolve: comment with the answer, close the ticket, then append a context pointer to the map's Decisions-so-far. If the workflow calls for a gist, publish it only with authorization for its content and visibility.
