# Code Review Report

- **Task ID**: `remove-legacy-toolbelt`
- **Branch**: `agent/remove-legacy-toolbelt` @ `3003f99` (base `main` @ `680d245`; iteration 1 was `8839715`)
- **Worktree**: `G:\Coding\Projects\Canvas\CanvasWindowComposer-legacy-toolbelt`
- **Iteration**: `2`
- **Verdict**: `APPROVED`

## Summary

Iteration 2 (`3003f99`, "test(overview): close reviewer gaps for the dock-only toolbelt") addresses both
Majors and all actionable Minors from iteration 1. No Blocker or Major remains; the residual items are
Nits only.

What changed and how it was verified:

- **Major #1 — missing z-order guard: resolved.** `DockStaysOnTopAcrossRaiseCanvasPanelsAndComposerWaitCycle`
  (`Tests/UnifiedDockTests.cs:389-444`) invokes the private `RaiseCanvasPanels` and
  `SetComposerWaitingForBrowser(true/false)` via reflection (same idiom as
  `Tests/MinimapNavigationTests.cs:273`), re-runs `RefreshConfig()`, and after every cycle asserts
  `IsDockVisible`, `IsRegistered("UnifiedDock")`, `!IsRegistered("AnnotationToolbar")` and
  `AssertAbove(dockHandle, pass)` for every `MonitorHandles` pass with a local `GW_HWNDPREV` walk
  (`Tests/UnifiedDockTests.cs:674-683`, identical to `MinimapNavigationTests.AssertAbove`). The assertion is
  deterministic rather than lucky: `CanvasLayerCoordinator.RaiseAll()` raises tiers in ascending order, so
  `DockAndToolbars (30)` is always re-raised after the `CanvasSurface (0)` passes. Tagged
  `[Trait("Category","NativeRendering")]`, so it is serialized by the runner.
- **Major #2 — DPI-fragile tolerance: resolved.** `DockFollowsTaskbarSettingImmediatelyAndAcrossCanvasModeChanges`
  now reads the real `PInvoke.GetWindowRect` of the dock HWND and derives the floating margin from
  `VisualTreeHelper.GetDpi(dock).DpiScaleY` (`Tests/UnifiedDockTests.cs:364-372`), i.e. exactly the term
  `Position()` uses (`UnifiedDockWindow.cs:117`), with a ±1 px tolerance. `GuidedTutorialPanelSitsAboveTheDock`
  now compares `tour.Bottom` against the real HWND top (`:645-648`). Both are DPI-correct and no longer
  self-referential to the cached `_lastPosition`.
- **Minor — stale `pass` capture: resolved.** The `KeyPressed` handler resolves the visible/primary pass on
  every key press and bails out (`return false`, i.e. the key is not swallowed) when no pass is visible
  (`OverviewManager.Annotations.cs:201-212`). Correct thread (WPF input and the passes share the STA UI
  thread) and no allocation concern.
- **Minor — untested mapping/swallowing: resolved.** `ToFormsKeys` is now a pure
  `internal static FormsKeys ToFormsKeys(Key, ModifierKeys)` (`UnifiedDockWindow.cs:141-153`) with
  `OnPreviewKeyDown` doing the `Key.System → SystemKey` unwrap, and
  `DockKeyboardBridgeMapsWpfKeysToTheFormsShortcutTable` (`Tests/UnifiedDockTests.cs:656-672`) covers
  no-modifier, Ctrl+Shift, Alt+F4 and `Key.None → FormsKeys.None`. Untagged and not on an STA thread — correct,
  the mapping touches no HWND. `DockForwardsKeyboardShortcutsToTheCanvas` additionally asserts an unbound
  `F9` leaves `args.Handled == false` (`:517-525`), so the bridge provably does not blanket-swallow keys.
- **Minor — dead hook: resolved.** `OpenStrokeMenu()` removed from `UnifiedDockControl`; the four remaining
  openers are all used by `DockContextualCommandsEditRealSelectedAnnotationsAndKeepHistoryWorking`.
- **Minor — deleted dropdown no-op test: accepted.** The deleted assertion only proved that the *deleted*
  `AnnotationToolbar` type's own `CloseDropDowns()` no-ops. The manager-level contract still has a direct
  assertion — `Tests/UiBugsAndShapesTests.cs:OverviewManagerClosesActiveMenusOnEscapeAndHide` asserts
  `Assert.False(overview.CloseActiveMenus())`, which reaches the new
  `_unifiedDock?.Panel.HasOpenDropDown == true` gate (`OverviewManager.WindowMenus.cs:152-156`) — and
  `OpenDockMenuBlocksPeekAndClosesOnEscape` covers the open→closed dock menu path. No extra dock-level no-op
  assertion is required.

Unchanged and still clean from iteration 1: no legacy references in `Source/`/`Tests/` (only the negative
assertion at `Tests/UnifiedDockTests.cs:421` and the unrelated WPF `_controlsButton`), one
`DockAndToolbars` registration, no ad-hoc `SetWindowPos`/`Topmost` outside `UnifiedDockWindow`, dispose order
intact, `CanvasDockTheme` trimmed to live members, `AGENTS.md` updated, no encoding damage, no artefacts in
the diff.

Build (full `-t:Rebuild`, Release, `--no-restore`): **0 errors, 1 warning**, the pre-existing `xUnit2013` in
`Tests/WorkspaceLinkCandidateTests.cs:99`, outside the diff. Acceptance criterion 7 met.

## Findings

| Severity | File | Line | Issue Description | Recommended Action |
| :--- | :--- | :--- | :--- | :--- |
| Nit | `Tests/UnifiedDockTests.cs` | 410-412 | The reflection helper uses `GetMethod(...)!`; renaming/removing a private method surfaces as a `NullReferenceException` instead of a readable failure. | `var method = typeof(OverviewManager).GetMethod(...); Assert.NotNull(method); method.Invoke(...)`, or include the name in the failure message. |
| Nit | `Tests/UnifiedDockTests.cs` | 430 | `Assert.True(dock.IsAlive)` mid-cycle asserts almost nothing (the dock is alive by construction) and does not pin the composer-wait contract (e.g. that the dock is not left visible-on-top of the browser hand-off). | Drop the line, or assert the actual contract for the waiting state and re-assert `IsDockVisible` after the `false` cycle. |
| Nit | `Source/Overlays/Overview/UnifiedDockWindow.cs` | 132-134 | The `Key.System → e.SystemKey` extraction is not covered by a test (only the pure `ToFormsKeys` mapping is); `KeyEventArgs.SystemKey` cannot easily be faked without real Alt state. | Acceptable as-is; note it in the test comment so a future reader does not assume Alt combos are covered end-to-end. |
| Nit | `Source/Overlays/Overview/OverviewManager.Annotations.cs` | 211 | `_unifiedDock.Panel.ControlsClicked += () => ToggleControlsPopup(pass);` still captures the creation-time pass that the keyboard path just stopped capturing, so the popup can be positioned/owned by a stale pass after a monitor change. Pre-existing and low risk. | For consistency, resolve the pass inside `ToggleControlsPopup` (or at click time) the same way the key bridge now does. |
| Nit | `Tests/UnifiedCanvasControlTests.cs` | 27-28 | The retargeted shapes test accepts any single `"Shapes" / "Rectangle" / "Ellipse" / "Arrow"` label (legitimate, the button label mirrors the last shape) but therefore no longer proves that three separate shape buttons were not re-introduced. | Optional: assert exactly one shape-labelled button, e.g. `Assert.Single(toolLabels, l => l is "Shapes" or "Rectangle" or "Ellipse" or "Arrow")`. |
| Nit | commit `3003f99` | — | Spec T6 asked for the dead `_controlsButton` / `CanvasControlsButton` removal to be a separate commit; it remains folded into `8839715`. | Mention in the merge description; no code change needed. |
| Nit | `Source/Overlays/Overview/OverviewManager.Annotations.cs` | 26-29, 173-212 | Pre-existing `\r\r\n` line endings persist in the rewritten hunks, so `git diff --check` reports trailing whitespace on added lines. Not introduced by this task. | Optional whitespace-only follow-up. |

## Test Verification Assessment

Inspected runs in the worktree (working tree clean and identical to `3003f99`):

| Run | Phase | Total | Passed | Failed | Notes |
| :--- | :--- | :--- | :--- | :--- | :--- |
| `run-20261117-005948-17520\filtered` | `UnifiedDockTests` only | 15 | 15 | 0 | includes all six new/changed dock tests plus the mapping test |
| `run-20261117-010024-17860\filtered` | filtered suite | 95 | 92 | 3 | the 3 are the same pre-existing environmental failures: `ZoneOverviewIntegrationTests.ProjectsWindowKeepsUnsavedEditsAcrossCanvasToggles`, `…RemainsAboveCanvasAndSurvivesCanvasToggles` (both die in `AssertPointerHits`, `Tests/ZoneOverviewIntegrationTests.cs:191`) and `OverviewWindowInteractionIntegrationTests.InteractControlWheelPansCanvasWithoutActivatingApp` (zoom 0.9 vs 1.0) |
| `run-20261117-010119-26372\unit` | non-native | 1820 | 1802 | 18 | `DockKeyboardBridgeMapsWpfKeysToTheFormsShortcutTable` passed |
| `run-20261117-010119-26372\native` | native | 301 | 263 | 38 | all 16 dock/controls integration tests passed, including `DockStaysOnTopAcrossRaiseCanvasPanelsAndComposerWaitCycle` |

- **New/changed tests executed**: PASS in both the filtered and the full run.
- **Failure attribution**: VERIFIED INDEPENDENTLY. I compared the failing-test sets of the iteration-1 full
  run (`run-20261116-122524-21968`) and the iteration-2 full run (`run-20261117-010119-26372`): 56 failing
  cases / 39 distinct test names in both, with an empty `Compare-Object` diff in both directions — zero new,
  zero fixed. The failures remain pointer hit-tests, wheel-zoom settling, drawer/inventory/settings/web-resource
  native tests, and the annotation-text-editor cluster, none of which touch the dock, the layer coordinator,
  the composer wait path or the annotation style commands. This corroborates the reported `680d245` baseline
  comparison (the baseline run folder itself is no longer present to re-inspect).
- **Categorisation**: correct. The two new HWND-touching tests carry
  `[Trait("Category","NativeRendering")]` and run in the serialized native phase; the pure mapping test is
  deliberately untagged and runs in the fast phase.
- **Assertion strength**: SUFFICIENT. The z-order guard asserts real HWND ordering, not cached state; the
  geometry guard asserts the real window rect with a DPI-derived margin; the keyboard guard asserts both the
  handled path (`Escape` → `OverviewMode.Hidden`) and the not-handled path (`F9`), plus the four mapping cases;
  the migrated contextual-command test still asserts real document/style/history state.
- **Coverage adequacy**: SUFFICIENT for this change. Every acceptance criterion (1–7) now has an automated or
  verified-manual witness, with criterion 3 covered by the new raise-cycle test.

## Recommendation & Next Step

`APPROVED` — cleared for the Merger stage.

- No Blocker or Major findings remain; the Nits above are optional polish and must not delay the merge.
- Suggested merge notes: fold-in of the optional T6 controls-button removal into `8839715`, the missing
  end-to-end Alt-combo coverage (pure mapping only), and the pre-existing `\r\r\n` line endings in
  `OverviewManager.Annotations.cs`.
- Merger: verify `main` freshness (`680d245`) before the fast-forward/merge, and keep
  `OverviewManager.Annotations.cs` conflict-aware — it is a hot file shared with the in-flight agent-card work.
