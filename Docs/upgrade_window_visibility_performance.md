# Window visibility and performance upgrade

## Implementation status

Updated 11 October 2026.

| Step | Status |
| --- | --- |
| 1. Pure placement planner | Implemented and verified locally; details below |
| 2. Native-operation results and performance baseline | Implemented; synthetic baseline recorded, real-app matrix remains |
| 3. Runtime visibility and automatic-parking state | Next |
| 4. Cached previews | Pending |
| 5. Opt-in automatic minimization | Pending |
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
  Predicted positions while requests are pending still support existing reconciliation;
  they do not represent confirmed native geometry. Failed final desktop recovery
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

### Next implementation checkpoint

Step 3: add runtime visibility and ownership state, separating user minimization
from Canvas-owned automatic parking, with identity validation, viewport margins
and transition delays. First add classification/state tests with automatic native
minimization still disabled. Step 4 then provides cached previews; step 5 introduces
opt-in native minimization and real-application validation. Do not infer resource
idling merely from geometric off-screen classification or native API acknowledgement.
