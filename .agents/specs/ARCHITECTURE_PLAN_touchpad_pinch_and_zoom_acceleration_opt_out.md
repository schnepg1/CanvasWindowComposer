# Architecture Plan: Precision Touchpad & Trackpad Pinch Parity with Opt-Out Zoom Acceleration

**Task ID:** `touchpad-pinch-zoom-acceleration-opt-out`  
**Phase:** Phase 4 of Smooth Canvas Zooming & Panning  
**Target Branch:** `main`  
**Status:** `PLAN_READY`  
**Architect:** System Architect Subagent  

---

## 1. Executive Summary & Problem Context

CanvasWindowComposer provides infinite-workspace navigation on Windows 10 and 11, supporting camera scaling from 5% (distant spatial overview) to 200%+ (detailed inspection / magnifier). In earlier phases:
- **Phase 1** introduced dynamic wireframe Level-of-Detail (LOD) for distant zoom levels during motion.
- **Phase 2** implemented post-motion settle staggering for DWM thumbnails, partitioning thumbnail restoration into concentric rings to eliminate IPC hitches.
- **Phase 3** designed a bounded $O(1)$ LRU memory cache for placeholder card images and icon deduplication.

### The Problem in Phase 4: Touchpad Pinch & Zoom Acceleration Parity

While physical desktop mouse wheel navigation was tuned with cadence-based zoom acceleration, precision touchpad and trackpad navigation suffers from critical usability defects:

1. **Touchpad Pinch-to-Zoom is Inverted to Vertical Pan:**
   In historical commit `f20052092174a5258af26397f7118d9799c8e6be` ("Swap canvas wheel zoom and pan controls"), [`OverviewNavigationInput.cs`](file:///Source/Overlays/Overview/OverviewNavigationInput.cs) was modified so that an unmodified vertical mouse wheel zooms the canvas, while holding `Ctrl` was repurposed to pan vertically (`return control ? (0, delta, 0) : (0, 0, delta / WheelDelta);`).
   Because Windows Precision Touchpad (PTP) drivers deliver two-finger pinch-to-zoom gestures as high-frequency `WM_MOUSEWHEEL` messages with `MK_CONTROL` set, **touchpad pinch gestures are translated into vertical panning rather than zooming**. Furthermore, two-finger vertical scrolling (which emits `WM_MOUSEWHEEL` without `MK_CONTROL`) causes sudden unwanted zoom hops instead of natural 2D canvas scrolling.
2. **Missing `WM_GESTURE` (GID_ZOOM) Handling:**
   [`OverviewOverlay.cs`](file:///Source/Overlays/Overview/OverviewOverlay.cs) does not intercept `WM_GESTURE` (0x0119) messages. On touchscreens, Windows Touch digitizers, and touchpad drivers configured for direct gesture notifications, `WM_GESTURE` falls through to `DefWindowProc`, which either drops the gesture or generates synthetic `WM_MOUSEWHEEL` messages that suffer from the same inverted translation.
3. **Artificial Cadence Throttling on High-Report-Rate Touchpads:**
   In [`SmoothZoomTracker.cs`](file:///Source/Core/SmoothZoomTracker.cs), the cadence calculation clamps delta-time using `Math.Max(dt, MinIntervalMs)` where `MinIntervalMs = 12.0`. Precision Touchpads frequently report events at 120 Hz or 144 Hz (intervals of 6.9 ms to 8.3 ms). Clamping to 12.0 ms artificially throttles the calculated notch rate by up to 50%, underestimating the user's gesture velocity.
4. **Fractional Delta Deadband Dropping:**
   High-resolution touchpads report tiny, sub-detent wheel deltas (e.g., $\Delta = 1$ to $4$, corresponding to $\approx 0.008$ to $0.033$ notches). In [`SmoothZoomTracker.cs`](file:///Source/Core/SmoothZoomTracker.cs), `IsActive = Math.Abs(_targetZoom - _currentZoom) >= StopThreshold;` where `StopThreshold = 0.0005`. At distant zoom scales ($Z < 0.25$), a single fractional step calculates $step = notches \times 0.1 \times Z \le 0.00041 < 0.0005$. `IsActive` evaluates to `false`, discarding the notch until multiple subsequent messages arrive, creating noticeable deadband lag and jerky step jumps.
5. **Lack of User Opt-Out for Zoom Acceleration:**
   While cadence-based scroll acceleration improves rapid navigation across deep zoom tiers, users with specific precision pointing needs or sensitivities require uniform, constant-speed zoom steps. No user-toggleable setting exists to disable acceleration while preserving smooth exponential spring interpolation.

---

## 2. Codebase Reconnaissance & Investigation Findings

### 2.1. Input Message Pipeline Analysis

The following table traces the input pipeline from Windows OS hardware messages down to camera rendering:

| Stage | File | Current Behavior | Defect / Limitation |
|---|---|---|---|
| **Win32 Window Message Receiver** | [`OverviewOverlay.cs`](file:///Source/Overlays/Overview/OverviewOverlay.cs) | Handles `WM_MOUSEWHEEL` (0x020A) & `WM_MOUSEHWHEEL` (0x020E). Ignores `WM_GESTURE` (0x0119). Does not query `GetCurrentInputMessageSource`. | Cannot distinguish physical mouse wheel from touchpad two-finger scroll; drops native `GID_ZOOM` gesture packets. |
| **Input Normalization** | [`OverviewNavigationInput.cs`](file:///Source/Overlays/Overview/OverviewNavigationInput.cs) | `TranslateWheel` maps `control ? (0, delta, 0) : (0, 0, delta / WheelDelta)`. | Inverts touchpad pinch (sends Ctrl+wheel) into Pan Y; inverts touchpad vertical scroll into Zoom; violates standard Windows Ctrl+wheel zoom conventions. |
| **Pointer Coordinator** | [`OverviewManager.Pointer.cs`](file:///Source/Overlays/Overview/OverviewManager.Pointer.cs) | `HandleMouseWheel` routes `TranslateWheel` outputs to `ZoomOverview` or `PanOverview`. | Receives corrupted zoom vs pan intent from `TranslateWheel`. |
| **Smooth Zoom Tracker** | [`SmoothZoomTracker.cs`](file:///Source/Core/SmoothZoomTracker.cs) | Computes velocity acceleration with `MinIntervalMs = 12.0` and deadband `StopThreshold = 0.0005`. `AccelerationEnabled` field exists but is hard-coded to `true`. | Artificially throttles 120Hz/144Hz touchpad cadence; drops low-delta fractional packets; not wired to configuration. |
| **Configuration & Preferences** | [`AppConfig.cs`](file:///Source/Application/AppConfig.cs)<br>[`IAppConfig.cs`](file:///Source/Application/AppConfig.cs)<br>[`CanvasPreferences.cs`](file:///Source/Application/AppConfig.cs) | Exposes `SmoothZoom`, `MaximumZoom`, `DisableMouseCurve`, etc. | Missing `ZoomAcceleration` setting (must default to `true` for opt-out). |
| **Settings UI** | [`ZoomSettingsControl.cs`](file:///Source/Application/ZoomSettingsControl.cs) | Controls "Zoom & cards" settings including `SmoothZoom` and threshold sliders. | Missing "Zoom acceleration" checkbox and explanatory hint text. |

### 2.2. Hardware Input Differentiation in Win32

Under Windows 8, 10, and 11, Win32 desktop applications can determine the exact hardware origin of any `WM_MOUSEWHEEL` / `WM_MOUSEHWHEEL` message via `GetCurrentInputMessageSource`:

```csharp
[DllImport("user32.dll", SetLastError = true)]
internal static extern bool GetCurrentInputMessageSource(out INPUT_MESSAGE_SOURCE inputMessageSource);

internal struct INPUT_MESSAGE_SOURCE
{
    public INPUT_MESSAGE_DEVICE_TYPE deviceType;
    public INPUT_MESSAGE_ORIGIN_ID originId;
}

internal enum INPUT_MESSAGE_DEVICE_TYPE
{
    IMDT_UNAVAILABLE = 0,
    IMDT_KEYBOARD = 1,
    IMDT_MOUSE = 2,
    IMDT_TOUCH = 3,
    IMDT_PEN = 4,
    IMDT_TOUCHPAD = 5
}
```

By querying `GetCurrentInputMessageSource`:
- **When `deviceType == IMDT_TOUCHPAD`:**
  - Two-finger vertical scroll (`control == false, horizontal == false`) $\rightarrow$ **Pan Y** (natural 2D canvas pan).
  - Two-finger horizontal scroll (`horizontal == true`) $\rightarrow$ **Pan X**.
  - Two-finger pinch gesture (`control == true`) $\rightarrow$ **Zoom** (anchored to cursor focal point).
- **When `deviceType == IMDT_MOUSE`:**
  - Standard wheel scroll (`control == false, horizontal == false`) $\rightarrow$ **Zoom** (preserves default Canvas overview mouse behavior).
  - Ctrl + wheel scroll (`control == true, horizontal == false`) $\rightarrow$ **Zoom** (standard Windows Ctrl+wheel zoom parity).
  - Shift + wheel or horizontal tilt $\rightarrow$ **Pan X**.
- **When `deviceType == IMDT_UNAVAILABLE` (fallback / synthetic test events):**
  - `control == true` $\rightarrow$ **Zoom** (unifying trackpad pinch and Ctrl+wheel).
  - `horizontal || shift` $\rightarrow$ **Pan X**.
  - `control == false && !horizontal && !shift` $\rightarrow$ **Zoom**.

Additionally, registering the window with `RegisterTouchpadCapableWindow` ensures Windows routes high-resolution touchpad data and gesture notifications to the overlay window handle rather than degrading to basic mouse emulation.

---

## 3. Architecture & Component Design

```
+----------------------------------------------------------------------------------------------------+
|                                      Windows Input Messages                                        |
|   - WM_MOUSEWHEEL (0x020A)             - WM_MOUSEHWHEEL (0x020E)            - WM_GESTURE (0x0119)  |
+----------------------------------------------------------------------------------------------------+
                                                  |
                                                  v
+----------------------------------------------------------------------------------------------------+
|                                    OverviewOverlay.WndProc                                         |
| - Intercepts WM_GESTURE:                                                                           |
|     * Extracts GESTUREINFO (GID_ZOOM)                                                              |
|     * Translates distance ratio (D / D_prev) to fractional zoom notches                            |
|     * Dispatches OnGestureZoom(client.X, client.Y, notches)                                        |
| - Intercepts WM_MOUSEWHEEL / WM_MOUSEHWHEEL:                                                       |
|     * Calls GetCurrentInputMessageSource(out source)                                               |
|     * Identifies isTouchpad = (source.deviceType == IMDT_TOUCHPAD)                                 |
|     * Dispatches OnWheel(client.X, client.Y, delta, horizontal, control, shift, isTouchpad)        |
+----------------------------------------------------------------------------------------------------+
                                                  |
                                                  v
+----------------------------------------------------------------------------------------------------+
|                                  OverviewNavigationInput                                           |
| - TranslateWheel(delta, horizontal, control, shift, isTouchpad):                                   |
|     * Touchpad Pinch (isTouchpad && control)      => (PanX: 0, PanY: 0, Zoom: delta / 120.0)      |
|     * Touchpad 2-finger scroll (isTouchpad)       => (PanX: delta_H, PanY: delta_V, Zoom: 0)       |
|     * Mouse Wheel (Ctrl or normal)                => (PanX: 0, PanY: 0, Zoom: delta / 120.0)      |
|     * Shift+Wheel / Horizontal                    => (PanX: delta, PanY: 0, Zoom: 0)               |
+----------------------------------------------------------------------------------------------------+
                                                  |
                                                  v
+----------------------------------------------------------------------------------------------------+
|                                 OverviewManager.Pointer.cs                                         |
| - HandleMouseWheel(...) & HandleGestureZoom(...):                                                  |
|     * Routes PanX/PanY to PanOverview(panX, panY)                                                  |
|     * Routes ZoomNotches to ZoomOverview(focalVirtualX, focalVirtualY, zoomNotches)                |
+----------------------------------------------------------------------------------------------------+
                                                  |
                                                  v
+----------------------------------------------------------------------------------------------------+
|                                    SmoothZoomTracker                                               |
| - AccelerationEnabled = _appConfig.ZoomAcceleration                                                |
| - MinIntervalMs = 2.0 ms (supports 120Hz/144Hz touchpad report intervals)                          |
| - Deadband handling: if notches != 0, activate immediately (no dropping small fractional steps)   |
| - If AccelerationEnabled == false:                                                                 |
|     * Multiplier locked to 1.0 (no velocity escalation)                                            |
|     * Frame-0 immediacy preserved                                                                  |
|     * Smooth exponential spring decay preserved                                                    |
+----------------------------------------------------------------------------------------------------+
```

### 3.1. Precision Touchpad & Gesture Input Handling

#### 3.1.1. Win32 Input Interop
File: `Source/System/NativeInputInterop.cs` (or added to existing native structures in `OverviewOverlay.cs`)

Declare the necessary Win32 constants and structures for `WM_GESTURE` and `GetCurrentInputMessageSource`:
- Messages:
  - `WM_GESTURE = 0x0119`
  - `WM_GESTURENOTIFY = 0x011A`
- Gesture IDs:
  - `GID_BEGIN = 1`, `GID_END = 2`, `GID_ZOOM = 3`, `GID_PAN = 4`
- Gesture Flags:
  - `GF_BEGIN = 0x00000001`, `GF_INERTIA = 0x00000002`, `GF_END = 0x00000004`
- Structures:
  - `GESTUREINFO` with fields `cbSize`, `dwFlags`, `dwID`, `hwndTarget`, `ptsLocationX`, `ptsLocationY`, `dwInstanceID`, `dwSequenceID`, `ullArguments`, `cbExtraArgs`.
  - `INPUT_MESSAGE_SOURCE` and `INPUT_MESSAGE_DEVICE_TYPE`.
- P/Invoke:
  - `GetGestureInfo(IntPtr hGestureInfo, ref GESTUREINFO pGestureInfo)`
  - `CloseGestureInfoHandle(IntPtr hGestureInfo)`
  - `GetCurrentInputMessageSource(out INPUT_MESSAGE_SOURCE inputMessageSource)`

#### 3.1.2. `OverviewOverlay.cs` Enhancements
1. In `WndProc(ref Message m)`:
   - Handle `WM_GESTURE` (0x0119):
     ```csharp
     if (m.Msg == NativeInputInterop.WM_GESTURE)
     {
         if (HandleGestureMessage(ref m))
         {
             m.Result = IntPtr.Zero;
             return;
         }
     }
     ```
   - In `HandleGestureMessage`:
     Extract `GESTUREINFO`. If `dwID == GID_ZOOM`:
     - Center point: `var client = PointToClient(new Point(gi.ptsLocationX, gi.ptsLocationY));`
     - When `dwFlags & GF_BEGIN != 0`: record `_lastGestureDistance = gi.ullArguments;`
     - When continuing: compute scale delta:
       $$\Delta_{\text{ratio}} = \frac{\text{gi.ullArguments} - \text{_lastGestureDistance}}{\text{_lastGestureDistance}}$$
       $$\text{notches} = \frac{\Delta_{\text{ratio}}}{\text{OverviewCamera.ZoomStep}}$$
       Dispatch `OnGestureZoom?.Invoke(this, client.X, client.Y, notches);`
       Update `_lastGestureDistance = gi.ullArguments;`
     - When `dwFlags & GF_END != 0`: reset `_lastGestureDistance = 0;`
     - Call `CloseGestureInfoHandle(m.LParam);` and return `true`.
   - In `WM_MOUSEWHEEL` / `WM_MOUSEHWHEEL`:
     Query `GetCurrentInputMessageSource(out var source)`.
     Determine `bool isTouchpad = (source.deviceType == INPUT_MESSAGE_DEVICE_TYPE.IMDT_TOUCHPAD);`
     Pass `isTouchpad` to `OnWheel`:
     `OnWheel?.Invoke(this, client.X, client.Y, wheel.Delta, wheel.Horizontal, wheel.Control, wheel.Shift, isTouchpad);`

#### 3.1.3. `OverviewNavigationInput.cs` Input Normalization
Refactor `TranslateWheel` to take `bool isTouchpad = false`:

```csharp
public static (double PanX, double PanY, double ZoomNotches) TranslateWheel(
    int delta, bool horizontal, bool control, bool shift = false, bool isTouchpad = false)
{
    if (isTouchpad)
    {
        // Touchpad input:
        // - Pinch gesture is sent as Ctrl+wheel -> Zoom
        // - 2-finger horizontal scroll -> Pan X
        // - 2-finger vertical scroll -> Pan Y
        if (control) return (0, 0, delta / WheelDelta);
        if (horizontal || shift) return (delta, 0, 0);
        return (0, delta, 0);
    }

    // Physical mouse input / synthetic fallback:
    // - Shift+wheel or horizontal tilt -> Pan X
    // - Ctrl+wheel -> Zoom (standard Windows shortcut)
    // - Bare wheel -> Zoom (canvas overview default)
    if (horizontal || shift) return (delta, 0, 0);
    return (0, 0, delta / WheelDelta);
}
```

*Note on backward compatibility:* When `control == true`, whether originating from physical mouse Ctrl+wheel or precision touchpad pinch, it translates to **Zoom**. This fixes the touchpad pinch regression while honoring universal Windows conventions.

### 3.2. Fractional Delta Normalization & Cadence Un-throttling in `SmoothZoomTracker.cs`

1. **Reduce Minimum Cadence Interval (`MinIntervalMs`):**
   ```csharp
   // Was: public const double MinIntervalMs = 12.0;
   public const double MinIntervalMs = 2.0;
   ```
   Clamping to 2.0 ms allows report rates up to 500 Hz (easily accommodating 120 Hz and 144 Hz Precision Touchpads) without artificial cadence throttling.
2. **Prevent Fractional Delta Deadband Discarding:**
   In `PushZoom`:
   ```csharp
   // Existing check prevented tiny steps from starting active interpolation:
   // IsActive = Math.Abs(_targetZoom - _currentZoom) >= StopThreshold;
   
   // Enhanced check:
   // If the user delivered a finite non-zero notch, smooth zoom must activate
   // unless clamped hard against ZoomMin/Max boundaries.
   double targetDiff = Math.Abs(nextTarget - currentZoom);
   if (targetDiff < OverviewCamera.ZoomEpsilon)
   {
       if (!IsActive) return;
   }

   _targetZoom = nextTarget;
   _currentZoom = currentZoom;
   _focalVirtualX = virtualX;
   _focalVirtualY = virtualY;
   _focalWorldX = worldX;
   _focalWorldY = worldY;
   _lastTick = now;
   IsActive = Math.Abs(_targetZoom - _currentZoom) >= OverviewCamera.ZoomEpsilon;
   ```
   Using `OverviewCamera.ZoomEpsilon` ($1 \times 10^{-4}$) rather than `0.0005` ensures that even the smallest single-unit fractional delta ($\Delta = 1$, notches $\approx 0.0083$) initiates smooth spring interpolation.
3. **Anchor Invariance:**
   `SmoothZoomTracker` already maintains `_focalVirtualX`, `_focalVirtualY`, `_focalWorldX`, and `_focalWorldY`. When cursor position updates mid-gesture, the focal virtual and world coordinates are updated smoothly so the canvas remains pinned beneath the user's fingers or cursor.

### 3.3. Deactivatable Zoom Acceleration Setting

#### 3.3.1. Interface & Config Schema
File: `Source/Application/AppConfig.cs`

1. In `IAppConfig`:
   ```csharp
   bool ZoomAcceleration => true;
   ```
2. In `AppConfig`:
   - Property:
     ```csharp
     public bool ZoomAcceleration { get; private set; } = true;
     ```
   - In `Load()`:
     ```csharp
     ZoomAcceleration = GetBool(values, "ZoomAcceleration", defaultValue: true);
     ```
   - In `Preferences` getter:
     Pass `ZoomAcceleration` to `CanvasPreferences`:
     ```csharp
     public CanvasPreferences Preferences
     {
         get
         {
             lock (_watcherGate) return new(!DisableSearch, !DisableAltPan, !DisableZoomHotkey,
                 !DisableMouseCurve, ShowScreenFixedWindowsDuringPan, !DisableGreedyDraw,
                 EnableAutomaticMinimization, EnableFlipRendering, EnableStartupIntro, ReduceStartupMotion, LowQualityMode,
                 EnableLivePreviewsTier, EnableDetailedCardsTier, EnableSimpleCardsTier,
                 OverviewCardEnterZoom, OverviewCardExitZoom, OverviewSimpleCardEnterZoom, OverviewSimpleCardExitZoom,
                 ShowTaskbarInCanvas, AnimateWindowNavigation, DesktopPanMomentum, CanvasPanMomentum,
                 EnableShortcutZoomOut, ShortcutZoomOutAmount,
                 EnableCanvasPeek, CanvasPeekDelayMs, SmoothZoom, MaximumZoom,
                 ZoomAcceleration);
         }
     }
     ```
   - In `SavePreferences(CanvasPreferences preferences)`:
     ```csharp
     ["ZoomAcceleration"] = preferences.ZoomAcceleration.ToString().ToLowerInvariant(),
     ```
   - In `WriteDefault()`:
     ```ini
     ; Accelerate zoom velocity during rapid wheel flicking
     ; Turn off to use uniform, constant-speed zoom steps
     ;ZoomAcceleration=true
     ```

3. In `CanvasPreferences` record:
   ```csharp
   internal sealed record CanvasPreferences(
       ...,
       bool SmoothZoom = true, double MaximumZoom = OverviewZoomDefaults.MaximumZoom,
       bool ZoomAcceleration = true);
   ```

4. In `Tests/FakeAppConfig.cs`:
   ```csharp
   public bool ZoomAcceleration { get; set; } = true;
   ```

#### 3.3.2. Settings UI (`ZoomSettingsControl.cs`)
File: `Source/Application/ZoomSettingsControl.cs`

1. Declare field:
   ```csharp
   private readonly CheckBox _live, _detailed, _simple, _shortcutZoomOut, _peek, _smoothZoom, _zoomAcceleration;
   ```
2. In constructor, add directly following `_smoothZoom`:
   ```csharp
   _smoothZoom = Option("Smooth canvas zooming", preferences.SmoothZoom);
   Hint("Smoothly glides zoom levels when scrolling the mouse wheel or using + / -. Turn off for immediate discrete steps.");

   _zoomAcceleration = Option("Zoom acceleration", preferences.ZoomAcceleration);
   _zoomAcceleration.IsEnabled = _smoothZoom.IsChecked == true;
   _smoothZoom.Checked += (_, _) => _zoomAcceleration.IsEnabled = true;
   _smoothZoom.Unchecked += (_, _) => _zoomAcceleration.IsEnabled = false;
   Hint("Accelerates zoom velocity during rapid wheel flicking. Turn off for uniform, constant-speed zoom steps.");
   ```
3. In "Reset zoom defaults":
   ```csharp
   _zoomAcceleration.IsChecked = true;
   ```
4. In `TryApply`:
   ```csharp
   updated = current with {
       ...,
       SmoothZoom = _smoothZoom.IsChecked == true,
       ZoomAcceleration = _zoomAcceleration.IsChecked == true,
       MaximumZoom = _maximumZoom.Value / 100
   };
   ```

#### 3.3.3. Wiring in `OverviewManager`
File: `Source/Overlays/Overview/OverviewManager.cs`

1. In constructor (line ~328):
   ```csharp
   _smoothZoom = new SmoothZoomTracker(_gestureClock);
   _smoothZoom.MaxZoom = _appConfig.MaximumZoom;
   _smoothZoom.AccelerationEnabled = _appConfig.ZoomAcceleration;
   ```
2. In `RefreshConfig()` (line ~379):
   ```csharp
   _smoothZoom.MaxZoom = _appConfig.MaximumZoom;
   _smoothZoom.AccelerationEnabled = _appConfig.ZoomAcceleration;
   ```
3. In `ApplyConfig()` (line ~842):
   ```csharp
   _smoothZoom.MaxZoom = _appConfig.MaximumZoom;
   _smoothZoom.AccelerationEnabled = _appConfig.ZoomAcceleration;
   ```

#### 3.3.4. SmoothZoomTracker Dynamics When Acceleration is Disabled
In `SmoothZoomTracker.PushZoom`:
When `AccelerationEnabled == false`:
- `multiplier` is locked to `1.0`.
- `CurrentMultiplier = 1.0`.
- `IsAccelerating = false`.
- `_streak = 0`.
- `effectiveNotches = notches * 1.0`.
- `_activeLambda = _baseLambda`.
- `_targetZoom` moves by exactly $\text{notches} \times \text{ZoomStep} \times \text{targetZoom}$.
- Frame-0 immediacy is preserved via `AdvanceSmoothZoom(now + 16)` in `OverviewManager.SmoothZoom.cs`.
- Exponential spring decay smoothly closes the distance to `_targetZoom` without any velocity escalation.

---

## 4. Atomic Implementation Task Checklist

### Task 1: Native Input Definitions & Device Source Interop
- **Files:** `Source/System/NativeInputInterop.cs` (or `Source/Overlays/Overview/OverviewOverlay.cs`)
- **Changes:**
  - Define `WM_GESTURE = 0x0119`, `WM_GESTURENOTIFY = 0x011A`.
  - Define `GID_ZOOM = 3`, `GID_PAN = 4`, `GF_BEGIN = 1`, `GF_END = 4`.
  - Define `GESTUREINFO` struct and P/Invoke declarations for `GetGestureInfo`, `CloseGestureInfoHandle`.
  - Define `INPUT_MESSAGE_SOURCE`, `INPUT_MESSAGE_DEVICE_TYPE`, and P/Invoke declaration for `GetCurrentInputMessageSource`.
- **Verification:** Unit tests verifying struct size and marshaling integrity.

### Task 2: Gesture & Touchpad Capture in `OverviewOverlay.cs`
- **Files:** `Source/Overlays/Overview/OverviewOverlay.cs`
- **Changes:**
  - Add event/callback: `public Action<OverviewOverlay, int, int, double>? OnGestureZoom;`
  - Update `OnWheel` signature to accept `bool isTouchpad`: `public Action<OverviewOverlay, int, int, int, bool, bool, bool, bool>? OnWheel;`
  - Intercept `WM_GESTURE` in `WndProc`. Process `GID_ZOOM` distance delta, convert to notches, invoke `OnGestureZoom`.
  - In `WM_MOUSEWHEEL` / `WM_MOUSEHWHEEL`, query `GetCurrentInputMessageSource`, resolve `isTouchpad`, pass to `OnWheel`.
- **Verification:** Test in `Tests/OverviewOverlayTests.cs` (or synthetic `WndProc` simulation) confirming `GID_ZOOM` and `WM_MOUSEWHEEL` dispatch correct callbacks.

### Task 3: Input Normalization in `OverviewNavigationInput.cs`
- **Files:** `Source/Overlays/Overview/OverviewNavigationInput.cs`
- **Changes:**
  - Update `TranslateWheel(int delta, bool horizontal, bool control, bool shift = false, bool isTouchpad = false)`:
    - When `isTouchpad`: `control` $\rightarrow$ Zoom; `horizontal || shift` $\rightarrow$ Pan X; otherwise Pan Y.
    - When not `isTouchpad`: `horizontal || shift` $\rightarrow$ Pan X; otherwise Zoom (`delta / WheelDelta`).
- **Verification:** `Tests/OverviewNavigationInputTests.cs` covering touchpad pinch, touchpad 2-finger scroll, mouse wheel, mouse Ctrl+wheel, and Shift+wheel.

### Task 4: Pointer & Gesture Routing in `OverviewManager`
- **Files:**
  - `Source/Overlays/Overview/OverviewManager.cs`
  - `Source/Overlays/Overview/OverviewManager.Pointer.cs`
- **Changes:**
  - Wire `pass.OnGestureZoom = HandleGestureZoom;` in `OverviewManager.cs`.
  - Implement `HandleGestureZoom(OverviewOverlay pass, int x, int y, double notches)` routing to `ZoomOverview`.
  - Update `HandleMouseWheel` to accept `bool isTouchpad` and pass to `TranslateWheel`.
- **Verification:** Integration tests verifying touchpad pinch calls `ZoomOverview` and touchpad scroll calls `PanOverview`.

### Task 5: Fractional Delta & High-Cadence Normalization in `SmoothZoomTracker.cs`
- **Files:** `Source/Core/SmoothZoomTracker.cs`
- **Changes:**
  - Change `MinIntervalMs = 2.0` (from 12.0).
  - In `PushZoom`, update activation threshold from `StopThreshold = 0.0005` to `OverviewCamera.ZoomEpsilon` ($1 \times 10^{-4}$) so single-notch fractional steps are not dropped.
- **Verification:** `Tests/SmoothZoomTrackerTests.cs` verifying rapid 6ms cadence and fractional 0.01-notch activations.

### Task 6: Configuration & Preferences Schema (`ZoomAcceleration`)
- **Files:**
  - `Source/Application/AppConfig.cs`
  - `Tests/FakeAppConfig.cs`
- **Changes:**
  - Add `bool ZoomAcceleration => true;` to `IAppConfig`.
  - Add `ZoomAcceleration` property, INI parsing, preferences mapping, saving, and defaults in `AppConfig`.
  - Add `bool ZoomAcceleration = true` to `CanvasPreferences` record.
  - Add `ZoomAcceleration = true` to `FakeAppConfig`.
- **Verification:** `Tests/CanvasPreferencesTests.cs` testing persistence, defaults, and round-tripping.

### Task 7: Settings UI Surface in `ZoomSettingsControl.cs`
- **Files:** `Source/Application/ZoomSettingsControl.cs`
- **Changes:**
  - Add `_zoomAcceleration` checkbox under `_smoothZoom` in "Zoom & cards".
  - Wire enablement to `_smoothZoom.IsChecked`.
  - Add hint text: `"Accelerates zoom velocity during rapid wheel flicking. Turn off for uniform, constant-speed zoom steps."`
  - Support reset defaults and `TryApply`.
- **Verification:** `Tests/SettingsWindowTests.cs` testing toggle, enablement, cancel, and save behavior.

### Task 8: Tracker Wiring in `OverviewManager`
- **Files:**
  - `Source/Overlays/Overview/OverviewManager.cs`
- **Changes:**
  - Set `_smoothZoom.AccelerationEnabled = _appConfig.ZoomAcceleration;` in constructor, `RefreshConfig()`, and `ApplyConfig()`.
- **Verification:** Tests in `Tests/SmoothZoomIntegrationTests.cs` verifying acceleration toggle updates tracker state.

---

## 5. Test & Verification Strategy

### 5.1. Unit Test Plan (Fast Non-Native Tests: `Category!=Native*`)

1. **`CanvasPreferencesTests.cs`:**
   - `ZoomAcceleration_DefaultsToTrueAndRoundTripsThroughPreferences`:
     Loads clean config, verifies `ZoomAcceleration == true`, saves `false`, reloads, verifies `false`.
   - `ZoomAcceleration_CommentedInDefaultConfig`:
     Verifies generated `config.ini` contains `;ZoomAcceleration=true`.
2. **`SettingsWindowTests.cs`:**
   - `ZoomAccelerationCheckboxTogglesAndSavesOnlyOnRequest(string action, bool zoomAcceleration)`:
     Tests UI checkbox interaction, cancel retention, and save persistence.
   - `ZoomAcceleration_DisabledWhenSmoothZoomIsUnchecked`:
     Verifies unchecking "Smooth canvas zooming" sets `_zoomAcceleration.IsEnabled = false`.
   - `ZoomAcceleration_ResetDefaultsRestoresCheckedState`:
     Verifies "Reset zoom defaults" checks `_zoomAcceleration`.
3. **`SmoothZoomTrackerTests.cs`:**
   - `PushZoom_WithAccelerationDisabled_FastScrollsRemainAtMultiplierOne`:
     Fires rapid wheel flicks (20 ms intervals, > 40 notches/sec).
     Asserts `CurrentMultiplier == 1.0` and `IsAccelerating == false`.
     Asserts target zoom increments by strictly constant linear steps.
   - `PushZoom_WithAccelerationDisabled_PreservesFrameZeroImmediacyAndSpringDecay`:
     Verifies that when acceleration is off, the first tick advances immediately on Frame 0 and spring decay smoothly interpolates to target.
   - `PushZoom_FractionalTouchpadDeltas_ActivatesAndInterpolatesSmoothly`:
     Fires fractional notches ($0.01$ and $0.05$) at distant zoom ($Z = 0.15$).
     Asserts `IsActive == true` and smooth interpolation completes without deadband stalls.
   - `PushZoom_HighReportRateCadence_CalculatesVelocityWithoutArtificialThrottling`:
     Fires events with $dt = 6$ ms and $dt = 8$ ms.
     Verifies calculated rate scales with actual frequency.
4. **`OverviewNavigationInputTests.cs`:**
   - `TranslateWheel_TouchpadPinch_ReturnsZoomNotches`:
     `TranslateWheel(delta: 24, horizontal: false, control: true, shift: false, isTouchpad: true)` $\rightarrow$ `(0, 0, 0.20)`.
   - `TranslateWheel_TouchpadScroll_ReturnsPanCoordinates`:
     `TranslateWheel(delta: 15, horizontal: false, control: false, shift: false, isTouchpad: true)` $\rightarrow$ `(0, 15, 0)`.
     `TranslateWheel(delta: 20, horizontal: true, control: false, shift: false, isTouchpad: true)` $\rightarrow$ `(20, 0, 0)`.
   - `TranslateWheel_MouseWheel_ReturnsZoom`:
     `TranslateWheel(delta: 120, horizontal: false, control: false, shift: false, isTouchpad: false)` $\rightarrow$ `(0, 0, 1.0)`.
     `TranslateWheel(delta: 120, horizontal: false, control: true, shift: false, isTouchpad: false)` $\rightarrow$ `(0, 0, 1.0)`.
   - `TranslateWheel_ShiftWheel_ReturnsPanX`:
     `TranslateWheel(delta: 120, horizontal: false, control: false, shift: true, isTouchpad: false)` $\rightarrow$ `(120, 0, 0)`.

### 5.2. Integration & Native Desktop Tests

Tests exercising `OverviewManager` and `OverviewOverlay` with live HWNDs must be run with the test runner:
- Test: `SmoothZoomIntegrationTests.cs`
  - `TouchpadPinch_SmoothZoomEnabled_AnimatesZoomTowardCursorFocalPoint`
  - `TouchpadScroll_PansCameraSmoothly`
  - `ZoomAcceleration_WhenDisabledInConfig_ExecutesConstantSpeedSteps`

### 5.3. Test Runner Invariants (`AGENTS.md`)

- Run non-native tests:
  ```powershell
  powershell -NoProfile -ExecutionPolicy Bypass -File Tools\Run-Tests.ps1 -Configuration Release -Filter "Category!=NativeRendering&Category!=NativeParking&Category!=NativePlacement&Category!=NativePreview"
  ```
- Run full suite (under machine-wide named mutex `Global\CanvasWindowComposer.Tests`):
  ```powershell
  Tests\run_tests.bat -Configuration Release
  ```

---

## 6. Edge Cases & Failure Modes Analysis

1. **`GetCurrentInputMessageSource` Unsupported or Fails:**
   On legacy virtualization environments or remote desktop sessions where `GetCurrentInputMessageSource` returns `IMDT_UNAVAILABLE`:
   - Fallback logic checks `control`: if `control == true`, treats as **Zoom** (unifying touchpad pinch and Ctrl+wheel).
   - If `control == false`, treats as **Zoom** (preserving standard mouse wheel behavior).
   - This ensures graceful degradation without crashes or dead zones.
2. **Abrupt Gesture Reversal During Touchpad Pinch:**
   When a user pinches out and rapidly reverses to pinching in:
   - `SmoothZoomTracker` detects direction reversal (`direction != _lastDirection`).
   - Resets streak and multiplier to 1.0.
   - Pivots target zoom from current intermediate zoom level without visual lurching.
3. **High-Frequency Micro-Deltas Near Zoom Limits:**
   When near `ZoomMin` (0.05) or `MaxZoom`:
   - `nextTarget` clamps cleanly against boundaries.
   - If already at boundary, `Math.Abs(nextTarget - currentZoom) < ZoomEpsilon` aborts activation cleanly without triggering unnecessary render frames or CPU wakeups.
4. **Focal Point Drift During Multi-Finger Motion:**
   If the user's hand shifts across the trackpad while pinching:
   - Screen coordinates $(x, y)$ update on each message.
   - `SmoothZoomTracker.PushZoom` updates `_focalVirtual` and re-anchors `_focalWorld` so the canvas zooms relative to the moving touch centroid rather than drifting away.
5. **Settings Synchronization Race:**
   If a user updates settings while smooth zoom is actively interpolating:
   - `OverviewManager.RefreshConfig()` immediately updates `_smoothZoom.AccelerationEnabled`.
   - If acceleration was active, it gracefully clamps to current target without interrupting the ongoing spring decay tick.

---

## 7. Deliverable Summary & Readiness Confirmation

All components, interop signatures, mathematical normalizations, and test cases have been fully mapped to existing codebase patterns. The implementation plan is atomic, backward-compatible, and ready for worktree execution by the developer agent.
