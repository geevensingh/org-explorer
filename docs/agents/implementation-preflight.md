# Implementation preflight

Complete this gate before implementation edits, integration-branch creation or
ticket dispatch, including resumed work and direct fixes outside skills.
A PR-time fetch is too late. Passing Git ancestry alone does not approve an old
spec's assumptions.

1. Establish the intended base from the user's request and any integration owner.
   Ordinary work uses remote `main`; an integration or stack branch requires an
   explicit intended destination. Read [account routing](issue-tracker.md#account-and-repository-routing).
   From the implementation worktree, run:

   ```powershell
   .\eng\Test-ImplementationBaseline.ps1 -BaseBranch main
   ```

   The script checks a clean named branch and no in-progress operation, verifies
   the GitHub account/repository, fetches the exact branch and compares HEAD with
   the fetched remote-tracking SHA, never local `main`. It reports the starting
   branch/SHA, base ref/SHA, ahead/behind counts and divergence. Fetch failure,
   missing refs and stale/diverged ancestry fail closed. It changes no checkout
   files or branch tips; only the remote-tracking ref is refreshed.
   GitHub fetches require the repository's HTTPS origin so the verified `gh`
   account, rather than an independently selected SSH identity, supplies credentials.

   Report these values before proceeding. On failure, stop before edits and
   obtain agreement to update or intentionally stack, preserving existing work.
   Updating a user-owned checkout requires permission; never silently pull,
   reset, stash or rebase it. After an agreed update or base change, rerun.
   Ahead-only commits are retained, not automatically approved as task scope.

2. Reconcile the spec against the verified base's current product behavior.
   Read the affected implementation, tests, applicable ADRs and current docs;
   inspect their recent upstream history. If the spec's source revision is
   known, compare its affected paths with the fetched base, for example
   `git diff <spec-source-sha> <base-sha> -- <affected-paths>`.
   Otherwise, establish each material assumption from current files instead
   of treating the spec's age or an unchanged issue as evidence of freshness.
   State the assumptions checked and any upstream changes affecting them.
   A material contradiction in behavior, UX or acceptance criteria is a
   checkpoint: stop and ask the owner to reconcile it before implementation,
   rather than restoring superseded behavior or guessing which source wins.
   Completion means every material assumption in the affected behavior is
   supported or explicitly reconciled by the owner.

3. Use this verified baseline for the work. For a pushed integration/stack
   branch, run the same command with its branch name instead of `main`. For an
   unpushed coordinator-owned integration branch, the coordinator first completes
   this gate against its intended remote base, incorporates any
   [documentation handoff](documentation-handoff.md), and records its exact
   integration tip. Each ticket worktree then runs:

   ```powershell
   .\eng\Test-ImplementationBaseline.ps1 -BaseBranch integration/example -LocalBase -ExpectedBaseRevision <full-coordinator-tip-sha>
   ```

   Run in the ticket's own worktree. The local branch must still match that
   exact SHA, and the ticket HEAD must include it. This mode is only for the
   explicitly intended, unpushed integration baseline, not an offline fallback
   for failed fetches or stale `main`. A mismatched/missing baseline requires
   coordination, not a reset. If the script is not yet in a newly created ticket
   worktree, invoke it by absolute path from the coordinator's worktree with
   `-RepositoryPath` pointing at the ticket worktree.

Record the successful SHAs and reconciled assumptions in the session handoff
(and implementation PR when requested). Rerun after changing bases or resuming
implementation; a prior pass is a point-in-time check, not a freshness lease.
