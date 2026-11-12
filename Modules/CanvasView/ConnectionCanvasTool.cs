using System;
using System.Collections.Generic;
using System.Linq;

namespace CanvasDesktop.CanvasView;

public sealed record ConnectionTarget(CanvasSelectionItem Item, ConnectionPort Port, CanvasPoint Point);

public sealed class ConnectionCanvasTool : ICanvasTool
{
    // Shares the radius of the note edge handle the port dot sits on.
    public const double HandleHitRadiusDip = AnnotationGeometry.HandleHitRadius;
    // Port magnet reach in DIP, shared by drawing a connector and by dropping a dragged end.
    public const double MagneticSnapRadiusDip = 32.0;

    private readonly AnnotationDocument _document;
    private readonly Func<Guid> _desktop;
    private readonly Func<IReadOnlyList<CanvasSelectionItem>> _items;
    private readonly CanvasSelection _selection;

    private CanvasSelectionItem? _sourceItem;
    private ConnectionPort _sourcePort;
    private CanvasPoint _startPoint;
    private ConnectionTarget? _snapTarget;
    private CanvasPoint _currentPoint;
    private bool _active;

    public bool HasPendingInteraction => _active;
    public CanvasAnnotation? Preview { get; private set; }
    public ConnectionTarget? HoveredHandle { get; private set; }
    public ConnectionTarget? SnapTarget => _snapTarget;
    public double DefaultStroke { get; set; } = 8.0;
    public string DefaultColor { get; set; } = "#38BDF8";

    public event Action<CanvasAnnotation>? ConnectionCreated;

    public ConnectionCanvasTool(
        AnnotationDocument document,
        Func<Guid> desktop,
        Func<IReadOnlyList<CanvasSelectionItem>> items,
        CanvasSelection selection)
    {
        _document = document;
        _desktop = desktop;
        _items = items;
        _selection = selection;
    }

    public ConnectionTarget? FindHandleAt(CanvasPointer p)
    {
        var selectedIds = _selection.Ids;
        if (selectedIds.Count == 0) return null;
        ConnectionPort[] ports = [ConnectionPort.Top, ConnectionPort.Right, ConnectionPort.Bottom, ConnectionPort.Left];

        var pointerDip = p.View.LocalToDip(p.Position);

        // Handles belong to the focused item only: an unselected border must fall through
        // to normal selection instead of silently spawning a connection.
        foreach (var item in _items())
        {
            if (!selectedIds.Contains(item.Key) || !IsConnectable(item)) continue;
            if (!IsPointerNearBounds(item.Bounds, p.World, p.View.Zoom, 40)) continue;

            foreach (var port in ports)
            {
                var portWorld = AnnotationGeometry.PortPosition(item.Bounds, port);
                var portDip = p.View.LocalToDip(p.View.WorldToLocal(portWorld));
                double dx = portDip.X - pointerDip.X;
                double dy = portDip.Y - pointerDip.Y;
                if (dx * dx + dy * dy <= HandleHitRadiusDip * HandleHitRadiusDip)
                {
                    return new ConnectionTarget(item, port, portWorld);
                }
            }
        }
        return null;
    }

    public void UpdateHover(CanvasPointer p)
    {
        if (_active) return;
        HoveredHandle = FindHandleAt(p);
    }

    public bool PointerDown(CanvasPointer p)
    {
        if (p.Button != CanvasPointerButton.Left) return false;
        var handle = FindHandleAt(p);
        if (handle == null) return false;

        _sourceItem = handle.Item;
        _sourcePort = handle.Port;
        _startPoint = handle.Point;
        _currentPoint = p.World;
        _active = true;
        _snapTarget = null;
        UpdatePreview();
        return true;
    }

    public void PointerMove(CanvasPointer p)
    {
        if (!_active || _sourceItem == null)
        {
            HoveredHandle = FindHandleAt(p);
            return;
        }

        _currentPoint = p.World;
        _snapTarget = FindSnapTarget(p);
        UpdatePreview();
    }

    public void PointerUp(CanvasPointer p)
    {
        if (!_active || _sourceItem == null)
        {
            Cancel();
            return;
        }

        PointerMove(p);

        var snap = _snapTarget;
        var source = _sourceItem;
        var sourcePort = _sourcePort;
        var start = _startPoint;
        var end = snap != null ? snap.Point : _currentPoint;
        double dragDistDip = AnnotationGeometry.Distance(
            p.View.LocalToDip(p.View.WorldToLocal(start)),
            p.View.LocalToDip(p.Position));

        Cancel();

        // Reject micro-drags / accidental clicks
        if (dragDistDip < 8.0) return;

        var desktopId = _desktop();
        var arrow = new CanvasAnnotation(
            Id: Guid.NewGuid(),
            DesktopId: desktopId,
            Kind: AnnotationKind.Arrow,
            A: start,
            B: end,
            Color: DefaultColor,
            Stroke: DefaultStroke,
            Routing: ConnectorRouting.Curved,
            SourceId: source.Key.Id,
            SourcePort: sourcePort,
            SourceEphemeral: source.Ephemeral,
            TargetId: snap?.Item.Key.Id,
            TargetPort: snap?.Port ?? ConnectionPort.Auto,
            TargetEphemeral: snap?.Item.Ephemeral ?? false,
            Text: "");

        _document.Put(arrow);
        ConnectionCreated?.Invoke(arrow);
    }

    public void Cancel()
    {
        _active = false;
        _sourceItem = null;
        _snapTarget = null;
        Preview = null;
        HoveredHandle = null;
    }

    public CanvasCursor CursorAt(CanvasPointer p)
    {
        if (_active) return CanvasCursor.Crosshair;
        return FindHandleAt(p) != null ? CanvasCursor.Crosshair : CanvasCursor.Default;
    }

    /// <summary>Cards, projects and note/shape annotations can anchor connectors; arrows cannot.</summary>
    public static bool IsConnectable(CanvasSelectionItem item)
        => item.Key.Kind != CanvasItemKind.Annotation || item.Annotation is { Kind: not AnnotationKind.Arrow };

    private ConnectionTarget? FindSnapTarget(CanvasPointer p)
    {
        if (_sourceItem == null) return null;
        var allItems = _items();
        var pointerDip = p.View.LocalToDip(p.Position);
        ConnectionPort[] ports = [ConnectionPort.Top, ConnectionPort.Right, ConnectionPort.Bottom, ConnectionPort.Left];

        ConnectionTarget? portTarget = null;
        double bestDistSq = MagneticSnapRadiusDip * MagneticSnapRadiusDip;
        ConnectionTarget? bodyTarget = null;
        double bodyArea = double.MaxValue;

        foreach (var item in allItems)
        {
            if (item.Key == _sourceItem.Key || !IsConnectable(item)) continue;
            if (!IsPointerNearBounds(item.Bounds, p.World, p.View.Zoom, 80)) continue;

            foreach (var port in ports)
            {
                var portWorld = AnnotationGeometry.PortPosition(item.Bounds, port);
                var portDip = p.View.LocalToDip(p.View.WorldToLocal(portWorld));
                double dx = portDip.X - pointerDip.X;
                double dy = portDip.Y - pointerDip.Y;
                double distSq = dx * dx + dy * dy;
                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    portTarget = new ConnectionTarget(item, port, portWorld);
                }
            }

            // Dropping anywhere on a target attaches too. The smallest item under the pointer
            // wins, so a note lying on a card connects and the result never depends on the
            // order items happen to be enumerated in.
            double area = item.Bounds.Width * item.Bounds.Height;
            if (area < bodyArea && CanvasSelection.Contains(item.Bounds, p.World))
            {
                var autoPorts = AnnotationGeometry.AutoPorts(_sourceItem.Bounds, item.Bounds);
                bodyTarget = new ConnectionTarget(item, autoPorts.Target, AnnotationGeometry.PortPosition(item.Bounds, autoPorts.Target));
                bodyArea = area;
            }
        }
        // An explicit port dot is a stronger intent than the body of a larger item.
        return portTarget ?? bodyTarget;
    }

    private void UpdatePreview()
    {
        if (_sourceItem == null) return;
        var endPoint = _snapTarget != null ? _snapTarget.Point : _currentPoint;
        var targetPort = _snapTarget != null ? _snapTarget.Port : ConnectionPort.Auto;
        var targetId = _snapTarget?.Item.Key.Id;

        Preview = new CanvasAnnotation(
            Id: Guid.Empty,
            DesktopId: _desktop(),
            Kind: AnnotationKind.Arrow,
            A: _startPoint,
            B: endPoint,
            Color: DefaultColor,
            Stroke: DefaultStroke,
            Routing: ConnectorRouting.Curved,
            SourceId: _sourceItem.Key.Id,
            SourcePort: _sourcePort,
            TargetId: targetId,
            TargetPort: targetPort);
    }

    private static bool IsPointerNearBounds(CanvasRect bounds, CanvasPoint world, double zoom, double marginDip)
    {
        double marginWorld = marginDip / Math.Max(0.001, zoom);
        return world.X >= bounds.X - marginWorld &&
               world.X <= bounds.X + bounds.Width + marginWorld &&
               world.Y >= bounds.Y - marginWorld &&
               world.Y <= bounds.Y + bounds.Height + marginWorld;
    }
}

/// <summary>Dropping an arrow end decides what it attaches to; empty space detaches it.</summary>
public static class ConnectionRetarget
{
    private static readonly ConnectionPort[] Ports = [ConnectionPort.Top, ConnectionPort.Right, ConnectionPort.Bottom, ConnectionPort.Left];

    public static CanvasAnnotation Resolve(CanvasAnnotation arrow, bool sourceEndpoint, CanvasPoint point,
        IReadOnlyList<CanvasSelectionItem> items, double zoom = 1)
    {
        var otherId = sourceEndpoint ? arrow.TargetId : arrow.SourceId;
        // The magnet is a physical distance, so it reaches the same amount at every zoom level.
        double reach = ConnectionCanvasTool.MagneticSnapRadiusDip / Math.Max(0.001, zoom);
        double nearest = double.MaxValue;
        CanvasSelectionItem? target = null;
        ConnectionPort port = ConnectionPort.Auto;

        // A port dot is the same target whether the arrow is dropped on it or drawn towards it.
        foreach (var item in items)
        {
            if (!ConnectionCanvasTool.IsConnectable(item) || item.Key.Id == arrow.Id || item.Key.Id == otherId) continue;
            if (!IsWithin(item.Bounds, point, reach)) continue;
            foreach (var candidate in Ports)
            {
                double distance = AnnotationGeometry.Distance(point, AnnotationGeometry.PortPosition(item.Bounds, candidate));
                if (distance > reach || distance >= nearest) continue;
                nearest = distance;
                target = item;
                port = candidate;
            }
        }

        if (target is null)
        {
            // Dropping inside an item still attaches: the smallest item under the pointer wins.
            double smallest = double.MaxValue;
            foreach (var item in items)
            {
                if (!ConnectionCanvasTool.IsConnectable(item) || item.Key.Id == arrow.Id || item.Key.Id == otherId) continue;
                if (!CanvasSelection.Contains(item.Bounds, point)) continue;
                double area = item.Bounds.Width * item.Bounds.Height;
                if (area >= smallest) continue;
                smallest = area;
                target = item;
            }
            if (target != null) port = NearestPort(target.Bounds, point);
        }

        if (target is null)
            return sourceEndpoint
                ? arrow with { SourceId = null, SourcePort = ConnectionPort.Auto, SourceEphemeral = false }
                : arrow with { TargetId = null, TargetPort = ConnectionPort.Auto, TargetEphemeral = false };

        var anchor = AnnotationGeometry.PortPosition(target.Bounds, port);
        return sourceEndpoint
            ? arrow with { A = anchor, SourceId = target.Key.Id, SourcePort = port, SourceEphemeral = target.Ephemeral }
            : arrow with { B = anchor, TargetId = target.Key.Id, TargetPort = port, TargetEphemeral = target.Ephemeral };
    }

    private static bool IsWithin(CanvasRect bounds, CanvasPoint point, double margin)
        => point.X >= bounds.X - margin && point.X <= bounds.X + bounds.Width + margin
        && point.Y >= bounds.Y - margin && point.Y <= bounds.Y + bounds.Height + margin;

    public static ConnectionPort NearestPort(CanvasRect bounds, CanvasPoint point)
    {
        ConnectionPort nearest = ConnectionPort.Top;
        double best = double.MaxValue;
        foreach (var port in Ports)
        {
            double distance = AnnotationGeometry.Distance(point, AnnotationGeometry.PortPosition(bounds, port));
            if (distance < best) { best = distance; nearest = port; }
        }
        return nearest;
    }
}
