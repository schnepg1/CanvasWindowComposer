using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace CanvasDesktop;

internal sealed partial class OverviewManager
{
    private readonly List<DrawerDragFeedback> _drawerDragFeedback = new();

    private void AttachDrawerDragFeedback(DrawerRowDrag drag, bool inventory)
    {
        _drawerDragFeedback.Add(new(drag, (id, point) => PreviewDrawerLanding(id, inventory, point),
            id => _placeholderWorkspace?.Document.Items.FirstOrDefault(i => i.Id == id) is { } item
                ? (item.Name.Length > 0 ? item.Name : item.Title) : "Window", new CanvasPlacementFeedbackView(_screens)));
        drag.Moved += position => SetOtherDrawerDropTarget(inventory, IsOtherDrawerTarget(inventory, position.Screen));
        drag.HeldChanged += held => { if (!held) SetOtherDrawerDropTarget(inventory, false); };
    }

    private bool IsOtherDrawerTarget(bool inventory, Point screen)
        => inventory
            ? _temporaryDrawer?.ContainsScreenPoint(screen.X, screen.Y) == true
            : _backpack?.ContainsScreenPoint(screen.X, screen.Y) == true;

    private void SetOtherDrawerDropTarget(bool inventory, bool active)
    {
        if (inventory)
        {
            if (active) _temporaryDrawer?.OpenPeek();
            _temporaryDrawer?.Panel.SetDropTarget(active);
        }
        else _backpack?.Panel.SetDropTarget(active);
    }

    /// <summary>Transfer the dragged item between the two drawers instead of placing it on the canvas.</summary>
    private bool TryCrossDrawerDrop(Guid id, bool inventory, Point screen)
    {
        if (_placeholderWorkspace == null || !IsOtherDrawerTarget(inventory, screen)) return false;
        if (inventory)
        {
            bool moved = _placeholderWorkspace.MoveInventoryToTemporaryArea(id);
            _backpack?.Panel.SetHint(moved
                ? "Moved to the temporary area. It is no longer kept in the backpack."
                : _placeholderWorkspace.Status ?? "This item stays in your backpack.");
        }
        else
        {
            bool collected = _placeholderWorkspace.CollectInInventory(id);
            _temporaryDrawer?.Panel.SetHint(collected
                ? "Moved to the backpack."
                : _placeholderWorkspace.Status ?? "This window stays in the temporary area.");
        }
        return true;
    }

    private bool TryDrawerDropTarget(Guid id, bool inventory, Point? screen, out ArrivalViewport viewport, out double? x, out double? y)
    {
        viewport = InventoryViewport(); x = y = null;
        var item = _placeholderWorkspace?.Document.Items.FirstOrDefault(i => i.Id == id);
        if (item == null) return false;
        if (screen is not { } point) return true;
        if (!CanvasPlacementGeometry.TryWorldAnchor(viewport, point, item.Bounds, !inventory, out var wx, out var wy)) return false;
        x = wx; y = wy;
        return true;
    }

    internal WindowRect? PreviewDrawerLanding(Guid id, bool inventory, Point screen)
    {
        if (_disposed || _placeholderWorkspace == null || IsOtherDrawerTarget(inventory, screen)
            || !TryDrawerDropTarget(id, inventory, screen, out var view, out var x, out var y)) return null;
        WorldRect placed;
        bool found = inventory ? _placeholderWorkspace.TryPreviewInventoryDrop(id, view, out placed, x, y)
            : _placeholderWorkspace.TryPreviewTemporaryDrop(id, view, out placed, x, y);
        if (!found) return null;
        return CanvasPlacementGeometry.Project(placed, view.Camera, CurrentMode == OverviewMode.Hidden);
    }

    private void CancelDrawerDrags(OverviewMode from, OverviewMode to)
    {
        _backpack?.Panel.Drag.Cancel(); _temporaryDrawer?.Panel.Drag.Cancel();
        SetOtherDrawerDropTarget(inventory: true, active: false);
        SetOtherDrawerDropTarget(inventory: false, active: false);
    }

    private void DisposeDrawerDragFeedback()
    {
        foreach (var feedback in _drawerDragFeedback) feedback.Dispose();
        _drawerDragFeedback.Clear();
        SetOtherDrawerDropTarget(inventory: true, active: false);
        SetOtherDrawerDropTarget(inventory: false, active: false);
    }
}
