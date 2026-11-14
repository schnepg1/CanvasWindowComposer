# ARCHITECTURE PLAN: Fix AI Canvas Composer Window Popup, Alt+Q Navigation, and Closing Crash

**Target Branch:** `fix_ai_canvas_crash` -> `main`
**Task Identifier:** `fix-ai-canvas-crash`

---

## 1. Problem Statement & Root Cause Analysis

### A. Pop-up Visibility Failure
- **Root Cause**: In `OverviewManager.Composer.cs`, `OpenCanvasComposer` creates `CanvasComposerWindow` and calls `form.Show()` without establishing Win32 window ownership (`new WindowInteropHelper(form).Owner = owner.Handle`).
- When `RaiseOverviewPassesAboveTopmostWindows()` runs, `OverviewOverlay` instances (which are fullscreen topmost Win32/DirectX windows) are raised to `HWND_TOPMOST`. Because `CanvasComposerWindow` is unowned, the fullscreen overlay covers it or pushes it behind the canvas view.
- Furthermore, `form.Location` and `form.Size` were calculated mixing physical pixels (`owner.Screen.WorkingArea`) with WPF Device Independent Pixels (DIPs) without applying DPI scale (`owner.DeviceDpi / 96.0`), placing the window off-screen or clipping it on scaled displays.
- `ShowInTaskbar = true` made it an independent top-level window appearing in Windows taskbar and Alt+Tab, rather than an integrated canvas popup.

### B. Alt+Q Shortcut Blocked
- **Root Cause**: `OverviewManager.Annotations.cs` defines:
  ```csharp
  private bool CanvasEditorInputSuspended => ... || _composerWindow != null || ...;
  public bool OverviewHotkeySuspended => CanvasEditorInputSuspended || ...;
  ```
  When `_composerWindow != null`, `OverviewHotkeySuspended` evaluates to `true`. In `OverviewInputs.OnOverviewHotkey()`, pressing Alt+Q immediately returns when `OverviewHotkeySuspended` is true, trapping the user on the canvas.

### C. Lag & Crash on Closing (Event Log: Exception 0xe0434352 / 0xc000041d)
- **Root Cause**: Windows Application Event Log shows:
  ```text
  System.InvalidOperationException: Visibility cannot be set to Visible and Show, ShowDialog, Close or WindowInteropHelper.EnsureHandle cannot be called while a Window is closing.
     at System.Windows.Window.VerifyNotClosing()
     at System.Windows.Window.InternalClose(Boolean shutdown, Boolean ignoreCancel)
     at CanvasDesktop.CanvasComposerWindow.Dispose() in CanvasComposerWindow.cs:line 505
     at CanvasDesktop.OverviewManager.CancelCanvasComposer() in OverviewManager.Composer.cs:line 133
     at CanvasDesktop.OverviewManager.<>c__DisplayClass136_0.<OpenCanvasComposer>b__4() in OverviewManager.Composer.cs:line 40
     at CanvasDesktop.CanvasComposerWindow.<.ctor>b__71_12(Object _, CancelEventArgs e) in CanvasComposerWindow.cs:line 324
     at System.Windows.Window.WmClose()
  ```
  When the user clicks the title bar 'X' button, WPF begins closing and raises `Closing`. The handler set `e.Cancel = true; Dismissed?.Invoke();`. `Dismissed` calls `CancelCanvasComposer()` -> `form.Dispose()` -> `Close()`. Calling `Close()` reentrantly inside WPF `WmClose`/`Closing` triggers `VerifyNotClosing()` which throws `InvalidOperationException` on the UI thread, causing lags and crashing `CanvasDesktop.exe`.

---

## 2. Target Architecture & Component Design

### 1. `CanvasComposerWindow.cs`
- **Ownership & Taskbar**: Set `ShowInTaskbar = false;` and `Topmost = true;`. Accept `IntPtr owner` in constructor (or establish via `WindowInteropHelper.Owner = owner`).
- **WPF Lifecycle & Clean Close**:
  - Introduce `private bool _isClosing;`.
  - In `Closing`: record `_isClosing = true;`, cancel pending generation tokens (`_request?.Cancel()`), and do NOT cancel the close with `e.Cancel = true`.
  - In `Closed`: call `Dispose(); Dismissed?.Invoke();`.
  - In `Dispose()`: cancel operations, dispose connections, and call `Close()` only if `!_isClosing`.
- **Keyboard Interop**: Call `System.Windows.Forms.Integration.ElementHost.EnableModelessKeyboardInterop(this);`.

### 2. `OverviewManager.Composer.cs`
- In `OpenCanvasComposer`:
  - Pass `owner.Handle` (or set `new WindowInteropHelper(form).Owner = owner.Handle`).
  - Calculate size and location using `double scale = owner.DeviceDpi / 96.0;` so the window is correctly placed in DIPs within the screen's working area.
  - In `form.Dismissed`: defensively verify `if (_composerWindow == form)` before calling `CancelCanvasComposer()`.
  - In `CancelCanvasComposer`: set `_composerWindow = null;` before calling `form.Dispose()` to prevent re-entrant dismissal.

### 3. `OverviewInputs.cs` & `OverviewManager.Annotations.cs`
- Allow Alt+Q to dismiss the composer and exit overview:
  - In `OverviewInputs.OnOverviewHotkey()`: if `_overview.ComposerWindow != null`, call `_overview.CancelComposer()` and transition to `OverviewMode.Hidden`.
  - Or exempt `_composerWindow != null` from blocking `OverviewHotkeySuspended` so Alt+Q can always toggle or exit the canvas view.
  - In `HideInternal()` / `OverviewManager.cs`: ensure `CancelCanvasComposer()` is called so the composer is dismissed if overview is closed by other triggers.

### 4. Tests (`Tests/CanvasComposerOverviewTests.cs`)
- Update `Assert.True(window.ShowInTaskbar)` to `Assert.False(window.ShowInTaskbar)`.
- Add test: `ClosingComposerViaWindowCloseDoesNotThrowAndDismissesCleanly`:
  Simulate clicking the 'X' button by calling `window.Close()`. Verify no exception is thrown, `overview.ComposerWindow` becomes null, and overview remains stable.
- Add test: `AltQHotkeyClosesComposerAndExitsOverview`:
  Verify that when composer is open, pressing Alt+Q hotkey cleanly closes the composer window and transitions overview to hidden.

---

## 3. Implementation Checklist

- [ ] **Step 1: Fix `CanvasComposerWindow.cs`**:
  - Implement clean `_isClosing` flag and non-throwing `Dispose()` / `Close()`.
  - Set `ShowInTaskbar = false;`.
  - Establish `WindowInteropHelper.Owner = owner;`.
  - Add `ElementHost.EnableModelessKeyboardInterop(this);`.
- [ ] **Step 2: Fix `OverviewManager.Composer.cs`**:
  - Update `OpenCanvasComposer` DPI-aware sizing/positioning and owner assignment.
  - Update `CancelCanvasComposer()` and `form.Dismissed` to eliminate reentrancy.
- [ ] **Step 3: Fix Alt+Q Hotkey & Overview Hide**:
  - Ensure Alt+Q with `_composerWindow != null` cancels composer and exits overview mode.
  - Ensure hiding overview cleans up composer window.
- [ ] **Step 4: Update & Add Automated Tests**:
  - Update existing tests in `CanvasComposerOverviewTests.cs`.
  - Add tests for `window.Close()` and Alt+Q exit.
- [ ] **Step 5: Build & Run Tests**:
  - Compile with `dotnet build CanvasDesktop.slnx -c Release`.
  - Execute test suite via `Tools\Run-Tests.ps1` under named mutex.
- [ ] **Step 6: Review, Merge & Housekeeping**:
  - Code review diff audit.
  - Merge `fix_ai_canvas_crash` into `main`.
  - Clean up temporary files/processes.
