# Person-first import preview

Status: approved in the November 25, 2026 design interview; implementation is
pending. Deliver this document with its corresponding implementation, not as an
independent planning-document merge.

Source baseline: `caec4da7a96a57a5e49b3ea047f29eaabf299835`, matching freshly
fetched `origin/main` during the interview.

## Purpose and scope

Make the preview primarily answer "What will Apply change?" rather than present
a running log of supplied values. Prioritize people with actual differences,
combine each Person's changes, and retain complete reviewable details.

This changes review presentation only. Preserve the
[partial-update import contract](import-format.md),
[ADR 0004](adr/0004-external-partial-imports.md), the native Windows visual
direction, and existing import safety:

- Keep the import format, validation, and whole-batch Apply behavior unchanged.
- Preserve omitted people and fields, explicit clears, archive/reactivation,
  Favorite changes, and evidence recording.
- Keep errors blocking the entire batch, warnings visible, exact replay a
  distinct no-op, and stale previews rejected before Apply.
- Do not add per-Person or per-field selection, filtering, an evidence-audit
  interface, new import limits, or another UI framework.

Use the existing glossary's Person, Observation, Profile change, and Import batch
language. No new domain term or ADR is required.

## Review hierarchy

Retain the import-source controls and explicit preview/Apply flow. Present:

1. Validation errors, warnings, and relevant batch status separately from Person
   entries, above the review list.
2. Batch impact counts.
3. One entry for each new or changed Person.
4. A counted, initially collapsed "No Person changes" section at the end.

Do not mix warnings, errors, or repeated omission/evidence explanations into the
Person-change list. Collapsing entries or the final section must not hide
diagnostics or batch status.

Partition people into the main list and final section while preserving import-file
order within each. Use email as the primary identity. Keep field ordering
consistent with the existing review order; do not introduce alphabetical or
severity sorting.

## One expandable entry per changed Person

Each main-list Person has one disclosure entry, initially collapsed. Expanding it
reveals details directly underneath, not in another window and not as another
top-level entry for the same Person.

### Collapsed presentation

The entire collapsed entry occupies **one line**, including identity and summary.
Show complete inline differences when they fit within that line at the current
available width. For example, using a fictitious identity:

```text
> person@example.com -- Office state: (none) -> Washington; Office country: (none) -> USA
```

When the full differences do not fit, use a concise count instead:

```text
> person@example.com -- 2 changes
> person@example.com -- 12 changes
```

Use available rendered space, not an arbitrary field-count cutoff. Long or
multiline values therefore use the count presentation even when few fields
change. Reevaluate the presentation when the available width changes. Do not wrap
the collapsed entry or truncate before/after values into ambiguous fragments.

Add, Archive, and Reactivate remain explicit in the collapsed entry, including
when ordinary differences are represented by a count:

```text
> new.person@example.com -- Add
> person@example.com -- Archive; 12 changes
```

For existing people, the change count includes each distinct changed field or
status/preference action once. A visible Archive or Reactivate label identifies
an action included in that total, not an extra change.

The full identity and review details must remain accessible in compact layouts.
Use ordinary native disclosure, keyboard, focus, and accessibility patterns
without reducing typography to force content onto a line.

### Expanded presentation

For an existing Person, show every changed field's full before and after values,
with explicit actions where that is the appropriate representation. For example:

```text
v person@example.com -- 2 changes
  Field           Before   After
  Office state    (none)   Washington
  Office country  (none)   USA
```

Long or multiline values may wrap in expanded details. Preserve the distinctions
among omitted values, explicit clears, and supplied values. Keep Manager, Photo,
archive/reactivation, and Favorite outcomes explicit; do not invent a file-path
comparison for a Photo change or clear.

For a new Person, identify the Add action and show supplied initial values on
expansion. These are a new Person's baseline, not differences from an existing
Person. Do not label them as an existing Person's field changes.

Do not add lists of supplied-but-unchanged fields to expanded entries.

## People without Person changes

"No Person changes" is a presentation group for existing people whose accepted
Person values, archive status, and Favorite state do not differ. It includes both
same-value updates and email-only records.

Show its count and keep the section collapsed initially. Expanding it reveals
email identities only, in import-file order. Do not repeat a per-Person
explanation, show unchanged-field lists, or add evidence-detail expanders.

Explain once that supplied Observations may still be recorded even when Person
values do not change; omitted fields remain unchanged. This does not promise that
every entry supplies evidence: an email-only record supplies no field Observation.

This preserves evidence behavior without building an evidence-audit interface.
The same-value fields are not generally information that changes a user's
import-approval decision.

## Batch impact counts

Show these mutually exclusive totals, including zeros:

- **People added:** identities not already present in the Workspace.
- **Existing people changed:** existing people with at least one actual field,
  archive/reactivation, or Favorite difference.
- **No Person changes:** existing people without such differences.

For an ordinary valid preview, each Person belongs to exactly one total,
regardless of how many fields change.

Also show nonzero action counts for existing people:

- Archived.
- Reactivated.
- Manager changed.
- Photo changed, including a clear.
- Favorite changed.

These are overlapping breakdowns, not additional people or mutually exclusive
categories. A Person with both a Manager change and archive action contributes
once to "Existing people changed" and once to each applicable action count.
Initial values of new people belong to their Add details, not to counts of changes
from existing values.

Ordinary text-field differences remain included in the changed-Person total and
entry details; do not add a summary category for every textual field. Counts must
describe the actual preview, not make an invalid or incomplete batch appear fully
validated.

An all-unchanged ordinary batch explicitly reports zero Person changes while
noting that Observations may still be recorded. Preserve the existing Apply
eligibility; do not relabel it as an exact replay.

An exact replay retains a distinct already-applied/no-op status. Do not present
the original batch's effects as new additions, changes, or evidence to record.

## Acceptance scenarios

- Two short differences for one Person appear in one collapsed entry when the
  complete identity and differences fit on one line. Otherwise its count appears;
  expanding it exposes both full before/after differences.
- Many changes, long values, multiline values, and narrower widths keep collapsed
  entries to one line without losing expanded details. Widening can restore the
  full inline summary.
- Interleaved changed and unchanged records become a main change list and a
  collapsed end section, preserving file order in each. Neither section exposes
  unchanged-field lists.
- Counts distinguish additions, changed existing people, and people without
  differences. Overlapping archive, Manager, Photo, and Favorite actions do not
  double-count the population.
- Add entries disclose supplied initial values without inventing existing-value
  comparisons. Archive and Reactivate remain explicit when collapsed; all other
  changed fields/actions remain fully reviewable on expansion.
- Same-value updates and email-only records remain accepted according to the
  existing contract. An ordinary all-unchanged batch stays distinct from exact
  replay. Diagnostics remain visible and existing blocking/freshness rules hold.

Follow [the testing strategy](testing.md) for implementation evidence. Grouping,
counts, and unchanged import semantics belong at the shared workflow/view-model
surface; actual single-line rendering, width adaptation, disclosure, and keyboard
behavior require the appropriate WPF evidence. Do not substitute a mockup or
string-length heuristic for proof of rendered line count.
