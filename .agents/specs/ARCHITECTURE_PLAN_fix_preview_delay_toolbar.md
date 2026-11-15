# Architecture Plan: Fix Canvas Peek Streaming Delay and Toolbar Flashing

**Task ID:** `fix-preview-delay-toolbar`  
**Target Branch:** `main`  
**Working Branch:** `fix_preview_delay_toolbar`  
**Worktree:** `fix_preview_delay_toolbar`  

---

## 1. Problem Statement & Scope

### Observed Issues
1. **Preview Streaming Delay (1-Second Lag upon Usage Activation)**:
   - When hovering over a window card, the hover-over pop-out preview (`CanvasPeekWindow`) appears.
   - When double-clicking to activate interaction ("Work here"), interacting with elements inside the preview window exhibits an approximate 1-second delay before the preview streams the updated content.
   - Root Cause: During `Zooming` mode interaction, `OverviewOverlay` was not configured with `WS_EX_LAYERED`. Windows DWM and modern UI frameworks (Chromium, Edge, WPF) apply aggressive background occlusion throttling (throttling to 1 FPS / 1000ms) when a window is covered by an opaque non-layered top-level window. In contrast, `WS_EX_LAYERED` explicitly kicks DWM composition and defeats background occlusion throttling (as already leveraged in `Panning` mode).

2. **Toolbar Flashing Over the Preview Window**:
   - `UnifiedDockWindow` (WPF) and `AnnotationToolbar` (WinForms) are topmost canvas panel windows at the bottom of the screen.
   - `CanvasPeekWindow` was created without `WS_EX_TOPMOST`, and its `RaiseWithoutActivation()` passed `HWND.Null` instead of `HWND_TOPMOST` (`(HWND)new IntPtr(-1)`).
   - In addition, frequent visual frame updates and tool refreshes (`RefreshToolPreview()` -> `RefreshAnnotationUi()`) re-raised `_unifiedDock` to `HWND_TOPMOST` without re-raising `_peekWindow`.
   - Consequently, whenever `CanvasPeekWindow` extended near or overlapped the toolbar region, the dock/toolbar flashed above and below the preview window.

---

## 2. Component & Architecture Impact

### Affected Files
- `Source/Overlays/Overview/CanvasPeekWindow.cs`:
  - Add `WS_EX_TOPMOST` (`0x00000008`) to `CreateParams`.
  - Update `RaiseWithoutActivation()` to pass `(HWND)new IntPtr(-1)` (`HWND_TOPMOST`).
- `Source/Overlays/Overview/OverviewManager.Interaction.cs`:
  - In `SetInteractionStyle(bool active)`, pass `active || CurrentMode == OverviewMode.Panning || IsStartupIntroActive` to `SetModeStyle` so `OverviewOverlay` becomes layered during interaction.
- `Source/Overlays/Overview/OverviewManager.cs`:
  - In mode transitions and style reconciliation (lines 929-931), include `_windowInteraction.Active || PeekWorking` in `wantLayered`.
- `Source/Overlays/Overview/OverviewManager.Panels.cs`:
  - In `RaiseCanvasPanels()`, raise `_peekWindow` after `_unifiedDock` to maintain preview topmost priority.
- `Source/Overlays/Overview/OverviewManager.Annotations.cs`:
  - In `RefreshAnnotationUi()`, if `_peekWindow is { Visible: true } peek`, ensure `peek.RaiseWithoutActivation()` is called after `RefreshUnifiedDock`.
- `Source/Overlays/Overview/OverviewManager.Peek.cs`:
  - In `TryUsePeekForeground`, update `_wm.SetTransientPreviewWindow(window)` when routing to an owned modal or child.
- `Tests/CanvasPeekIntegrationTests.cs`:
  - Add automated test assertions verifying:
    1. `CanvasPeekWindow` has `WS_EX_TOPMOST` and remains topmost over `UnifiedDockWindow`.
    2. `OverviewOverlay` receives `WS_EX_LAYERED` when interaction is active in `Zooming` mode.

---

## 3. Atomic Task Breakdown

### Task 1: Ensure CanvasPeekWindow TopMost Z-Order
- File: `Source/Overlays/Overview/CanvasPeekWindow.cs`
- Modify `CreateParams` to include `0x00000008` (`WS_EX_TOPMOST`).
- Update `RaiseWithoutActivation()` to pass `(HWND)new IntPtr(-1)`.

### Task 2: Maintain Preview Window Stacking in OverviewManager
- Files: `Source/Overlays/Overview/OverviewManager.Panels.cs`, `Source/Overlays/Overview/OverviewManager.Annotations.cs`
- In `RaiseCanvasPanels()`, call `_peekWindow?.RaiseWithoutActivation()` after `_unifiedDock.RaiseWithoutActivation()`.
- In `RefreshAnnotationUi()`, call `_peekWindow?.RaiseWithoutActivation()` after `RefreshUnifiedDock` if `_peekWindow` is visible.

### Task 3: Defeat Background Occlusion Throttling During Interaction
- Files: `Source/Overlays/Overview/OverviewManager.Interaction.cs`, `Source/Overlays/Overview/OverviewManager.cs`
- In `SetInteractionStyle(bool active)`:
  - Call `pass.SetModeStyle(active || CurrentMode == OverviewMode.Panning || IsStartupIntroActive, active || CurrentMode == OverviewMode.Panning);`
- In `OverviewManager.cs`:
  - Update `wantLayered` calculation: `bool wantLayered = CurrentMode == OverviewMode.Panning || IsStartupIntroActive || _windowInteraction.Active || PeekWorking;`

### Task 4: Keep Owned Modal Dialogs Live in WindowManager
- File: `Source/Overlays/Overview/OverviewManager.Peek.cs`
- In `TryUsePeekForeground(IntPtr window)`:
  - Add `_wm.SetTransientPreviewWindow(window);` before `_peekWindow!.ChangeSource(...)`.

### Task 5: Automated Test Verification
- File: `Tests/CanvasPeekIntegrationTests.cs`
- Add test verifying that entering interaction activates `WS_EX_LAYERED` on `OverviewOverlay` and that `CanvasPeekWindow` holds `WS_EX_TOPMOST`.
- Run tests via `Tools/Run-Tests.ps1 -Configuration Release` under the named mutex.

---

## 4. Test & Verification Strategy
- **Unit & Logic Tests**: Fast Sta tests verifying window styles, layering flags, and z-order.
- **Native Preview Tests**: Tests in `CanvasPeekIntegrationTests.cs` (Category: `NativePreview`) serialized under `Global\CanvasWindowComposer.Tests` mutex.
- **Preflight Requirement**: Verify `CanvasDesktop` is closed before running desktop tests.
