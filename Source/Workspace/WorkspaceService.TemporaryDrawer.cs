using System;
using System.Collections.Generic;
using System.Linq;

namespace CanvasDesktop;

// Drawer commands are separate from arrival capture and the saved temporary-zone object.
internal sealed partial class WorkspaceService
{
    public IReadOnlyList<WorkspaceItemView> TemporaryAreaItems => TemporaryZone is { } zone
        ? Document.Items.Where(i => IsTemporaryAreaMember(i, zone) && BoundWindow(i.Id) != null).Select(View).ToArray()
        : Array.Empty<WorkspaceItemView>();

    private bool IsTemporaryAreaMember(WorkspaceItem item, WorkspaceZone zone)
        => item.DesktopId == _desktop && item.Placement == WorkspacePlacement.Temporary
            && !item.Bounds.InInventory && zone.ItemIds.Contains(item.Id);

    /// <summary>Explicit placement promotes a live temporary-zone entry only after native acceptance.</summary>
    public bool PlaceFromTemporaryArea(Guid id, ArrivalViewport viewport, double? x = null, double? y = null)
    {
        if (_disposed || !_started || _switching || TemporaryZone is not { } zone || Find(id) is not { } item
            || !IsTemporaryAreaMember(item, zone) || BoundWindow(id) is not { } live || !StillCurrent(live)) return false;
        var source = item.Bounds;
        if (!NearbyWindowPlacement.TryPlace(source, viewport, InventoryPlacementObstacles(id), out var placed, x, y))
        { _status = "No clear landing space here. Pan to open canvas and try again."; Changed?.Invoke(); return false; }
        var restored = _api.RestoreNativeState(live.Identity.Handle, _canvas.WorldToScreen(placed), placed.State);
        if (!restored.Accepted || !StillCurrent(live))
        { _status = "This window could not be placed. It remains in the temporary area."; Changed?.Invoke(); return false; }
        if (!_wm.RestorePlacement(live.Identity.Handle, placed))
        {
            // RestorePlacement can update canvas geometry before a native move is rejected.
            _canvas.RestoreWindow(live.Identity.Handle, source);
            if (StillCurrent(live)) RestoreBinding(live, item);
            _status = "This window could not be placed. It remains in the temporary area.";
            Changed?.Invoke(); return false;
        }
        item.Bounds = placed; item.Placement = WorkspacePlacement.Deliberate;
        live.NeedsInitialMatch = false;
        RemoveTemporaryMembership(id);
        _status = null; InvalidateOverviewSnapshot(); RemoveCard(id); EnsureCards(); Save(); PublishTemporaryAreaChange(); Changed?.Invoke();
        return true;
    }
}
