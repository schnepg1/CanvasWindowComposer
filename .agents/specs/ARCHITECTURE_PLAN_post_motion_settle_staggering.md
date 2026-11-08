# Architecture Plan: Post-Motion Settle Staggering

- **Task ID**: `post-motion-settle-staggering`
- **Target Branch**: `main`
- **Agent Worktree**: `..\CanvasWindowComposer-settle-staggering`
- **Agent Branch**: `agent/settle-staggering`
- **Author**: System Architect (`architect-planning`)

---

## 1. Problem Statement & Scope

### 1.1 The Issue
During camera motion (`OverviewManager.MotionActive == true` / `SimplifyDuringMotion == true`), live DWM thumbnails are suppressed to prevent out-of-process DWM IPC thrashing and UI thread stalls.
When navigation halts, `MotionActive` transitions to `false` and `SimplifyDuringMotion` becomes `false`.

On the very first frame after motion ceases, `OverviewThumbnails.Reconcile()` unsuppresses ALL active windows simultaneously. In workspaces with 20–40+ open windows, this executes 20–40+ synchronous cross-process DWM Win32 API calls (`DwmRegisterThumbnail`, `DwmUpdateThumbnailProperties`) within a single frame callback, generating a 10–25 ms CPU/DWM IPC spike.

This spike produces a noticeable 1-frame hitch right as the camera comes to a halt.

### 1.2 The Goal
Implement **Post-Motion Settle Staggering**:
1. When motion ceases, partition visible windows into concentric batches based on their Euclidean distance from the camera focal point (`GetTransitionFocalPoint(camera)` or primary screen center).
2. Awaken the closest batch (Batch 0: up to 8 windows directly under the user's focus) immediately on Frame 0 (0 ms delay).
3. Awaken subsequent batches (Batch 1: +16 ms, Batch 2: +32 ms) across the next 1–2 frames using coalesced frame scheduling (`_visualFrames.Request()`).
4. Keep the total settle duration under 50 ms (well within the 150 ms placeholder card fade duration).
5. If total visible windows <= 8, bypass staggering and awaken all windows immediately.
6. If the user initiates new camera motion before settle finishes, immediately abort settle and return to motion state.

---

## 2. Component & Architecture Impact

### 2.1 Files Touched
1. `Source/Overlays/Overview/OverviewThumbnails.cs`
   - Introduce settle staggering state: `BeginSettle(CanvasPoint focal, CanvasCamera camera)`, `AdvanceSettle()`, `IsSettling`, and batch distance calculations.
   - In `ComputeTarget(CanvasCamera camera)`: when settle is active, apply the progressive visibility quota based on distance from the focal point.
2. `Source/Overlays/Overview/OverviewManager.cs`
   - In `ReconcileVisualFrame()`: when `wasMoving && !isMoving`, initiate thumbnail settle staggering with the current focal point.
   - If `_thumbnails.IsSettling`, schedule the next frame via `_visualFrames.Request()` so the message pump remains fluid.
3. `Source/Overlays/Overview/OverviewManager.GesturePreviews.cs`
   - Reset or coordinate settle state on gesture interruptions (`ObserveGestureMotion()`, `ResetGesturePreviews()`).
4. `Tests/OverviewThumbnailSettleTests.cs` (New)
   - Unit tests covering distance sorting, batching thresholds, frame-by-frame progression, and interruption recovery.
5. `Docs/smooth-zooming-and-panning.md`
   - Document the post-motion settle staggering invariants and troubleshooting steps.

---

## 3. Atomic Task Breakdown

### Task 1: OverviewThumbnails Settle Staggering Engine
- In `OverviewThumbnails.cs`:
  - Define `public const int SettleBatchSize = 8;`
  - Add `private bool _settleActive;`
  - Add `private int _settleBatchIndex;`
  - Add `private CanvasPoint _settleFocal;`
  - Add `public bool IsSettling => _settleActive;`
  - Implement `public void BeginSettle(CanvasPoint focal, CanvasCamera camera)`:
    - If total visible uncached windows <= `SettleBatchSize`, set `_settleActive = false` (all awaken on frame 0).
    - Else set `_settleActive = true; _settleBatchIndex = 0; _settleFocal = focal;`.
  - Implement `public bool AdvanceSettle()`:
    - Increments `_settleBatchIndex`.
    - If all batches are awakened, set `_settleActive = false; return false;`.
    - Returns `true` if more batches remain.
  - Implement `public void CancelSettle()`: sets `_settleActive = false;`.
  - In `ComputeTarget(CanvasCamera camera)`:
    - When `_settleActive`:
      - Sort candidates by distance from `_settleFocal`:
        $$d = \sqrt{(c_x - \text{focal}_x)^2 + (c_y - \text{focal}_y)^2}$$
      - Mark windows in `index < (_settleBatchIndex + 1) * SettleBatchSize` as `visible = true`.
      - Higher-index windows remain `visible = false` (suppressed) for this tick.

### Task 2: OverviewManager Coordination
- In `OverviewManager.cs`:
  - Track `_wasMoving = isMoving;` across `ReconcileVisualFrame()` calls.
  - When `_wasMoving && !isMoving`:
    - Call `_thumbnails.BeginSettle(focal, camera);`
  - In `ReconcileVisualFrame()`:
    - If `_thumbnails.IsSettling`:
      - Call `_thumbnails.AdvanceSettle();`
      - Call `_visualFrames.Request();` to pump the next settle frame without delay.
  - If `isMoving`:
    - Call `_thumbnails.CancelSettle();`

### Task 3: Comprehensive Automated Tests (`Tests/OverviewThumbnailSettleTests.cs`)
- Test 1: Workspaces with <= 8 windows awaken all windows on frame 0 with no settle delay.
- Test 2: Workspaces with 24 windows awaken exactly 8 on frame 0, 16 on frame 1, and all 24 on frame 2.
- Test 3: Central windows (closest to focal point) awaken in Batch 0; outer windows awaken in later batches.
- Test 4: Screen-fixed and cached windows are never delayed by settle staggering.
- Test 5: Re-entering motion during settle aborts the stagger immediately without orphaned state.

### Task 4: Documentation Update
- In `Docs/smooth-zooming-and-panning.md`, add section on Post-Motion Settle Staggering.

---

## 4. Invariants & Verification Strategy

- **WPF / UI Thread Safety**: All state changes occur on the UI STA thread inside `ReconcileVisualFrame()`.
- **DWM Stacking Safety**: `CycleTaskbarsOnTop` continues to ensure taskbars sit above thumbnails on all passes.
- **Mutex Test Execution**: Run tests with `Tools/Run-Tests.ps1` or `dotnet test Tests/CanvasDesktop.Tests.csproj -c Release -p:CanvasSkipTestPreflight=true --filter "Category!=NativeRendering&Category!=NativeParking&Category!=NativePlacement&Category!=NativePreview"`.
