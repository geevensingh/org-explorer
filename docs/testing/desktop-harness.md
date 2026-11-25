# Desktop and invisible harness operation

[Return to Testing](../testing.md).

For native setup, invisible BAML loading, bounded UI Automation observations, fixture isolation or visual evidence, use this reference. First read the [desktop permission and isolation prerequisite](../testing.md#real-desktop-execution). Use the [complete required scenario selections](required-desktop-scenarios.md), [CI/artifact gates](ci-and-artifacts.md) and, only when explicitly selected, [scale verification](scale-verification.md).

## Invisible compiled checks

`--verify-xaml` is a separate non-UI build/load contract, not a desktop scenario.
It constructs the actual compiled MainWindow BAML and event connectors on STA
with isolated settings, requires the window to remain invisible with no native
main-window handle, then closes it. It never calls Show or runs the application
dispatcher; startup restore/update Loaded handlers and an external UI Automation
client are not run.
This catches XAML connection failures that compilation alone misses. It does not
prove rendered controls, navigation, dialog discovery or any smoke/scale result.

For non-UI build/load commands, read [local contract checks](ci-and-artifacts.md#local-contract-checks). The separate compiled profile-refresh/domain continuations remain under [Testing](../testing.md#required-prmain-execution).

## Running required desktop suites

On that authorized desktop, the required command is:

```powershell
.\eng\Run-CiLane.ps1 desktop
```

It builds the real product and harness, runs all selected scenarios **sequentially**
in one process, verifies complete named results, then publishes a local
product-check folder to reject leaked fixtures/test assemblies. That publish is
not installer verification or release publication. After the lane prepares the
ephemeral embedded update trust and fixture environment, the runner's invocation
has this form (the lane uses its isolated artifact paths):

```powershell
dotnet run --project tests\OrgExplorer.Desktop.Smoke -c Release -- `
  src\OrgExplorer.Desktop\bin\Release\net10.0-windows\OrgExplorer.Desktop.exe
```

## Display prerequisites

Before any app launch, the high-resolution fixture requires GitHub Actions,
`RUNNER_ENVIRONMENT=github-hosted`, Windows and 96 DPI. It enumerates the actual
primary display's monitor-compatible modes, requires an advertised 1920 x 1080
32-bpp mode, tests it with `CDS_TEST`, and applies it dynamically exactly once
with no registry/persistence, unsafe-mode, primary-device or DPI changes.
It then observes the exact selected mode, actual 1920 x 1080 monitor bounds,
96 DPI, work area >=1800 x 1000 and track capacity >=1100 x 900 within the normal
bounded observation. Driver rejection, restart requests, missing modes, wrong
read-back or inadequate capacity fail the lane; there is no synthetic fallback.
`display.json` records actual native prerequisites, not rendered acceptance.
The job VM is disposable, so no restoration step is needed. This is not
permission to change any local, self-hosted or shared display. Explicit UI-scale
execution reuses the same preparation on hosted VMs; an already-authorized
isolated local desktop receives read-only capacity verification, never a change.

## Fixture isolation and investigation

Both required desktop suites use ordinary fixtures. They do not construct the
complete scale population; all genuine UI-scale work has explicit execution in [scale verification](scale-verification.md).
Every scenario opts out of startup update checks before launching the app,
except the controlled `update-settings` and `installer-update` scenarios.
This includes ordinary smoke, manual normal-build execution and installed
create/reopen flows; they must never fetch or apply a production release.
Every scenario also supplies its own `ORG_EXPLORER_SETTINGS_DIRECTORY`, initially
without a remembered Workspace. Launch fails closed without an explicit absolute
settings directory, so no fixture falls back to the workstation owner's actual
preferences or Workspace. Repeated launches within one scenario deliberately
share only that scenario's settings to exercise durable reopening.

For investigation, append one name to the harness command, not the app
(`profile-journey`, for example). Older `migration`, `--search-favorites`,
`--person-notes`, `--team-notes` selectors remain aliases.
The obsolete combined `scale` / `--scale` / `--ui-scale` selectors are rejected;
use the isolated `--ui-scale-prefix` and `--ui-scale-tail` selectors with their
candidate-bound logical handoff. A single explicit scale scenario is investigative,
not complete scale acceptance. `--high-resolution` selects only the
three required high-resolution scenarios and is never part of default ordinary smoke.
A targeted pass is not the complete required lane. `DESKTOP_RESULTS_DIRECTORY`
may select a separate result directory for manual runs.
The full selection and `update-settings` require the prepared update fixture and
matching test-trust build; a production-trust build cannot substitute for that
controlled evidence. Other focused scenarios may use a normal build.

## Native action and observation

Successful navigation waits for meaningful local completion and asserts destination
email/context. Explicit rejection paths invoke once, assert retained owner,
selection/draft and actionable status, and never retry their way into success.
Pending and in-progress messages must explain waiting; failures require
retry/discard. Poll bounded eventual states; keep existing 20-second smoke and
180-second scale limits. Photo/initials visibility and geometry poll within that
existing smoke bound; persistent mismatches report the last observed geometry.
Missing desktop pixels or failed capture still fail explicitly rather than
being retried as layout. Remote content is never fetched just to render notes or
profiles; loopback listeners and actual pixels/text are evidence, not filenames
or image dimensions alone.

Read-only multi-control native checks batch their requested IDs and properties
in one fresh UI Automation observation, avoiding a full-window search for each
field. Each readiness poll and post-action check captures again; observations
do not survive navigation, focus/scroll changes or updates. Text-pattern, pixel,
selection and keyboard assertions remain actual provider/desktop observations,
with the same complete scenario selection, size matrices and execution budgets.
Responsive editor checks acquire the displayed identity inside the existing
readiness poll, so a transient post-resize absence cannot bypass that wait.

UI Automation invocation can return before WPF processes the queued button
handler. Click and value helpers acquire an enabled control and its required
pattern within the existing bounded observation wait, then invoke or set once
without a second control lookup. Only preparation is retried, never the action.
Raw Click/Set and idle/startup observations never open or close Settings.
Physical copy click/hover preparation likewise captures a visible usable point
within that wait, then sends input once. Eligible targets must be enabled;
deliberate missing-value checks retain their disabled automation peers.
Copy/owner hovers send actual absolute mouse movement on the verified primary
display, not position-only `SetCursorPos`, which left the hosted cursor suppressed
despite hitting the correct value. The native visible-hand/non-hand assertion remains.
Hosted cursor observation samples twice to retain its repeated-read regression.
Every read reinitializes `CURSORINFO.cbSize`, which the API can clear on return;
reusing that output made later reads fail with Win32 87. Invisible local contracts
never query the interactive cursor, which can be inaccessible on a locked desktop.
Tooltip discovery checks native top-level popup children before the same descendant
query, avoiding full reading-document traversal when the popup is top-level. Exact
tooltip content, cursor, clipboard and rendered-feedback predicates are unchanged.
Copy readiness failures retain the target's enabled/offscreen/focus state and bounds.
Named physical-click targets are located inside that same readiness wait, so queued
input cannot race an unguarded control lookup before preparation begins.
The ordinary copy fixture scrolls its reading surface to the top once after profile
Save, before acquiring any fact target, as the responsive copy fixture already does.
UIA's offscreen flag alone can be false even when retained edit scrolling clips the
fact above that viewport; acquiring an actual clickable point remains required.
Rendered contrast uses one actual desktop bitmap capture, then the original six-pixel
sample grid and luminance threshold against its pixels, rather than repeating desktop
readback for every sample. This is neither offscreen rendering nor a reduced predicate.
Feedback reads request name, offscreen state and bounds together in a fresh UIA cache
snapshot per observation; no metadata is reused across gestures, and the same actual
desktop-pixel check still proves rendering.

## Clipboard observation

Exact clipboard-copy observation uses the existing bounded STA dispatcher pump,
servicing the harness's own OLE clipboard-owner messages while another process copies.
The real clipboard equality and 20-second bound remain; no copied-text effect is faked.
Only a read-side `COMException` with `0x800401D0 (CLIPBRD_E_CANT_OPEN)` remains
pending within that same wait. It never substitutes empty text, repeats a copy
gesture/write, or restarts the deadline. Timeout records the last read's availability
or text mismatch and preserves both the wait failure and latest cannot-open exception;
unexpected read errors propagate. Runner-category `ClipboardObservationTests` drive
the same tooling observer with a controlled external reader and bounded wait, without
accessing the local clipboard or opening a window. Actual native proof stays hosted.

## Harness runtime diagnostics

Bounded `DIAGNOSTIC: harness-runtime` JSON lines investigate ordinary-lane
runtime without changing the workload or acceptance clocks. Every native scenario
and compiled profile-refresh/update invocation emits a start/end record; compiled
copy and SQLite, native copy and SQLite note/profile work have named child phases.
Copy starts before its first existing PASS, with fixed payload/Team/missing-value
checkpoints; SQLite checkpoints separate held navigation and repeated dispatch.
An unmatched start/checkpoint identifies interrupted work, not success.
Each phase carries the exact `GITHUB_SHA`, run and attempt (null when unbound
locally), its parent/context and outcome. Fixed-size, thread-safe in-memory
counters retain started/completed counts, total and maximum elapsed observation
cost for existing discovery, shared native requests, waits/predicates, filtered
note snapshots, capture/pixels and compiled copy/SQLite layout. Capture measures
screenshot acquisition, PNG encoding and metadata writing;
standalone path-safe state validation records no capture operations. The existing
invisible capture-name regression verifies that distinction without taking screenshots.
These diagnostics add no per-poll writes, UIA reads/cache warming, actions or retries. Existing
direct native latency requests and samples remain unchanged. Nested/inclusive
measurements overlap and concurrent snapshots are non-atomic; these are neither
database commit times nor replacement product latency, and nothing is subtracted
from the raw acceptance samples or lane budget. Expected diagnostic output failures
cannot replace an action's result/exception. No note/profile values or exception text
are recorded by these diagnostics. Harness regressions cover bound early
failure/interruption records, unchanged outcomes on output failure and bounded
parallel aggregation. These probes are investigation, not a root-cause fix.
Expected serialization/output failures retain process-wide bounded counts and
last exception type/HResult, never exception messages. They emit the fixed stderr
marker `DIAGNOSTIC: harness-runtime-output-failure investigationIncomplete=true`,
without retrying the failed writer recursively. If stderr also fails, its
count/type/HResult remain observable through `HarnessRuntime.OutputFailures`.
Later records carry `diagnosticsComplete=false` and `outputFailures`; action
outcome is separate from evidence completeness. Missing boundaries or any output
failure make investigation evidence incomplete, even when actions passed.
Unexpected diagnostic programming errors are not silently caught.

## Local-work readiness observation

`AwaitLocalWorkIdle` preserves its original exact `Ready` activity and ordinal
non-`Pending` status guards before requesting one fresh, four-ID filtered native
snapshot per eligible poll. The snapshot contains only Email, WorkspaceStart and
the two note surfaces, never elements/properties retained across polls or owners.
Absent Email still requires the actual visible starting view; empty Email retains
its original optional-note behavior. A selected Person still requires Person then
Team feedback without `Pending`, including offscreen note reading. Required-control
absence and provider failures are not readiness, and no local-save phase/status is
normalized. The existing twenty-second observer/catches remain unchanged.
Runner regressions pin short-circuit order, startup/required-control failures,
per-poll query count, changed owners/feedback and unavailable providers. These are
observation contracts, not rendered usability, local durability or latency claims.
Journeys prepare Settings explicitly before folder, Import or update actions,
and deliberately return to People before subsequent writing. Rejected-close
observations require Settings to be closed by the app before retrying the
retained note; input helpers cannot repair a hidden editor and mask that failure.
The headless readiness regression scripts missing/disabled controls and transient
pattern discovery at that shared helper; hosted WPF remains the actual UI proof.

## Native picker operation

The native picker helper targets the observed Folder Edit field (ID 1152), not
the file-dialog entry or a Search box. Select Folder/Cancel match both their
native automation ID and Button role before obtaining InvokePattern: shell
folder items can reuse numeric IDs 1/2 and are not dialog actions. Exactly one
action is invoked before the unchanged close observation; no second click or
action retry repairs a still-open picker. Cancellation compares actual before/after
orientation, displayed path, preference bytes and untouched Workspace contents;
HelpText need not be empty because WPF can supply useful tooltip fallback.
Installed-flow failure diagnostics only read the existing automation tree and
retain the original exception; they never call the maximize/open-window helper.

The native-picker helper invokes the visible action once, fills the native folder
entry and accepts or cancels once, then observes dialog closure. It never creates
folders, repairs Settings, retries Open or substitutes an app-side path setter.
Empty folders and malformed selectable Workspaces are explicit scenario fixtures;
the rejected-open checks retain their complete byte/preference assertions.

## Visual evidence

Visual-refresh journeys can capture named, already-observed states through the
shared `CaptureVisualEvidence` harness helper. It reads actual desktop pixels
without opening, focusing, resizing or repairing a view. The existing hosted
desktop lanes retain these PNGs beside JSON recording the source revision,
run/attempt, executable hash, DPI and captured bounds. Calls require unique
state names within a scenario and never overwrite an earlier observation.
Optional application-monitor capture retains the full actual monitor, including
the taskbar region, and labels its scope separately from a window-bounds capture.
Captures follow the same desktop-isolation rules below; they are comparison
material, not behavioral proof or owner visual acceptance.

The existing profile and installed desktop journeys compare the live window's
small/large `WM_GETICON` pixels with the matching produced native frames, saving
unscaled PNGs and a light/dark compositing plate with executable hash and candidate
metadata. This is native-resource evidence, not a screenshot of the taskbar or
Start menu. Alongside the existing window screenshot, those journeys also save
actual full application-monitor pixels with the same candidate/executable
identity. Review the monitor PNG for the visible taskbar and app button; neither
registrations nor the HICON plate proves those shell pixels. Capture does not
open Start, reveal an auto-hidden taskbar, change theme/focus, or replay setup.
Start-menu and setup-dialog appearance therefore remains unproven by this path.
The isolated installed lifecycle also resolves the real Desktop/Start
shortcuts and Installed Apps icon target after install, upgrade and reinstall.
These checks do not replace actual Windows small-size/surface review or the
whole-pass owner review. They must not be run on the shared local desktop.
