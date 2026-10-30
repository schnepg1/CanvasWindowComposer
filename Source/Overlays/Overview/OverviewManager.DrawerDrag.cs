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
                ? (item.Name.Length > 0 ? item.Name : item.Title) : "Window", new DrawerDragFeedbackView(_screens)));
    }

    private bool TryDrawerDropTarget(Guid id, bool inventory, Point? screen, out ArrivalViewport viewport, out double? x, out double? y)
    {
        viewport = InventoryViewport(); x = y = null;
        var item = _placeholderWorkspace?.Document.Items.FirstOrDefault(i => i.Id == id);
        if (item == null) return false;
        if (screen is not { } point) return true;
        bool Contains(ScreenRect r) => point.X >= r.Left && point.X < r.Right && point.Y >= r.Top && point.Y < r.Bottom;
        // A release on the taskbar or another Canvas panel is not a canvas drop.
        if (!viewport.Screens.Any(Contains) || viewport.Reserved?.Any(Contains) == true) return false;
        x = viewport.Camera.X + point.X / viewport.Camera.Zoom - (inventory ? 0 : item.Bounds.W / 2);
        y = viewport.Camera.Y + point.Y / viewport.Camera.Zoom - (inventory ? 0 : item.Bounds.H / 2);
        return true;
    }

    internal WindowRect? PreviewDrawerLanding(Guid id, bool inventory, Point screen)
    {
        if (_disposed || _placeholderWorkspace == null || !TryDrawerDropTarget(id, inventory, screen, out var view, out var x, out var y)) return null;
        WorldRect placed;
        bool found = inventory ? _placeholderWorkspace.TryPreviewInventoryDrop(id, view, out placed, x, y)
            : _placeholderWorkspace.TryPreviewTemporaryDrop(id, view, out placed, x, y);
        if (!found) return null;
        // Overview thumbnails use the actual zoomed size; native desktop windows enforce a minimum size.
        return CurrentMode == OverviewMode.Hidden ? view.Camera.Project(placed)
            : new WindowRect((int)((placed.X - view.Camera.X) * view.Camera.Zoom), (int)((placed.Y - view.Camera.Y) * view.Camera.Zoom),
                Math.Max(1, (int)(placed.W * view.Camera.Zoom)), Math.Max(1, (int)(placed.H * view.Camera.Zoom)));
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
