using System;
using System.Collections.Generic;
using System.Linq;

namespace CanvasDesktop;

// Drawer commands are separate from arrival capture and the saved temporary-zone object.
internal sealed partial class WorkspaceService
{
    public IReadOnlyList<WorkspaceItemView> TemporaryAreaItems => TemporaryZone is { } zone
        ? Document.Items.ToArray().Where(i => IsTemporaryAreaMember(i, zone) && BoundWindow(i.Id) != null).Select(View).ToArray()
        : Array.Empty<WorkspaceItemView>();

    private bool IsTemporaryAreaMember(WorkspaceItem item, WorkspaceZone zone)
        => item.DesktopId == _desktop && item.Placement == WorkspacePlacement.Temporary
            && !item.Bounds.InInventory && zone.ItemIds.Contains(item.Id);

    /// <summary>Explicit placement promotes a live temporary-zone entry only after native acceptance.</summary>
    public bool PlaceFromTemporaryArea(Guid id, ArrivalViewport viewport, double? x = null, double? y = null)
    {
        if (_disposed || !_started || _switching || TemporaryZone is not { } zone || Find(id) is not { } item
            || !IsTemporaryAreaMember(item, zone)) return false;
        using var writer = BeginExplicitPlacement();
        if (!writer.HasValue || BoundWindow(id) is not { } live || !StillCurrent(live)) return false;
        var source = item.Bounds;
        if (!TryPreviewTemporaryDrop(id, viewport, out var placed, x, y))
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

    public bool TryPreviewTemporaryDrop(Guid id, ArrivalViewport viewport, out WorldRect placed, double? x = null, double? y = null)
    {
        placed = default;
        if (_disposed || !_started || _switching || TemporaryZone is not { } zone || Find(id) is not { } item
            || !IsTemporaryAreaMember(item, zone) || _bindings.ForItem(id) is not { } live || !StillCurrent(live)) return false;
        return NearbyWindowPlacement.TryPlace(item.Bounds, viewport, InventoryPlacementObstacles(id), out placed, x, y);
    }

    /// <summary>Move a carried inventory window into the temporary area as a session entry.</summary>
    public bool MoveInventoryToTemporaryArea(Guid id)
    {
        if (_disposed || !_started || _switching || Find(id) is not { } item
            || !item.Bounds.InInventory || item.DesktopId != _desktop) return false;
        using var writer = BeginExplicitPlacement(inventory: true);
        if (!writer.HasValue) return false;
        if (BoundWindow(id) is not { } live || !StillCurrent(live))
        {
            _inventoryStatus = _status = "Only open windows can move to the temporary area.";
            Changed?.Invoke();
            return false;
        }
        var source = item.Bounds;
        WorldRect placed;
        try
        {
            var world = source; world.InInventory = false; world.PinnedToScreen = false;
            placed = PlaceTemporary(live, world);
        }
        catch (InvalidOperationException)
        {
            _inventoryStatus = _status = "The temporary area has no free space for this window.";
            Changed?.Invoke();
            return false;
        }
        var restored = _api.RestoreNativeState(live.Identity.Handle, _canvas.WorldToScreen(placed), placed.State);
        if (!restored.Accepted || !StillCurrent(live))
        {
            _inventoryStatus = _status = "Windows could not restore this item. It stays in your backpack.";
            Changed?.Invoke();
            return false;
        }
        if (!_wm.RestorePlacement(live.Identity.Handle, placed))
        {
            _wm.TryCarryWindow(live.Identity.Handle, source);
            _inventoryStatus = _status = "Windows could not finish placing this item. It stays in your backpack.";
            Changed?.Invoke();
            return false;
        }
        item.Bounds = placed; item.Placement = WorkspacePlacement.Temporary;
        live.NeedsInitialMatch = false;
        if (TemporaryZone is { } area)
        {
            if (!area.ItemIds.Contains(id)) area.ItemIds.Add(id);
            _temporaryAreaChanged = true;
        }
        _inventoryStatus = _status = null;
        InvalidateOverviewSnapshot(); RemoveCard(id); EnsureCards(); Save(); PublishTemporaryAreaChange(); Changed?.Invoke();
        return true;
    }
}
