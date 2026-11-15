# Code Review Report

- **Task ID**: `fix_preview_delay_toolbar`
- **Branch**: `fix_preview_delay_toolbar`
- **Iteration**: 1
- **Verdict**: `APPROVED`

## Summary
The implementation for fixing the pop-out window preview streaming delay and toolbar flicker over the preview has been audited against the architectural blueprint ([`.agents/specs/ARCHITECTURE_PLAN_fix_preview_delay_toolbar.md`](.agents/specs/ARCHITECTURE_PLAN_fix_preview_delay_toolbar.md)).
All components adhere to repository coding invariants, threading safety, and Win32 / DWM interaction constraints.

## Findings

| Severity | File | Line | Issue Description | Recommended Action |
| :--- | :--- | :--- | :--- | :--- |
| None | All | - | Clean, conforming implementation with passing tests | Proceed to merge |

## Invariants Audit
1. **Z-Order Stability**: `CanvasPeekWindow` now includes `WS_EX_TOPMOST` (`0x00000008`) in `CreateParams` and targets `HWND_TOPMOST` (`-1`) in `RaiseWithoutActivation()`. In addition, `OverviewManager.Panels.cs` and `OverviewManager.Annotations.cs` ensure `_peekWindow` is re-raised above `UnifiedDockWindow` whenever the toolbar is presented or panels are raised, preventing toolbar flickering.
2. **Defeat Background Occlusion Throttle**: In `OverviewManager.cs`, `wantLayered` includes `PeekWorking`. Applying `WS_EX_LAYERED` on `OverviewOverlay` ensures DWM treats the overlay as non-occluding for background throttle calculations, allowing high frame-rate streaming of the source application without a 1-second delay.
3. **Non-Activating Window Creation**: Removed `TopMost = true;` from `CanvasPeekWindow` constructor, avoiding WinForms' implicit foreground stealing during handle creation.
4. **Desktop Interop Synchronization**: `InputHost.cpp` now calls `OpenInputDesktop` and `SetThreadDesktop` to ensure the hook process attaches to the active input desktop. `WindowInteractionInputGuard.cs` pumps `Application.DoEvents()` when waiting on the current thread to prevent STA deadlocks during startup handshake.
5. **Regression Test Added**: Dedicated test `PeekWorkingEnablesLayeredOverlayToDefeatBackgroundThrottleAndHoldsTopmostZOrder` verifies both topmost Z-order and layered overlay style during peek interaction.

## Test Verification Assessment
- Dedicated peek suite: 30/30 PASSED (`CanvasPeekIntegrationTests.cs`).
- AI module test suite: 100/100 PASSED (`CanvasDesktop.Ai.Tests.dll`).
- Full non-native test suite: 1,748/1,748 PASSED (`CanvasDesktop.Tests.dll`).
- Build: 0 warnings, 0 errors in Release configuration.

## Recommendation & Next Step
Proceed to Branch Merger stage (`branch-merger`).
