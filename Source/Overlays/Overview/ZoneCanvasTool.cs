using System;
using System.Collections.Generic;

namespace CanvasDesktop;

/// <summary>
/// Adapts project-area policy to the UI-neutral tool contract. No forms, capture,
/// window manager, workspace service or native calls are needed for gestures.
/// </summary>
internal sealed class ZoneCanvasTool : ICanvasTool
{
    private readonly Func<bool> _enabled;
    private readonly Func<IReadOnlyList<ZoneView>> _zones;
    private readonly Func<Guid, IReadOnlyList<WorldRect>> _members;
    public OverviewZoneInteraction Gesture { get; } = new();
    public bool HasPendingInteraction => Gesture.Active || Gesture.Armed;
    public event Action<ZoneGestureResult>? Completed;

    public ZoneCanvasTool(Func<bool> enabled, Func<IReadOnlyList<ZoneView>> zones,
        Func<Guid, IReadOnlyList<WorldRect>> members)
    {
        _enabled = enabled; _zones = zones; _members = members;
    }

    public void Arm() => Gesture.Arm();

    public bool PointerDown(CanvasPointer pointer)
    {
        if (!_enabled() || pointer.Button != CanvasPointerButton.Left) return false;
        var world = pointer.World;
        if (Gesture.Armed || pointer.Modifiers.HasFlag(CanvasModifiers.Shift))
            Gesture.BeginDraw(world.X, world.Y);
        else if (HitCorner(pointer) is { } resize)
            Gesture.BeginResize(resize.Zone, resize.Corner, world.X, world.Y);
        else if (HitHeader(pointer) is { } zone)
            Gesture.BeginMove(zone, world.X, world.Y, _members(zone.Id));
        else return false;
        return true;
    }

    public void PointerMove(CanvasPointer pointer)
    {
        var world = pointer.World;
        Gesture.Update(world.X, world.Y);
    }

    public void PointerUp(CanvasPointer pointer)
    {
        var world = pointer.World;
        var result = Gesture.Complete(world.X, world.Y, pointer.View.Zoom);
        if (result != null) Completed?.Invoke(result);
    }

    public void Cancel() => Gesture.Cancel();

    public CanvasCursor CursorAt(CanvasPointer pointer)
    {
        if (Gesture.Armed) return CanvasCursor.Crosshair;
        return HitCorner(pointer) is { } hit
            ? hit.Corner is ZoneResizeCorner.TopLeft or ZoneResizeCorner.BottomRight
                ? CanvasCursor.ResizeNorthWestSouthEast : CanvasCursor.ResizeNorthEastSouthWest
            : CanvasCursor.Default;
    }

    private ZoneView? HitHeader(CanvasPointer pointer)
    {
        var zones = _zones();
        for (int i = zones.Count - 1; i >= 0; i--)
            if (ZoneLabelLayout.Get(zones[i].Bounds, pointer.View) is { } label
                && label.Contains(pointer.Position.X, pointer.Position.Y)) return zones[i];
        return null;
    }

    private (ZoneView Zone, ZoneResizeCorner Corner)? HitCorner(CanvasPointer pointer)
    {
        var zones = _zones();
        for (int i = zones.Count - 1; i >= 0; i--)
            foreach (var handle in ZoneResizeLayout.Get(zones[i].Bounds, pointer.View, hitTarget: true))
                if (handle.Rect.Contains(pointer.Position.X, pointer.Position.Y)) return (zones[i], handle.Corner);
        return null;
    }
}
