# Architecture Plan: Minimap Flight Visibility, 5-Second Motion Keepalive & Z-Order Reliability

## 1. Problem Statement & Scope
The minimap overlay has several bugs and regressions:
1. **Flight Navigation Visibility**: While camera flight (`WindowNavigationFlight`, `NavigateToWindow`, `NavigateToCamera`) is active, the minimap should be active to visualize where the flight is traveling. Currently, flight does not explicitly activate the minimap, does not keep it active during long flights, and may let it fade out mid-flight.
2. **Motion Activation & 5-Second Hold**: Any camera movement (zooming or panning) in Canvas mode or Desktop mode should activate the minimap and keep it visible for 5 seconds (`HoldMs = 5000`) before fading. Currently, `HoldMs` is set to `2000` (2 seconds), and certain desktop pan gestures (e.g. `input.DragStarted`) only call `RaiseWithoutActivation` without waking the minimap (`NotifyCanvasChanged`).
3. **Hover Keepalive**: Hovering over the minimap must keep it active indefinitely with full opacity so the user can interact with it. Currently, hover relies only on WinForms `OnMouseEnter` (which is unreliable for layered windows and does not handle cursors already over the client area upon show), and `OnMouseMove` does not restore full opacity or cancel fade.
4. **Z-Order Invisibility Bug ("sometimes doesn't show at all")**:
   - In `MinimapOverlay.RaiseWithoutActivation()`, `PInvoke.SetWindowPos` passes `HWND.Null` as `hWndInsertAfter`. For a topmost window in Win32, `HWND.Null` (`HWND_TOP`) drops the window beneath other topmost windows. Fullscreen `OverviewOverlay` passes (`_passes`) are topmost surfaces (`TopMost = true`, raised with `(HWND)new IntPtr(-1)`). Consequently, the minimap gets trapped underneath the fullscreen DirectX overlay and becomes completely invisible.
   - `NotifyCanvasChanged()` never called `RaiseWithoutActivation()`.
   - `OverviewManager.RaiseCanvasPanels()` raises all other floating tools above fullscreen passes but had no hook to raise the minimap.

---

## 2. Component & Architecture Impact

### Files Modified:
1. `Source/Overlays/Minimap/MinimapOverlay.cs`:
   - Increase `HoldMs` from `2000` to `5000`.
   - Fix `RaiseWithoutActivation()` to pass `(HWND)new IntPtr(-1)` (`HWND_TOPMOST`).
   - Call `RaiseWithoutActivation()` in `NotifyCanvasChanged()`.
   - Call `NotifyCanvasChanged()` on `input.DragStarted`.
   - Subscribe to `_overview.CanvasPanelsRaised`, `_overview.WindowNavigationStarted`, and `_overview.WindowNavigationEnded`.
   - Enhance hover detection in `ApplyFadeOnUi` and `OnMouseMove`: if cursor is within `ClientRectangle`, maintain `_hovered = true`, cancel fade, and maintain `Opacity = MinimapOpacity`.
   - In `ApplyFadeOnUi`: while `_overview.WindowNavigationActive` is true, refresh `_touchTicks`, keep `Opacity = MinimapOpacity`, and prevent fade.
2. `Source/Overlays/Overview/OverviewManager.WindowNavigation.cs`:
   - Add `internal event Action? WindowNavigationStarted;` and `internal event Action? WindowNavigationEnded;`.
   - Invoke `WindowNavigationStarted` on flight start in `NavigateToWindow` and `NavigateToCamera`.
   - Invoke `WindowNavigationEnded` on flight completion or cancellation in `StopWindowNavigation()`.
3. `Source/Overlays/Overview/OverviewManager.Panels.cs`:
   - Add `internal event Action? CanvasPanelsRaised;`.
   - Invoke `CanvasPanelsRaised?.Invoke();` in `RaiseCanvasPanels()`.
4. `Source/Overlays/Overview/OverviewManager.cs`:
   - Include `WindowNavigationActive` in `MotionActive`.
5. `Tests/MinimapNavigationTests.cs`:
   - Add automated tests verifying:
     - 5-second hold timeout configuration.
     - Flight start/end triggers minimap activation and keepalive.
     - Hover keeps minimap active and restores opacity.
     - Topmost z-order placement (`HWND_TOPMOST`).

---

## 3. Atomic Task Breakdown

### Task 1: Update Minimap Overlay Constants & Topmost Z-Order
- Target: `Source/Overlays/Minimap/MinimapOverlay.cs`
- Change `HoldMs` to `5000L`.
- In `RaiseWithoutActivation()`, change `HWND.Null` to `(HWND)new IntPtr(-1)`.
- In `NotifyCanvasChanged()`, invoke `RaiseWithoutActivation()`.
- On `input.DragStarted`, invoke `NotifyCanvasChanged()`.

### Task 2: Add Flight & Panel Z-Order Events to OverviewManager
- Target: `Source/Overlays/Overview/OverviewManager.WindowNavigation.cs` and `OverviewManager.Panels.cs`
- Add events `WindowNavigationStarted`, `WindowNavigationEnded`, and `CanvasPanelsRaised`.
- Fire `WindowNavigationStarted` in `NavigateToWindow` and `NavigateToCamera`.
- Fire `WindowNavigationEnded` in `StopWindowNavigation()` when active flight terminates.
- Fire `CanvasPanelsRaised` in `RaiseCanvasPanels()`.
- Include `WindowNavigationActive` in `MotionActive` in `OverviewManager.cs`.

### Task 3: Implement Flight Visibility & Robust Hover Keepalive in MinimapOverlay
- Target: `Source/Overlays/Minimap/MinimapOverlay.cs`
- Wire `WindowNavigationStarted` -> `NotifyCanvasChanged()`, `RaiseWithoutActivation()`.
- Wire `WindowNavigationEnded` -> `RestartFade()`.
- Wire `CanvasPanelsRaised` -> `RaiseWithoutActivation()`.
- In `ApplyFadeOnUi`: check `IsMouseOver` (`ClientRectangle.Contains(PointToClient(Cursor.Position))`) or `WindowNavigationActive`. If either is true, keep `_touchTicks = Environment.TickCount64`, reset opacity to `MinimapOpacity`, and stop fade timer.
- In `OnMouseMove`: if not hovered, set `_hovered = true`, stop fade timer, and reset opacity.

### Task 4: Automated Testing
- Target: `Tests/MinimapNavigationTests.cs`
- Add tests covering:
  - Flight activation: starting flight displays minimap and keeps it alive.
  - Hover keepalive: hovering over the minimap prevents fade and restores full opacity.
  - 5-second hold: minimap holds for 5000ms after movement.

---

## 4. Test & Verification Plan
1. Fast in-memory unit tests:
   ```powershell
   dotnet test Tests/CanvasDesktop.Tests.csproj -c Release -p:CanvasSkipTestPreflight=true --filter "FullyQualifiedName~Minimap"
   ```
2. Full test suite under named mutex via `Tools/Run-Tests.ps1`:
   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File Tools\Run-Tests.ps1 -Configuration Release -NoPrompt
   ```
3. Merge & cleanup:
   - Fast-forward or merge commit into `main`.
   - Verify working directory and remove worktree.
