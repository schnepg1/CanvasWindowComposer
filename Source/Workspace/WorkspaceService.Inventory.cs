using System;
using System.Collections.Generic;
using System.Linq;

namespace CanvasDesktop;

internal sealed record InventoryCollection(Guid Id, string Title, WorldRect Source, bool Automatic);

internal sealed partial class WorkspaceService
{
    private string? _inventoryStatus;
    public string? InventoryStatus => _persistence.LastError ?? _inventoryStatus;
    internal Func<ArrivalViewport>? ArrivalViewportProvider { get; set; }
    public IReadOnlyList<WorkspaceItemView> InventoryItems => Document.Items
        .Where(i => i.DesktopId == _desktop && i.Bounds.InInventory).Select(View).ToArray();
    public event Action<InventoryCollection>? InventoryCollected;
    public event Action<Guid?>? InventoryRequested;

    public void SetInventoryOpen(bool open) { Document.InventoryOpen = open; Save(); }

    public bool CollectInInventory(Guid id)
    {
        if (_disposed || !_started || _switching || Find(id) is not { } item || item.DesktopId != _desktop) return false;
        Capture();
        if (Find(id) != item) return false;
        var live = BoundWindow(id);
        if (live == null && item.Placement == WorkspacePlacement.Temporary) return false;
        var source = item.Bounds;
        var carried = source; carried.InInventory = true; carried.PinnedToScreen = false;
        if (live != null && (!StillCurrent(live) || !_wm.TryCarryWindow(live.Identity.Handle, carried)))
        { _inventoryStatus = _status = "This window could not be picked up. It is still on the canvas."; Changed?.Invoke(); return false; }
        item.Bounds = carried; item.Placement = WorkspacePlacement.Deliberate;
        if (live != null) live.NeedsInitialMatch = false;
        _inventoryStatus = null;
        RemoveCard(id); Zones.ForgetItem(id); RemoveTemporaryMembership(id);
        InvalidateOverviewSnapshot(); Save(); PublishTemporaryAreaChange();
        Changed?.Invoke(); InventoryCollected?.Invoke(new(id, DisplayTitle(item), source, false));
        return true;
    }

    public bool DropFromInventory(Guid id, double? worldX = null, double? worldY = null)
    {
        if (_disposed || !_started || _switching || Find(id) is not { } item || !item.Bounds.InInventory || item.DesktopId != _desktop) return false;
        var view = ArrivalViewportProvider?.Invoke() ?? new ArrivalViewport(_canvas.Camera, [_windowActions.Viewport]);
        var source = item.Bounds;
        var dropped = source; dropped.InInventory = dropped.PinnedToScreen = false;
        if (dropped.State == WindowState.Maximized) dropped.State = WindowState.Normal;
        if (!NearbyWindowPlacement.TryPlace(dropped, view, InventoryPlacementObstacles(id), out dropped, worldX, worldY))
        { _inventoryStatus = _status = "No clear landing space here. Pan to some open canvas and try again."; Changed?.Invoke(); return false; }
        if (BoundWindow(id) is { } live)
        {
            if (!StillCurrent(live)) return false;
            var result = _api.RestoreNativeState(live.Identity.Handle, _canvas.WorldToScreen(dropped), dropped.State);
            if (!result.Accepted || !StillCurrent(live))
            { _inventoryStatus = _status = "Windows could not place this item. It stays in your backpack."; Changed?.Invoke(); return false; }
            if (!_wm.RestorePlacement(live.Identity.Handle, dropped))
            {
                _wm.TryCarryWindow(live.Identity.Handle, source);
                _inventoryStatus = _status = "Windows could not finish placing this item. It stays in your backpack.";
                Changed?.Invoke(); return false;
            }
        }
        item.Bounds = dropped; item.Placement = WorkspacePlacement.Deliberate;
        _inventoryStatus = _status = null; InvalidateOverviewSnapshot(); EnsureCards(); Save(); Changed?.Invoke();
        return true;
    }

    private IReadOnlyList<WorldRect> InventoryPlacementObstacles(Guid except, IntPtr ignoreHandle = default)
    {
        var handles = new HashSet<IntPtr>();
        if (ignoreHandle != IntPtr.Zero) handles.Add(ignoreHandle);
        if (BoundWindow(except) is { } live) handles.Add(live.Identity.Handle);
        if (_cards.TryGetValue(except, out var card)) handles.Add(card.Handle);
        return Document.Items.Where(i => i.Id != except && i.DesktopId == _desktop && !i.Bounds.InInventory && !i.Bounds.PinnedToScreen).Select(i => i.Bounds)
            .Concat(_canvas.Windows.Where(p => !handles.Contains(p.Key) && !p.Value.InInventory && !p.Value.PinnedToScreen).Select(p => p.Value))
            .Concat(Document.Zones.Where(z => z.DesktopId == _desktop).Select(z => z.Bounds))
            .Concat(AdditionalPlacementObstacles?.Invoke() ?? Array.Empty<WorldRect>()).Where(ZoneGeometry.ValidBounds).ToArray();
    }

    private void OnInventoryWindowFocused(IntPtr handle)
    {
        if (ValidBinding(handle)?.ItemId is { } id && Find(id)?.Bounds.InInventory == true) InventoryRequested?.Invoke(id);
    }

    private static string DisplayTitle(WorkspaceItem item) => item.Name.Length > 0 ? item.Name : item.Title;
}
