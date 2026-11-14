# ARCHITECTURE PLAN: Add Folder Selection to Project Canvas Right-Click Menu

**Target Branch:** `project_folder_select_menu` -> `main`
**Task Identifier:** `project_folder_select_menu`

---

## 1. Problem Statement & User Intent

### A. Missing Feature
- **Current Behavior**: Projects (areas) drawn on the canvas can have an associated file-system folder path (`ProjectFolderPath`), which automatically docks and manages a folder placeholder card within the area. Currently, this folder selection is accessible **only** within the Project Management window (`ZoneWorkspaceWindow`, opened from the toolbar/dock or via "Projects…"), via the `_folderButton` ("Choose project folder…").
- **Requested Behavior**: The project right-click context menu (which appears when right-clicking on a project area header/label on the canvas) must also provide folder selection ("Choose project folder…"), allowing users to directly assign or update the project folder from the canvas without opening the project management window.
- **Workflow & Lifecycle Requirements**: Implement the feature, verify with automated tests under the machine-wide named mutex, review, merge locally into `main`, and clean up.

---

## 2. Codebase Reconnaissance & Root Cause Analysis

### A. Project Context Menu on the Canvas
- In `Source/Overlays/Overview/OverviewManager.WindowMenus.cs`:
  ```csharp
  private bool TryShowAnnotationContextMenu(OverviewOverlay pass, Point location)
  {
      ...
      if (_zones != null)
      {
          var zone = _zones.Items.LastOrDefault(z => !z.IsTemporaryArea && !z.IsMission
              && ZoneLabelLayout.Get(z.Bounds, pointer.View) is { } label
              && label.Contains(pointer.Position.X, pointer.Position.Y));
          if (zone != null)
          {
              _annotationContextMenu?.Dispose();
              var menu = _annotationContextMenu = new ContextMenuStrip();
              var putProjectInBackpack = new ToolStripMenuItem("Put in backpack");
              putProjectInBackpack.Click += (_, _) => pass.BeginInvoke(() =>
              {
                  if (!_disposed) CollectProjectInInventory(zone.Id);
              });
              menu.Items.Add(putProjectInBackpack);

              PInvoke.SetForegroundWindow((HWND)pass.Handle);
              menu.Show(pass, location);
              return true;
          }
      }
      return false;
  }
  ```
  The context menu shown when right-clicking a project header currently contains **only** "Put in backpack".

### B. Existing Folder Selection Logic
- In `Source/Workspace/ZoneUi.cs`:
  ```csharp
  private void ChooseProjectFolder(Guid id)
  {
      var picker = new Microsoft.Win32.OpenFolderDialog { Title = "Choose project folder" };
      var zone = System.Linq.Enumerable.FirstOrDefault(_workspace.Zones.Items, z => z.Id == id);
      if (zone?.ProjectFolderPath is { } path && System.IO.Directory.Exists(path)) picker.InitialDirectory = path;
      if (_window == null || picker.ShowDialog(_window) != true) return;
      bool saved = _workspace.SetProjectFolder(id, picker.FolderName);
      _window.Report(saved ? "Project folder saved. Its placeholder can be moved freely."
          : _workspace.Status ?? "The project folder could not be saved.");
  }
  ```
- In `Source/Workspace/WorkspaceZoneOperations.cs`:
  `WorkspaceService.SetProjectFolder(Guid id, string path)`:
  - Validates that directory exists.
  - Calls `ZoneProjectFolder.TryAssign(Document, _desktop, id, path, ...)` to dock the project folder placeholder item into the zone.
  - Updates cards, saves workspace, notifies zones (`Zones.NotifyDesktopChanged()`), reprojects, and fires `Changed?.Invoke()`.

---

## 3. Target Architecture & Component Design

### 1. `WorkspaceDialogs.cs` (`Source/Workspace/WorkspaceForms.cs`)
- Add a shared static helper:
  ```csharp
  public static bool ChooseProjectFolder(IWin32Window? owner, WorkspaceService workspace, Guid id,
      Action<string>? report = null, Func<string?, string?>? folderPicker = null, Window? wpfOwner = null)
  ```
  - Reads existing `ProjectFolderPath` from `workspace.Zones.Items`. If valid and exists, sets `picker.InitialDirectory`.
  - If a `folderPicker` test hook is provided, invokes it; otherwise creates `Microsoft.Win32.OpenFolderDialog { Title = "Choose project folder" }` and displays it with `wpfOwner` or default foreground ownership.
  - On valid folder selection, calls `workspace.SetProjectFolder(id, selectedPath)`.
  - Invokes `report?.Invoke(...)` with success message ("Project folder saved. Its placeholder can be moved freely.") or error message (`workspace.Status ?? "The project folder could not be saved."`).
  - Returns `bool saved`.

### 2. `OverviewManager.WindowMenus.cs` & `OverviewManager.Zones.cs`
- In `TryShowAnnotationContextMenu`:
  - When a zone header is hit, add:
    ```csharp
    var chooseFolder = new ToolStripMenuItem("Choose project folder…");
    chooseFolder.Click += (_, _) => pass.BeginInvoke(() =>
    {
        if (!_disposed && _placeholderWorkspace != null)
        {
            ChooseProjectFolder(pass, zone.Id);
        }
    });
    menu.Items.Add(chooseFolder);
    menu.Items.Add(new ToolStripSeparator());
    menu.Items.Add(putProjectInBackpack);
    ```
- Implement `ChooseProjectFolder(OverviewOverlay pass, Guid zoneId)` in `OverviewManager`:
  - Calls `WorkspaceDialogs.ChooseProjectFolder(pass, _placeholderWorkspace, zoneId, report: msg => _zoneWindow?.Report(msg), folderPicker: ProjectFolderPickerOverride)`.
  - Expose internal test hook: `internal Func<string?, string?>? ProjectFolderPickerOverride { get; set; }`.

### 3. `ZoneUi.cs`
- Refactor `ZoneUi.ChooseProjectFolder(Guid id)` to delegate to `WorkspaceDialogs.ChooseProjectFolder(null, _workspace, id, report: msg => _window?.Report(msg), wpfOwner: _window)` to eliminate duplicate code.

### 4. Automated Tests (`Tests/OverviewToolIntegrationTests.cs`)
- Add integration test:
  `RightClickingCanvasProjectAreaHeaderOpensContextMenuWithChooseProjectFolder`
  - Creates a `WorkspaceZone` and attaches workspace to `OverviewManager`.
  - Sets `overview.ProjectFolderPickerOverride = _ => tempDir`.
  - Simulates right-click down on zone header location (`ZoneLabelLayout.Get`).
  - Asserts `_annotationContextMenu` is visible and contains:
    - `"Choose project folder…"`
    - `"Put in backpack"`
  - Triggers the click action of `"Choose project folder…"`.
  - Verifies that `zone.ProjectFolderPath` is updated to `tempDir` and the folder card is created in `rig.Workspace.Document.Items`.

---

## 4. Implementation Checklist

- [ ] **Task 1: Shared Project Folder Dialog**
  - Update `Source/Workspace/WorkspaceForms.cs` with `WorkspaceDialogs.ChooseProjectFolder`.
  - Refactor `Source/Workspace/ZoneUi.cs` to reuse `WorkspaceDialogs.ChooseProjectFolder`.
- [ ] **Task 2: Project Right-Click Context Menu**
  - Update `Source/Overlays/Overview/OverviewManager.WindowMenus.cs` to add `"Choose project folder…"` to the zone context menu.
  - Implement `ChooseProjectFolder` and `ProjectFolderPickerOverride` test hook in `OverviewManager`.
- [ ] **Task 3: Automated Tests**
  - Add `RightClickingCanvasProjectAreaHeaderOpensContextMenuWithChooseProjectFolder` in `Tests/OverviewToolIntegrationTests.cs`.
- [ ] **Task 4: Build & Run Serialized Tests**
  - Run `dotnet build CanvasDesktop.slnx -c Release`.
  - Run `Tools\Run-Tests.ps1 -Configuration Release -NoPrompt` under machine-wide named mutex.
- [ ] **Task 5: Review & Audit**
  - Diff inspection and safety review report.
- [ ] **Task 6: Merge to `main` & Cleanup**
  - Commit changes to `project_folder_select_menu`.
  - Merge branch into `main`.
  - Verify `main` build/tests.
  - Clean up worktree and branch.
