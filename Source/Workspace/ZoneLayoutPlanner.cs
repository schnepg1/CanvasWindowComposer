using System;
using System.Collections.Generic;
using System.Linq;

namespace CanvasDesktop;

internal sealed record ZoneItemPlacement(Guid Id, WorldRect Bounds);
internal sealed record ZoneLayoutPlan(Guid ZoneId, WorldRect Bounds, IReadOnlyList<ZoneItemPlacement> Items);

/// <summary>Pure, all-or-nothing world-space layout policy. No HWNDs or UI state.</summary>
internal static class ZoneLayoutPlanner
{
    public static bool TryTranslate(WorldRect original, double dx, double dy, out WorldRect moved)
    {
        moved = original; moved.X += dx; moved.Y += dy;
        return double.IsFinite(dx) && double.IsFinite(dy) && WorkspaceGeometry.ValidBounds(moved);
    }

    public static bool TryMove(WorkspaceZone? zone, IReadOnlyList<WorkspaceItem> items, double dx, double dy,
        out ZoneLayoutPlan? plan)
    {
        plan = null;
        if (zone == null || !TryTranslate(zone.Bounds, dx, dy, out var bounds)) return false;
        var byId = items.ToDictionary(i => i.Id);
        var placements = new List<ZoneItemPlacement>();
        foreach (var id in zone.ItemIds.Distinct())
        {
            if (!byId.TryGetValue(id, out var item) || item.DesktopId != zone.DesktopId) return false;
            if (item.Bounds.PinnedToScreen) continue;
            if (!TryTranslate(item.Bounds, dx, dy, out var moved)) return false;
            placements.Add(new(id, moved));
        }
        plan = new(zone.Id, bounds, placements);
        return true;
    }

    public static bool TryReplay(WorkspaceZone? zone, IReadOnlyList<WorkspaceItem> currentItems,
        ZoneLayoutEdit edit, bool redo, out ZoneLayoutPlan? plan)
    {
        plan = null;
        var expected = redo ? edit.Before : edit.After;
        var target = redo ? edit.After : edit.Before;
        if (zone == null || zone.Id != expected.ZoneId || !SamePlacement(zone.Bounds, expected.Bounds)
            || edit.Membership != null && !zone.ItemIds.OrderBy(i => i).SequenceEqual(edit.Membership)) return false;
        var items = currentItems.ToDictionary(i => i.Id);
        var placements = new List<ZoneItemPlacement>();
        foreach (var before in expected.Items)
        {
            if (!items.TryGetValue(before.Id, out var item) || item.DesktopId != zone.DesktopId
                || !SamePlacement(item.Bounds, before.Bounds)) return false;
            var destination = target.Items.First(p => p.Id == before.Id).Bounds;
            // Undo only geometry; a user's newer minimize/maximize or z-order choice survives.
            var b = item.Bounds;
            b.X = destination.X; b.Y = destination.Y; b.W = destination.W; b.H = destination.H;
            if (!WorkspaceGeometry.ValidBounds(b)) return false;
            placements.Add(new(item.Id, b));
        }
        if (!WorkspaceGeometry.ValidBounds(target.Bounds)) return false;
        plan = new(zone.Id, target.Bounds, placements);
        return true;
    }

    internal static bool SamePlacement(WorldRect a, WorldRect b)
        => Math.Abs(a.X - b.X) < 0.01 && Math.Abs(a.Y - b.Y) < 0.01
            && Math.Abs(a.W - b.W) < 0.01 && Math.Abs(a.H - b.H) < 0.01 && a.PinnedToScreen == b.PinnedToScreen;
}
