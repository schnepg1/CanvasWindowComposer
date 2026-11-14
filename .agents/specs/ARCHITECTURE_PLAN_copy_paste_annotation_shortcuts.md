# Architecture Plan: Annotation Copy/Paste Shortcuts & Double-Click Text Placement

## 1. Executive Summary & Problem Statement

### 1.1 User Request
> "annotation tools, text and shapes, need copy and paste function with shortcuts. double click on canvas should start text placement, merge when finish and clean after yourself housekeeping"

### 1.2 Objective & Scope
This plan defines the architectural blueprint for two primary functional enhancements in `CanvasWindowComposer`:
1. **Annotation Copy, Cut, and Paste with Standard Shortcuts (`Ctrl+C`, `Ctrl+X`, `Ctrl+V`)**:
   - Enable users to duplicate or transfer single or multiple annotations (Text, Rectangle, Ellipse, Arrow) across the canvas.
   - Implement intelligent connector re-mapping: when both source/target and connecting arrows are copied together, their relationship is preserved; when copied in isolation, dangling endpoints are gracefully detached.
   - Support cascading paste offset (`+32, +32` world units per consecutive paste) to prevent overlapping occlusion.
   - Support plain-text fallback: pasting external clipboard text creates a new text card at cursor/viewport location.
   - Support WinForms OS clipboard interoperability with robust error handling for busy/locked clipboard conditions (`ExternalException`).
   - Integrate with UI toolbars, unified dock, and context menus.
2. **Double-Click Canvas Text Placement**:
   - Double-clicking empty canvas space (not hitting windows, mission cards, or existing notes) immediately begins text placement at the cursor's world position.
   - Automatically instantiates a transient text note with active/default styling, focuses `AnnotationTextEditor`, and cleanly discards if cancelled or left empty.
3. **Housekeeping & Merge Governance**:
   - Enforce locked dependency restoration, serialized test execution under the `Global\CanvasWindowComposer.Tests` mutex, and complete worktree cleanup.

---

## 2. Architectural Impact & Subsystem Design

```
+-------------------------------------------------------------------------+
|                              Source/                                    |
|  +-------------------------------------------------------------------+  |
|  |                   Source/Overlays/Overview/                       |  |
|  |  OverviewManager.Pointer.cs       OverviewManager.Keyboard.cs     |  |
|  |  (Double-click empty canvas)      (Ctrl+C, Ctrl+X, Ctrl+V)        |  |
|  |                 |                               |                 |  |
|  |                 v                               v                 |  |
|  |  +-------------------------------------------------------------+  |  |
|  |  |            OverviewManager.Clipboard.cs (NEW)               |  |  |
|  |  |   - CopySelectedAnnotations()                               |  |  |
|  |  |   - CutSelectedAnnotations()                                |  |  |
|  |  |   - PasteAnnotations(Point? screenPoint)                    |  |  |
|  |  |   - OS Clipboard interop & retry (ExternalException)        |  |  |
|  |  +-------------------------------------------------------------+  |  |
|  |         |                                              |          |  |
|  |         v                                              v          |  |
|  |  AnnotationToolbar.cs / UnifiedDockControl.cs   WindowMenus.cs    |  |
|  |  (Cut/Copy/Paste toolbar actions)               (Context menu)    |  |
|  +-------------------------------------------------------------------+  |
+-----------------------------------|-------------------------------------+
                                    | calls pure domain logic
+-----------------------------------|-------------------------------------+
|                             Modules/CanvasView/                         |
|  +-------------------------------------------------------------------+  |
|  |  AnnotationClipboard.cs (NEW)                                     |  |
|  |   - AnnotationClipboardPackage (JSON serialization)               |  |
|  |   - PrepareForPaste(items, desktopId, offsetX, offsetY)           |  |
|  |   - Remap GUIDs, translate points, re-anchor connectors           |  |
|  |   - CreateFromPlainText(text, desktopId, worldPoint)              |  |
|  +-------------------------------------------------------------------+  |
|  |  Annotations.cs                                                   |  |
|  |   - AnnotationDocument.Paste(desktop, items) -> ApplyBatch        |  |
|  +-------------------------------------------------------------------+  |
+-------------------------------------------------------------------------+
```

### 2.1 Pure Domain Layer (`Modules/CanvasView/`)
The `Modules/CanvasView` library targets `net10.0` with no WinForms/WPF/OS dependencies. All serialization, ID remapping, connector topology resolution, and coordinate translations remain in this module:
- **`AnnotationClipboardPackage`**: DTO record containing format version (`Version = 1`) and array of `CanvasAnnotation` records.
- **`AnnotationClipboard`**: Static domain service providing:
  - `Serialize(IEnumerable<CanvasAnnotation> items)`: Serializes annotations into compact JSON format, automatically excluding agent/mission cards (`AgentTaskId.Length > 0`).
  - `TryDeserialize(string? json, out IReadOnlyList<CanvasAnnotation> items)`: Deserializes JSON safely, catching `JsonException`.
  - `PrepareForPaste(items, targetDesktop, offsetX, offsetY)`: Generates fresh GUIDs for every item, translates `A` and `B` coordinates, re-maps `SourceId` and `TargetId` if both ends exist in the clipboard set, and sets dangling endpoints to `null` with `ConnectionPort.Auto`.
  - `CreateFromPlainText(text, desktopId, position, ...)`: Constructs a new `CanvasAnnotation` of kind `Text`, truncating to `AnnotationDocument.MaximumTextLength` (10,000 characters).
- **`AnnotationDocument` (`Annotations.cs`)**:
  - Expose `public bool Paste(Guid desktop, IReadOnlyList<CanvasAnnotation> items)` which wraps `ApplyBatch(Revision, desktop, items)`. This ensures adding multiple pasted annotations is committed as a single atomic history transaction (one undo step).

### 2.2 Presentation & Overlay Layer (`Source/Overlays/Overview/`)
- **`OverviewManager.Clipboard.cs`**:
  New partial class handling Windows Forms OS clipboard interactions:
  - Format name: `CanvasDesktop.Annotations.v1`.
  - When copying, places both `CanvasDesktop.Annotations.v1` (JSON) and plain text (`DataFormats.UnicodeText`) on the clipboard if text notes are included.
  - When pasting, inspects `CanvasDesktop.Annotations.v1` first. If present, deserializes and pastes the annotations with cascading offset. If not present but plain text is available, creates a text note at cursor or camera center.
  - Clipboard locks: Wraps all calls to `Clipboard.SetDataObject` and `Clipboard.GetDataObject` in `try-catch (Exception ex)` with retry parameters (`copy: true, retryTimes: 3, retryDelay: 50`), setting `_annotationError` on failure rather than crashing.
- **`OverviewManager.Keyboard.cs` & `OverviewManager.Annotations.cs`**:
  - Update `HandleAnnotationKey(KeyEventArgs e)`:
    - `Ctrl+C`: calls `CopySelectedAnnotations()`.
    - `Ctrl+X`: calls `CutSelectedAnnotations()`.
    - `Ctrl+V`: calls `PasteAnnotations()`.
  - Ensure key routing checks `_annotationEditor != null || _shapeEditor != null` early so typing `Ctrl+C` or `Ctrl+V` inside a text box does not trigger canvas annotation clipboard actions.
- **`OverviewManager.Pointer.cs` (`HandleDoubleClick`)**:
  - When double-clicking with left mouse button on canvas:
    - If hit test matches an existing annotation: edits note/shape (existing behavior).
    - If hit test matches a window: navigates/zooms to window (existing behavior).
    - If hit test matches empty canvas (no window, no annotation, no mission card): starts text placement at `(wx, wy)` by creating a transient text note and opening `QueueAnnotationTextEdit`.
- **`AnnotationToolbar.cs` & `UnifiedDockControl.cs`**:
  - Add Cut, Copy, and Paste commands to toolbar menus.
  - Add `CanCopy`, `CanCut`, `CanPaste` state flags to `AnnotationToolbarState`.
- **`OverviewManager.WindowMenus.cs`**:
  - In `TryShowAnnotationContextMenu`: add "Cut note" and "Copy note" items to the context menu.

---

## 3. File Change Manifest

| Action | File Path | Subsystem | Description |
| :--- | :--- | :--- | :--- |
| **CREATE** | `Modules/CanvasView/AnnotationClipboard.cs` | Pure Domain | `AnnotationClipboardPackage` and static methods for serialization, remapping, connector topology adjustment, and plain text card creation. |
| **MODIFY** | `Modules/CanvasView/Annotations.cs` | Pure Domain | Add `AnnotationDocument.Paste(...)` helper delegating to `ApplyBatch`. |
| **CREATE** | `Source/Overlays/Overview/OverviewManager.Clipboard.cs` | WPF/WinForms Host | Implement `CopySelectedAnnotations()`, `CutSelectedAnnotations()`, `PasteAnnotations()`, and paste offset tracking. |
| **MODIFY** | `Source/Overlays/Overview/OverviewManager.Keyboard.cs` | WPF/WinForms Host | Route `Ctrl+C`, `Ctrl+X`, `Ctrl+V` to `OverviewManager.Clipboard.cs` via `HandleAnnotationKey`. |
| **MODIFY** | `Source/Overlays/Overview/OverviewManager.Annotations.cs` | WPF/WinForms Host | Add key handling in `HandleAnnotationKey`, reset paste offset on selection changes. |
| **MODIFY** | `Source/Overlays/Overview/OverviewManager.Pointer.cs` | WPF/WinForms Host | Implement empty canvas double-click handler to trigger text placement via `QueueAnnotationTextEdit`. |
| **MODIFY** | `Source/Overlays/Overview/AnnotationToolbar.cs` | WPF/WinForms Host | Add Cut/Copy/Paste events and menu items to the `_more` dropdown; update `AnnotationToolbarState`. |
| **MODIFY** | `Source/Overlays/Overview/UnifiedDockControl.cs` | WPF UI | Add Cut/Copy/Paste menu items and events to the unified dock popup menu. |
| **MODIFY** | `Source/Overlays/Overview/OverviewManager.WindowMenus.cs` | WPF/WinForms Host | Add Cut/Copy/Paste menu entries to the annotation context menu. |
| **CREATE** | `Tests/AnnotationClipboardTests.cs` | Test Suite | Unit tests for clipboard serialization, GUID remapping, connector preservation/detachment, cascading offset, and text fallback. |
| **MODIFY** | `Tests/CanvasAnnotationTests.cs` | Test Suite | Unit tests for `AnnotationDocument.Paste` undo/redo atomicity and item count limits. |

---

## 4. Atomic Task Breakdown

### Task 1: Core Domain Clipboard Data Contract & Remapping Engine
- **Target File**: `Modules/CanvasView/AnnotationClipboard.cs` (NEW)
- **Signatures**:
  ```csharp
  public sealed class AnnotationClipboardPackage
  {
      public int Version { get; set; } = 1;
      public CanvasAnnotation[] Items { get; set; } = [];
  }

  public static class AnnotationClipboard
  {
      public const string DataFormat = "CanvasDesktop.Annotations.v1";
      public const double DefaultPasteOffset = 32.0;

      public static string Serialize(IEnumerable<CanvasAnnotation> items);
      public static bool TryDeserialize(string? json, out IReadOnlyList<CanvasAnnotation> items);
      public static IReadOnlyList<CanvasAnnotation> PrepareForPaste(
          IReadOnlyList<CanvasAnnotation> sourceItems,
          Guid targetDesktop,
          double offsetX,
          double offsetY);
      public static CanvasAnnotation CreateFromPlainText(
          string text,
          Guid desktopId,
          CanvasPoint position,
          string color = "#FBBF24",
          double stroke = 3,
          double fontSize = AnnotationDocument.DefaultFontSize,
          AnnotationTextAlign textAlign = AnnotationTextAlign.Left,
          AnnotationTextEmphasis emphasis = AnnotationTextEmphasis.Normal,
          AnnotationCardFill cardFill = AnnotationCardFill.Solid);
  }
  ```
- **Requirements & Invariants**:
  - Filter out any annotation with `AgentTaskId.Length > 0`.
  - For every item in `sourceItems`, allocate a new `Guid.NewGuid()`.
  - Re-map `item.SourceId` and `item.TargetId` if the referenced ID is present in the copied set.
  - If a referenced ID is NOT in the copied set, reset `SourceId = null` (or `TargetId = null`) and set port to `ConnectionPort.Auto`.
  - Translate positions: `A = A + (offsetX, offsetY)`, `B = B + (offsetX, offsetY)`.
  - Wrap coordinate translation within valid canvas boundary limits (`Math.Abs(coord) <= 10,000,000`).
- **Verification**: Unit tests in `Tests/AnnotationClipboardTests.cs`.

---

### Task 2: Atomic Batch Paste in `AnnotationDocument`
- **Target File**: `Modules/CanvasView/Annotations.cs`
- **Signatures**:
  ```csharp
  public bool Paste(Guid desktop, IReadOnlyList<CanvasAnnotation> items)
  {
      if (items.Count == 0) return false;
      if (_items.Count + items.Count > MaximumItems)
          throw new InvalidOperationException($"Cannot paste: canvas exceeds maximum limit of {MaximumItems} annotations.");
      return ApplyBatch(Revision, desktop, items);
  }
  ```
- **Requirements & Invariants**:
  - Validate that `_items.Count + items.Count <= MaximumItems`.
  - Use `ApplyBatch` with `expectedRevision == Revision` to guarantee single-step undo and redo.
  - Trigger `NotifyChanged(desktop)`.
- **Verification**: Unit tests in `Tests/CanvasAnnotationTests.cs`.

---

### Task 3: Unit Tests for Clipboard Serialization, Remapping & Batch Paste
- **Target File**: `Tests/AnnotationClipboardTests.cs` (NEW)
- **Test Cases**:
  1. `SerializeAndDeserialize_PreservesAllAnnotationProperties`: Round-trip text, shape, and arrow properties.
  2. `PrepareForPaste_GeneratesNewGuids_AndAppliesOffset`: Verifies translated coordinates and distinct GUIDs.
  3. `PrepareForPaste_ConnectedArrowAndCards_PreservesConnections`: When Card A, Card B, and connecting Arrow are copied together, Arrow.SourceId and Arrow.TargetId reference the new GUIDs of A and B.
  4. `PrepareForPaste_IsolatedArrow_DetachesMissingEndpoints`: When only an Arrow is copied, `SourceId` and `TargetId` become `null`.
  5. `CreateFromPlainText_ProducesValidTextAnnotation`: Validates text note creation, default dimensions, and bounds check.
  6. `AnnotationDocument_Paste_CreatesSingleUndoStep`: Pasting 3 items creates exactly one undo entry; undo removes all 3; redo restores all 3.
  7. `AnnotationDocument_Paste_ExceedingMaximumItems_Throws`: Fails if total count exceeds 2,000.
- **Verification**: Run `powershell -NoProfile -ExecutionPolicy Bypass -File Tools\Run-Tests.ps1 -Configuration Release -Filter "FullyQualifiedName~AnnotationClipboardTests" -AllowRunningApp`.

---

### Task 4: Double-Click Text Placement on Canvas
- **Target File**: `Source/Overlays/Overview/OverviewManager.Pointer.cs`
- **Implementation Logic**:
  - In `HandleDoubleClick(OverviewOverlay pass, MouseEventArgs e)`:
    ```csharp
    int vx = e.X + pass.OriginX;
    int vy = e.Y + pass.OriginY;
    var (wx, wy) = _camera.WorldFromVirtual(vx, vy);

    int hit = HitOverviewWindow(pass, e.X, e.Y, wx, wy);
    if (hit >= 0)
    {
        var entry = _windows.Windows[hit];
        if (!NavigateToWindow(entry.HWnd)) GoToWindow(entry.HWnd, entry.World);
        return;
    }

    // Double-click on empty canvas starts text placement
    StartTextPlacementAt(pass, new Point(e.X, e.Y), new CanvasPoint(wx, wy));
    ```
  - Implement `StartTextPlacementAt`:
    ```csharp
    private void StartTextPlacementAt(OverviewOverlay pass, Point screenPoint, CanvasPoint worldPoint)
    {
        CancelInteraction();
        var desktop = _annotationDesktop();
        var tool = _annotationTool;
        var textItem = new CanvasAnnotation(
            Id: Guid.NewGuid(),
            DesktopId: desktop,
            Kind: AnnotationKind.Text,
            A: worldPoint,
            B: new CanvasPoint(
                worldPoint.X + AnnotationCanvasTool.DefaultTextCardWidth,
                worldPoint.Y + AnnotationCanvasTool.DefaultTextCardHeight),
            Color: tool?.Color ?? "#FBBF24",
            Stroke: tool?.Stroke ?? 3,
            FontSize: tool?.FontSize ?? AnnotationDocument.DefaultFontSize,
            TextAlign: tool?.TextAlign ?? AnnotationTextAlign.Left,
            Emphasis: tool?.Emphasis ?? AnnotationTextEmphasis.Normal,
            CardFill: tool?.CardFill ?? AnnotationCardFill.Solid);

        tool?.SetMode(AnnotationMode.Select);
        QueueAnnotationTextEdit(textItem, new Point(screenPoint.X + pass.OriginX, screenPoint.Y + pass.OriginY));
    }
    ```
- **Requirements & Invariants**:
  - If user types text and confirms, `Finished` event commits item via `_annotationDocument.Put(...)` and selects it.
  - If user cancels (Esc) or leaves text blank, the item is discarded and no empty note is committed.
  - Existing double-click behaviors (zooming into window, editing existing note) remain unaffected.
- **Verification**: Integration test simulating double-click on empty canvas and verifying `QueueAnnotationTextEdit` invocation.

---

### Task 5: OS Clipboard Service & Shortcuts in `OverviewManager`
- **Target Files**:
  - `Source/Overlays/Overview/OverviewManager.Clipboard.cs` (NEW)
  - `Source/Overlays/Overview/OverviewManager.Annotations.cs`
  - `Source/Overlays/Overview/OverviewManager.Keyboard.cs`
- **Implementation**:
  - Manage `int _annotationPasteOffsetCount`:
    - Increment on each consecutive paste.
    - Reset to 0 on `CopySelectedAnnotations()`, `CutSelectedAnnotations()`, or when selection changes.
    - Wrap offset count if it exceeds 10 (`_annotationPasteOffsetCount % 10`) to prevent drift across viewports.
  - `CopySelectedAnnotations()`:
    - Gathers selected annotations (`Selection.Ids` or `_annotationTool.Selected`).
    - Serializes via `AnnotationClipboard.Serialize`.
    - Creates `DataObject` with `CanvasDesktop.Annotations.v1` and plain text.
    - Calls `Clipboard.SetDataObject(dataObject, copy: true, retryTimes: 3, retryDelay: 50)`.
    - Catches `Exception`, reports to `_annotationError`.
  - `CutSelectedAnnotations()`:
    - Calls `CopySelectedAnnotations()`. If true, calls `DeleteAnnotation()`.
  - `PasteAnnotations(Point? screenPoint = null)`:
    - Inspects `Clipboard.GetDataObject()`.
    - If `CanvasDesktop.Annotations.v1` is present: deserializes, prepares items with cascading offset, calls `_annotationDocument.Paste(desktop, items)`, updates `Selection` and `_annotationTool.Selected`.
    - Else if `DataFormats.Text` is present: extracts text, constructs text note via `AnnotationClipboard.CreateFromPlainText`, fits text, pastes into document, and selects it.
  - In `HandleAnnotationKey(KeyEventArgs e)`:
    - Route `Ctrl+C` -> `CopySelectedAnnotations()`.
    - Route `Ctrl+X` -> `CutSelectedAnnotations()`.
    - Route `Ctrl+V` -> `PasteAnnotations()`.
    - Mark `e.Handled = true; e.SuppressKeyPress = true;`.
- **Verification**: Unit and integration tests in `Tests/AnnotationOverviewIntegrationTests.cs`.

---

### Task 6: Toolbar, Dock & Context Menu Integration
- **Target Files**:
  - `Source/Overlays/Overview/AnnotationToolbar.cs`
  - `Source/Overlays/Overview/UnifiedDockControl.cs`
  - `Source/Overlays/Overview/OverviewManager.WindowMenus.cs`
- **Changes**:
  - `AnnotationToolbar.cs`:
    - Add events: `public event Action? CutRequested, CopyRequested, PasteRequested;`.
    - In `_more` dropdown, add:
      - `Cut annotation` (`Ctrl+X`)
      - `Copy annotation` (`Ctrl+C`)
      - `Paste annotation` (`Ctrl+V`)
    - In `UpdateState(AnnotationToolbarState state)`: enable/disable Cut and Copy depending on `state.HasSelectedAnnotations || state.Selected != null`.
  - `UnifiedDockControl.cs`:
    - Add matching menu items and event dispatchers.
  - `OverviewManager.WindowMenus.cs`:
    - In `TryShowAnnotationContextMenu`, add "Cut note" and "Copy note" before "Delete note".
- **Verification**: Toolbar state unit tests in `Tests/AnnotationToolbarTests.cs`.

---

### Task 7: Comprehensive Integration & Regression Testing
- **Target Files**:
  - `Tests/AnnotationOverviewIntegrationTests.cs`
  - `Tests/AnnotationToolbarTests.cs`
- **Verification**:
  - Run full test suite via `Tools\Run-Tests.ps1 -Configuration Release`.

---

## 5. Test & Verification Strategy

### 5.1 Test Categorization & Isolation Rules
Following `AGENTS.md`:
1. **Non-Native Unit & Domain Tests (`Category!=Native*`)**:
   - `AnnotationClipboardTests.cs`: pure logic tests for JSON serialization, ID remapping, connector retention/detachment, and bounds calculation.
   - `CanvasAnnotationTests.cs`: document paste transactions, history order, and maximum item limit enforcement.
   - `AnnotationToolbarTests.cs`: menu states, shortcut labels, and event invocation.
   - Fast, in-memory, run with `-AllowRunningApp`.
2. **Native Desktop Integration Tests**:
   - Serialized under the machine-wide named mutex: `Global\CanvasWindowComposer.Tests`.
   - Preflight check requires `CanvasDesktop.exe` to be closed.

### 5.2 Verification Execution Commands
```powershell
# 1. Restore locked dependencies
dotnet restore CanvasDesktop.slnx --locked-mode

# 2. Build Release configuration
dotnet build CanvasDesktop.slnx -c Release --no-restore

# 3. Execute pure domain tests (can run with -AllowRunningApp)
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\Run-Tests.ps1 -Configuration Release -Filter "FullyQualifiedName~AnnotationClipboardTests|FullyQualifiedName~CanvasAnnotationTests" -AllowRunningApp

# 4. Execute full non-native and native test suites under named mutex
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\Run-Tests.ps1 -Configuration Release -NoPrompt
```

---

## 6. Risk Analysis, Edge Cases & Failure Modes

| Risk / Edge Case | Cause / Trigger | Architectural Mitigation |
| :--- | :--- | :--- |
| **Clipboard Lock Contention (`ExternalException`)** | Another background application (e.g. Remote Desktop, clipboard history manager) locks the Windows clipboard. | Wrap all OS `Clipboard` operations in `try-catch` blocks. Use `Clipboard.SetDataObject(..., copy: true, retryTimes: 3, retryDelay: 50)`. Surface friendly error message in `_annotationError` without throwing or crashing. |
| **Consecutive Paste Coordinate Explosion** | User presses `Ctrl+V` multiple times in succession. | Apply cascading offset of `+32, +32` world units per paste. Wrap counter at 10 (`_annotationPasteOffsetCount % 10`). Clamp coordinates using `Math.Clamp` to stay strictly within valid canvas limits (`±10,000,000`). |
| **Dangling Arrow Connectors** | User copies an arrow without copying its source or target cards. | `AnnotationClipboard.PrepareForPaste` checks whether `SourceId` and `TargetId` exist in the copied GUID mapping table. If absent, resets `SourceId = null` and `TargetId = null`, setting `ConnectionPort.Auto`. |
| **Interference with Text Box Editing** | User presses `Ctrl+C` or `Ctrl+V` while editing text in `AnnotationTextEditor`. | `HandleAnnotationKey` explicitly checks `if (_annotationEditor != null \|\| _shapeEditor != null) return true;` before checking `Ctrl+C`/`Ctrl+V`. This leaves standard text box shortcut handling to the native WinForms TextBox. |
| **Empty Text Card Cancellation** | User double-clicks canvas, opening text editor, but presses `Escape` or leaves text blank. | The transient `CanvasAnnotation` is not committed to `_annotationDocument` prior to editor completion. If cancelled or blank, `AnnotationTextEditor.Finished` discards it without touching the document or recording an undo step. |
| **Exceeding Canvas Item Limit** | Pasting items when canvas is near 2,000 items. | `AnnotationDocument.Paste` checks `_items.Count + items.Count <= MaximumItems`. If breached, rejects the paste cleanly and assigns an error message to `_annotationError`. |
| **Cross-Desktop Pasting** | User copies annotations on Desktop A, switches to Desktop B, and pastes. | `AnnotationClipboard.PrepareForPaste` takes the current `targetDesktop` (`_annotationDesktop()`), guaranteeing that all pasted annotations belong exclusively to the active virtual desktop. |

---

## 7. Housekeeping & Merge Protocol

In strict accordance with `AGENTS.md` and `.agents/teams/workflow.yaml`:
1. **Worktree Isolation**:
   - The Programmer agent must work in an isolated worktree branch:
     ```powershell
     git worktree add "..\CanvasWindowComposer-copy-paste-annotations" -b "agent/copy-paste-annotations" HEAD
     ```
2. **Reviewer Gate**:
   - Reviewer audits diff against this architecture plan, verifying thread safety, dispatcher usage, and lock integrity.
   - Requires verdict `APPROVED` before merger proceeds.
3. **Merger Execution**:
   - Verify target branch freshness (`git fetch origin`).
   - Merge `agent/copy-paste-annotations` into target branch (`copy_paste_annotation_shortcuts` / `schnepg1`).
   - Run post-merge regression tests under the `Global\CanvasWindowComposer.Tests` mutex.
4. **Housekeeping & Process Lock Cleanliness**:
   - Ensure all `dotnet`, `testhost`, or `CanvasDesktop` processes are terminated.
   - Cleanly remove worktree:
     ```powershell
     git worktree remove "..\CanvasWindowComposer-copy-paste-annotations"
     ```
   - Delete temporary agent branch.
