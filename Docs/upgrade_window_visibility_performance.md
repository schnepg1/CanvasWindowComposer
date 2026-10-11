# Window visibility and performance upgrade

## Implementation status

Updated 11 October 2026.

| Step | Status |
| --- | --- |
| 1. Pure placement planner | Implemented and verified locally; details below |
| 2. Native-operation results and performance baseline | Next |
| 3. Runtime visibility and automatic-parking state | Pending |
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
- The planner expresses desired placement; native success/failure is still not
  reported through structured results. This is the next step, before introducing
  automatic minimization and relying on successful state transitions.
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

### Next implementation checkpoint

Step 2: introduce structured native-operation results (including failures and
durations), then add a repeatable benchmark that separates Canvas, DWM and target
application costs across visible, off-screen/default, clipped and manually
minimized states. Include stationary overview, panning and restoration latency.
Only proceed to resource-state transitions once those outcomes are observable.
