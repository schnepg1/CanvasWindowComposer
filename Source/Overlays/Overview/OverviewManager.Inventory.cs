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
        if (!_inventoryAttached) { AfterModeChanged += OnInventoryModeChanged; _inventoryAttached = true; }
        RefreshInventory();
    }

    private ArrivalViewport InventoryViewport()
        => new(CurrentMode == OverviewMode.Hidden ? _mainCanvas.Camera : ViewCamera, _screens.AllWorkingAreas,
            _backpack?.IsVisible == true ? new[] { _backpack.ScreenBounds } : Array.Empty<ScreenRect>());

    private BackpackWindow EnsureBackpack()
    {
        if (_backpack != null) return _backpack;
        _backpack = new(_screens.PrimaryWorkingArea);
        _backpack.CloseRequested += () => SetInventoryVisible(false);
        _backpack.Panel.OpenRequested += open => SetInventoryVisible(open, activate: open);
        _backpack.Panel.PlaceRequested += id => DropInventory(id, null);
        _backpack.Panel.DropRequested += (id, point) => DropInventory(id, new System.Drawing.Point((int)point.X, (int)point.Y));
        _backpack.Panel.KeepRequested += id => _placeholderWorkspace?.CollectInInventory(id);
        _backpack.Panel.OpenItemRequested += id => _placeholderWorkspace?.Open(id);
        _backpack.Panel.ForgetRequested += id => _placeholderWorkspace?.Forget(id);
        return _backpack;
    }

    public void ToggleInventory()
        => SetInventoryVisible(!(_placeholderWorkspace?.Document.InventoryOpen ?? false), activate: true);

    private void SetInventoryVisible(bool open, Guid? selected = null, bool activate = false)
    {
        if (_disposed || _placeholderWorkspace == null) return;
        if (open) CancelInteraction();
        _placeholderWorkspace.SetInventoryOpen(open);
        EnsureBackpack(); RefreshInventory(); _backpack!.Panel.Highlight(selected);
        _backpack.Present(open, CurrentMode != OverviewMode.Hidden, _screens.PrimaryWorkingArea, activate && open);
        if (!open && CurrentMode == OverviewMode.Zooming) FocusOverview();
    }

    private void OnInventoryRequested(Guid? id) => SetInventoryVisible(true, id);
    private void OnInventoryModeChanged(OverviewMode from, OverviewMode to) => RefreshInventory();

    private void RefreshInventory()
    {
        if (_disposed || _placeholderWorkspace == null) return;
        bool open = _placeholderWorkspace.Document.InventoryOpen;
        if (_backpack == null && !open && CurrentMode == OverviewMode.Hidden) return;
        var backpack = EnsureBackpack();
        backpack.Panel.UpdateItems(_placeholderWorkspace.InventoryItems);
        backpack.Panel.SetHint(_placeholderWorkspace.InventoryStatus);
        backpack.Present(open, CurrentMode != OverviewMode.Hidden, _screens.PrimaryWorkingArea);
    }

    private void OnInventoryCollected(InventoryCollection collection)
    {
        if (_disposed || _placeholderWorkspace == null) return;
        RefreshInventory();
        var backpack = EnsureBackpack();
        backpack.Panel.UpdateItems(_placeholderWorkspace.InventoryItems);
        backpack.Present(_placeholderWorkspace.Document.InventoryOpen, CurrentMode != OverviewMode.Hidden, _screens.PrimaryWorkingArea);
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

    private void DropInventory(Guid id, System.Drawing.Point? point)
    {
        if (_placeholderWorkspace == null) return;
        var camera = CurrentMode == OverviewMode.Hidden ? _mainCanvas.Camera : ViewCamera;
        double? x = point.HasValue ? camera.X + point.Value.X / camera.Zoom : null;
        double? y = point.HasValue ? camera.Y + point.Value.Y / camera.Zoom : null;
        bool dropped = _placeholderWorkspace.DropFromInventory(id, x, y);
        _backpack?.Panel.SetHint(dropped ? "Unpacked. This place is now kept on the canvas." : _placeholderWorkspace.Status);
        RefreshOverviewWindows();
        if (CurrentMode != OverviewMode.Hidden) ReconcileVisuals();
    }

    private bool TryCollectInventoryDrop(OverviewOverlay pass, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || _placeholderWorkspace == null || _backpack == null
            || !_backpack.ContainsScreenPoint(e.X + pass.OriginX, e.Y + pass.OriginY)) return false;
        Guid[] ids;
        IntPtr[] sessionHandles = Array.Empty<IntPtr>();
        IntPtr handle = IntPtr.Zero;
        if (_tools.Kind == CanvasInteractionKind.Window && _windowDragMoved && _dragIndex >= 0 && _dragIndex < _windows.Count)
        {
            handle = _windows.Windows[_dragIndex].HWnd; ids = Array.Empty<Guid>();
        }
        else if (_tools.Kind == CanvasInteractionKind.Tool && _selectionTool is { Ghosts.Count: > 0 } selection
            && selection.Ghosts.All(i => i.Key.Kind == CanvasItemKind.Window) && (selection.Offset.X != 0 || selection.Offset.Y != 0))
        {
            ids = selection.Ghosts.Select(i => i.Key.Id).Where(id => _placeholderWorkspace.Document.Items.Any(i => i.Id == id)).ToArray();
            sessionHandles = selection.Ghosts.Select(i => _selectionWindows.FirstOrDefault(p => p.Value.Id == i.Key.Id).Key)
                .Where(h => h != IntPtr.Zero).ToArray();
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
        _backpack.Panel.SetDropTarget(false);
        Selection.Clear();
        RefreshOverviewWindows(); ReconcileVisuals();
        return true;
    }

    private void UpdateInventoryDropHover(OverviewOverlay pass, MouseEventArgs e)
    {
        _backpack?.Panel.SetDropTarget((_tools.Kind == CanvasInteractionKind.Window || _selectionTool?.Ghosts.Count > 0)
            && _backpack.ContainsScreenPoint(e.X + pass.OriginX, e.Y + pass.OriginY));
    }

    private void DisposeInventory()
    {
        AfterModeChanged -= OnInventoryModeChanged;
        if (_placeholderWorkspace != null)
        {
            _placeholderWorkspace.InventoryCollected -= OnInventoryCollected;
            _placeholderWorkspace.InventoryRequested -= OnInventoryRequested;
            _placeholderWorkspace.ArrivalViewportProvider = null;
        }
        _inventoryFlight?.Dispose(); _backpack?.Dispose();
    }
}
