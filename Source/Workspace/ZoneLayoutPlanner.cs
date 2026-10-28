using System;
using System.Collections.Generic;
using System.Linq;

namespace CanvasDesktop;

internal sealed record ZoneItemPlacement(Guid Id, WorldRect Bounds);
internal sealed record ZoneLayoutPlan(Guid ZoneId, WorldRect Bounds, IReadOnlyList<ZoneItemPlacement> Items)
{
    public IReadOnlyList<ZoneItemPlacement> Areas { get; init; } = Array.Empty<ZoneItemPlacement>();
    public IReadOnlyDictionary<Guid, Guid[]> AreaMembership { get; init; } = new Dictionary<Guid, Guid[]>();
}

/// <summary>Pure, all-or-nothing world-space layout policy. No HWNDs or UI state.</summary>
internal static class ZoneLayoutPlanner
{
    public static bool TryTranslate(WorldRect original, double dx, double dy, out WorldRect moved, bool area = false)
    {
        moved = original; moved.X += dx; moved.Y += dy;
        return double.IsFinite(dx) && double.IsFinite(dy)
            && (area ? ZoneGeometry.ValidBounds(moved) : WorkspaceGeometry.ValidBounds(moved));
    }

    public static bool TryMove(WorkspaceZone? zone, IReadOnlyList<WorkspaceItem> items, double dx, double dy,
        out ZoneLayoutPlan? plan, IReadOnlyList<WorkspaceZone>? subtree = null)
    {
        plan = null;
        if (zone == null || !TryTranslate(zone.Bounds, dx, dy, out var bounds, area: true)) return false;
        subtree ??= new[] { zone };
        var areas = new List<ZoneItemPlacement>();
        foreach (var child in subtree.Where(z => z.Id != zone.Id))
        {
            if (child.DesktopId != zone.DesktopId || !TryTranslate(child.Bounds, dx, dy, out var moved, area: true)) return false;
            areas.Add(new(child.Id, moved));
        }
        var byId = items.ToDictionary(i => i.Id);
        var placements = new List<ZoneItemPlacement>();
        foreach (var id in subtree.SelectMany(z => z.ItemIds).Distinct())
        {
            if (!byId.TryGetValue(id, out var item) || item.DesktopId != zone.DesktopId) return false;
            if (item.Bounds.PinnedToScreen) continue;
            if (!TryTranslate(item.Bounds, dx, dy, out var moved)) return false;
            placements.Add(new(id, moved));
        }
        plan = new(zone.Id, bounds, placements)
        {
            Areas = areas,
            AreaMembership = subtree.ToDictionary(z => z.Id, z => z.ItemIds.OrderBy(i => i).ToArray())
        };
        return true;
    }

    public static bool TryReplay(WorkspaceZone? zone, IReadOnlyList<WorkspaceItem> currentItems,
        ZoneLayoutEdit edit, bool redo, out ZoneLayoutPlan? plan, IReadOnlyList<WorkspaceZone>? currentAreas = null)
    {
        plan = null;
        var expected = redo ? edit.Before : edit.After;
        var target = redo ? edit.After : edit.Before;
        if (zone == null || zone.Id != expected.ZoneId || !SamePlacement(zone.Bounds, expected.Bounds)
            || edit.Membership != null && !zone.ItemIds.OrderBy(i => i).SequenceEqual(edit.Membership)) return false;
        var zones = (currentAreas ?? new[] { zone }).ToDictionary(z => z.Id);
        foreach (var pair in expected.AreaMembership)
            if (!zones.TryGetValue(pair.Key, out var member) || member.DesktopId != zone.DesktopId
                || !member.ItemIds.OrderBy(i => i).SequenceEqual(pair.Value)) return false;
        foreach (var before in expected.Areas)
            if (!zones.TryGetValue(before.Id, out var child) || child.DesktopId != zone.DesktopId
                || !SamePlacement(child.Bounds, before.Bounds)) return false;
        if (target.Areas.Any(p => !ZoneGeometry.ValidBounds(p.Bounds))) return false;
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
        if (!ZoneGeometry.ValidBounds(target.Bounds)) return false;
        plan = new(zone.Id, target.Bounds, placements) { Areas = target.Areas, AreaMembership = target.AreaMembership };
        return true;
    }

    internal static bool SamePlacement(WorldRect a, WorldRect b)
        => Math.Abs(a.X - b.X) < 0.01 && Math.Abs(a.Y - b.Y) < 0.01
            && Math.Abs(a.W - b.W) < 0.01 && Math.Abs(a.H - b.H) < 0.01 && a.PinnedToScreen == b.PinnedToScreen;
}
