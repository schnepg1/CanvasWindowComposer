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
        if (_disposed || _placeholderWorkspace == null || !TryDrawerDropTarget(id, inventory, screen, out var view, out var x, out var y)) return null;
        WorldRect placed;
        bool found = inventory ? _placeholderWorkspace.TryPreviewInventoryDrop(id, view, out placed, x, y)
            : _placeholderWorkspace.TryPreviewTemporaryDrop(id, view, out placed, x, y);
        if (!found) return null;
        return CanvasPlacementGeometry.Project(placed, view.Camera, CurrentMode == OverviewMode.Hidden);
    }

    private void CancelDrawerDrags(OverviewMode from, OverviewMode to)
    {
        _backpack?.Panel.Drag.Cancel(); _temporaryDrawer?.Panel.Drag.Cancel();
    }

    private void DisposeDrawerDragFeedback()
    {
        foreach (var feedback in _drawerDragFeedback) feedback.Dispose();
        _drawerDragFeedback.Clear();
    }
}
