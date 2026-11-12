# Agreed v1 design

This document records the design agreed during the project interview. It is a
specification, not evidence that the app, workflows, or repository rules exist.
The concrete producer-facing file contract is in [Import format](import-format.md).

The [agreed adaptive UI redesign](ui-design.md) refines the presentation described
here and adds ongoing-note designation and remembered view preferences. It is
planned, not implemented. Where presentation differs, use that refinement while
preserving the ownership, inheritance, import, editing, and data-safety rules here.

## Product and platform

Build an org explorer with rich person profiles, not a relationship-reminder CRM.
The main interface is a searchable, collapsible reporting tree beside the selected
person's profile. Users can move between managers and reports, favorite people,
and search structured profile fields and note text entirely offline. Search
results distinguish historical context from current information.

The initial support target is supported Windows 11 x64 versions, using .NET 10
and WPF. Velopack supplies the Windows installer and updater. Do not promise
Windows 10, native ARM64, or non-Windows support. Keep the stored data portable
even though the initial UI is Windows-specific.

Exercise a representative population of 10,000 people, with photos, notes, and
history. This is a performance target, not a hard import cap. Measure load,
navigation, search, and update behavior; do not silently invent an agreed latency
guarantee or substitute a smaller dataset.

## Opening a workspace

Remember the last successfully opened workspace and reopen it automatically on
startup. Show the empty app with workspace-opening controls only on first launch
or when the previous workspace cannot be reopened, not a disabled profile editor
as the normal startup experience.

If reopening fails, explain the failure and offer Retry or Choose another folder.
Do not silently create a replacement workspace at the old location or present a
failed reopen as a successfully opened empty workspace.

## Local files and the sync boundary

All canonical person data, notes, photos, and history belong in a user-chosen local
workspace folder, separate from the installation directory. Use readable
per-person records, separate Markdown note files, and ordinary image files, not
one monolithic workspace data file or an app-only encrypted store.

Browsing and editing work without internet. Privacy relies on device and
sync-account access controls: someone with access to the folder or its cloud copy
can read it. There is no hosted user-data service, external AI processing, or
upload of private notes by this app.

Each client saves profile drafts on explicit Save, autosaves the quick edits
described below, and notices external file changes.
The external engine owns syncing and conflict resolution, whether it is OneDrive,
SharePoint, Google Drive, or another folder provider. Do not add provider APIs,
semantic merges, or an application conflict-resolution UI. Provider-created
alternate copies or competing versions may need handling outside the app.
The stale-profile warning below protects an unsaved draft; it does not introduce
an in-app compare/merge workflow.

External changes must be validated before becoming current application state.
Incomplete sync delivery, invalid files, or incompatible versions must be visible
problems, not silently accepted data or invented manager records. Do not overwrite
files to "repair" an external conflict. Pending local save errors must remain
visible and must prevent an updater restart that would lose an edit.

Derived views and indexes are rebuildable, not sources of truth. Detailed
workspace filenames, metadata encoding, and recovery mechanics must be specified
with implementation while preserving these boundaries.

## Person profiles

Email is the stable, unique identity. It is not an ordinary editable field; name
and alias are not alternative identity keys. The assumption deliberately excludes
automatic continuity across changed or reused email addresses.

| Field | Meaning |
|---|---|
| Directory name | The directory display name; retain it when a preferred name exists. |
| Preferred name | Display prominently when supplied; searchable alongside the directory name. |
| Alias | Short company handle, not another name or full email address. |
| Job title and department | Structured organizational attributes. |
| Office building | Building name or number, as text. |
| Office number | Room or office identifier, as text; preserve leading zeros and letters. |
| Office state and country | Geographic attributes, separate from building and office number. |
| LinkedIn profile URL | Explicit profile link, not an invitation to scrape or fetch profile data. |
| Photo | Locally stored image; a clear placeholder is allowed when genuinely unavailable. |
| Favorite | Quick access to frequently consulted people. |

All non-identity profile fields are editable in the app and importable. Preferred
name and LinkedIn URL are not protected override layers: an explicit import may
replace them after review. Omission preserves them.

Notes are the sole exception: **notes cannot be imported**. They remain local
workspace documents, including copies delivered by the external sync provider.
An imported profile change may cause a local historical capture or review flag,
but imported data must not supply note bodies or note operations.

Opening a profile, rendering Markdown, or displaying a photo must not fetch
remote content. An explicitly selected external link may open in the user's
browser. App update traffic is separate from private workspace data.

### Viewing and editing

Default to View mode when opening a workspace or selecting a person. Present
populated profile attributes as readable, copyable values, not always-visible
editors or disabled textboxes. Omit empty optional profile fields from View mode.
Keep email non-editable, manager/report navigation available, and a supplied
LinkedIn URL available as an explicitly opened link.

One Edit profile action enters a person-scoped edit mode using the existing
editing controls as the starting point. Show all editable fields, including
empty ones. Directory name, alias, job title, department, office fields, LinkedIn
URL, manager changes, and choosing or clearing a photo all belong to this mode.
Stage these changes until explicit Save; do not apply manager or photo changes
immediately. Cancel discards the profile draft without applying it. Successful
Save and Cancel both return to View mode. Failed saves show the error and retain
the draft.

Keep these quick actions independent of the main profile Save/Cancel:

- Preferred name appears as ordinary text with a small one-click Edit action.
  If unset, show Add preferred name rather than hiding the quick action.
  Opening the inline editor retains its autosave behavior.
- Favorite remains a one-click toggle that saves immediately.
- Person notes and team notes retain their own editing and autosave behavior,
  described below.

Profile Save must preserve independently saved quick changes; Cancel must not
undo them.

When leaving an unsaved profile draft, offer Save / Discard / Stay. This includes
selecting another person, opening Import, switching workspaces, closing the app,
or taking another action that would abandon the draft. A failed or stale Save
keeps the draft open instead of proceeding with navigation.

Use the selected profile's version when entering Edit profile as the stale-save
baseline, not its version when the app started. If that profile changes externally
between entering Edit profile and clicking Save, warn and block Save while
preserving the draft. Changes already incorporated before editing, or changes to
unrelated profiles, do not constitute this conflict. Do not silently overwrite
newer profile data.

Put Archive / Reactivate in the person's More menu rather than leaving those
controls permanently visible or including them in the profile draft. Require
confirmation, then apply the action independently of profile Save/Cancel.

## Importing person updates

Import has its own view, reachable without entering Edit profile. Remove the
import file chooser, preview, review, and Apply controls from the normal people
view rather than keeping them at the top of the window. Preserve the existing
preview, review, and explicit Apply workflow in the dedicated view.

The app defines and consumes the [versioned JSON contract](import-format.md).
Another agent, session, or tool produces the file and any accompanying local
photos. GAL authentication and extraction are outside this app.

The producer initially supplies selected managers' complete reporting subtrees
and ancestor context, with later scope expansion possible. The receiver does not
treat a partial update as proof that a subtree or company directory is complete.
Absent people are not departures, and displayed report counts describe imported
reports, not a verified total company headcount.

Required import behavior:

- Omitted people and fields are unchanged; explicit null clears nullable fields.
- Creating a person requires email, directory name, and explicit manager or
  no-manager. Other fields and photos can arrive later.
- Resolve manager references against existing people and everyone in the same
  batch. Input order must not matter.
- Validate the complete proposed state before applying any changes. Unknown
  managers, active reporting cycles, invalid supplied photos, and active reports
  left pointing to an archived manager are errors.
- Preview additions, changes, reporting moves, clears, and archives. Require
  explicit apply; do not silently skip invalid records.
- Revalidate if the workspace or import resources change after preview.
- Apply reporting history against the old state and complete proposed new state,
  not transient intermediate manager-first writes.
- Replays must not duplicate people, profile changes, or historical snapshots.

Archival is explicit and retains the person's information, notes, photo, and
history. Do not permanently delete personal records through imports, infer
reassignments, archive a whole subtree implicitly, or reactivate someone merely
because an ordinary field was updated.

## Person notes and team notes

Both kinds of notes are private to the workspace owner. "Team" is display scope,
not shared access.

Existing notes open in rendered Read mode. A one-click Edit action reveals the
title and Markdown editors, retaining their current autosave behavior rather than
joining the main profile Save/Cancel workflow. New note opens directly for editing.

Hiding empty optional profile fields does not hide notes or their sections.
Person notes and team notes sections remain visible when empty, with an
empty-state message and New note action. An existing note remains accessible
and editable even when its body is empty.

A person note appears only as that person's direct context. A team note belongs
to the manager on whom it was written. Current descendant profiles show the team
notes of every manager in their formal reporting chain, with source attribution.
Notes owned by the selected person are distinguishable from inherited notes.

Informal leads and project membership do not drive inheritance in v1. There is no
independent permanent-team entity. Replacing a manager does not automatically
transfer their notes to the replacement.

When a manager changes role or reporting context, potentially stale team notes
remain visible with a needs-review warning in both change review and inherited
context. The user can confirm, edit, or archive them; the app does not silently
hide or relocate them.

## Observed history

If a person's reporting context changes, current inherited notes follow the new
chain. Former team notes do not follow the person into their new current team.
Preserve the old chain and locally available team-note versions in historical
context. Later note edits cannot rewrite that preserved context.

Changes to an ancestor can change a descendant's inherited context even when the
direct manager stays the same. Compare complete chains, avoid synthetic transfers
while assembling an import, and treat initial creation as a baseline rather than
a discovered prior transfer. Archival closes current context without erasing it.

Retain structured profile-field changes too, recording old/new values and whether
they came from an import or local edit. This does not require a complete edit
version history for every person or team note.

Dates must distinguish source observations from local recording/application time.
If November 1 confirms one manager and November 6 confirms another, show the
observed interval and detection information, not an invented effective transfer
date. An office-only update does not reconfirm the manager. Historical team-note
text is what was available locally when the change was processed, not guaranteed
to be the text at the unknown real-world transfer time.

Do not manufacture pre-import history. Older observations need clear provenance
and preview warnings, not impossible chronological claims or silently guessed
effective dates.

## CI and GitHub enforcement

The approved [testing strategy](testing.md) defines current commands, test
placement and the separation of exhaustive desktop acceptance into
focused WPF scenarios plus separate performance suites. It supersedes the
earlier expectation that every full scale workload runs on every PR.

Run CI on every PR and after every commit reaches `main`, including documentation-
only and test-only changes. Use separate parallel jobs with independently visible
checks:

| Lane | Required coverage |
|---|---|
| Core and view models | Identity, explicit patch semantics, graph rules, inheritance, history, and view-model behavior. |
| Filesystem integration | Actual temporary-folder import, validation/apply, saves, external refresh, photos, migration, and recovery behavior. |
| Desktop smoke | Actual Windows launch, open-profile, edit, and save flows; not a test-only success flag substituting for UI behavior. |

Use fictitious people and local fixtures, never company data. Exercise the
10,000-person target in explicit performance suites while retaining targeted
large-graph correctness in normal PR coverage. Both complete non-UI performance
and genuine WPF-scale suites have explicit commands and share nightly/manual
verification; required desktop smoke retains ordinary WPF-specific journeys.
Future publication must run both commands fresh against its exact candidate
and fail closed; this callable contract is not an implemented publication gate.
See the testing strategy for commands, schedule activation and revision evidence.
Test updater verification, failure, and restart behavior
without acquiring real releases or using production secrets.

A stable aggregate check, named `CI`, must pass only when every required lane
succeeds. Failures, skipped required work, cancellations, and timeouts cannot
produce a passing aggregate. Parallelize lanes and cache dependencies without
introducing a long serialized critical path.

Aim for five minutes of CI execution. Bound the total execution critical path
within a thirty-minute fail-fast budget; do not merely give each serialized job a
separate thirty minutes. Hosted runner queues and subsequent CD packaging and
publication are measured separately. Verify timings once real workflows exist.

Use **GitHub rulesets, not classic branch protections**, to require PRs and the
passing CI gate for `main`, including the owner's changes. Do not allow direct
pushes as an owner bypass. Only squash PR merges are allowed; disable merge-commit
and rebase merging.

These rules and merge options need authorized configuration. They have not been
changed by writing this specification.

## Automatic product releases

After successful `main` CI, publish automatically only when product-impacting
changes exist relative to the last successfully published release. Generate
versions and tags; do not require a manual tag or per-release selection step.

| Normally product-impacting | Normally not product-impacting |
|---|---|
| App source and UI assets | Repository documentation |
| Runtime dependencies and shipped configuration | Tests and test-only dependencies |
| Shipped import schemas and in-app help | Development tooling unrelated to shipped output |
| Build, packaging, and update-publication logic affecting the product | CI-only changes that do not alter product output |

Classify paths by effect on the released product, not file extension. Shipped
Markdown help counts; a repository design note does not. Mixed commits count if
they contain product changes.

Compare against the last successful release, not the previous commit: a failed
product release followed by a docs-only commit must not strand pending product
changes. Handle the first release explicitly. Serialize or guard publication so
an older finishing build cannot replace the newest release/feed.

Published packages contain the app and required resources, never the user's
workspace, actual employee fixtures, credentials, or private signing keys.
Version the app, import contract, workspace, and update protocol deliberately;
one new app build does not automatically mean a new data format.

## Installer and automatic updates

Use a Velopack Windows installer and a public GitHub Releases feed. Package the
runtime needed by the Windows desktop app so the installer does not depend on a
separate manually installed .NET desktop runtime. Installation data and the
workspace remain separate.

Settings exposes Check for updates; automatic startup checks are enabled by
default. A discovered authorized newer version is downloaded, installed, and the
app restarted automatically.

Wait for pending local saves and in-progress import application before applying
and restarting. Do not wait for the external sync engine. Save failure prevents a
data-losing restart. Unsaved profile drafts must also be protected: use the
Save / Discard / Stay decision rather than silently saving or discarding a draft
to restart. Reopen the same workspace after restart.

Offline, rate-limit, invalid-signature, download, install, and restart failures
must be explicit status/errors while retaining the existing usable app wherever
possible. Failed checks do not mean "up to date." Neither update checks nor
downloads may block ordinary offline browsing or editing.

### Update authentication

Use project-key signed update manifests without requiring a paid public-trust
certificate service for v1. Embed only trusted verification public keys in the
app; keep private release-signing material outside source control and clients.

Before unattended apply, verify the manifest with a trusted key and bind it to the
authorized version, channel/platform, and package identities and cryptographic
hashes. Verify downloaded bytes match those authorized hashes as well as
Velopack's integrity checks. Use standard cryptographic implementations, not
custom cryptographic algorithms.

No unverified feed may select substitute assets after a valid manifest was
checked. Control any framework implicit pending-update application paths so they
cannot bypass authentication or safe restart handling. Key rotation must be
authorized by existing trust or an explicit trusted rebootstrap, not by trusting
an arbitrary new key downloaded alongside an unsigned manifest.

This authenticates releases to an already-trusted installed app. It does not
establish a publicly verified legal publisher or Windows installer reputation.
Initial unsigned installers may encounter OS warnings or blocks. Do not bypass
warnings or weaken OS protections.

### Automatic workspace migrations

Known supported migrations, including breaking migrations, may run automatically
as part of app upgrades. Preserve recoverable original data, validate conversion,
and handle failures safely; installer rollback alone does not restore workspace
files.

If an app encounters an unsupported workspace or import version, immediately
notify the user and suggest upgrading. Do not write, guess at, or downgrade
unsupported data. Keep update/settings access available even when workspace
writing must be blocked.

## Outside v1

Direct GAL authentication/extraction, provider-specific synchronization, app-level
semantic conflict resolution, informal-team inheritance, permanent team entities,
broader CRM interaction/reminder/task features, extra OS/CPU targets, and publicly
trusted Windows code signing are not required for this first release.
