# ARCHITECTURE PLAN — Remove the legacy WinForms toolbelt (single-dock migration)

```yaml
task_id: remove-legacy-toolbelt
target_branch: main
repo: schnepg1/CanvasWindowComposer
requires_native_tests: true
triage: MULTI-AGENT
status: PLAN_READY
```

---

## 1. Problem Statement

Two independent toolbelts are created, laid out and shown at the **same bottom-centre band** of the
same monitor, in the same z-order tier:

| | Legacy belt | Current belt |
| :--- | :--- | :--- |
| Type | WinForms `Form` (`CanvasDockSurface`) + `ToolStrip` | WPF `CanvasPanelWindow` hosting a WPF-UI `UserControl` |
| Root type | `AnnotationToolbar` | `UnifiedDockWindow` / `UnifiedDockControl` |
| Created in | `OverviewManager.Annotations.cs:213-247` | `OverviewManager.Annotations.cs:275-314` |
| Shown in | `OverviewManager.Annotations.cs:262-272` | `OverviewManager.Annotations.cs:329` |
| Layer | `CanvasLayerTier.DockAndToolbars` | `CanvasLayerTier.DockAndToolbars` |

Both are topmost, both are wired to the *same* manager callbacks and both consume the *same*
`AnnotationToolbarState`. The WPF dock is raised last, so the legacy belt normally hides **behind**
it — but several code paths re-assert the legacy belt (or a window it owns) above the WPF dock,
which is the observed flicker.

The legacy belt must be deleted. This is not a cosmetic cleanup: the duplication causes real
behavioural defects (see §2.3).

---

## 2. Root-Cause Analysis (verified in the current tree)

### 2.1 Duplicate creation and presentation

`OverviewManager.Annotations.cs`

- `213-247` — legacy toolbar is instantiated, `SetAvailableWidth(...)`, `UpdateState(...)`, and every
  event (`ModeRequested`, `ComposerRequested`, `WebResourceRequested`, `ColorRequested`,
  `Undo/Redo`, `Cut/Copy/Paste`, `RetrySaveRequested`, `KeyDown` …) is subscribed a **second** time.
- `248-272` — `_annotationToolbar.Show(pass)` + `Location = bottom-centre of toolbarArea` on every
  refresh.
- `275-332` — the WPF dock repeats the identical wiring and presents itself with
  `Present(true, new ScreenRect(toolbarArea…))`, i.e. the same rectangle.

Refresh frequency is high: `RefreshAnnotationUi()` is invoked from selection changes
(`OverviewManager.Selection.cs:69,230`) and zone refresh (`OverviewManager.Zones.cs:319`).

### 2.2 Competing z-order re-raise paths (flicker)

| # | Path | Effect |
| - | :--- | :--- |
| 1 | `OverviewManager.Panels.cs:49-55` registers **both** windows in `DockAndToolbars`; `RaiseCanvasPanels()` runs on every pass `Activated` (`OverviewManager.cs:553`), selection change (`:263`) and transition (`:1039`). | `CanvasLayerCoordinator.RaiseAll()` calls `SetWindowPos(HWND_TOPMOST)` on the legacy belt and on the dock in the same frame. |
| 2 | `OverviewManager.Composer.cs:127-130` — `_annotationToolbar?.TopMost = !waiting`. | WinForms re-asserts `WS_EX_TOPMOST`, re-inserting the form at the **top** of the topmost band, above the WPF dock. |
| 3 | `AnnotationToolbar.ShowProperties()` (`AnnotationToolbar.cs:251-255`) called from `UpdateState` (`:243`) and `OnVisibleChanged` (`:256-261`). | A *second* owned topmost `CanvasDockSurface` (`_properties`, `:24`) is shown above the belt — and therefore above the new dock, because it is a freshly shown topmost owner window. |
| 4 | `AnnotationToolbar.RegisterMenu` (`:158-168`) calls `PInvoke.SetForegroundWindow(...)` on drop-down open. | Foreground/`WM_ACTIVATE` churn while both belts are visible. |
| 5 | `UnifiedDockWindow.Position()` early-returns when geometry is unchanged (`UnifiedDockWindow.cs:110`). | The *new* dock deliberately does not re-assert itself, so any legacy re-assert (1–3) stays on top until the next geometry change. |

Net effect: intermittent inversion — the legacy belt (or its properties strip) jumps in front of the
WPF dock. Matches the reported symptom exactly.

### 2.3 Functional defects caused by the duplication

1. **New dock's menus are invisible to the blocking-UI gate.** `HasBlockingModalOrDropdownUi`
   (`OverviewManager.Peek.cs:30-38`), `HandleCameraBookmarkKey` (`OverviewManager.CameraBookmarks.cs:47`)
   and `CloseActiveMenus` (`OverviewManager.WindowMenus.cs:152-157`) all test
   `_annotationToolbar?.HasOpenDropDown`. The WPF dock exposes `Panel.HasOpenDropDown`
   (`UnifiedDockControl.cs:91`) but it is only closed as a side effect of the legacy check. Consequence:
   dwell/peek previews and camera-bookmark shortcuts can fire while a WPF dock menu is open.
2. **Double subscriptions** to every manager callback (two handlers per event) — `AiTeams` state is
   pushed twice (`OverviewManager.Missions.cs:171-175`), `SetInteractMode` twice
   (`OverviewManager.Interaction.cs:29-30`), dropdown close twice.
3. **Two `TopMost` windows in the same tier** violate the spirit of
   `Source/Overlays/Overview/AGENTS.md` Rule 1 ("one canonical window per tier role, no z-order fighting").
4. **Tutorial layout reads the legacy belt** (`OverviewManager.GuidedTutorial.cs:238-239`), so the tour
   panel is positioned relative to a window that will be deleted — and today is positioned against the
   wrong (lower) belt.

### 2.4 Feature parity check (legacy → WPF dock)

| Legacy capability | WPF dock equivalent | Status |
| :--- | :--- | :--- |
| Select / Shapes (split: Rect, Ellipse, Arrow) / Text / **AI agent** | `_selectButton`, `_shapesButton` + `ShowShapesMenu`, `_textButton`, `_agentButton` | ✅ |
| Undo / Redo + enabled state | `_undoButton`, `_redoButton`, `UpdateState` opacity | ✅ |
| Status text + "Retry saving" link | `_statusBadge` + `MouseLeftButtonUp → RetrySaveRequested` | ✅ |
| More menu: edit / cut / copy / paste / delete / save, nearby, AI Compose, **AI Teams**, web resource, backgrounds, projects, workflow import/export | `ShowMoreMenu` — same item set + `InputGestureText` shortcuts | ✅ |
| Contextual properties (colour swatches, stroke, size, style/align, fill) | `_propertiesShelf` (`BuildPropertiesShelf`, `UpdatePropertiesVisibility`) | ✅ |
| Interact-mode HUD, Controls cheatsheet | `_interactHudPanel`, `_controlsButton` | ✅ |
| Responsive width / overflow (`SetAvailableWidth`) | WPF `SizeToContent` + `Position()` measure loop | ✅ (native) |
| **Keyboard forwarding while the belt has focus** (`KeyDown += HandleKeyDown`, `AnnotationToolbar.cs:129,246`) | none | ❌ **gap — must be added** |
| High-contrast / accessible names (`CanvasDockTheme` HC colours) | dark-only WPF theme | ⚠️ pre-existing gap, out of scope (record as follow-up) |

---

## 3. Component Impact

### 3.1 DELETE

| File | Action |
| :--- | :--- |
| `Source/Overlays/Overview/AnnotationToolbar.cs` | delete the whole file (292 lines). Keep the `AnnotationToolbarState` record — move it to `UnifiedDockControl.cs` (or `CanvasDockTheme.cs`) so the type survives. |
| `Source/Overlays/Overview/CanvasDockChrome.cs` | delete `DockGlyph`, `CanvasDockStrip`, `CanvasDockColorTable`, `CanvasDockRenderer`, `CanvasDockSurface` (lines 10-230, 232-305). **Keep** `CanvasDockTheme` (still used by `TutorialTourPanel.cs:11-134`). |
| `Tests/AnnotationToolbarTests.cs` | delete after migrating the two still-unique cases (see §4.1). |

### 3.2 MODIFY — production

| File | Change |
| :--- | :--- |
| `OverviewManager.Annotations.cs` | drop `_annotationToolbar` field (`:26`) and the `internal AnnotationToolbar? AnnotationToolbar` accessor (`:43`); delete legacy wiring block `:213-247`, the `UpdateState`/`SetAvailableWidth` block `:249-261` and the show/locate block `:262-272`; `HideAnnotationUi` (`:177-183`) keeps only `_unifiedDock.Present(false)`; drop dispose of the toolbar (`:772`); `MessageBox.Show(_annotationToolbar, …)` (`:243`, `:311`) → owner = active pass (`_passes.FirstOrDefault(p => p.Visible)`) or `null`. Rename `RefreshUnifiedDock` → `RefreshDock` (optional). |
| `OverviewManager.Panels.cs` | delete the `AnnotationToolbar` layer registration (`:49-51`); keep `UnifiedDock` as the only `DockAndToolbars` entry. |
| `OverviewManager.Peek.cs` | `:37` → `\|\| _unifiedDock?.Panel.HasOpenDropDown == true`. |
| `OverviewManager.CameraBookmarks.cs` | `:47` → same replacement. |
| `OverviewManager.WindowMenus.cs` | `:152-157` → close/close-test the WPF dock menu (`_unifiedDock?.Panel`), independent of the legacy belt. |
| `OverviewManager.Composer.cs` | `:127` remove legacy `Hide()`; `:130` remove `_annotationToolbar?.TopMost` toggle (the dock already re-presents on `RefreshToolPreview`/`RefreshAnnotationUi`). |
| `OverviewManager.Zones.cs` | `:270` → `_unifiedDock?.Present(false)` (or rely on `HideAnnotationUi`). |
| `OverviewManager.Missions.cs` | `:171,174` → keep only `_unifiedDock?.Panel.SetAiTeamsActive(...)`. |
| `OverviewManager.GuidedTutorial.cs` | `:238-239` → position the tour panel above `_unifiedDock.Bounds.Top - dpiPadding` (needs the new `Bounds` accessor in §3.3). |
| `OverviewManager.Interaction.cs` | `:30` `_controlsButton?.SetInteractMode(enabled)` → remove together with the dead controls button (Task 5). |
| `OverviewManager.cs` | `:809`, `:930` → `_unifiedDock?.Panel.CloseDropDowns()`. |
| `Source/Overlays/Overview/AGENTS.md` | Rule 4 snippet → `_unifiedDock?.Panel.HasOpenDropDown`; tier table example list loses `AnnotationToolbar`; test list loses `AnnotationToolbarTests.cs`. |

### 3.3 MODIFY — new dock (parity work)

| File | Change |
| :--- | :--- |
| `UnifiedDockWindow.cs` | add `internal ScreenRect Bounds => _lastPosition;` (physical px, already DPI-scaled by `Position()`); add a keyboard bridge: `public event Action<System.Windows.Forms.Keys>? KeyPressed;` fed from `OnPreviewKeyDown`/`OnKeyDown` with `Keyboard.Modifiers` mapped to `Keys`; mark `e.Handled` only when the manager handled it. |
| `UnifiedDockControl.cs` | host the relocated `AnnotationToolbarState` record (or leave it in place); no visual change required. |
| `OverviewManager.Annotations.cs` | wire `_unifiedDock.KeyPressed += keys => HandleKeyDown(pass, new KeyEventArgs(keys));` where the legacy `KeyDown` hook used to be. |

### 3.4 MODIFY — tests

| File | Change |
| :--- | :--- |
| `Tests/UnifiedCanvasControlTests.cs` | `:11-59` re-target the two `AnnotationToolbar` strip assertions to `UnifiedDockControl` (`Shapes` menu = Rect/Ellipse/Arrow; More menu contains `Projects…` once, no `Project Navigator…`). |
| `Tests/ZoneOverviewIntegrationTests.cs` | `:99-105` open "Projects…" through `overview.UnifiedDock.Panel.OpenMoreMenu()` instead of the legacy strip. |
| `Tests/TelemetryTests.cs` | `:326-341` delete (already covered by `UnifiedDockTests.StandaloneUnifiedDockControl_AiTeamsToggledAndState`). |
| `Tests/UiBugsAndShapesTests.cs` | `:111-122` re-target `CloseDropDowns`/`HasOpenDropDown` to `UnifiedDockControl`. |
| `Tests/UnifiedDockTests.cs` | add the regression cases from §4.2. |

### 3.5 Optional (dead code of the same generation)

`_controlsButton` (`OverviewManager.Annotations.cs:29,31,197-211,775`) is constructed but never shown
(the `Show` call is commented out at `:208-210`). `CanvasControlsButton`
(`CanvasControlsUi.cs:7`) then has no live consumer. Removing both is safe and removes one more
topmost `Form`; it is a **separate commit** so it can be dropped if scope must stay tight.
`CanvasControlsPopup` stays (used by `ToggleControlsPopup`).

---

## 4. Test & Verification Strategy

### 4.1 Migrated coverage (must not be lost)

| Legacy test | Fate |
| :--- | :--- |
| `MoreMenuProvidesCutCopyPasteActionsAndRespectsState` | already duplicated by `UnifiedDockTests:257-305` → delete legacy copy. |
| `ContextCommandsEditRealSelectedAnnotationsAndKeepHistoryWorking` (`:170-213`) | **unique end-to-end value** (colour/size/style/fill/delete/undo/redo against a real `OverviewManager`). Re-target the driver to the WPF dock: invoke `ColorRequested`/`FontSizeRequested`/`EmphasisRequested`/`TextAlignRequested`/`CardFillRequested` + `Delete/Undo/Redo` through `overview.UnifiedDock.Panel`. |
| `ToolbarFollowsTaskbarSettingImmediatelyAndAcrossCanvasModeChanges` (`:215-250`) | **unique** (dock geometry vs `ShowTaskbarInCanvas`). Re-target to `UnifiedDockWindow.Bounds`: bottom == `area.Bottom`, horizontally centred, across `ShowTaskbarInCanvas` toggles, `Panning → Zooming`, `Hidden → Zooming`. |
| `FirstClickActivatesAToolAndOpensItsMenuAfterCanvasRegainsFocus` (`:14-74`) | WinForms `MA_ACTIVATE` only → delete; WPF equivalent is `UnifiedDockTests` click tests. |
| `CompactDockRemembersShapesAndKeepsEveryCommandReachable`, `ContextFollowsToolAndSelectedStyle…`, `NarrowDockKeepsToolsVisible…` | covered by `UnifiedDockTests:18-148` (arrange/interact/properties shelf) → delete. |

### 4.2 New regression tests (`Tests/UnifiedDockTests.cs`)

1. `SingleDockOnly_NoLegacyWindowIsCreated` — after `TransitionTo(Zooming)`, enumerate the manager's
   top-level windows (or assert via reflection that no `CanvasDockSurface` instance exists) and assert
   exactly one `DockAndToolbars` layer is registered (`overview.LayerCoordinator` count/`IsRegistered("UnifiedDock")`
   and `IsRegistered("AnnotationToolbar") == false`).
2. `DockStaysOnTopAcrossRaiseCanvasPanelsAndComposerWaitCycle` — force `RaiseCanvasPanels()` (via
   `overview.TransitionTo`/`Activated` simulation) and the composer wait toggle, then assert
   `UnifiedDock.IsDockVisible` and that the dock's HWND is still the topmost of the tier
   (`GetWindow(GW_HWNDPREV)`/`GetWindowRect` comparison, `Category=NativeRendering`).
3. `DockMenuBlocksPeekAndIsClosedByCloseActiveMenus` — open `Panel.OpenMoreMenu()`, assert
   `Panel.HasOpenDropDown`, call `overview.HandleEscape()` (or the `CloseActiveMenus` path) and assert
   the menu is closed and `overview.HandleEscape()` did not leave Zooming.
4. `DockKeyboardBridgeForwardsShortcuts` — raise a WPF key event on the dock host and assert the
   manager handled it (e.g. `Space` starts pan / `Esc` returns to Select).
5. `TutorialPanelSitsAboveDock` — start the guided tour and assert
   `_tourPanel.Bottom <= UnifiedDock.Bounds.Top` (native).

### 4.3 Commands (serialized runner only — native tests share the desktop)

```powershell
# in the agent worktree
dotnet restore CanvasDesktop.slnx --locked-mode
dotnet build CanvasDesktop.slnx -c Release --no-restore

# focused, fast first
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\Run-Tests.ps1 -Configuration Release -NoBuild -Filter "FullyQualifiedName~UnifiedDock|FullyQualifiedName~UnifiedCanvasControl|FullyQualifiedName~ZoneOverviewIntegration"

# full suite (non-native + native phases under the machine-wide mutex)
Tests\run_tests.bat -Configuration Release
```

`CanvasDesktop.exe` must be closed before the native phase.

---

## 5. Atomic Task Checklist

- [ ] **T1 — Dock parity plumbing.** `UnifiedDockWindow.cs`: add `internal ScreenRect Bounds => _lastPosition;` and the `KeyPressed` bridge (`OnPreviewKeyDown`/`OnKeyDown`, WPF `Key`+`ModifierKeys` → `System.Windows.Forms.Keys`, mark handled when set).
- [ ] **T2 — Relocate the state record.** Move `AnnotationToolbarState` out of `AnnotationToolbar.cs` (e.g. into `UnifiedDockControl.cs`); keep the type name for now (rename is T9). Compile-only change.
- [ ] **T3 — Cut the legacy presentation.** `OverviewManager.Annotations.cs`: delete the legacy wiring/`UpdateState`/`Show`/`UpdateState` block and `HideAnnotationUi`'s legacy line; wire `_unifiedDock.KeyPressed`; replace the two `MessageBox.Show(_annotationToolbar, …)` owners; rename `RefreshUnifiedDock` → `RefreshDock` (optional).
- [ ] **T4 — Repoint every consumer.** `Panels.cs` (layer registration), `Peek.cs`, `CameraBookmarks.cs`, `WindowMenus.cs`, `Composer.cs`, `Zones.cs`, `Missions.cs`, `GuidedTutorial.cs`, `OverviewManager.cs:809/930`, dispose path. Verify each `HasOpenDropDown` check now reads `_unifiedDock?.Panel.HasOpenDropDown`.
- [ ] **T5 — Delete the dead belt.** Remove `Source/Overlays/Overview/AnnotationToolbar.cs`; trim `CanvasDockChrome.cs` to `CanvasDockTheme` only; remove `_annotationToolbar` field/accessor. `dotnet build` must be warning-clean (unused members gone).
- [ ] **T6 (optional, separate commit) — Dead controls button.** Remove `_controlsButton`, its `Interaction.cs` call and `CanvasControlsButton` (`CanvasControlsUi.cs`), keeping `CanvasControlsPopup`.
- [ ] **T7 — Tests.** Delete `Tests/AnnotationToolbarTests.cs` after migrating the two unique cases; re-target `UnifiedCanvasControlTests`, `ZoneOverviewIntegrationTests`, `UiBugsAndShapesTests`; delete `TelemetryTests.AnnotationToolbar_AiTeamsToggle_ControlsState`; add §4.2 cases 1–5.
- [ ] **T8 — Docs.** Update `Source/Overlays/Overview/AGENTS.md` (tier table, Rule 4 snippet, test list). `Docs/AI Mission Control.md` and `Docs/team-pods.md` already describe only the WPF dock — verify no wording references the legacy belt.
- [ ] **T9 (optional)** Rename `AnnotationToolbarState` → `CanvasDockState` (touches `UnifiedDockControl`, `OverviewManager.Annotations`, `UnifiedDockTests`).

---

## 6. Edge Cases & Failure Modes

| Risk | Mitigation |
| :--- | :--- |
| **Lost keyboard shortcuts** when the WPF dock holds focus (legacy `KeyDown` hook removed). | T1 bridge; test §4.2-4. |
| **Peek/context popup ordering.** `PeekPreview` (40) must stay above `DockAndToolbars` (30). | Keep `_layerCoordinator.RaiseAbove(CanvasLayerTier.DockAndToolbars)` in `RefreshDock`; §4.2-2 asserts it. |
| **Multi-monitor / mixed DPI.** `UnifiedDockWindow.Bounds` is physical px; the tour panel math mixes DPI-scaled logical px. | Clamp with `Math.Max(area.Top, …)` and convert with `pass.DeviceDpi/96`; §4.2-5 covers single-monitor only — verify manually on a secondary display. |
| **Composer "waiting for browser"** flow loses its toolbar hide. | `HideAnnotationUi()` already calls `_unifiedDock.Present(false)` and is invoked from that path; drop only the legacy lines. |
| **Dispose ordering.** Removing `_annotationToolbar.Dispose()` must not leave the dock alive after `DisposeAnnotations`. | Dispose order stays `_unifiedDock` → `_controlsPopup` → (controls button) → mission control. |
| **High-contrast users** lose the WinForms HC-themed belt. | Pre-existing divergence (the WPF dock is already the primary UI); record as a follow-up ticket, not in this change. |
| **Reflection-based tests** that look up `_annotationToolbar` by name (`AnnotationToolbarTests:190,232`). | Both live in the file deleted by T7; grep for `_annotationToolbar` afterwards must return only docs/history. |
| **Merge hazard** — `OverviewManager.Annotations.cs` is a hot file shared with the in-flight agent-card/subagent work. | One agent, one worktree, rebase on `main` immediately before merge; reviewer checks diff vs. spec. |

---

## 7. Acceptance Criteria

1. `grep -r "AnnotationToolbar\|CanvasDockSurface\|CanvasDockStrip\|DockGlyph" Source Tests` returns **no** matches except `AnnotationToolbarState` (until T9).
2. Only one `DockAndToolbars` layer is registered; `OverviewManager` creates exactly one bottom-band window.
3. Repeated canvas activation / selection churn / composer wait toggles produce **no** z-order change of the dock (no flicker), verified visually and by §4.2-2.
4. An open WPF dock menu blocks peek dwell, camera-bookmark keys and `HandleEscape` close-path exactly like the old gate did.
5. Guided-tour panel sits above the dock.
6. `Tests\run_tests.bat -Configuration Release` green (non-native + native phases).
7. `dotnet build CanvasDesktop.slnx -c Release` yields no new warnings.

---

## 8. Handover

```yaml
status: "PLAN_READY"
task_id: "remove-legacy-toolbelt"
target_branch: "main"
plan_path: ".agents/specs/ARCHITECTURE_PLAN_remove_legacy_toolbelt.md"
estimated_tasks: 9   # 7 required + 2 optional
requires_native_tests: true
next_role: "worktree-developer"
worktree_suggestion: "..\\CanvasWindowComposer-legacy-toolbelt (branch agent/remove-legacy-toolbelt)"
```
