# Documentation handoff

Use this contract for Markdown produced or changed by a design/spec workflow
whose corresponding behavior still needs implementation. The planning result is
a pushed, unmerged documentation commit. Its changes reach the default branch
with the matching implementation, not through an independent planning-docs merge.

Documentation corrections and agent instructions that are themselves the complete
change can ship normally; they do not require unrelated application changes.

## Handoff record

Include a `## Documentation handoff` section in the spec and every implementation
ticket. For a design with Markdown changes, use this record:

- **Source repository:** repository containing the pushed documentation commit.
- **Source branch:** pushed branch keeping that commit reachable.
- **Base commit:** full commit SHA immediately before the documentation commit.
- **Documentation commit:** full immutable SHA of the approved Markdown changes.
- **Markdown files:** exact repository-relative paths changed by that commit.
- **Integration owner:** coordinating session or owning ticket.
- **Integration branch:** receiving branch and intended destination for child PRs.

Complete and verify the source fields before publishing a spec or ticket as
`ready-for-agent`. Integration owner/branch may be explicitly pending during
planning; the receiver must establish them before implementation or child dispatch.
Exact paths belong in this record even when the surrounding spec/ticket avoids
implementation file paths.

If the design produced no Markdown changes, use
`None (this design produced no Markdown changes).` Do not create an empty commit.
An empty current diff is not proof of this case: the documentation might already
be committed. Missing or unpublished documentation is an incomplete handoff, not
`None`.

## Verify the source

Use `eng\Verify-DocumentationHandoff.ps1` for the Git checks in this contract,
rather than rebuilding them in each workflow. Use a full-history source clone;
confirm its configured remote matches **Source repository** and follow
[account routing](issue-tracker.md#account-and-repository-routing) before fetching.
The verifier uses the process's `gh` credential helper, bypassing cached Git
credentials without changing saved configuration.

Set the source/base/documentation/file variables from the approved handoff and
the default branch from the source repository:

```powershell
.\eng\Verify-DocumentationHandoff.ps1 `
  -SourceBranch $sourceBranch -DefaultBranch $defaultBranch `
  -BaseCommit $baseCommit -DocumentationCommit $documentationCommit `
  -MarkdownFiles $markdownFiles | ConvertTo-Json -Depth 3
```

The checkout containing the script and its `origin` remote are the defaults;
use `-RepositoryPath` and `-Remote` when verifying a different source clone.
The command fetches fresh source/default refs into temporary refs and removes
them on success or failure, preserving the checkout, local writing, and existing
tracking refs.

A source branch may advance after the approved documentation commit: that commit
must remain **reachable**, not equal the branch tip. Keep the immutable
documentation SHA in the record; the returned source/default tips are observations,
not replacements. The verifier checks the single immediate base parent, a nonempty
Markdown-only delta, the exact changed path list, and that the documentation commit
is not already reachable from the freshly fetched default branch. Renames are
treated as deletion/addition; list both repository-relative Git paths.

A failed command leaves the handoff unverified. It does not replace approval of
the Markdown, incorporation checks after a cherry-pick, or the joint-delivery
decision about whether the matching behavior is complete.

## Prepare the handoff

For Markdown changes, after the user confirms the shared understanding and
approves the documentation:

1. Inspect the worktree and isolate the approved Markdown changes. Include
   affected design documents, glossary entries, and ADRs, not unrelated work.
2. Create or identify a focused documentation-only commit containing those
   changes. Preserve unrelated local changes and follow repository commit rules.
3. Push the source branch using the configured repository/account routing.
   Run the shared verifier against the recorded source/base/documentation fields
   and approved Markdown file list.
4. Return the handoff record. Keep the source branch reachable and unmerged into
   the default branch until delivery with implementation.

Producer completion requires the verified record or the verified no-changes case,
not merely a local edit or a narrative summary. If publishing or verification
fails, report the unfinished handoff rather than declaring it ready.

## Carry the handoff into specs and tickets

Read any source issue and its comments. Preserve a verified no-changes record
unchanged. For a committed documentation handoff, run the shared verifier before
publishing ready work, even if a prior session verified the record. Preserve the
source repository, branch, SHAs, and Markdown file list exactly. Ownership and the
integration target can become more specific as implementation is assigned.

If the context says planning Markdown changed but supplies no verifiable record,
obtain the producer's handoff before publishing ready work. Do not reconstruct
the changes from a prose summary or silently substitute a different commit.

For multiple tickets carrying Markdown changes, designate one implementation
coordinator as integration owner. Every ticket carries the record and the shared
integration destination.
Establishing the documented baseline is a start prerequisite, not a later
documentation-cleanup ticket. Show this arrangement when seeking approval of the
ticket breakdown; keep genuine ticket-to-ticket blocking edges explicit.

## Consume and verify before implementation

First inspect the handoff record. For the verified
`None (this design produced no Markdown changes).` case, skip the documentation
checklist below and the joint-delivery gates under Deliver together. No source
reference, source/base/applied SHAs, incorporation proof, or documentation-specific
integration owner/branch is required. Continue the ordinary implementation and PR
workflow, including its genuine ticket dependencies, intended PR target,
validation, and user approvals; this contract does not require an integration
branch for that case.

Missing, unpublished, or unverified handoffs do not qualify for this bypass. For
a committed documentation handoff, complete the following checklist:

1. Read the record and the source Markdown. Run the shared verifier in the
   receiving source clone to fetch and verify the pushed source, source/base SHAs,
   and approved Markdown delta.
   An inaccessible or mismatched source blocks implementation.
   If the planning changes already reached the default branch without their
   implementation, report the sequencing failure rather than treating their
   availability as a completed handoff.
2. Establish the integration owner and branch. For one implementation ticket,
   this can be its implementation branch. For multiple tickets, the coordinator
   establishes a common integration branch and incorporates the documentation
   before dispatching children.
3. Use the documentation commit as a branch base when its ancestry is intended;
   otherwise cherry-pick the focused commit into the existing implementation
   branch. Preserve local changes and resolve conflicts explicitly. Check for an
   already incorporated handoff before applying it again.
4. Verify incorporation through commit ancestry when the source commit is
   preserved. After a cherry-pick, compare the approved Markdown delta with the
   applied change, including any conflict resolutions. Record the source SHA,
   applied SHA, and integration branch in the receiving session's handoff or
   implementation PR. Merely reading the issue is not incorporation.
5. Before a child starts, verify that its own checkout includes the coordinator's
   applied documentation baseline. When child PRs are requested, target the
   integration branch, not the default branch; otherwise the first child could
   publish documentation for the unfinished remainder of the feature.

## Deliver together

These joint-delivery conditions apply to handoffs carrying Markdown changes.

An implementation PR, or the coordinator's integration PR for multiple tickets,
may be opened as a draft while the handed-off Markdown still describes behavior
under construction. It may target the default branch and include that Markdown
before the behavior is complete, including the ordinary early-draft step after
the first child merge. Child PRs still target the integration branch.

The implementation owner verifies that the behavior described by the handed-off
Markdown changes is implemented and that the final implementation/integration PR
includes both. For multiple tickets, child results integrate first; the completed
feature and its documentation then reach the default branch together.

Check this delivery condition before marking the PR ready and before merging to
the default branch, not before opening a draft. Keep the PR in draft while the
documented behavior is incomplete; planning Markdown must not land in the default
branch ahead of its implementation.

Follow ordinary validation and user approval requirements. A successful handoff
is not permission to merge, and a documentation-only planning PR is not delivery
of the corresponding behavior.
