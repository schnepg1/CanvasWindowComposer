# Canvas Overlays & Overview Subsystem Guide (`Source/Overlays/Overview/`)

> [!IMPORTANT]
> Read this file before creating or modifying any canvas overlays, floating panels, toolbars, popups, or window preview/interaction components in this directory.

---

## 1. Architectural Scope
This subsystem manages the visual canvas surface, floating UI panels, toolbars, popups, and the window interaction / peek preview pipelines. 
The central coordinator is `OverviewManager` (split into partial classes `OverviewManager.*.cs`).

---

## 2. Invariants & Mandatory Patterns

### Rule 1: Deterministic Window Layering via `CanvasLayerCoordinator`
Canvas panels and floating toolbars compete for top-level visibility. **Never** call ad-hoc Win32 `SetWindowPos(HWND_TOPMOST)` or WinForms `TopMost = true`. Doing so causes Z-order fighting, toolbar flicker, and focus stealing.

All floating windows and panels must register with `CanvasLayerCoordinator` under their canonical `CanvasLayerTier`:

| Tier | Value | Examples |
| :--- | :---: | :--- |
| `CanvasSurface` | `0` | Base canvas render surfaces (`OverviewOverlay`) |
| `Zones` | `10` | Zone boundary overlays, `ZoneWindow` |
| `BackpackAndDrawers` | `20` | `BackpackWindow`, `TemporaryDrawer`, `BackgroundEditor` |
| `DockAndToolbars` | `30` | `UnifiedDockWindow`, `AnnotationToolbar` |
| `PeekPreview` | `40` | `CanvasPeekWindow` (hover pop-out preview) |
| `ContextPopups` | `50` | `CanvasControlsPopup`, context menus |
| `ModalDialogs` | `60` | Modal confirmation dialogs |

- **Raising Panels:** Invoke `_layerCoordinator.RaiseAll()` or `_layerCoordinator.RaiseAbove(CanvasLayerTier.DockAndToolbars)` to raise windows in strictly ascending tier order.
- **Non-Activating TopMost Creation:** To keep a floating window topmost without stealing foreground focus on creation, set `WS_EX_TOPMOST` (`0x00000008`) and `WS_EX_NOACTIVATE` (`0x08000000`) in `CreateParams` rather than setting `TopMost = true` in the constructor.
- **Exception Isolation:** Window raising callbacks are wrapped with `catch (ObjectDisposedException)` so asynchronously closed panels do not disrupt the stack.

---

### Rule 2: DWM Background Occlusion Throttling Defeat (`RequiresDwmOcclusionBypass`)
Windows Desktop Window Manager (DWM) and modern graphics runtimes (Chromium, Edge, WPF/Direct3D) apply aggressive background occlusion throttling (~1 FPS / 1,000 ms frame pacing) when an application window is covered by an opaque, non-layered top-level window.

- To guarantee interactive frame rates when streaming source applications through DWM thumbnails, `OverviewOverlay` must enable `WS_EX_LAYERED` whenever `RequiresDwmOcclusionBypass` is `true`:
  ```csharp
  private bool RequiresDwmOcclusionBypass =>
      CurrentMode == OverviewMode.Panning || IsStartupIntroActive || _windowInteraction.Active || PeekWorking;
  ```
- Any new interaction mode or preview feature that streams covered application windows must be included in `RequiresDwmOcclusionBypass`.

---

### Rule 3: Canvas Peek Lifecycle State Machine (`CanvasPeekState`)
The pop-out window preview (`CanvasPeekWindow`) lifecycle is strictly governed by `CanvasPeekState`:
```
Hidden  ──▶  DwellHovering  ──▶  OpeningAnimation  ──▶  PreviewOnly  ──▶  Interacting ("Work here")
  ▲                │                    │                    │                     │
  └────────────────┴────────────────────┴────────────────────┴─────────────────────┘
                                       │
                                       ▼
                                    Closing
```
- **State Properties:**
  - `_peekState == CanvasPeekState.Interacting`: The user has activated interactive usage ("Work here").
  - `PeekWorking`: Returns `_peekState == CanvasPeekState.Interacting && _peekWindow?.Working == true`.
- **Teardown Invariant:** All closing and disposal paths in `CloseWindowPeek()` must reset state within `try { ... } finally { _peekState = CanvasPeekState.Hidden; }`.

---

### Rule 4: Centralized Modal & Dropdown UI Detection
Do not inspect individual panel flags to determine if UI is blocked. Always use:
```csharp
private bool HasBlockingModalOrDropdownUi =>
    _controlsPopup?.Visible == true
    || _backpack?.Panel.IsOpen == true
    || _temporaryDrawer?.State.IsOpen == true
    || _windowContextMenu?.Visible == true
    || _interactionMenu?.Visible == true
    || _activeCardMenu?.Visible == true
    || _annotationToolbar?.HasOpenDropDown == true;
```
When introducing a new drawer, dropdown, or popup, add it to `HasBlockingModalOrDropdownUi` so dwell previews and canvas gestures do not trigger over it.

---

### Rule 5: Native Input Interop Diagnostics (`CanvasInputHost`)
When forwarding input to native windows via `CanvasInputHost.exe`:
- Use `CanvasInputHostExitCode` to interpret process exit results.
- `WindowInteractionInputGuard` must pump `Application.DoEvents()` when waiting on the current thread to prevent STA deadlocks during the initial startup handshake.

---

## 3. Dedicated Verification Tests
When modifying components in this subsystem, verify against:
- `Tests/CanvasLayerCoordinatorTests.cs` (layer hierarchy, sequencing, exception safety)
- `Tests/CanvasPeekIntegrationTests.cs` (dwell activation, topmost Z-order, occlusion bypass)
- `Tests/CanvasPeekPolicyTests.cs` (layout math, state machine enums)
- `Tests/UnifiedDockTests.cs` and `Tests/AnnotationToolbarTests.cs` (dock and toolbar integration)
