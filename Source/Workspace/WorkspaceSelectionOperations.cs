using System;
using System.Collections.Generic;
using System.Linq;

namespace CanvasDesktop;

internal sealed record WorkspaceSelectionPlacement(Guid Desktop, IReadOnlyList<ZoneItemPlacement> Items,
    IReadOnlyList<ZoneItemPlacement> Areas)
{
    public IReadOnlyDictionary<Guid, Guid[]> Membership { get; init; } = new Dictionary<Guid, Guid[]>();
}

internal sealed partial class WorkspaceService
{
    internal WorkspaceSelectionPlacement SelectionSnapshot(IEnumerable<Guid> itemIds, IEnumerable<Guid> areaIds)
    {
        Capture();
        var items = itemIds.ToHashSet(); var areas = areaIds.ToHashSet();
        return new(_desktop, Document.Items.Where(i => i.DesktopId == _desktop && items.Contains(i.Id))
            .Select(i => new ZoneItemPlacement(i.Id, i.Bounds)).ToArray(),
            Document.Zones.Where(z => z.DesktopId == _desktop && areas.Contains(z.Id))
            .Select(z => new ZoneItemPlacement(z.Id, z.Bounds)).ToArray())
        { Membership=Document.Zones.Where(z=>z.DesktopId==_desktop && areas.Contains(z.Id)).ToDictionary(z=>z.Id,z=>z.ItemIds.OrderBy(id=>id).ToArray()) };
    }

    /// <summary>Validate the entire saved-ID batch before touching logical or native geometry.</summary>
    internal bool ApplySelectionPlacement(WorkspaceSelectionPlacement expected, WorkspaceSelectionPlacement target)
    {
        if (_disposed || !_started || _switching || expected.Desktop != _desktop || target.Desktop != _desktop) return false;
        Capture();
        bool Valid(IReadOnlyList<ZoneItemPlacement> before, IReadOnlyList<ZoneItemPlacement> after,
            IReadOnlyDictionary<Guid, WorldRect> current, bool area = false)
        {
            if (before.Count != after.Count || before.Select(i => i.Id).Distinct().Count() != before.Count
                || after.Select(i => i.Id).Distinct().Count() != after.Count) return false;
            return before.All(p => current.TryGetValue(p.Id, out var bounds) && !bounds.PinnedToScreen
                && ZoneLayoutPlanner.SamePlacement(bounds, p.Bounds)
                && after.Any(a => a.Id == p.Id && (area ? ZoneGeometry.ValidBounds(a.Bounds) : WorkspaceGeometry.ValidBounds(a.Bounds)) && !a.Bounds.PinnedToScreen));
        }
        if (expected.Membership.Any(p=>!Document.Zones.Any(z=>z.Id==p.Key && z.DesktopId==_desktop && z.ItemIds.OrderBy(id=>id).SequenceEqual(p.Value)))
            || !Valid(expected.Items, target.Items, Document.Items.Where(i => i.DesktopId == _desktop).ToDictionary(i => i.Id, i => i.Bounds))
            || !Valid(expected.Areas, target.Areas, Document.Zones.Where(i => i.DesktopId == _desktop).ToDictionary(i => i.Id, i => i.Bounds), area:true))
        { _status = "Selection changed. Select the group again before moving or undoing it."; return false; }

        var targets = new List<(IntPtr Handle, WorldRect Bounds, bool Live)>();
        foreach (var placement in target.Items)
        {
            var live = BoundWindow(placement.Id);
            var card = _cards.GetValueOrDefault(placement.Id);
            if (live != null && _canvas.HasWindow(live.Identity.Handle)) targets.Add((live.Identity.Handle, placement.Bounds, true));
            if (card != null && _canvas.HasWindow(card.Handle)) targets.Add((card.Handle, placement.Bounds, false));
        }
        foreach (var (handle, bounds, _) in targets) _canvas.SetWindow(handle, bounds.X, bounds.Y, bounds.W, bounds.H);
        foreach (var p in target.Items)
        {
            var item = Document.Items.First(i => i.Id == p.Id);
            var bounds = item.Bounds; bounds.X=p.Bounds.X; bounds.Y=p.Bounds.Y; bounds.W=p.Bounds.W; bounds.H=p.Bounds.H;
            item.Bounds = bounds;
        }
        foreach (var p in target.Areas) Document.Zones.First(z => z.Id == p.Id).Bounds = p.Bounds;
        _status = null;
        Save(); // Persistence failure retains the logical edit and its undo, like existing zone operations.
        bool nativeAccepted = true;
        foreach (var (handle, bounds, live) in targets)
            if (live && _canvas.GetWindowState(handle) != WindowState.Normal)
                nativeAccepted &= _api.RestoreNativeState(handle, _canvas.WorldToScreen(bounds), _canvas.GetWindowState(handle)).Accepted;
        foreach (var p in target.Items)
            if (_cards.TryGetValue(p.Id, out var card)) card.UpdateItem(View(Document.Items.First(i => i.Id == p.Id)));
        if (!nativeAccepted) _status = "The group moved, but Windows could not update some restore positions.";
        if (targets.Count > 0) _wm.Reproject(isAsync: true);
        Changed?.Invoke(); Zones.NotifyDesktopChanged();
        return true;
    }
}
