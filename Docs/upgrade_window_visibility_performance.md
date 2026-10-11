# Window visibility and performance upgrade

## Implementation status

Updated 11 October 2026.

| Step | Status |
| --- | --- |
| 1. Pure placement planner | Implemented and verified locally; details below |
| 2. Native-operation results and performance baseline | Implemented; synthetic baseline recorded, real-app matrix remains |
| 3. Runtime visibility and automatic-parking state | Implemented and verified locally; native automatic minimization remains disabled |
| 4. Cached previews | Implemented and verified locally; best-effort native capture, automatic parking remains disabled |
| 5. Opt-in automatic minimization | Implemented; unit/native smoke checks pass; disabled by default, real-app matrix pending |
| 6. Remaining Canvas overhead | Pending; per-batch monitor snapshot included in step 1 |
| 7. Rendering modernization and UI upgrade | Pending |

## Implementation order

1. Extract placement decisions into a pure planner.
   Feed it canvas coordinates, camera, monitor geometry, pinned state, and native state. Have it calculate intended placement and visibility without calling Windows. This is already identified as the next step in [the refactoring notes](identity-and-matching.md). Use one consistent policy for batch projection, individual restores, and monitor changes.
2. Add native-operation results and a repeatable performance baseline.
   Record failed moves/clipping/restores, operation durations, and thumbnail counts. Measure the same workloads while visible, off-screen with current defaults, clipped, and manually minimized. Include stationary overview, panning, and restoration. This establishes whether the main cost comes from applications, Canvas, or DWM.
3. Introduce a separate runtime visibility state.
   Distinguish:
   - Visible.
   - Near the viewport.
   - Off-screen.
   - Automatically minimized by Canvas.
   - Minimized by the user.
   Keep automatic parking separate from saved layout and user intent. Reuse the runtime identity work to avoid restoring a recycled window handle. Add viewport margins and delays so windows don’t repeatedly minimize and restore around an edge.
4. Implement cached previews and their lifecycle.
   Allow a canvas window representation to use either a live DWM preview or a cached image. Capture before parking, limit image resolution and total memory, and invalidate previews when their window identity disappears. Opening overview must be able to show parked windows without restoring all of them.
5. Implement automatic native minimization as an opt-in feature.
   Start with eligible, normal, unpinned windows that remain far off-screen. Restore without stealing focus as they approach the viewport; show their cached preview during the transition. Handle manual taskbar/Alt-Tab restoration, desktop switches, dialogs, and shutdown recovery. Test these with real applications before changing defaults.
   Keep the one-pixel workaround initially. Replace it where the new parking policy succeeds, and retain it only for demonstrated compatibility cases.
6. Optimize the remaining Canvas overhead.
   Cache monitor geometry per topology change, skip unchanged native moves, reduce repeated enumeration/allocation, and stop redrawing stationary overlays unnecessarily. Replace global unclipping with selective handling where live previews require it. Add a spatial index only if measurements show all-window scans becoming significant.
7. Modernize rendering, then upgrade the UI shell.
   Evaluate flip-model/composition-based rendering, shared GPU resources, and frame scheduling behind the existing interfaces. Then migrate forms/settings/interaction UI. The resource policy and cached-preview machinery should carry over intact.
Build native integration coverage alongside steps 1–5, particularly for mixed DPI, disconnected monitors, multiple windows from one app, failed restoration, and exit/restart.
The first useful milestone is: many off-screen windows remain represented on the canvas, eligible applications are actually minimized, and opening overview does not wake all of them. That delivers the resource-saving behavior we discussed independently of the eventual UI technology.

## Completed: step 1 - pure placement planner

Implemented locally on 11 October 2026. This checkpoint prepares the visibility
controller; it does not implement automatic minimization or cached previews.

### Placement policy and integration

- Added [WindowPlacementPlanner](../Source/Core/WindowPlacementPlanner.cs), a pure
  policy taking world bounds/state, an immutable camera snapshot, monitor working
  areas, clipping policy, prior clipping state and placement intent. Its result
  separates the logical projected rectangle from the native target, viewport
  intersection, move decision and clip/unclip decision. No HWNDs or OS calls enter
  the planner. Intersection is geometric and does not claim that an application
  has stopped rendering or reduced resource use.
- Added `CanvasCamera` for shared projection math. `Canvas.WorldToScreen` and the
  planner use the same rounding and minimum native size rules (200 x 100 pixels).
  Existing world-coordinate and workspace-file formats are unchanged.
- Batch projection, individual minimize/restore event paths, and explicit saved
  layout restoration now use the same off-screen policy. Previously individual
  restoration bypassed clipping. When clipping is enabled, restoring an off-screen
  window now clips and parks it while retaining its real canvas coordinates.
  An explicit saved pinned placement can still be positioned without clipping;
  routine camera projection leaves pinned, minimized and maximized windows alone.
- Moved pin-placement geometry into the planner. Visible actual placements are
  retained; otherwise the projected placement or nearest monitor is used. Pinning
  preserves window dimensions. Final desktop recovery continues to use the
  existing `DesktopViewport.Fit` policy through the planner, fitting oversized
  windows fully into a working area. Those policies intentionally differ.
- Added `WindowManager.ReprojectForDisplayChange` and connected it to the existing
  overview display-change handler. A display change re-evaluates clipping and
  re-parks already clipped windows against the new monitor layout; adding a
  monitor can unclip a newly visible window. Saved world positions are preserved.
  Routine projection still skips repeated movement of clipped windows.
- Each batch now reads monitor working areas once, rather than rebuilding monitor
  arrays for every window and edge search. This is a per-operation snapshot, not
  a long-lived topology cache. Intersection arithmetic uses wider intermediates
  and ignores non-positive monitor areas.

### Preserved behavior and limits

- Production `DisableGreedyDraw=true` remains the default: off-screen clipping
  and one-pixel parking are opt-in. Tests exercise both settings. The one-pixel
  compatibility policy, temporary overview/Alt-Tab unclipping, and existing
  asynchronous projection strategy remain in place.
- The planner expresses desired placement. Step 1 did not report native outcomes;
  step 2 below adds structured acknowledgements before introducing automatic
  minimization and relying on successful state transitions.
- No new DPI scaling is applied: coordinates follow the existing screen-pixel
  convention. Actual mixed-DPI transitions, disconnected-monitor behavior of
  screen-fixed windows, elevated/third-party apps, missed events and crash recovery
  still need native integration coverage. The existing HWND/PID tracking is not
  strengthened by this checkpoint.
- No installed application was replaced or restarted, and no performance saving
  percentage or maximum window count is claimed.

### Verification

`dotnet test Tests/CanvasDesktop.Tests.csproj --no-restore --configuration Release --verbosity minimal`
passes all **332 tests**, up from 292, with no build warnings or errors.

- Planner tests cover fractional projection, minimum sizes, exact edges and
  one-pixel intersections, negative-origin/multiple monitors and gaps, invalid
  topology fallback, large-coordinate intersection arithmetic, retained edge
  parking, pinned placement, and desktop recovery.
- Window-manager tests cover individual/batch parity, unchanged saved bounds,
  minimize/restore events, display removal/addition, and one monitor query for a
  100-window projection pass. This last assertion verifies call count, not CPU/GPU
  throughput.
- Workspace manual-link and exit/restart regressions now run with clipping both
  enabled and disabled, checking native placement separately from canvas state.
- Two `Category=NativePlacement` smoke cases use the real `Win32WindowApi` on
  unshown windows owned by the test process. They verify native empty regions,
  edge placement, deferred movement back into view and region removal, without
  showing or moving user windows. Monitor geometry is synthetic; these are not
  cross-process, actual hardware-topology, live-preview or resource benchmarks.

## Completed: step 2 - native outcomes, diagnostics and synthetic baseline

Implemented and measured locally on 11 October 2026. The native/diagnostic
infrastructure and repeatable synthetic baseline are complete. A real-application
compatibility and performance matrix remains before enabling automatic minimization.

### Native outcomes and failure handling

- Added [NativeOperationResult and PerformanceDiagnostics](../Source/System/PerformanceDiagnostics.cs).
  Moves, deferred batches, region changes and native placement restores now report
  success, queued acknowledgement, failure or cancellation, elapsed API duration,
  affected HWND and an error code where the API defines one. Acknowledgement is
  not proof of a final placement or a displayed frame. Asynchronous moves are
  explicitly `Queued`, rather than reported as completed.
- Added [WindowBatchMover](../Source/System/WindowBatchMover.cs), with an injectable
  native boundary for failure tests. A failed `DeferWindowPos` abandons the deferred
  handle and replays the **entire** discarded batch individually. Previously the
  successful prefix was silently lost. Failed finalization reports affected HWNDs;
  fallback success retains the original API failure in diagnostics. Cancellation
  finalizes a valid queued prefix and labels unattempted windows; it cannot interrupt
  an in-flight USER32 call or undo prior movement. See Microsoft's
  [DeferWindowPos contract](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-deferwindowpos).
- The initial benchmark exposed `ERROR_INVALID_PARAMETER` (87) during transient
  panning: `DeferWindowPos` rejected `SWP_ASYNCWINDOWPOS`. Asynchronous requests now
  use individual `SetWindowPos` calls directly, while synchronous movement retains
  deferred batching. The corrected benchmark recorded no native failures.
- Failed clipping/unclipping does not advance tracked region state or move a window
  as though the visibility change succeeded. Failed/cancelled/superseded parking
  remains eligible for another placement attempt. Projection-worker results are
  consumed on the UI thread; stale results do not replace a different newer target.
  Pending predictions are kept separately from the last observed/acknowledged
  placement; a failed request retains the reconciliation baseline. Neither a
  prediction nor API acknowledgement proves final native geometry. Failed final
  desktop recovery
  aggregates errors and continues processing the other windows.
- Failed `SetWindowRgn` frees its untransferred GDI region. Native restoration checks
  `GetWindowPlacement` before modifying it, preserving existing placement flags.
  APIs without a documented extended error return an unspecified error rather than
  a stale `GetLastError` value.

### Observable costs and export

- The collector keeps per-operation counts/outcomes, total/maximum duration, the
  last 256 operations and a separate last 128 failures in memory under a lock.
  Storage is bounded; normal collection performs no disk I/O and stores no window
  titles, executable paths or document names. Aggregate durations overlap when a
  batch contains instrumented sub-calls and must not be summed as independent work.
- [DwmThumbnailApi](../Source/System/DwmThumbnailApi.cs) instruments registration,
  updates and release. Current/peak registration counts include wallpaper and
  taskbars, not only application previews. A failed unregister retains an
  unconfirmed registration in the gauge; the gauge is not DWM memory usage.
- Overview grid and minimap renderers record frame count and duration including
  `Present`, with presentation failures. These durations include submission and
  vsync waiting, not measured GPU execution time or first-paint latency. The
  benchmark exercises overview; the separate minimap path is instrumented but is
  not covered by the synthetic workload.
- Added the tray action **Export performance diagnostics...**, exporting the
  running instance's bounded snapshot as JSON. Existing environment diagnostics
  remain separate. No installed application was replaced or restarted, so the new
  tray action becomes available when a build containing these changes is launched.

### Repeatable measurement tool

Added [PerformanceHarness](../Tools/PerformanceHarness/README.md), which uses the
production placement/controller/overview code and a separate owned helper process
with animated GDI windows. It installs no global input hooks and enrolls only its
helper's windows, checking their HWND/PID ownership. Temporary windows and overlays
close on completion; the helper also watches its parent for exit.

It measures visible, off-screen/default, clipped, explicitly minimized, stationary
Zooming overview, Panning overview and repeated restoration. Repeats rotate phase
order. Reports retain setup diagnostics, actual native rectangles/styles, process
CPU and memory, per-process GPU engine counters, thumbnail counts, operation
statistics and restore-batch API durations. Inaccessible counters remain null with
an error, rather than appearing as zero.

The host measures the production controllers and overlays **plus the measurement
loop**, excluding the tray app's input hooks, workspace timers and other services.
DWM also includes unrelated desktop activity. The synthetic helper is not a browser,
video player, editor or GPU-heavy application; its default workload explicitly stops
simulation while minimized. A second `--always-work` mode continues simulation and
shows why native minimization cannot guarantee application idling.

### Local baseline: 11 October 2026

Two runs used 8 windows, 2 repeats and 3-second measurement phases, with a 300 ms
settling interval. Host: Windows build 22631, .NET 10.0.12, 12 logical processors,
one 1920 x 1080 monitor with a 1920 x 1032 working area. All eight visible test
windows fit. Off-screen rectangles and the native minimized styles were checked
in the output. The always-work run omitted overview. Exact per-sample data and
commands are retained in the [condensed baseline JSON](Performance/visibility-baseline-2026-10-11.json);
full local histories remain in the ignored `Tools/PerformanceHarness/results/`.

The table shows **ranges of process CPU percentages of one logical core** across
the two repeats, not machine utilization or confidence intervals. CPU-time sampling
and the shared desktop introduce substantial variation in these short runs.

| Phase | Canvas harness CPU | Cooperative target CPU | Always-work target CPU |
| --- | ---: | ---: | ---: |
| Visible | 0.00-2.08 | 16.58-21.35 | 20.79-24.36 |
| Off-screen/default | 0.52-1.04 | 24.47-49.76 | 17.14-43.68 |
| Clipped | 0.00-1.04 | 21.78-46.23 | 14.02-21.79 |
| Minimized | 0.00-0.52 | 0.00 | 8.83-35.38 |
| Stationary overview | 3.12-5.71 | 24.90-35.94 | Not sampled |
| Panning | 4.15-9.87 | 33.22-54.03 | Not sampled |
| Repeated restoration | 2.06-2.44 | 26.30-38.50 | 15.81-27.47 |

Canvas harness values in this table come from the cooperative run. Observations:

- Moving or clipping the helper off-screen did not stop its simulation. Native
  minimization stopped the cooperative helper's work, while the always-work helper
  continued using CPU. These are demonstrations of application behavior, not
  measured saving percentages for real applications.
- Stationary overview rendered 181-184 grid frames per phase, roughly 60 frames/s,
  despite no camera changes. Mean frame duration including `Present` was 16.65-16.85 ms.
  Stationary and panning overview retained 9-10 thumbnail registrations including
  shell previews. This supports the planned stationary-rendering and preview work.
- The cooperative run's four measured **eight-window restore batches** took
  889-1289 ms; the always-work run's six batches took 762-956 ms. These synchronous
  API durations include native restoration, placement and unclipping, not time to
  a correct first frame. They support keeping cached previews before automatic
  minimization and measuring restoration on real apps rather than assuming it is cheap.
- The helper's private memory remained roughly 13.4-15.6 MiB across these phases;
  these runs establish no reliable memory saving. The overview-capable host retained
  roughly 77.5-80.9 MiB of private memory after warmup, versus 19.2-23.9 MiB in the
  separate run that skipped overview. These are separate synthetic processes/runs,
  not an isolated causal GPU-resource memory comparison.
- GPU engine counters were available and retained separately by process/engine.
  DWM CPU and memory reads returned `Win32Exception` in this session and are marked
  unavailable. No DWM CPU attribution or aggregate GPU saving is claimed.
- Both corrected runs recorded **zero failed native operations**, including setup.
  There is no maximum-window-count claim, throughput guarantee or supported-app
  allowlist from these samples.

### Verification and remaining limits

`dotnet test Tests/CanvasDesktop.Tests.csproj --no-restore --configuration Release --verbosity minimal`
passes all **350 tests**, up from 332 after step 1. Release builds of the app and
benchmark pass with no warnings or errors.

New coverage includes deferred-batch begin/middle/finalization failures, full-prefix
fallback, partial failure, cancellation, async acknowledgement, bounded/thread-safe
collection, thumbnail reset semantics, failed clip/unclip/parking retries, worker
cancellation followed by a synchronous commit, and native recovery continuation.
Native smoke tests additionally check invalid HWND failure reporting and a valid
async move without the unsupported deferred flag. Synthetic smoke and both measured
runs exercised cross-process movement and live DWM overview rendering.

Still pending: the real-app workload matrix described in the harness README,
first-correct-frame restoration latency, elevated applications, actual mixed-DPI
and disconnected-monitor transitions, dialogs/self-restoring apps, video/WebGL,
and profiling the entire tray application. Some existing callers outside placement
policy still treat these native operations as best-effort; their failures are now
recorded, not automatically repaired by a new resource-state controller. Global
unclipping, continuous stationary rendering and compatibility edge parking are
otherwise retained for the later optimization steps.

## Completed: step 3 - runtime visibility and automatic-parking state

Implemented locally on 11 October 2026. Automatic native minimization remains
disabled: this checkpoint establishes classification, ownership and timing policy.

### Runtime policy and integration

- Added [WindowVisibilityTracker](../Source/Core/WindowVisibilityTracker.cs), a
  session-only state machine distinguishing visible, near-viewport, off-screen,
  automatically minimized and user-minimized windows. Geometry uses logical camera
  projections, never the one-pixel compatibility parking position. Pinned windows
  use their actual screen rectangle. Multiple monitors are tested independently;
  wide intersection arithmetic supports negative origins and large coordinates.
- Visibility observations run during discovery, background ticks, camera changes
  (including suspended overview projection), batch/individual placement, and native
  minimize/restore events. `WindowManager.GetRuntimeVisibility` exposes immutable
  snapshots. Removal, reset and disposal discard runtime state. No runtime fields
  were added to `WorldRect`, canvas snapshots or workspace files. Existing user
  minimize events still collapse the saved canvas representation; Canvas-owned
  minimization has a separate ownership check.
- The departure margin is **512 screen pixels**, and the approach margin is
  **256 pixels**. A window classified far away must enter the smaller approach
  band before becoming near; a near window must leave the larger departure band
  before becoming off-screen. Eligible normal, unpinned windows require **1500 ms**
  continuously far away before `CanPark`. Near parked windows require **150 ms**
  before `ShouldRestore`; actual viewport intersection or loss of eligibility
  requests recovery immediately. Invalid topology/geometry, unavailable identity,
  user minimization, maximization and other-desktop windows cannot become parking
  candidates. These are policy signals only; no minimize or restore is issued.
- Reused the existing `WorkspaceWindowIdentity` (HWND, PID and process-start ticks)
  and `WorkspaceIdentity.InspectProcess`. Process inspection runs once per PID in
  a tick pass; camera frames reuse the last observed identity and invalidate a
  changed PID immediately. Individual event paths refresh identity. An unavailable
  identity discards ownership and cannot authorize automatic parking.
- Parking reservations include a fresh session lifetime token. Identity replacement,
  removal and re-enrollment invalidate older tokens, including same-process HWND
  reuse when destruction/removal was observed. Ownership requires a matching
  reservation, a successful operation for that HWND, and confirmed native minimized
  state. Failure, cancellation, queued acknowledgement alone and unconfirmed
  minimization do not establish ownership. Native restoration relinquishes ownership;
  a later minimization is treated as user-owned. Existing region clipping remains
  independent of automatic-minimization ownership.

### Verification and limits

Added [WindowVisibilityTests](../Tests/WindowVisibilityTests.cs) covering exact
viewport/margin edges, hysteresis, dwell reset, ownership confirmation and failures,
restore delay and immediate recovery, identity/process-start replacement, lifetime
invalidation, unavailable identity, invalid topology, wide/multi-monitor geometry,
pinned/maximized exclusions, stale removal, suspended projection and unchanged layout.
All **17** new cases pass when filtered to `FullyQualifiedName~WindowVisibilityTests`.

`dotnet test Tests/CanvasDesktop.Tests.csproj --no-restore --configuration Release --verbosity minimal -p:NoWarn=WPF0001`
passes the current working-tree suite (**390 tests**, including concurrent unrelated
work). The unsuppressed command initially failed on the existing experimental WPF
`Window.ThemeMode` diagnostic in `Source/Workspace/ZoneWindow.cs`; suppression was
limited to the validation command. No production project suppression was added by
this checkpoint.

No real applications were minimized and no resource savings are claimed. The
tracker is independent of native calls; step 5 must connect reservation/completion
to actual operations, revalidate fresh native identity immediately before every
parking/restoration, handle pending-operation events and failures, and preserve
shutdown/desktop-switch recovery. Polling identity is not an atomic OS guarantee:
same-process HWND reuse with a missed destruction event cannot be proven from
HWND/PID/process-start alone. Cached previews, native minimization, dialogs and the
real-application compatibility matrix remain steps 4/5.

### Next implementation checkpoint

Step 4 is implemented below. The next checkpoint is opt-in native minimization
in step 5. Do not infer resource idling from geometric classification or native
API acknowledgement.


## Review fixes for steps 1 and 2

Implemented on 11 October 2026 following the code review.

- Fixed failed-move reconciliation: the window manager now retains the previous
  observed/acknowledged placement separately from its pending request. Failed or
  cancelled requests remain retryable without disabling subsequent manual drag
  and resize tracking. Explicit saved placement and pinning use the same tracking
  path. Initial requests establish an observed baseline even without prior discovery.
- Pending requests have manager-local revision IDs, so an older worker completion
  cannot consume a newer request merely because their requested rectangles match.
  Reconciliation recognizes both the pending expected rectangle and the previous
  placement while a worker move is outstanding. Pending tracking is cleared when
  a window is removed, a manual change supersedes it, or the canvas is reset.
- Fixed the fake batch API to honor `PosOnly`/`SWP_NOSIZE`. Batch projection
  intentionally retains native window dimensions; individual and saved placement
  may resize to the projected dimensions. These paths share logical position and
  clipping policy, not an unconditional promise of equal native sizes. Accepted
  position-only moves now retain the known native dimensions in the reconciliation
  baseline, avoiding feedback from a resize that did not happen.
- Strengthened parity assertions to inspect resulting rectangles and added a
  fractional-zoom case where projected and actual sizes differ, with clipping both
  enabled and disabled. Added fake and real USER32 move-only versus resize checks;
  native tests use unshown windows owned by the test process.
- Added regression cases for individual/batch failures with and without prior
  registration, unchanged geometry after failure, subsequent manual drag/resize,
  successful retry, failed saved placement/pinning, and reconciliation while a
  worker request is pending.

Verification: the full current workspace Release test run passes **391 tests**
with no warnings or errors. These fixes add 13 regression cases; the total also
includes concurrent visibility/zone work in this workspace. The earlier 350-test
result remains the original step 2 checkpoint. No installed application was
replaced or restarted, and the recorded synthetic baseline was not remeasured
for these correctness fixes.

## Completed: step 4 - cached previews and lifecycle

Implemented locally on 11 October 2026. This supplies the preview machinery for
step 5; automatic native minimization is still disabled. Normal operation keeps
using live previews until a future parking controller explicitly requests capture
and establishes automatic-parking ownership.

### Capture, limits and ownership

- Added [WindowPreviewCache](../Source/Core/WindowPreviewCache.cs), owned by the
  window manager. Captures use the runtime identity plus its session lifetime
  token, rather than HWND alone. Identity replacement/unavailability, removal,
  reset and disposal invalidate images. Results arriving after invalidation are
  discarded and disposed; a stale capture cannot be attached to a new lifetime.
- Images retain aspect ratio without upscaling, capped at **1024 x 576 pixels**.
  The default **32 MiB image budget** includes cached 32-bit pixel payloads, the
  shared unavailable-preview image and a reservation for one in-flight capture.
  LRU eviction and a separate **64-image count limit** bound both large snapshots
  and many tiny windows. `ImageBytes`, `Count` and `CapturePending` expose the
  controlled resources. This is not a cap on total process/GPU memory: temporary
  GDI bookkeeping, window redirection surfaces and DWM thumbnail resources are
  OS-managed overhead in addition to the image payload. Source surfaces have
  the same resolution/count bounds; their actual memory cost remains unmeasured.
- [WindowPreviewCapture](../Source/System/WindowPreviewCapture.cs) uses best-effort
  native `PrintWindow` capture on a background thread, with GDI mapping directly
  into the capped bitmap; it never allocates a full-resolution source image.
  Capture duration/outcome is recorded as `CaptureWindowPreview` in the existing
  performance collector. Entirely black/untouched frames are treated as unavailable.
  Nothing writes captured pixels to disk or workspace storage.
- There is at most **one in-flight capture per cache**, with no pending queue.
  New requests are rejected while capture or its undrained result is outstanding.
  The UI consumes completions during background ticks or preview-state refresh.
  Microsoft's [PrintWindow contract](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-printwindow)
  permits a blocking cross-process call: a hung application can stall this single
  background worker. Disposal does not join it; its bounded bitmap is released
  when the call returns, and no further workers can accumulate in that cache.
  A hung capture is not reported as successful or as a completed timeout.
- `WindowManager.CapturePreviewBeforeParking` refreshes identity and checks parking
  eligibility before requesting capture. `HasCachedPreview` becomes true only for
  a real image of the current lifetime; placeholders never satisfy it. Step 5 must
  wait for a valid image and revalidate identity/eligibility before minimizing.

### Overview representation

- Added [CachedPreviewWindow](../Source/Overlays/Overview/CachedPreviewWindow.cs),
  a static, borderless, nonactivating tool window outside the virtual desktop,
  created lazily as a DWM source for a cached bitmap. It has no taskbar/Alt-Tab
  entry. Live and cached sources therefore share the existing DWM registration
  order, per-monitor culling, camera projection and foreground handling, without
  introducing a second image layer with conflicting overlap order. The original
  application HWND remains the selection/hit-test identity.
- [OverviewThumbnails](../Source/Overlays/Overview/OverviewThumbnails.cs) now selects
  the live application or cached source according to runtime parking ownership.
  It re-registers when the source changes, including recycled numeric HWNDs after
  cache-surface replacement. Missing/evicted images use one shared bounded
  **Preview unavailable** image; failure to create a source never falls back to
  the parked application's live thumbnail.
- Overview opening refreshes runtime identity and consumes completed captures;
  it does not request capture or restore automatically parked applications.
  Closing overview unregisters its thumbnails, then releases cached source HWNDs
  while retaining bounded CPU images for reopening. Cache changes coalesce a UI
  reconciliation; overview disposal detaches that subscription. Existing global
  unclipping remains unchanged for live previews, as planned for later steps.

### Verification and remaining limits

Added [WindowPreviewTests](../Tests/WindowPreviewTests.cs), with **19 cases** covering
resolution/aspect limits, image/count budgets, LRU eviction, single-flight capture,
failures/retry, late results, identity/lifetime invalidation, image/surface disposal,
surface reuse/reopening, a shared placeholder, source switching and re-registration.
A simulated parked-window test exercises production manager/list/thumbnail code,
confirming unchanged world bounds and no native move or restore calls.

Three `Category=NativePreview` smoke cases check invalid-HWND capture, rejection of
a black frame, and scaled red/blue pixels from a test-owned GDI window followed by
real DWM registration/update/release of its cached source. Owned source windows are
nonactivating and entirely off-desktop; no user application is moved or minimized.
The DWM assertions establish API acceptance, not a visual screenshot of the final
composited overview or first-correct-frame timing.

`dotnet test Tests/CanvasDesktop.Tests.csproj --no-restore --configuration Release --verbosity minimal`
passes **410 tests** in the current working tree, including concurrent unrelated
work, with no warnings or errors. The app and performance harness Release builds
also pass. The earlier experimental WPF diagnostic has been handled in the
concurrent zone work; this checkpoint's final validation needs no command-line
suppression. The step 2 performance baseline was not remeasured.

Native capture is best effort, not a supported-app guarantee. Some applications
ignore GDI mapping, produce incomplete GPU/protected/video content, or report
success with unusable pixels. Black-frame rejection is conservative (a legitimately
black window is also unavailable), and nonblack partial/stale frames cannot be
reliably detected from the return value. Already clipped windows may also fail
capture; they retain compatibility parking until a usable capture path is proven.
Real-application capture quality, actual mixed-DPI sizing, final composed overlap
and restoration transitions still need the step 5 compatibility matrix. A more
general capture backend can implement `IWindowPreviewCapture` without changing
cache ownership or overview source selection. No resource-saving claim is made.

### Next implementation checkpoint

Step 5 is implemented below. Real-application compatibility validation remains
required before changing defaults.

## Implemented: step 5 - opt-in automatic native minimization

Implemented on 11 October 2026. `EnableAutomaticMinimization=false` is the default
in `AppConfig`, its generated config, and backends without explicit support.
Set `EnableAutomaticMinimization=true` in `config.ini` to opt in; the user's live
configuration was not changed. The existing one-pixel/clipping placement policy
remains the fallback for ineligible windows and unavailable captures.

### Parking, previews and restoration

- [WindowManager.AutomaticParking](../Source/Core/WindowManager.AutomaticParking.cs)
  connects the step 3 departure/approach margins and timers to native operations.
  A window must remain eligible and far off-screen for **1,500 ms**, complete a
  fresh successful bounded capture, and pass fresh HWND/PID/process-start checks
  before parking. Placeholders and old images cannot satisfy a failed new capture.
  Capture/minimize failures back off for **10 seconds**.
- Only normal, unpinned windows on the current virtual desktop qualify. Native
  checks exclude the foreground window, disabled windows, owned windows,
  standard dialogs, windows with visible owned windows, tools and Canvas's own
  process. User-minimized and maximized windows remain outside automatic parking.
  Custom dialog frameworks cannot all be identified from these native properties.
- Ownership is recorded before the native minimize call to handle reentrant
  events. Only confirmed native minimization completes parking; a partially
  failed or throwing operation retains a recovery debt if it affected the window.
  Parking never changes saved world coordinates or `WindowState.Normal` intent.
  Projection and reconciliation skip owned windows rather than consuming their
  icon geometry. Pending projection work is cancelled/drained before minimizing.
- [Win32WindowApi](../Source/System/Win32WindowApi.cs) uses `SW_SHOWMINNOACTIVE`
  and verifies the native minimized style. Restoration uses a normal placement
  with `SW_SHOWNOACTIVATE`, clears restore-to-maximized intent, and reconciles the
  target rectangle with `SWP_NOACTIVATE | SWP_NOZORDER`. Identity is rechecked
  around these operations. These checks cannot make cross-process operations
  atomic; same-process HWND reuse with a missed destruction event remains a limit.
- Visible windows restore immediately; near-viewport windows restore after the
  **150 ms** approach delay. Cached overview sources remain selected for another
  **150 ms** after successful restoration, including camera movement during that
  interval. This is a transition allowance, not a first-correct-frame guarantee.
  Overview receives parking-state notifications and preserves original selection
  identities without waking every parked application when opened.
- Manual taskbar/Alt-Tab restoration relinquishes Canvas ownership and excludes
  that window lifetime from further parking. Foreground restoration recenters an
  off-screen canvas window, including monitors with negative origins. A new user
  minimization after automatic restoration remains user intent. Pinning restores
  an owned window before applying the pin.

### Recovery and verification

Disabling the option or becoming ineligible requests restoration. Failed restores
retain ownership and cached representation for a later retry. Reset (including
virtual-desktop switching), disposal and final desktop recovery attempt every
owned window, fitting its pre-parking rectangle onto available working areas.
Reset retains failed debts across model/cache clearing for later retries; disposal
and exit aggregate failures after attempting the remaining windows. Recovery skips
replaced identities, including final desktop fitting for a stale parking record.
User-minimized windows are not automatically expanded. There is no persisted
parking journal or crash/restart recovery guarantee.

[AutomaticParkingTests](../Tests/AutomaticParkingTests.cs) adds **26 fake-only
cases** covering capture gating, timers, eligibility, independent windows from one
process, saved intent, nonactivating restoration, transition retention, event
ordering, recycled handles, failures/retries, manual restoration, pinning, disabled
configuration and reset/disposal/exit recovery.
[AutomaticParkingNativeTests](../Tests/AutomaticParkingNativeTests.cs) adds **two
native smoke checks**: invalid handles fail closed; a nonactivating test-owned
off-desktop window minimizes/restores through the production API with its geometry
and foreground window preserved. These do not establish compatibility with other
applications or the visual quality of the final overview.

Live testing was paused while the user was gaming and resumed only after their
explicit authorization. The final Release test run passes **479 tests**, with no
warnings or errors, using:

```powershell
dotnet test Tests/CanvasDesktop.Tests.csproj --no-restore --configuration Release --verbosity minimal
```

The app and performance harness Release builds also pass with zero warnings or
errors. The harness was built only; the step 2 performance baseline was not rerun.

The real-application matrix remains pending: browsers, editors, GPU/video windows,
custom dialogs, mixed-DPI/topology changes, taskbar/Alt-Tab event ordering and
virtual-desktop transitions. Native minimize/restore calls are synchronous and
can block the UI when another process is slow; their production latency and actual
resource savings have not been measured. Existing operation diagnostics record
native outcomes/durations. Keep this feature opt-in until the compatibility matrix
and comparative performance measurements justify a default change.

### Next implementation checkpoint

Step 6: measure and reduce remaining Canvas overhead, including monitor-geometry
caching, unchanged moves, repeated scans/allocation, stationary overview redraws
and selective unclipping. Keep the step 5 compatibility matrix outstanding while
making these optimizations.
