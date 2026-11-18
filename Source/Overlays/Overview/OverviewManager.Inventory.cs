using System;
using System.Linq;
using System.Windows.Forms;

namespace CanvasDesktop;

internal sealed partial class OverviewManager
{
    private BackpackWindow? _backpack;
    private InventoryFlightWindow? _inventoryFlight;
    private bool _inventoryAttached;
    internal BackpackWindow? InventoryView => _backpack;

    private void AttachInventory(WorkspaceService workspace)
    {
        workspace.ArrivalViewportProvider = InventoryViewport;
        workspace.InventoryCollected += OnInventoryCollected;
        workspace.InventoryRequested += OnInventoryRequested;
        if (!_inventoryAttached) { BeforeModeChanged += CancelDrawerDrags; AfterModeChanged += OnInventoryModeChanged; _inventoryAttached = true; }
        RefreshInventory();
    }

    private ArrivalViewport InventoryViewport()
        => new(CurrentMode == OverviewMode.Hidden ? _mainCanvas.Camera : ViewCamera, _screens.AllWorkingAreas,
            (_backpack?.IsVisible == true ? new[] { _backpack.ScreenBounds } : Array.Empty<ScreenRect>())
                .Concat(_temporaryDrawer?.IsVisible == true ? new[] { _temporaryDrawer.ScreenBounds } : Array.Empty<ScreenRect>()).ToArray());

    private BackpackWindow EnsureBackpack()
    {
        if (_backpack != null) return _backpack;
        _backpack = new(_screens.PrimaryWorkingArea) { ClampBelowTaskbar = () => ShouldClampCanvasPanelsBelowTaskbar };
        _backpack.SetCanvasOwner(CanvasPanelOwner);
        _backpack.CloseRequested += () => SetInventoryVisible(false);
        _backpack.Panel.OpenRequested += open => SetInventoryVisible(open, activate: open);
        _backpack.Panel.HelpDismissedChanged += dismissed => _placeholderWorkspace?.SetInventoryHelpDismissed(dismissed);
        _backpack.Panel.PlaceRequested += id => DropInventory(id, null);
        _backpack.Panel.DropRequested += (id, point) => DropInventory(id, point);
        _backpack.Panel.KeepRequested += id => _placeholderWorkspace?.CollectInInventory(id);
        _backpack.Panel.FolderRequested += id =>
        {
            if (_disposed || _placeholderWorkspace == null) return;
            WorkspaceDialogs.ChooseProjectFolder(null, _placeholderWorkspace, id,
                report: msg => _backpack?.Panel.SetHint(msg),
                folderPicker: ProjectFolderPickerOverride,
                wpfOwner: _backpack);
            RefreshInventory();
        };
        _backpack.Panel.OpenItemRequested += id => _placeholderWorkspace?.Open(id);
        _backpack.Panel.ForgetRequested += id =>
        {
            if (_annotationDocument?.Items.FirstOrDefault(a => a.Id == id) is { } note)
            {
                _annotationDocument.Delete(note.DesktopId, id);
                _annotationStore?.Save();
                RefreshInventory();
                if (CurrentMode != OverviewMode.Hidden) ReconcileVisuals();
                return;
            }
            if (_placeholderWorkspace?.Document.Zones.FirstOrDefault(z => z.Id == id && z.InInventory) is { } zone)
            {
                zone.InInventory = false;
                _placeholderWorkspace.SaveNow();
                RefreshInventory();
                if (CurrentMode != OverviewMode.Hidden) ReconcileVisuals();
                return;
            }
            _placeholderWorkspace?.Forget(id);
        };
        AttachDrawerDragFeedback(_backpack.Panel.Drag, inventory: true);
        return _backpack;
    }

    public void ToggleInventory()
        => SetInventoryVisible(!(_placeholderWorkspace?.Document.InventoryOpen ?? false), activate: true);

    private void SetInventoryVisible(bool open, Guid? selected = null, bool activate = false, bool warn = false)
    {
        if (_disposed || _placeholderWorkspace == null) return;
        if (open) CancelInteraction();
        _placeholderWorkspace.SetInventoryOpen(open);
        EnsureBackpack(); RefreshInventory(); _backpack!.Panel.Highlight(selected);
        _backpack.Present(open, CurrentMode != OverviewMode.Hidden, _screens.PrimaryWorkingArea, activate && open);
        if (open && warn && selected is { } targetId) _backpack.Panel.WarnUnplacedItem(targetId);
        if (!open && CurrentMode == OverviewMode.Zooming) FocusOverview();
    }

    private void OnInventoryRequested(Guid? id) => SetInventoryVisible(true, id, activate: true, warn: id != null);
    private void OnInventoryModeChanged(OverviewMode from, OverviewMode to) => RefreshInventory();

    private void RefreshInventory()
    {
        if (_disposed || _placeholderWorkspace == null) return;
        bool open = _placeholderWorkspace.Document.InventoryOpen;
        if (_backpack == null && !open && CurrentMode == OverviewMode.Hidden) return;
        var backpack = EnsureBackpack();
        backpack.SetCanvasOwner(CanvasPanelOwner);
        var workspaceItems = _placeholderWorkspace.InventoryItems;
        var noteItems = _annotationDocument?.Items
            .Where(a => a.DesktopId == _annotationDesktop() && a.InInventory && a.Kind == AnnotationKind.Text)
            .Select(NoteToItemView).ToArray() ?? Array.Empty<WorkspaceItemView>();
        var projectItems = _placeholderWorkspace.Document.Zones
            .Where(z => z.DesktopId == _annotationDesktop() && z.InInventory && !z.IsTemporaryArea)
            .Select(ZoneToItemView).ToArray();
        backpack.Panel.UpdateItems(workspaceItems.Concat(noteItems).Concat(projectItems).ToArray());
        backpack.Panel.SetHelpDismissed(_placeholderWorkspace.Document.InventoryHelpDismissed);
        backpack.Panel.SetHint(_placeholderWorkspace.InventoryStatus);
        backpack.Present(open, CurrentMode != OverviewMode.Hidden, _screens.PrimaryWorkingArea);
    }

    private static WorkspaceItemView NoteToItemView(CanvasAnnotation note)
    {
        bool isAgent = MissionAgentCard.IsCard(note);
        string text = note.Text.Trim();
        string firstLine = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        string title = isAgent ? new MissionAgentCard(note).Name : (firstLine.Length > 0 ? firstLine : "Text note");
        var bounds = new WorldRect
        {
            X = Math.Min(note.A.X, note.B.X),
            Y = Math.Min(note.A.Y, note.B.Y),
            W = Math.Max(AnnotationCanvasTool.MinimumTextCardWidth, Math.Abs(note.B.X - note.A.X)),
            H = Math.Max(AnnotationCanvasTool.MinimumTextCardHeight, Math.Abs(note.B.Y - note.A.Y)),
            InInventory = true
        };
        return new WorkspaceItemView(
            note.Id,
            title,
            "",
            "",
            false,
            bounds,
            isAgent ? "Agent" : "Note",
            Name: isAgent ? (note.AgentWorkingFolder ?? note.AgentRole) : (text.Length > 0 ? text : title),
            IsFolder: false,
            ChromeProfilePath: "",
            LaunchMessage: null,
            WebResource: null,
            ExcludeFromAutomaticMinimization: false,
            Temporary: false
        );
    }

    private WorkspaceItemView ZoneToItemView(WorkspaceZone zone)
    {
        int members = _placeholderWorkspace?.Zones.Members(zone.Id).Count(m => m.Assigned) ?? 0;
        string detail = !string.IsNullOrWhiteSpace(zone.ProjectFolderPath)
            ? zone.ProjectFolderPath
            : $"{members} item{(members == 1 ? "" : "s")}";
        return new WorkspaceItemView(
            zone.Id,
            zone.Name,
            zone.Color,
            zone.ProjectFolderPath ?? "",
            false,
            zone.Bounds with { InInventory = true },
            "Project",
            Name: detail,
            IsFolder: true,
            ChromeProfilePath: "",
            LaunchMessage: null,
            WebResource: null,
            ExcludeFromAutomaticMinimization: false,
            Temporary: false
        );
    }

    internal bool CollectNoteInInventory(Guid id)
    {
        if (_disposed || _annotationDocument == null) return false;
        if (_annotationDocument.Items.FirstOrDefault(a => a.Id == id && a.DesktopId == _annotationDesktop()
            && a.Kind == AnnotationKind.Text) is not { } note)
            return false;
        var source = new WorldRect
        {
            X = Math.Min(note.A.X, note.B.X),
            Y = Math.Min(note.A.Y, note.B.Y),
            W = Math.Max(AnnotationCanvasTool.MinimumTextCardWidth, Math.Abs(note.B.X - note.A.X)),
            H = Math.Max(AnnotationCanvasTool.MinimumTextCardHeight, Math.Abs(note.B.Y - note.A.Y))
        };
        var updated = note with { InInventory = true };
        _annotationDocument.Put(updated);
        _annotationStore?.Save();
        Selection.Clear();
        _annotationTool?.ClearSelection();

        var backpack = EnsureBackpack();
        backpack.SetCanvasOwner(CanvasPanelOwner);
        RefreshInventory();
        backpack.NotifyArrival();
        backpack.Panel.Highlight(id);

        _inventoryFlight ??= new InventoryFlightWindow();
        var view = CurrentMode == OverviewMode.Hidden ? _mainCanvas.Camera : ViewCamera;
        bool isAgent = MissionAgentCard.IsCard(note);
        string text = note.Text.Trim();
        string firstLine = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        string title = isAgent ? new MissionAgentCard(note).Name : (firstLine.Length > 0 ? firstLine : "Text note");
        _inventoryFlight.Fly(title, view.Project(source), backpack.ScreenBounds, _screens.PrimaryBounds,
            _appConfig.ReduceStartupMotion || !System.Windows.SystemParameters.ClientAreaAnimation);

        if (CurrentMode != OverviewMode.Hidden) ReconcileVisuals();
        return true;
    }

    internal bool CollectProjectInInventory(Guid id)
    {
        if (_disposed || _placeholderWorkspace == null) return false;
        var zone = _placeholderWorkspace.Document.Zones.FirstOrDefault(z => z.Id == id && z.DesktopId == _annotationDesktop() && !z.IsTemporaryArea);
        if (zone == null) return false;
        zone.InInventory = true;
        _placeholderWorkspace.SaveNow();
        Selection.Clear();

        var backpack = EnsureBackpack();
        backpack.SetCanvasOwner(CanvasPanelOwner);
        RefreshInventory();
        backpack.NotifyArrival();
        backpack.Panel.Highlight(id);

        _inventoryFlight ??= new InventoryFlightWindow();
        var view = CurrentMode == OverviewMode.Hidden ? _mainCanvas.Camera : ViewCamera;
        _inventoryFlight.Fly(zone.Name, view.Project(zone.Bounds), backpack.ScreenBounds, _screens.PrimaryBounds,
            _appConfig.ReduceStartupMotion || !System.Windows.SystemParameters.ClientAreaAnimation);

        if (CurrentMode != OverviewMode.Hidden) ReconcileVisuals();
        return true;
    }

    private void OnInventoryCollected(InventoryCollection collection)
    {
        if (_disposed || _placeholderWorkspace == null) return;
        bool focusedOverflow = collection.Automatic && collection.SourceHandle != IntPtr.Zero
            && _win32.GetForegroundWindow() == collection.SourceHandle;
        if (focusedOverflow) SetInventoryVisible(true, collection.Id, activate: true);
        RefreshInventory();
        var backpack = EnsureBackpack();
        backpack.SetCanvasOwner(CanvasPanelOwner);
        backpack.NotifyArrival(); backpack.Panel.Highlight(collection.Id);
        if (!collection.Automatic) Selection.Clear();
        _inventoryFlight ??= new InventoryFlightWindow();
        var view = CurrentMode == OverviewMode.Hidden ? _mainCanvas.Camera : ViewCamera;
        _inventoryFlight.Fly(collection.Title, view.Project(collection.Source), backpack.ScreenBounds, _screens.PrimaryBounds,
            _appConfig.ReduceStartupMotion || !System.Windows.SystemParameters.ClientAreaAnimation);
        if (CurrentMode != OverviewMode.Hidden) RefreshOverviewWindows();
    }

    private bool HandleInventoryKey(KeyEventArgs e)
    {
        if (e.KeyCode != Keys.I || e.Modifiers != Keys.None || _windowInteraction.Active || _tools.Active) return false;
        ToggleInventory(); e.Handled = e.SuppressKeyPress = true; return true;
    }

    private void DropInventory(Guid id, System.Windows.Point? point)
    {
        if (_placeholderWorkspace == null) return;
        if (point is { } screen && TryCrossDrawerDrop(id, inventory: true, screen)) return;
        if (_annotationDocument?.Items.FirstOrDefault(a => a.Id == id && a.Kind == AnnotationKind.Text) is { } note)
        {
            if (point is { } dropPt)
            {
                var world = _camera.WorldFromVirtual((int)dropPt.X, (int)dropPt.Y);
                var targetAgent = MissionCards().LastOrDefault(c => CanvasSelection.Contains(c.Bounds, new CanvasPoint(world.x, world.y)));
                if (targetAgent != null && TryPromptAgentWithTextCard(note, targetAgent))
                {
                    RefreshInventory();
                    return;
                }
            }
            if (!TryDrawerDropTarget(id, true, point, out var view, out var x, out var y)) return;
            var noteBounds = new WorldRect
            {
                X = Math.Min(note.A.X, note.B.X),
                Y = Math.Min(note.A.Y, note.B.Y),
                W = Math.Max(AnnotationCanvasTool.MinimumTextCardWidth, Math.Abs(note.B.X - note.A.X)),
                H = Math.Max(AnnotationCanvasTool.MinimumTextCardHeight, Math.Abs(note.B.Y - note.A.Y)),
                InInventory = true
            };
            if (!NearbyWindowPlacement.TryPlace(noteBounds, view, _placeholderWorkspace.InventoryPlacementObstacles(id), out var placed, x, y))
            {
                _backpack?.Panel.SetHint("No clear landing space here. Pan to some open canvas and try again.");
                return;
            }
            var updated = note with
            {
                A = new(placed.X, placed.Y),
                B = new(placed.X + placed.W, placed.Y + placed.H),
                InInventory = false
            };
            _annotationDocument.Put(updated);
            _annotationStore?.Save();
            _backpack?.Panel.SetHint(MissionAgentCard.IsCard(note)
                ? "Unpacked. This agent card is now kept on the canvas."
                : "Unpacked. This note is now kept on the canvas.");
            RefreshInventory();
            if (CurrentMode != OverviewMode.Hidden) ReconcileVisuals();
            return;
        }
        if (_placeholderWorkspace != null && _placeholderWorkspace.Document.Zones.FirstOrDefault(z => z.Id == id && z.InInventory) is { } projectZone)
        {
            if (!TryDrawerDropTarget(id, true, point, out var view, out var targetX, out var targetY)) return;
            var viewportScreen = view.Screens.FirstOrDefault();
            double screenW = viewportScreen.Width > 0 ? viewportScreen.Width : 1920;
            double screenH = viewportScreen.Height > 0 ? viewportScreen.Height : 1080;
            double destX = targetX ?? (view.Camera.X + (screenW / view.Camera.Zoom - projectZone.Bounds.W) / 2);
            double destY = targetY ?? (view.Camera.Y + (screenH / view.Camera.Zoom - projectZone.Bounds.H) / 2);
            double dx = destX - projectZone.Bounds.X;
            double dy = destY - projectZone.Bounds.Y;

            if (_annotationDocument != null)
            {
                var all = SelectionItems();
                var group = CanvasSelection.Expand([new(CanvasItemKind.Project, projectZone.Id)], all);
                MoveSelection(new(all.Where(i => group.Contains(i.Key)).ToArray(), dx, dy));
            }
            else
            {
                _placeholderWorkspace.MoveZone(projectZone.Id, dx, dy);
            }

            projectZone.InInventory = false;
            _placeholderWorkspace.SaveNow();
            _backpack?.Panel.SetHint("Unpacked. This project is now kept on the canvas.");
            RefreshInventory();
            RefreshZoneLayout();
            if (CurrentMode != OverviewMode.Hidden) ReconcileVisuals();
            return;
        }
        if (_placeholderWorkspace == null || !TryDrawerDropTarget(id, true, point, out _, out var wx, out var wy)) return;
        bool dropped = _placeholderWorkspace.DropFromInventory(id, wx, wy);
        _backpack?.Panel.SetHint(dropped ? "Unpacked. This place is now kept on the canvas." : _placeholderWorkspace.Status);
        RefreshOverviewWindows();
        if (CurrentMode != OverviewMode.Hidden) ReconcileVisuals();
    }

    private bool TryCollectInventoryDrop(OverviewOverlay pass, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || _placeholderWorkspace == null || _backpack == null
            || !_backpack.ContainsScreenPoint(e.X + pass.OriginX, e.Y + pass.OriginY)) return false;
        Guid[] ids;
        Guid[] noteIds = Array.Empty<Guid>();
        Guid[] projectIds = Array.Empty<Guid>();
        IntPtr[] sessionHandles = Array.Empty<IntPtr>();
        IntPtr handle = IntPtr.Zero;
        if (_tools.Kind == CanvasInteractionKind.Window && _windowDragMoved && _dragIndex >= 0 && _dragIndex < _windows.Count)
        {
            handle = _windows.Windows[_dragIndex].HWnd; ids = Array.Empty<Guid>();
        }
        else if (_tools.Kind == CanvasInteractionKind.Tool)
        {
            if (_selectionTool is { Ghosts.Count: > 0 } selection && (selection.Offset.X != 0 || selection.Offset.Y != 0))
            {
                ids = selection.Ghosts.Where(i => i.Key.Kind == CanvasItemKind.Window)
                    .Select(i => i.Key.Id).Where(id => _placeholderWorkspace.Document.Items.Any(i => i.Id == id)).ToArray();
                sessionHandles = selection.Ghosts.Where(i => i.Key.Kind == CanvasItemKind.Window)
                    .Select(i => _selectionWindows.FirstOrDefault(p => p.Value.Id == i.Key.Id).Key)
                    .Where(h => h != IntPtr.Zero).ToArray();
                noteIds = selection.Ghosts.Where(i => i.Key.Kind == CanvasItemKind.Annotation)
                    .Where(i => i.Annotation is { Kind: AnnotationKind.Text })
                    .Select(i => i.Key.Id).ToArray();
                projectIds = selection.Ghosts.Where(i => i.Key.Kind == CanvasItemKind.Project)
                    .Select(i => i.Key.Id).ToArray();
            }
            else if (_zoneTool.Gesture.Kind == ZoneGestureKind.Move && _zoneTool.Gesture.MovingId is { } movingZoneId)
            {
                ids = Array.Empty<Guid>();
                projectIds = [movingZoneId];
            }
            else return false;

            if (ids.Length == 0 && sessionHandles.Length == 0 && noteIds.Length == 0 && projectIds.Length == 0) return false;
        }
        else return false;
        CancelInteraction();
        if (handle != IntPtr.Zero)
        {
            var id = _placeholderWorkspace.CardItem(handle) ?? _placeholderWorkspace.GetWindowContextItem(handle);
            if (id.HasValue) ids = new[] { id.Value };
        }
        foreach (var sessionHandle in sessionHandles)
            if (_placeholderWorkspace.GetWindowContextItem(sessionHandle) is { } itemId) ids = ids.Append(itemId).ToArray();
        foreach (var id in ids) _placeholderWorkspace.CollectInInventory(id);
        foreach (var id in noteIds) CollectNoteInInventory(id);
        foreach (var id in projectIds) CollectProjectInInventory(id);
        _backpack.Panel.SetDropTarget(false);
        Selection.Clear();
        RefreshOverviewWindows(); ReconcileVisuals();
        return true;
    }

    private void UpdateInventoryDropHover(OverviewOverlay pass, MouseEventArgs e)
    {
        _backpack?.Panel.SetDropTarget((_tools.Kind == CanvasInteractionKind.Window || _selectionTool?.Ghosts.Count > 0 || _zoneTool?.Gesture.Kind == ZoneGestureKind.Move)
            && _backpack.ContainsScreenPoint(e.X + pass.OriginX, e.Y + pass.OriginY));
    }

    private void DisposeInventory()
    {
        DisposeDrawerDragFeedback();
        AfterModeChanged -= OnInventoryModeChanged;
        BeforeModeChanged -= CancelDrawerDrags;
        if (_placeholderWorkspace != null)
        {
            _placeholderWorkspace.InventoryCollected -= OnInventoryCollected;
            _placeholderWorkspace.InventoryRequested -= OnInventoryRequested;
            _placeholderWorkspace.ArrivalViewportProvider = null;
        }
        _inventoryFlight?.Dispose(); _backpack?.Dispose();
    }
}
