# Code Review: Minimap Flight Visibility, 5-Second Motion Keepalive & Z-Order Reliability

## Review Metadata
- **Date**: 2026-11-09
- **Feature**: Minimap flight visualization, 5s timeout, hover keepalive, and topmost z-order fix
- **Files Inspected**:
  - `Source/Overlays/Minimap/MinimapOverlay.cs`
  - `Source/Overlays/Overview/OverviewManager.WindowNavigation.cs`
  - `Source/Overlays/Overview/OverviewManager.Panels.cs`
  - `Source/Overlays/Overview/OverviewManager.cs`
  - `Source/Overlays/Overview/TelemetryStatusPill.cs`
  - `Tests/MinimapNavigationTests.cs`

---

## Findings & Invariants Audit

### 1. Architectural & Behavioral Requirements
- **Flight Visibility**: When a window/camera flight begins (`NavigateToWindow`, `NavigateToCamera`), `WindowNavigationStarted` fires, immediately bringing up the minimap via `NotifyCanvasChanged()` and raising it.
- **Flight Keepalive**: While `_overview.WindowNavigationActive` is true, `ApplyFadeOnUi` refreshes `_touchTicks` to `Environment.TickCount64`, maintains full `MinimapOpacity`, and cancels the fade timer. When flight terminates, `WindowNavigationEnded` restarts the hold timer.
- **5-Second Motion Keepalive**: `HoldMs` is set to `5000` (5.0s). Panning or zooming in Canvas or Desktop mode keeps the minimap visible for 5s after motion stops.
- **Hover Keepalive**: If the cursor enters or moves over the minimap client rectangle, `_hovered` is set to true, fade is stopped, and `Opacity = MinimapOpacity`. When the cursor leaves, `RestartFade()` restarts the 5-second countdown.
- **Topmost Z-Order Bug**: `RaiseWithoutActivation()` previously passed `HWND.Null` (`HWND_TOP`), which in Win32 places topmost windows underneath other topmost windows (specifically the fullscreen `OverviewOverlay` passes). Updating to `(HWND)new IntPtr(-1)` (`HWND_TOPMOST`) ensures the minimap is on top of all fullscreen passes. In addition, `OverviewManager.CanvasPanelsRaised` ensures that whenever the overview raises its passes, the minimap is also re-asserted above them.

### 2. Threading & Native Safety
- All overlay operations, timers, and P/Invoke calls execute on the STA UI thread.
- `PInvoke.SetWindowPos` uses `SWP_NOACTIVATE | SWP_NOMOVE | SWP_NOSIZE` to avoid stealing keyboard focus from active applications or overview navigation.
- In `Dispose()`, all added event subscriptions (`CanvasPanelsRaised`, `WindowNavigationStarted`, `WindowNavigationEnded`, `DragStarted`) are cleanly detached.

### 3. Test Verification
- All 9 unit and native rendering tests in `Tests/MinimapNavigationTests.cs` passed.
- All 1577 non-native unit tests in `Tests/CanvasDesktop.Tests.csproj` passed with 0 failures.

---

## Verdict: APPROVED
All user requirements are verified and adhere strictly to repository invariants. Ready to merge to `main`.
