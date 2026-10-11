using System;
using System.Collections.Generic;

namespace CanvasDesktop;

internal enum ZoneGestureKind { None, Draw, Move, Resize }
internal sealed record ZoneGestureResult(ZoneGestureKind Kind, Guid ZoneId, WorldRect Bounds, double Dx, double Dy);

/// <summary>Pure pointer gesture state. Preview never mutates the workspace or canvas.</summary>
internal sealed class OverviewZoneInteraction
{
    private double _startX, _startY;
    private ZoneView? _zone;
    private ZoneResizeCorner _corner;
    private IReadOnlyList<WorldRect> _members = Array.Empty<WorldRect>();
    public ZoneGestureKind Kind { get; private set; }
    public bool Armed { get; private set; }
    public bool Active => Kind != ZoneGestureKind.None;
    public WorldRect? Preview { get; private set; }
    public Guid? MovingId => Kind is ZoneGestureKind.Move or ZoneGestureKind.Resize ? _zone?.Id : null;
    public double Dx { get; private set; }
    public double Dy { get; private set; }
    public IReadOnlyList<WorldRect> Ghosts { get; private set; } = Array.Empty<WorldRect>();

    public void Arm() { Cancel(); Armed = true; }
    public void BeginDraw(double x, double y) => Begin(ZoneGestureKind.Draw, null, x, y);
    public void BeginMove(ZoneView zone, double x, double y, IReadOnlyList<WorldRect>? members = null)
    {
        Begin(ZoneGestureKind.Move, zone, x, y);
        _members = members ?? Array.Empty<WorldRect>();
        Update(x, y);
    }

    public void BeginResize(ZoneView zone, ZoneResizeCorner corner, double x, double y)
    {
        _corner = corner;
        Begin(ZoneGestureKind.Resize, zone, x, y);
    }

    private void Begin(ZoneGestureKind kind, ZoneView? zone, double x, double y)
    {
        Cancel(); Kind = kind; _zone = zone; _startX = x; _startY = y;
        Update(x, y);
    }

    public void Update(double x, double y)
    {
        if (!Active) return;
        Dx = x - _startX; Dy = y - _startY;
        if (!double.IsFinite(Dx) || !double.IsFinite(Dy)) { Preview = null; Ghosts = Array.Empty<WorldRect>(); return; }
        if (Kind == ZoneGestureKind.Draw) Preview = ZoneGeometry.FromPoints(_startX, _startY, x, y);
        else if (Kind == ZoneGestureKind.Resize)
            Preview = ZoneResizeLayout.TryResize(_zone!.Bounds, _corner, Dx, Dy, out var resized) ? resized : null;
        else Preview = ZoneLayoutPlanner.TryTranslate(_zone!.Bounds, Dx, Dy, out var bounds) ? bounds : null;
        var ghosts = new List<WorldRect>(_members.Count);
        foreach (var member in _members)
        {
            if (!ZoneLayoutPlanner.TryTranslate(member, Dx, Dy, out var moved)) { Preview = null; ghosts.Clear(); break; }
            ghosts.Add(moved);
        }
        Ghosts = ghosts;
    }

    public ZoneGestureResult? Complete(double x, double y, double zoom)
    {
        Update(x, y);
        ZoneGestureResult? result = null;
        if (Preview is { } bounds && (Kind is ZoneGestureKind.Move or ZoneGestureKind.Resize
            ? Math.Abs(Dx * zoom) >= 3 || Math.Abs(Dy * zoom) >= 3
            : bounds.W * zoom >= 12 && bounds.H * zoom >= 12))
            result = new(Kind, _zone?.Id ?? Guid.Empty, bounds, Dx, Dy);
        Cancel();
        return result;
    }

    public void Cancel()
    {
        Kind = ZoneGestureKind.None; Armed = false; Preview = null; _zone = null; Dx = Dy = 0;
        _members = Array.Empty<WorldRect>(); Ghosts = Array.Empty<WorldRect>();
    }
}
