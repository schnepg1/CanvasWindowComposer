# Smooth Zooming and Panning Architecture & Troubleshooting Guide

## 1. Overview & Core Principles

CanvasWindowComposer provides infinite-canvas window management on Windows. Two fundamental navigation gestures drive user interaction:
- **Canvas Zooming:** Adjusting the scale factor ($\text{Zoom}$) toward or away from a focal point under the cursor.
- **Canvas Panning:** Translating the camera ($\text{CamX}, \text{CamY}$) horizontally and vertically across the virtual workspace.

Earlier versions suffered from noticeable latency, mouse-wheel detent jerks, threshold hitching (when windows transition between live DWM thumbnails and placeholder cards), and severe panning lag where mouse route history continued playing long after releasing the mouse button.

This document details the architectural mechanisms, lessons learned, and invariants required to maintain instant, butter-smooth navigation at native monitor refresh rates.

---

## 2. Core Navigation Invariants

When working on overview navigation, rendering, or window tracking, the following invariants **must always be preserved**:

```
+---------------------------------------------------------------------------------------+
|                                    MOTION ACTIVE                                      |
| (OverviewManager.MotionActive == true: Zooming, Drag Panning, Window Drag, Momentum)  |
+---------------------------------------------------------------------------------------+
        |                                                   |
        v                                                   v
[ FAST RENDERING PATH ]                             [ DEFERRED EXPENSIVE WORK ]
- Coalesce mouse moves via BeginInvoke              - Defer SetOverviewViewport()
- WPF renders asynchronously                        - Defer card mode & placeholder swaps
- Suppress active window glow animations            - Suppress synchronous FlushAnnotations()
- Freeze geometries (Direct3D composition)          - Skip redundant Win32 RaiseCanvasPanels()
- In-motion dynamic LOD wireframe rendering         - Bypass text layout & icon draws at distant zoom
        |                                                   |
        +-------------------------+-------------------------+
                                  |
                   When motion settles (!MotionActive)
                                  v
                    [ SYNCHRONOUS SETTLE / FLUSH ]
                    - SetOverviewViewport()
                    - FlushAnnotations() (WPF Dispatcher barrier)
                    - RaiseCanvasPanels()
                    - Re-enable glow animations & info cards
                    - Restore full placeholder detail (text, headers, icons)
```

---

## 3. Deep-Dive: Problems Discovered & Solutions Implemented

### 3.1. Eliminating Synchronous Render Blocking During Panning

#### The Problem
High-frequency mouse movements (125 Hz – 1000 Hz) caused severe panning sluggishness. When making circular motions with the mouse and releasing the middle button, the canvas continued playing out the circular path for seconds afterward.

#### The Root Cause
1. `HandleMouseMove` in `OverviewManager.Pointer.cs` was calling `_visualFrames.RequestAndFlush()` on every raw mouse packet.
2. In `OverviewManager.Zones.cs`, `RefreshZoneAnnotations()` executed:
   ```csharp
   firstPass?.FlushAnnotations();
   ```
   `FlushAnnotations()` called `Dispatcher.Invoke(DispatcherPriority.Render, RenderBarrier)` in `OverviewAnnotationWindow.cs`.
3. Forcing a synchronous WPF layout (`UpdateLayout()`) and render pass (`Dispatcher.Invoke`) on every sub-pixel mouse report cost 10–15 ms of UI thread time per event.
4. Mouse input arrived much faster than the UI thread could process it, filling the Windows message queue with a massive backlog of `WM_MOUSEMOVE` messages. The release event (`WM_MBUTTONUP`) sat at the back of the queue, causing the app to slowly drain the backlog long after the user physically let go.

#### The Fix
- **Bypass Synchronous Flush During Motion:** In [`OverviewManager.Zones.cs`](file:///C:/Users/ESCEHA/.gemini/antigravity/worktrees/CanvasWindowComposer/smooth_canvas_zooming/Source/Overlays/Overview/OverviewManager.Zones.cs#L287):
  ```csharp
  foreach (var p in _passes) if (p.Visible) p.PrepareAnnotations();
  if (!MotionActive) firstPass?.FlushAnnotations();
  ```
  During motion, WPF renders asynchronously via its composition engine. UI thread time per frame dropped from 15 ms to < 0.2 ms.
- **Asynchronous Coalesced Request:** In [`OverviewManager.Pointer.cs`](file:///C:/Users/ESCEHA/.gemini/antigravity/worktrees/CanvasWindowComposer/smooth_canvas_zooming/Source/Overlays/Overview/OverviewManager.Pointer.cs#L126), mouse moves now call `_visualFrames.Request()` instead of `RequestAndFlush()`. `RequestAndFlush()` is reserved for `HandleMouseUp` when settling into the final resting position.

---

### 3.2. Paced, Starvation-Free Frame Scheduling (`BeginInvoke` vs. `WM_TIMER`)

#### The Problem
Using `_visualFrames.Request()` with a WinForms `Timer` caused frame starvation during fast mouse drag or wheel bursts.

#### The Root Cause
`System.Windows.Forms.Timer` is based on Win32 `WM_TIMER`. In the Windows message pump, `WM_TIMER` and `WM_PAINT` have the lowest priority. If input messages (`WM_MOUSEMOVE`) arrive continuously, `WM_TIMER` is starved and never fires until the mouse stops moving.

#### The Fix
In [`OverviewManager.Frames.cs`](file:///C:/Users/ESCEHA/.gemini/antigravity/worktrees/CanvasWindowComposer/smooth_canvas_zooming/Source/Overlays/Overview/OverviewManager.Frames.cs#L16), `ScheduleVisualFrame` was redesigned to use WinForms `BeginInvoke`:
```csharp
private bool _visualFramePosted;

private void ScheduleVisualFrame(Action frame)
{
    _scheduledVisualFrame = frame;
    if (_visualFramePosted) return;

    if (_passes.Count > 0 && _passes[0].IsHandleCreated && !_passes[0].IsDisposed)
    {
        _visualFramePosted = true;
        try
        {
            _passes[0].BeginInvoke((Action)(() =>
            {
                _visualFramePosted = false;
                var scheduled = _scheduledVisualFrame;
                _scheduledVisualFrame = null;
                if (!_disposed && CurrentMode != OverviewMode.Hidden) scheduled?.Invoke();
            }));
            return;
        }
        catch (InvalidOperationException)
        {
            _visualFramePosted = false;
        }
    }
    // Fallback for headless unit test rigs without HWND
    _visualFrameTimer ??= new Timer { Interval = 16 };
    _visualFrameTimer.Tick += (_, _) =>
    {
        _visualFrameTimer?.Stop();
        var scheduled = _scheduledVisualFrame;
        _scheduledVisualFrame = null;
        if (!_disposed && CurrentMode != OverviewMode.Hidden) scheduled?.Invoke();
    };
    _visualFrameTimer.Start();
}
```
- `BeginInvoke` posts a normal-priority `WM_USER` message to the queue. It is never starved by `WM_MOUSEMOVE` and interleaves smoothly with input.
- `_visualFramePosted` guarantees that any number of high-frequency mouse reports collapse into a single pending frame, matching the monitor's refresh rate.

---

### 3.3. Smooth Zoom Acceleration & Immediate Frame 0 Response

#### The Problem
Mechanical mouse wheels generate discrete detents (notches, typically 120 units per click). Translating each notch directly into a stepped zoom change feels jerky. Furthermore, capping zoom speed makes fast wheel flicks feel sluggish, while uncapped stepping creates stutter when zooming rapidly.

#### The Architecture
- [`SmoothZoomTracker.cs`](file:///C:/Users/ESCEHA/.gemini/antigravity/worktrees/CanvasWindowComposer/smooth_canvas_zooming/Source/Overlays/Overview/SmoothZoomTracker.cs): Models zoom using exponential curves centered around the focal cursor point:
  $$\text{targetZoom} = \text{startZoom} \times \text{multiplier}^{\text{notches}}$$
- **Immediate Frame-0 Response:** Instead of waiting 16 ms for the first animation tick, a portion of the zoom delta is applied immediately on the input event tick. This eliminates the perceivable input hitch.
- **Velocity Acceleration:** Flicks in quick succession increase velocity exponentially (similar to mouse acceleration curves), allowing users to zoom across vast canvas distances with a rapid flick while retaining fine precision on single notches.
- **Critically-Damped Spring Animation:** Interpolates toward target zoom over time:
  $$\text{Zoom}(t) = \text{Target} - (\text{Target} - \text{Start}) \times e^{-\lambda t}$$

---

### 3.4. Deferring Window Viewports & Threshold Transitions During Motion

#### The Problem
At specific zoom thresholds, windows transition between live DWM thumbnails and placeholder cards. When zooming or panning rapidly, windows crossed these thresholds repeatedly, causing:
1. Live windows flickering into temporary text cards.
2. Synchronous recalculation of window bounds, occluders, and proximity parking on every animation tick.

#### The Fix
In [`OverviewManager.cs`](file:///C:/Users/ESCEHA/.gemini/antigravity/worktrees/CanvasWindowComposer/smooth_canvas_zooming/Source/Overlays/Overview/OverviewManager.cs#L120):
```csharp
bool isMoving = MotionActive;
if (!isMoving)
{
    _wm.SetOverviewViewport(proximityCamera,
        _passes.ConvertAll(p => (p.Screen.Bounds.X, p.Screen.Bounds.Y, p.Screen.Bounds.Width, p.Screen.Bounds.Height)));
    if (_placeholderWorkspace?.SetOverviewCardMode(_wm.OverviewUsesInfoCards) == true)
    {
        _placeholderTitles = _placeholderWorkspace.GetPlaceholderTitles();
        RefreshOverviewWindows();
    }
    RefreshCardIdentities();
}
```
- During motion (`isMoving == true`), existing live thumbnails and placeholders maintain their current representation without triggering threshold swaps or layout rebuilds.
- Only once motion has settled (`isMoving == false`) does the overview recalculate proximity, card modes, and titles.

---

### 3.5. Active Window Glow Animation Suppression & Geometry Freezing

#### The Problem
Active windows feature a pulsing glow border. When multiple active windows were visible simultaneously during zoom or pan:
- The pulsing animation continuously invalidated WPF visual trees and requested repaints.
- Unfrozen WPF geometry objects required re-evaluation on every frame.

#### The Fix
- In `PlaceholderAnnotationLayer.cs` and `OverviewAnnotationSurface.cs`, glow animation is suppressed during active motion:
  - While `motionActive == true`, the glow renders with a steady 0.5-opacity baseline instead of recalculating pulse opacity on every tick.
  - Geometry paths are frozen using `.Freeze()`:
    ```csharp
    geometry.Freeze();
    ```
    This signals to WPF that the geometry is immutable, enabling hardware caching and direct Direct3D composition.

---

### 3.6. Redundant Win32 Panel Reordering

#### The Problem
On every camera reconciliation frame, [`RaiseCanvasPanels()`](file:///C:/Users/ESCEHA/.gemini/antigravity/worktrees/CanvasWindowComposer/smooth_canvas_zooming/Source/Overlays/Overview/OverviewManager.Panels.cs#L26) was called:
- It executed `SetWindowPos` on 6 overlay tool windows (`_peekWindow`, `_backpack`, `_temporaryDrawer`, `_backgroundEditor`, `_zoneWindow`, `_zoneNavigator`).
- Since tool panel z-order does not change during camera drag, these Win32 calls were pure overhead.

#### The Fix
In [`OverviewManager.cs`](file:///C:/Users/ESCEHA/.gemini/antigravity/worktrees/CanvasWindowComposer/smooth_canvas_zooming/Source/Overlays/Overview/OverviewManager.cs#L136):
```csharp
if (!isMoving) RaiseCanvasPanels();
```
All 6 Win32 calls are bypassed entirely on motion frames.

---

### 3.7. Phantom Inertia Suppression

#### The Problem
If the user panned to a location, paused with the middle button held stationary, and then released the button, the canvas would occasionally fling into a momentum coast.

#### The Fix
In [`InertiaTracker.cs`](file:///C:/Users/ESCEHA/.gemini/antigravity/worktrees/CanvasWindowComposer/smooth_canvas_zooming/Source/Core/InertiaTracker.cs#L48):
```csharp
if (_samples.Count < 2 || (_samples.Count > 0 && released - _samples[^1].time > 40))
{
    _samples.Clear();
    return false;
}
```
If the pause between the last movement sample and mouse release exceeds 40 ms, the gesture is treated as an intentional stationary stop. Samples are cleared, and no inertia is kicked off.

---

### 3.8. Dynamic Level-of-Detail (LOD) Wireframe Rendering at Distant Zooms

#### The Problem
When zoomed out across large workspaces with 50+ windows in the overview, high-frequency camera panning and zooming generated substantial CPU layout time and hundreds of WPF drawing calls:
- Every visible placeholder card executed `PlaceholderLabelLayout.Get` and built WPF `FormattedText` objects with font measurements, word trimming, and glyph allocations.
- Each card rendered complex visual elements: logo icons with aspect scaling and easing interpolation, cached bitmap snapshots, launch status pills, and sweep animations.

#### The Architecture & Solution
In [`PlaceholderAnnotationLayer.cs`](file:///Source/Overlays/Overview/PlaceholderAnnotationLayer.cs):
- **Activation Conditions:** While camera motion is active (`OverviewManager.MotionActive == true`), cards at distant zoom (`zoom < 0.25`) or small projected size (`projected size < 40 px`) bypass all text layout, header styling, and icons.
- **Lightweight Wireframe Rectangles:** In-motion LOD cards render as single, flat, Direct3D-frozen color-coded wireframe rectangles (`dc.DrawRectangle(fill, outline, rect)`):
  - Inactive/normal cards: dark slate wireframe.
  - Active running cards: sapphire/azure wireframe.
  - Minimized cards: ocean teal wireframe.
- **Non-Destructive Restoration:** Dynamic LOD is strictly an in-motion optimization. When motion settles (`!MotionActive`), resting placeholder cards immediately hydrate with their expected detail level (Detailed, Simple, Live preview, icon scaling) based on standard zoom thresholds.

---

## 4. Troubleshooting Guide

Use this table if regressions occur in future changes:

| Symptom | Likely Cause | Files to Inspect |
|---|---|---|
| **Panning has a delayed trailing "animation" after releasing mouse** | Synchronous render blocking (`Dispatcher.Invoke`) in mouse move or queue starvation | `OverviewManager.Zones.cs`<br>`OverviewManager.Pointer.cs`<br>`OverviewManager.Frames.cs` |
| **Fast wheel zooming stutters or hitches at zoom thresholds** | Viewport or card mode being recalculated during active motion | `OverviewManager.cs` (`!isMoving` guard on `SetOverviewViewport`) |
| **Active windows flicker or drop frames during zoom** | Pulsing glow animation or card mode swapping while moving | `PlaceholderAnnotationLayer.cs`<br>`OverviewAnnotationSurface.cs` |
| **Flinging/inertia happens when releasing after a stop** | Missing stationary pause check in `InertiaTracker` | `InertiaTracker.cs` (check 40 ms threshold) |
| **Zoom feels laggy on the first mouse wheel tick** | Frame 0 immediate step missing from smooth zoom tracker | `SmoothZoomTracker.cs`<br>`OverviewManager.SmoothZoom.cs` |
| **Frame drops during camera drag with multiple tool windows open** | Redundant Win32 `SetWindowPos` calls per motion frame | `OverviewManager.cs` (`!isMoving` guard on `RaiseCanvasPanels`) |

---

## 5. Verification Test Suite

Always run the full suite before and after modifying navigation code:

```powershell
# Targeted zoom, pan, and inertia unit tests:
dotnet test Tests/CanvasDesktop.Tests.csproj -c Release -p:CanvasSkipTestPreflight=true --filter "FullyQualifiedName~FastZoomSimplificationTests|FullyQualifiedName~ActiveWindowPlaceholderGlowTests|FullyQualifiedName~InertiaTrackerTests|FullyQualifiedName~OverviewFrameSchedulerTests|FullyQualifiedName~MiddleMousePanMovesCameraAndMarksMotionActive"

# Complete non-native test suite (1,390+ tests):
dotnet test Tests/CanvasDesktop.Tests.csproj -c Release -p:CanvasSkipTestPreflight=true --filter "Category!=NativeRendering&Category!=NativeParking&Category!=NativePlacement&Category!=NativePreview"
```
