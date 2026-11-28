# Required desktop scenario selections

[Return to Testing](../testing.md).

For the complete ordinary and high-resolution native selections, including their embedded assertions and size matrices, use this reference. Read [harness setup and permission](desktop-harness.md#running-required-desktop-suites) before execution and [CI partition/artifact gates](ci-and-artifacts.md#required-prmain-execution) for required-lane results. For independent regressions, read [Person profiles](person-profiles.md), [note writing](note-writing.md) and [Workspace lifecycle](workspace-opening-exit.md).

## Ordinary required scenarios

The nine ordinary required names live in `tests\DesktopScenarios.json`. The lane
verifies native current-mode and monitor bounds at 1024 x 768 / 32 bpp / 96 DPI
without changing its display. They cover:

- `profile-journey`: preview/apply/rejection, Manager/report identity, preferred/directory
  binding, readable Preferred name and one-click Add/Edit, exact popup defaults
  and focus, physical Save of the unchanged Directory default and physical Enter
  clearing an equivalent existing name, retained inline validation failure,
  Cancel/Escape/outside-click of untouched defaults and distinct writing
  dismissal without mutation, popup discovery/focus and uniquely named normal,
  failure and dismissal captures,
  quiet chrome and Settings keyboard entry/focus, anchored photo/identity bounds,
  all ordinary facts with stable labelled missing-value slots and no More details gate,
  readable ordinary values and idle state after profile Cancel,
  primary copying of every eligible value, representative Enter/Space and the
  retained mouse/keyboard Copy value menus, exact multiline/leading-zero payloads,
  actual hand cursors/full-value/owner hints and rendered transient confirmations.
  Clipboard-sentinel checks protect absent values, Heading/Manager navigation,
  editor Enter and Workspace-path primary clicks. A genuinely held OS clipboard
  rejects copying without success or loss of staged profile writing; explicit
  retry returns to quiet reading. The responsive journey repeats the eligible
  property set, long/truncated/wrapped values and duplicate Team-name activation
  throughout its unchanged size matrix. Both remain hosted-only evidence,
  representative partial change/clear, save/reopen, actual copied PNG
  and JPEG pixels, photo clear/placeholder and Profile timeline. Its marked
  300 x 300, 300 x 150, 150 x 300, 72 x 72 and 72 x 36 PNGs prove centered
  reduction/enlargement, preserved square-marker proportions, independently
  expected pre-mask fill sizes (144 x 144, 288 x 144, 144 x 288, 144 x 144,
  288 x 144 DIP), axis-band positions and intentional circular exclusion.
  UIA requires both the existing photo viewport and accessible Image to stay at
  144 x 144 DIP. Centered fill happens inside a square drawing using the decoded
  bitmap; the independent pre-mask expectations are checked through source-band
  positions and square-marker proportions in actual pixels, not oversized
  accessible bounds. Bounds and pixels use effective Windows scaling. Initials retain
  their accessible text and blue/white colors; UIA text geometry and rendered ink
  prove proportional enlargement, and pixel spans prove the matching 144-DIP
  circle. Choosing/clearing stays staged until explicit Save; Cancel and
  Person switching retain the correct saved photo/initials footprint.
  An original initials timeout also writes failure-only
  `photo-rejection-<scenario>-ui.png` / `.json` diagnostics. The PNG is the last
  already-captured rejected `ReadPhotoPixels` buffer, retained before its GDI
  disposal, never a later screenshot. JSON carries its original acquisition
  interval, crop/viewport/text/previous-photo coordinates, DPI, the first false
  guard or probe and observed RGB, computed glyph bounds and circle-edge offsets.
  Unreached stages stay unobserved. Only one rejected buffer (up to 2 MiB) and
  bounded probe records are retained; a later pre-pixel refusal is labelled
  separately from that buffer. Native window/PID binding observations have
  their own timestamp, distinct from pixel acquisition. Source/run/attempt,
  app/workflow/harness and native hashes are bound after the original failure.
  PNG/JSON are emitted only after the unchanged twenty-second failure; output
  faults remain attached to the primary exception and separately reported.
  No extra desktop acquisition, GetPixel, UIA or business query is made, no
  predicate is replayed, and no capture/serialization time is deducted. All
  geometry, colour tolerance, expected values, focus and cursor behavior remain.
  Synthetic managed-raster recorder/PNG/refusal tests run in Core; they do not
  claim actual desktop pixels or fix app rendering. These diagnostics do not
  replace normal visual evidence or any required acceptance check.
  Clear is invoked once and its visible staged-clear notice must appear before
  asserting the unchanged saved photo or cancelling; pixels already present
  before the click do not establish that the handler ran.
  Import is opened
  explicitly, replaces the people view, and returns to reading with Import
  controls absent. Choosing/previewing retains the saved population; changing the
  path invalidates Apply until reviewed again.
  Inline editing additionally requires initial Directory-name focus, Tab to Alias,
  actual field Enter followed by Tab without submission, unchanged saved office
  data before explicit Save, and focus returning to Edit profile after Cancel.
  Named captures retain the field-Enter draft and staged photo clear.
- `draft-navigation`: retained profile/Preferred-name and Person/Team writing across
  real tree/search/Manager browsing, plus explicit dirty-profile Save / Discard / Stay, failed
  leave-save retention and Import protection versus independent failed Person/Team drafts,
  protected Import/Workspace replacement, retained owner/draft and deliberate discard.
  Opening the dedicated Import view exercises
  all three profile decisions and a locked-file leave-save rejection, then checks
  reading values after returning; independent failed notes still block Import.
- `person-note-rendering`: Read-first existing notes with both editors hidden,
  one-click editing, empty-section actions, direct New-note editing and empty-body
  accessibility, actual Markdown text, loopback
  no-fetch assertion, failed local transaction, usable retained-draft browsing,
  Workspace replacement/close decision, retry, independent ownership and durable reopening.
- `team-context-history`: explicit Team ownership, all-ancestor attribution,
  ongoing/Other designation, grouped Person search and independent exact secondary access, Read-first owned notes,
  direct New-note editing and empty-body accessibility, visible preference/save
  failures and collapsed attributed warnings at the ordinary desktop size,
  exact owner review navigation/confirmation with retained writing and archive/restore,
  secondary History with confirmations/observations, actual current/frozen
  reading and honest historical dates through a representative
  ancestor move, historical-source Person search, ordinary History access and reopen.
  Capture details additionally exposes complete transition/note evidence
  separately from frozen Markdown, with keyboard expansion/collapse and
  nonmutation checks. Named actual-pixel states retain the collapsed capture
  entry, expanded evidence and intermediate frozen-note boundary, including a
  note needing review when captured. The responsive journey repeats these states
  at 700 x 500 and 1100 x 900 alongside its complete long current/frozen reading.
- `search-favorites`: representative profile/note queries, real current,
  archived and frozen destinations, reading, favorites, failed-save rejection
  and retained favorites after reopening.
- `archival-navigation`: Person More/confirmation cancellation, dirty-profile
  Save/Discard/Stay and independent-note protection, rejected Manager archival, failed local save and retry,
  current-tree filtering, retained archived profile, selection protection,
  reactivation and explicit no-Manager UI.
- `compatibility-settings`: global Settings before open, current-format reopening,
  older/future-version nonmutation and write blocking, validation retry and an
  explicitly chosen supported alternative. Startup coverage also checks first-run controls, automatic
  same-Workspace reopening with the remembered or automatic fallback Person,
  an honest empty failed-startup recovery
  state with Retry/Choose actions and no replacement creation, successful Retry,
  failed manual attempts retaining the old default, and alternate-folder reopen.
- `update-settings`: actual default-enabled startup and manual checks against a
  controlled loopback feed with ephemeral embedded public trust, responsive local
  editing/saving during a pending check, signed availability, persisted opt-out
  and reopen, unknown signer/tampered signature/unsupported channel/rate-limit/
  offline rejection, and a visible signature failure on a later startup.
  Preference changes wait for the app's accepted-setting guidance as well as the
  checkbox state before closing; a visually toggled checkbox alone is not a save.
  Authorized results are consumed automatically: signed invalid package bytes
  fail Velopack verification. A valid controlled signed package then exercises
  real locked-note save rejection, retained writing, explicit retry, and final
  draft durability before the external installer boundary. The unpackaged
  boundary reports a deliberate installation failure; it is not an install pass.
  The lane prepares this normal-product test build and later verifies a fresh
  production-trust publish; see [the protocol contract](../update-protocol.md#controlled-verification).
- `external-refresh`: actual folder delivery changes the open profile, note
  editors, inherited reading/review and same-path photo pixels; invalid graph
  delivery becomes visibly stale, retains last good data and recovers. Person
  and Team typing across delivery also verifies first-autosave/retry rejection,
  retained editor/delivered writing and deliberate discard through real WPF.

## High-resolution required scenarios

The fourth required lane uses
`.\eng\Run-CiLane.ps1 high-resolution -HighResolutionPartition reading` and
`-HighResolutionPartition context` on separate disposable GitHub-hosted Windows
VMs. Its complete selection remains `tests\HighResolutionScenarios.json`;
`HighResolutionPartitions.json` supplies the exact disjoint split through
`--high-resolution-reading` and `--high-resolution-context`. The original
`--high-resolution` selector still requests the whole suite:

- `responsive-workspace`: the search/Favorites journey plus the complete
  700 x 500, 1100 x 900, host-width 959/960/961 and maximized matrix, actual
  Segoe UI text, People 240 / Person allocation 420 minimums, 600-DIP prose cap,
  all-facts ordering and long/missing text, anchored main scrolling, overlay bounds/focus,
  mouse/keyboard People resizing, percentage resize/reopen and search/clear position.
  At each reading size and the minimum Person allocation, the same real photo
  and a Manager's initials retain their 144-DIP circle without a footprint
  change on selection. Identity actions remain visible, focusable and beside
  the photo; the photo stays anchored during the existing long-note scroll.
- `responsive-draft-navigation`: the complete draft-protection journey plus
  actual compact overlay rejection retaining the exact owner/editor and feedback.
  Its inline form also traverses every ordinary field and Manager by Tab at
  700 x 500, 1100 x 900, host widths 959/960/961, maximized and the minimum
  Person allocation. Each focused control must fit the actual scroll viewport
  without moving identity or Save/Cancel; stable email stays read-only and notes
  stay below the draft. Named top/Manager and failed-leave-save captures support
  final whole-pass comparison, not automatic visual acceptance.
- `responsive-team-history`: the complete owned/inherited Team, independent
  draft and frozen-history journey plus the 700/1100 rendered warning/history
  matrices. All ordinary domain/ownership/history assertions remain in the
  ordinary journey as well.

The split is by explicit scenario name, never a conditional screen-size skip.
The two smoke manifests and explicit UI-scale manifest are nonempty, unique and
disjoint; each worker requires its own complete named `scenarios.json`. Ordinary
results cannot stand in for the high-resolution suite.
