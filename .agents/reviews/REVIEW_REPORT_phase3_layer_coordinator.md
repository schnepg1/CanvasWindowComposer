# Code Review Report

- **Task ID**: `phase3-layer-coordinator`
- **Branch**: `refactor/phase3-layer-coordinator`
- **Target Branch**: `main`
- **Commit**: `cfe23c5`
- **Iteration**: 1
- **Verdict**: `APPROVED`

## Summary
The changes introduced in Phase 3 ("Centralized Canvas Layer / Z-Order Stack Coordinator") have been audited against repository coding invariants, Win32 / WPF panel Z-order requirements, threading safety, and exception handling constraints.
The refactor introduces `CanvasLayerCoordinator` and the `CanvasLayerTier` enum to eliminate fragmented, ad-hoc `RaiseWithoutActivation()` ordering across `OverviewManager`. Z-order stack elevations are now centrally managed through ascending tier priority with FIFO tie-breaking. Higher tiers (e.g. `PeekPreview`, popups, modals) are deterministically elevated above dock and toolbar refreshes via `RaiseAbove(CanvasLayerTier.DockAndToolbars)`. Complete `ObjectDisposedException` isolation guarantees that background teardown of disposed panels does not interrupt layer stacking.

## Findings

| Severity | File | Line | Issue Description | Recommended Action |
| :--- | :--- | :--- | :--- | :--- |
| None | All | - | Clean, robust implementation with comprehensive unit tests, ascending tier order correctness, and thread-safe lock isolation | Proceed to merge |

## Invariants Audit

1. **`CanvasLayerTier` Enum Definitions (`Source/Overlays/Overview/CanvasLayerCoordinator.cs`)**:
   - `CanvasLayerTier` assigns explicit, spaced integer values reflecting the physical Z-order stack:
     ```csharp
     internal enum CanvasLayerTier
     {
         CanvasSurface = 0,
         Zones = 10,
         BackpackAndDrawers = 20,
         DockAndToolbars = 30,
         PeekPreview = 40,
         ContextPopups = 50,
         ModalDialogs = 60
     }
     ```
   - Ascending integer order mirrors Win32 non-activating Z-order rules: elevating lower tiers first followed by higher tiers guarantees that higher tiers remain topmost in the desktop visual hierarchy.

2. **`CanvasLayerCoordinator` Methods & Thread Safety (`Source/Overlays/Overview/CanvasLayerCoordinator.cs`)**:
   - **Thread Safety**: All state mutations and queries (`Register`, `Unregister`, `RaiseAll`, `RaiseAbove`, `Count`, `IsRegistered`) synchronize on private `object _syncLock`.
   - **Deadlock Avoidance**: `RaiseAll` and `RaiseAbove` snapshot the ordered registrations into an array inside `_syncLock` before invoking delegates outside the lock via `RaiseTargets(targets)`.
   - **Ascending & Stable Ordering**: Snapshot sorts by `r => (int)r.Tier`, then by `r => r.Sequence` (`_sequenceCounter`), ensuring deterministic FIFO ordering for layers registered within the same tier.
   - **Exception Isolation**:
     - `CheckAlive` catches `ObjectDisposedException` and returns `false`.
     - `RaiseTargets` catches `ObjectDisposedException` around each invocation, ensuring disposed panels during asynchronous or concurrent window teardown do not abort raising the remaining layers.

3. **`OverviewManager.Panels.cs` Integration**:
   - Exposes `_layerCoordinator` via internal getter for testability and inspection.
   - `RegisterCanvasLayers()` accurately maps all overlay components to their canonical tiers:
     - `CanvasLayerTier.Zones`: `_zoneWindow`
     - `CanvasLayerTier.BackpackAndDrawers`: `_backgroundEditor`, `_backpack`, `_temporaryDrawer`
     - `CanvasLayerTier.DockAndToolbars`: `_annotationToolbar`, `_unifiedDock`
     - `CanvasLayerTier.PeekPreview`: `_peekWindow`
   - `RaiseCanvasPanels()` invokes `RegisterCanvasLayers()`, delegates directly to `_layerCoordinator.RaiseAll()`, and fires `CanvasPanelsRaised?.Invoke()`.

4. **`OverviewManager.Annotations.cs` Integration**:
   - Replaces fragmented hardcoded peek elevation in `RefreshUnifiedDock()` with:
     ```csharp
     RegisterCanvasLayers();
     _layerCoordinator.RaiseAbove(CanvasLayerTier.DockAndToolbars);
     ```
   - When the unified dock is presented or refreshed (e.g. during frequent tool changes or note typing), all higher layers (`PeekPreview`, popups, modals) are systematically re-raised above the dock tier, preventing any visual occlusion.

5. **`CanvasDockSurface.RaiseWithoutActivation` (`Source/Overlays/Overview/CanvasDockChrome.cs`)**:
   - Implements safe non-activating Win32 elevation via `PInvoke.SetWindowPos((HWND)Handle, (HWND)new IntPtr(-1), 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE)` guarded by `Visible && !IsDisposed && IsHandleCreated`.

6. **Unit Test Coverage (`Tests/CanvasLayerCoordinatorTests.cs`)**:
   - `Register_And_RaiseAll_ExecutesInAscendingTierSequence`: Confirms execution strictly follows ascending tier order regardless of registration order.
   - `RaiseAll_PreservesInsertionOrderForSameTier`: Confirms FIFO stability.
   - `Register_ReplacesExistingLayer`: Confirms replacement of an existing layer maintains correct count and updates tier.
   - `Unregister_RemovesLayer`: Confirms unregistration cleanly removes layer.
   - `RaiseAbove_OnlyInvokesHigherTiers`: Confirms only strictly greater tiers are raised.
   - `RaiseAbove_WithHighestTier_InvokesNothing`: Confirms empty set when querying above maximum tier.
   - `IsAlive_False_SkipsInvocationWithoutThrowing`: Confirms dead layers are bypassed.
   - `IsAlive_ThrowingObjectDisposedException_TreatedAsDeadWithoutThrowing`: Confirms disposed probe resilience.
   - `RaiseWithoutActivation_ThrowingObjectDisposedException_DoesNotAbortRemainingLayers`: Confirms raise invocation exception resilience.
   - `ArgumentValidation_ThrowsOnInvalidArguments`: Confirms parameter guard contracts.

## Test Verification Assessment
- **CanvasLayerCoordinator Unit Tests**: 10/10 PASSED (`dotnet test --filter "FullyQualifiedName~CanvasLayerCoordinatorTests"`).
- **CanvasPeek Integration & Policy Tests**: 59/59 PASSED (`dotnet test --filter "FullyQualifiedName~CanvasPeek"`).
- **AI Modules Tests**: 100/100 PASSED (`dotnet test Tests/Ai/CanvasDesktop.Ai.Tests.csproj`).
- **Native Build**: 0 errors, 0 warnings across x64 and x86 targets for `CanvasInputHost`, `CanvasInputProbe`, and `CanvasMouseFilter`.
- **Regression Parity**: Confirmed parity with `main` baseline.

## Recommendation & Next Step
The Phase 3 refactoring provides a unified, robust, and clean Z-order coordinator that prevents panel occlusion while upholding all threading, lifecycle, and Win32 invariants.
Proceed to merge `refactor/phase3-layer-coordinator` into `main`.
