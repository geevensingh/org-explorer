# Documentation handoff

Use this contract for Markdown produced or changed by a design/spec workflow
whose corresponding behavior still needs implementation. The planning result is
a pushed, unmerged documentation commit. Its changes reach the default branch
with the matching implementation, not through an independent planning-docs merge.

Documentation corrections and agent instructions that are themselves the complete
change can ship normally; they do not require unrelated application changes.

## Handoff record

Include a `## Documentation handoff` section in the spec and every implementation
ticket. Use this record:

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

## Prepare the handoff

After the user confirms the shared understanding and approves the documentation:

1. Inspect the worktree and isolate the approved Markdown changes. Include
   affected design documents, glossary entries, and ADRs, not unrelated work.
2. Create or identify a focused documentation-only commit containing those
   changes. Preserve unrelated local changes and follow repository commit rules.
3. Push the source branch using the configured repository/account routing.
   Verify that the remote commit is retrievable and that its diff from the
   recorded base matches the approved Markdown file list and content.
4. Return the handoff record. Keep the source branch reachable and unmerged into
   the default branch until delivery with implementation.

Producer completion requires the verified record or the verified no-changes case,
not merely a local edit or a narrative summary. If publishing or verification
fails, report the unfinished handoff rather than declaring it ready.

## Carry the handoff into specs and tickets

Read any source issue and its comments, and preserve the source repository,
branch, SHAs, and Markdown file list exactly. Ownership and the integration target
can become more specific as implementation is assigned.

If the context says planning Markdown changed but supplies no verifiable record,
obtain the producer's handoff before publishing ready work. Do not reconstruct
the changes from a prose summary or silently substitute a different commit.

For multiple tickets, designate one implementation coordinator as integration
owner. Every ticket carries the record and the shared integration destination.
Establishing the documented baseline is a start prerequisite, not a later
documentation-cleanup ticket. Show this arrangement when seeking approval of the
ticket breakdown; keep genuine ticket-to-ticket blocking edges explicit.

## Consume and verify before implementation

1. Read the record and the source Markdown. Fetch the pushed reference into the
   receiving worktree and verify the source/base SHAs and approved Markdown diff.
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

The implementation owner verifies that the behavior described by the handed-off
Markdown changes is implemented and that the final implementation/integration PR
includes both. For multiple tickets, child results integrate first; the completed
feature and its documentation then reach the default branch together.

Check this delivery condition before opening a PR to the default branch. A branch
containing only planning Markdown remains a handoff awaiting implementation, not
an independently ready default-branch PR.

Follow ordinary validation and user approval requirements. A successful handoff
is not permission to merge, and a documentation-only planning PR is not delivery
of the corresponding behavior.
