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
    private ZoneHierarchy Hierarchy => new(_document.Zones.Where(z => z.DesktopId == _desktop()).ToArray());
    public IReadOnlyList<ZoneView> Items => Hierarchy.Views();
    public IReadOnlyList<ZoneNavigationArea> NavigationAreas => Items
        .Select(z => new ZoneNavigationArea(z, Find(z.Id)!.ItemIds.ToArray())).ToArray();

    internal IReadOnlyList<WorkspaceZone> Subtree(Guid id) => Hierarchy.Subtree(id);
    internal IReadOnlyList<WorldRect> MoveMembers(Guid id)
    {
        var tree = Subtree(id);
        var ids = tree.SelectMany(z => z.ItemIds).ToHashSet();
        return _document.Items.Where(i => ids.Contains(i.Id) && i.DesktopId == _desktop() && !i.Bounds.PinnedToScreen)
            .Select(i => i.Bounds).ToArray();
    }

    public ZoneService(WorkspaceDocument document, Func<Guid> desktop, Func<bool> save)
    { _document = document; _desktop = desktop; _save = save; }

    public static bool ValidName(string? name) => !string.IsNullOrWhiteSpace(name) && name.Length <= 120;
    public static bool ValidColor(string? color) => color is { Length: 7 } && color[0] == '#'
        && color.AsSpan(1).ToString().All(Uri.IsHexDigit);

    public bool Add(WorldRect bounds, out Guid id)
    {
        id = Guid.Empty;
        if (!ZoneGeometry.ValidBounds(bounds) || _document.Zones.Count >= MaximumZones) return false;
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
        zone.ItemIds = ids.OrderBy(i => i).ToList();
        return Persist();
    }

    internal bool TryPlanMove(Guid id, double dx, double dy, out ZoneLayoutPlan? plan)
        => ZoneLayoutPlanner.TryMove(Find(id), _document.Items, dx, dy, out plan, Subtree(id));

    internal ZoneLayoutPlan Snapshot(ZoneLayoutPlan plan)
        => new(plan.ZoneId, Find(plan.ZoneId)!.Bounds, plan.Items.Select(p =>
            new ZoneItemPlacement(p.Id, _document.Items.First(i => i.Id == p.Id).Bounds)).ToArray())
        {
            Areas = plan.Areas.Select(p => new ZoneItemPlacement(p.Id, Find(p.Id)!.Bounds)).ToArray(),
            AreaMembership = plan.AreaMembership
        };

    internal Guid[] Membership(Guid id) => Find(id)!.ItemIds.OrderBy(i => i).ToArray();

    internal bool TryPlanResize(Guid id, WorldRect bounds, out ZoneLayoutPlan? plan)
    {
        plan = null;
        var zone = Find(id);
        if (zone == null || !ZoneGeometry.ValidBounds(bounds)) return false;
        var resized = zone.Bounds;
        resized.X = bounds.X; resized.Y = bounds.Y; resized.W = bounds.W; resized.H = bounds.H;
        plan = new(id, resized, Array.Empty<ZoneItemPlacement>());
        return true;
    }

    internal bool TryReplay(ZoneLayoutEdit edit, bool redo, out ZoneLayoutPlan? plan)
        => ZoneLayoutPlanner.TryReplay(Find(edit.Before.ZoneId), _document.Items, edit, redo, out plan, _document.Zones);

    // The coordinator applies runtime canvas geometry before publishing this change.
    internal bool ApplyLayout(ZoneLayoutPlan plan)
    {
        var zone = Find(plan.ZoneId)!;
        zone.Bounds = plan.Bounds;
        foreach (var placement in plan.Areas) Find(placement.Id)!.Bounds = placement.Bounds;
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
    // Areas have no HWND dimensions or arbitrary size/position cap. Reject only
    // invalid rectangles and arithmetic overflow before projection or persistence.
    public static bool ValidBounds(WorldRect b) => double.IsFinite(b.X) && double.IsFinite(b.Y)
        && double.IsFinite(b.W) && double.IsFinite(b.H) && b.W >= 1 && b.H >= 1
        && double.IsFinite(b.X + b.W) && double.IsFinite(b.Y + b.H);

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
