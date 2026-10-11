using System;
using System.Collections.Generic;
using System.Linq;

namespace CanvasDesktop;

internal sealed class WorkspaceZone
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DesktopId { get; set; }
    public string Name { get; set; } = "Project area";
    public string Color { get; set; } = "#60A5FA";
    public WorldRect Bounds { get; set; }
}

internal sealed record ZoneView(Guid Id, string Name, string Color, WorldRect Bounds);

/// <summary>UI-independent zone commands. Views are detached and scoped to the active desktop.</summary>
internal sealed class ZoneService
{
    public const int MaximumZones = 256;
    private readonly WorkspaceDocument _document;
    private readonly Func<Guid> _desktop;
    private readonly Func<bool> _save;
    public event Action? Changed;
    public IReadOnlyList<ZoneView> Items => _document.Zones.Where(z => z.DesktopId == _desktop())
        .Select(z => new ZoneView(z.Id, z.Name, z.Color, z.Bounds)).ToArray();

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
