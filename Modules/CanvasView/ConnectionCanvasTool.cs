using System;
using System.Collections.Generic;
using System.Linq;

namespace CanvasDesktop.CanvasView;

public sealed record ConnectionTarget(CanvasSelectionItem Item, ConnectionPort Port, CanvasPoint Point);

public sealed class ConnectionCanvasTool : ICanvasTool
{
    public const double HandleHitRadiusDip = 14.0;
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
        var allItems = _items();
        var selectedIds = _selection.Ids;
        ConnectionPort[] ports = [ConnectionPort.Top, ConnectionPort.Right, ConnectionPort.Bottom, ConnectionPort.Left];

        // Check selected items first, then unselected non-annotation items
        var selectedItems = allItems.Where(i => i.Key.Kind != CanvasItemKind.Annotation && selectedIds.Contains(i.Key));
        var otherItems = allItems.Where(i => i.Key.Kind != CanvasItemKind.Annotation && !selectedIds.Contains(i.Key));
        var candidateItems = selectedItems.Concat(otherItems);

        var pointerDip = p.View.LocalToDip(p.Position);

        foreach (var item in candidateItems)
        {
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
            TargetId: snap?.Item.Key.Id,
            TargetPort: snap?.Port ?? ConnectionPort.Auto,
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

    private ConnectionTarget? FindSnapTarget(CanvasPointer p)
    {
        if (_sourceItem == null) return null;
        var allItems = _items();
        var pointerDip = p.View.LocalToDip(p.Position);
        ConnectionPort[] ports = [ConnectionPort.Top, ConnectionPort.Right, ConnectionPort.Bottom, ConnectionPort.Left];

        ConnectionTarget? bestTarget = null;
        double bestDistSq = MagneticSnapRadiusDip * MagneticSnapRadiusDip;

        foreach (var item in allItems)
        {
            if (item.Key == _sourceItem.Key || item.Key.Kind == CanvasItemKind.Annotation) continue;
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
                    bestTarget = new ConnectionTarget(item, port, portWorld);
                }
            }

            // Also snap if cursor is dropped inside the target item
            if (bestTarget == null && CanvasSelection.Contains(item.Bounds, p.World))
            {
                var autoPorts = AnnotationGeometry.AutoPorts(_sourceItem.Bounds, item.Bounds);
                var autoPortWorld = AnnotationGeometry.PortPosition(item.Bounds, autoPorts.Target);
                bestTarget = new ConnectionTarget(item, autoPorts.Target, autoPortWorld);
            }
        }
        return bestTarget;
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
