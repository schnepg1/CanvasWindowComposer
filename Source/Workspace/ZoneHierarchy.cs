using System;
using System.Collections.Generic;
using System.Linq;

namespace CanvasDesktop;

/// <summary>Full containment defines a folder-like tree, rebuilt from persisted world bounds.</summary>
internal sealed class ZoneHierarchy
{
    private readonly IReadOnlyList<WorkspaceZone> _zones;
    private readonly Dictionary<Guid, Guid?> _parents;

    public ZoneHierarchy(IReadOnlyList<WorkspaceZone> zones)
    {
        _zones = zones;
        _parents = zones.ToDictionary(z => z.Id, z => zones
            .Where(p => !p.IsTemporaryArea && !z.IsTemporaryArea && p.DesktopId == z.DesktopId && Contains(p.Bounds, z.Bounds))
            // A containing rectangle always has a larger width or height. Avoid
            // multiplying dimensions, which can overflow for very large areas.
            .OrderBy(p => p.Bounds.W).ThenBy(p => p.Bounds.H).ThenBy(p => p.Id)
            .Select(p => (Guid?)p.Id).FirstOrDefault());
    }

    public Guid? Parent(Guid id) => _parents.GetValueOrDefault(id);
    public IReadOnlyList<WorkspaceZone> Children(Guid id) => _zones.Where(z => Parent(z.Id) == id).ToArray();
    public IReadOnlyList<WorkspaceZone> Subtree(Guid id)
    {
        var result = new List<WorkspaceZone>();
        void Visit(WorkspaceZone zone)
        {
            result.Add(zone);
            foreach (var child in Children(zone.Id)) Visit(child);
        }
        if (_zones.FirstOrDefault(z => z.Id == id) is { } root) Visit(root);
        return result;
    }

    public IReadOnlyList<ZoneView> Views()
    {
        var result = new List<ZoneView>();
        void Visit(WorkspaceZone zone, int depth)
        {
            result.Add(new(zone.Id, zone.Name, zone.Color, zone.Bounds, zone.ItemIds.Count)
                { ParentId = Parent(zone.Id), Depth = depth, IsTemporaryArea = zone.IsTemporaryArea });
            foreach (var child in Children(zone.Id)) Visit(child, depth + 1);
        }
        foreach (var root in _zones.Where(z => Parent(z.Id) == null)) Visit(root, 0);
        return result;
    }

    internal static bool Contains(WorldRect outer, WorldRect inner) =>
        outer.W >= inner.W && outer.H >= inner.H && (outer.W > inner.W || outer.H > inner.H)
        && inner.X >= outer.X && inner.Y >= outer.Y
        && inner.X + inner.W <= outer.X + outer.W && inner.Y + inner.H <= outer.Y + outer.H;
}
