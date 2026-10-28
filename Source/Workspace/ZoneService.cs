using System;
using System.Collections.Generic;
using System.Linq;

namespace CanvasDesktop;

/// <summary>UI-independent zone commands. Views are detached and scoped to the active desktop.</summary>
internal sealed class ZoneService : IZoneCommands, IZoneNavigationSource
{
    public const int MaximumZones = 256;
    private readonly WorkspaceDocument _document;
    private readonly Func<Guid> _desktop;
    private readonly Func<bool> _save;
    public event Action? Changed;
    public Guid DesktopId => _desktop();
    public IReadOnlyList<ZoneView> Items => _document.Zones.Where(z => z.DesktopId == _desktop())
        .Select(z => new ZoneView(z.Id, z.Name, z.Color, z.Bounds, z.ItemIds.Count)).ToArray();
    public IReadOnlyList<ZoneNavigationArea> NavigationAreas => _document.Zones.Where(z => z.DesktopId == _desktop())
        .Select(z => new ZoneNavigationArea(new(z.Id, z.Name, z.Color, z.Bounds, z.ItemIds.Count), z.ItemIds.ToArray())).ToArray();

    public ZoneService(WorkspaceDocument document, Func<Guid> desktop, Func<bool> save)
    { _document = document; _desktop = desktop; _save = save; }

    public static bool ValidName(string? name) => !string.IsNullOrWhiteSpace(name) && name.Length <= 120;
    public static bool ValidColor(string? color) => color is { Length: 7 } && color[0] == '#'
        && color.AsSpan(1).ToString().All(Uri.IsHexDigit);

    public bool Add(WorldRect bounds, out Guid id)
    {
        id = Guid.Empty;
        if (!WorkspaceGeometry.ValidBounds(bounds) || _document.Zones.Count >= MaximumZones) return false;
        var zone = new WorkspaceZone { DesktopId = _desktop(), Bounds = bounds,
            Name = "Project area " + (Items.Count + 1) };
        _document.Zones.Add(zone);
        id = zone.Id;
        return Persist();
    }

    public bool Update(Guid id, string name, string color)
    {
        var zone = Find(id);
        if (zone == null || !ValidName(name) || !ValidColor(color)) return false;
        zone.Name = name.Trim(); zone.Color = color.ToUpperInvariant();
        return Persist();
    }

    public bool Remove(Guid id)
    {
        var zone = Find(id);
        if (zone == null) return false;
        _document.Zones.Remove(zone);
        return Persist();
    }

    public IReadOnlyList<ZoneMemberView> Members(Guid id)
    {
        var zone = Find(id);
        if (zone == null) return Array.Empty<ZoneMemberView>();
        var assigned = zone.ItemIds.ToHashSet();
        return _document.Items.Where(i => i.DesktopId == _desktop()).Select(i => new ZoneMemberView(i.Id,
            i.Name.Length > 0 ? i.Name : i.Title, assigned.Contains(i.Id), i.Bounds,
            i.Target.Length > 0 ? i.Target : i.ExecutablePath)).ToArray();
    }

    public bool SetMembership(Guid id, IReadOnlyCollection<Guid> itemIds)
    {
        var zone = Find(id);
        if (zone == null || itemIds == null || itemIds.Count > 2000) return false;
        var ids = itemIds.ToHashSet();
        var eligible = _document.Items.Where(i => i.DesktopId == _desktop()).Select(i => i.Id).ToHashSet();
        if (ids.Count != itemIds.Count || !ids.IsSubsetOf(eligible)) return false;
        foreach (var item in _document.Items.Where(i => ids.Contains(i.Id))) item.Placement = WorkspacePlacement.Deliberate;
        zone.ItemIds = ids.OrderBy(i => i).ToList();
        return Persist();
    }

    internal bool TryPlanMove(Guid id, double dx, double dy, out ZoneLayoutPlan? plan)
        => ZoneLayoutPlanner.TryMove(Find(id), _document.Items, dx, dy, out plan);

    internal ZoneLayoutPlan Snapshot(ZoneLayoutPlan plan)
        => new(plan.ZoneId, Find(plan.ZoneId)!.Bounds, plan.Items.Select(p =>
            new ZoneItemPlacement(p.Id, _document.Items.First(i => i.Id == p.Id).Bounds)).ToArray());

    internal Guid[] Membership(Guid id) => Find(id)!.ItemIds.OrderBy(i => i).ToArray();

    internal bool TryPlanResize(Guid id, WorldRect bounds, out ZoneLayoutPlan? plan)
    {
        plan = null;
        var zone = Find(id);
        if (zone == null || !WorkspaceGeometry.ValidBounds(bounds)) return false;
        var resized = zone.Bounds;
        resized.X = bounds.X; resized.Y = bounds.Y; resized.W = bounds.W; resized.H = bounds.H;
        plan = new(id, resized, Array.Empty<ZoneItemPlacement>());
        return true;
    }

    internal bool TryReplay(ZoneLayoutEdit edit, bool redo, out ZoneLayoutPlan? plan)
        => ZoneLayoutPlanner.TryReplay(Find(edit.Before.ZoneId), _document.Items, edit, redo, out plan);

    // The coordinator applies runtime canvas geometry before publishing this change.
    internal bool ApplyLayout(ZoneLayoutPlan plan)
    {
        var zone = Find(plan.ZoneId)!;
        zone.Bounds = plan.Bounds;
        var items = _document.Items.ToDictionary(i => i.Id);
        foreach (var placement in plan.Items) items[placement.Id].Bounds = placement.Bounds;
        return Persist();
    }

    internal void ForgetItem(Guid id)
    {
        foreach (var zone in _document.Zones) zone.ItemIds.Remove(id);
        Changed?.Invoke();
    }

    private WorkspaceZone? Find(Guid id) => _document.Zones.FirstOrDefault(z => z.Id == id && z.DesktopId == _desktop());
    private bool Persist() { bool saved = _save(); Changed?.Invoke(); return saved; }
    public void NotifyDesktopChanged() => Changed?.Invoke();
}

internal static class ZoneGeometry
{
    public static WorldRect FromPoints(double x1, double y1, double x2, double y2)
        => new() { X = Math.Min(x1, x2), Y = Math.Min(y1, y2), W = Math.Abs(x2 - x1), H = Math.Abs(y2 - y1) };

    // Fit to a single physical monitor, including its virtual-screen offset.
    public static (double X, double Y, double Zoom) Fit(WorldRect bounds, ScreenRect viewport)
    {
        const double padding = 48;
        double zoom = Math.Clamp(Math.Min(Math.Max(1, viewport.Width - padding * 2) / bounds.W,
            Math.Max(1, viewport.Height - padding * 2) / bounds.H), OverviewCamera.ZoomMin, OverviewCamera.ZoomMax);
        return (bounds.X + bounds.W / 2 - (viewport.X + viewport.Width / 2.0) / zoom,
            bounds.Y + bounds.H / 2 - (viewport.Y + viewport.Height / 2.0) / zoom, zoom);
    }
}
