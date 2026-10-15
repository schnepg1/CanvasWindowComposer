namespace CanvasDesktop.CanvasView;

public readonly record struct CanvasPoint(double X, double Y);
public readonly record struct CanvasRect(double X, double Y, double Width, double Height);

/// <summary>
/// Immutable projection shared by pointer input, painting and hit testing.
/// Local coordinates are physical monitor pixels; DIPs are a separate conversion.
/// This geometry has no native window placement minimums or window state.
/// </summary>
public readonly record struct CanvasViewTransform
{
    public double CameraX { get; }
    public double CameraY { get; }
    public double Zoom { get; }
    public CanvasRect Monitor { get; }
    public double DpiX { get; }
    public double DpiY { get; }

    public CanvasViewTransform(double cameraX, double cameraY, double zoom,
        CanvasRect monitor, double dpiX = 1, double dpiY = 1)
    {
        if (!double.IsFinite(cameraX) || !double.IsFinite(cameraY))
            throw new ArgumentOutOfRangeException(nameof(cameraX));
        if (!double.IsFinite(zoom) || zoom <= 0) throw new ArgumentOutOfRangeException(nameof(zoom));
        if (!double.IsFinite(dpiX) || dpiX <= 0) throw new ArgumentOutOfRangeException(nameof(dpiX));
        if (!double.IsFinite(dpiY) || dpiY <= 0) throw new ArgumentOutOfRangeException(nameof(dpiY));
        CameraX = cameraX; CameraY = cameraY; Zoom = zoom;
        Monitor = monitor; DpiX = dpiX; DpiY = dpiY;
    }

    public CanvasPoint VirtualToWorld(CanvasPoint p) => new(p.X / Zoom + CameraX, p.Y / Zoom + CameraY);
    public CanvasPoint WorldToVirtual(CanvasPoint p) => new((p.X - CameraX) * Zoom, (p.Y - CameraY) * Zoom);
    public CanvasPoint LocalToWorld(CanvasPoint p) => VirtualToWorld(new(p.X + Monitor.X, p.Y + Monitor.Y));
    public CanvasPoint WorldToLocal(CanvasPoint p)
    {
        var v = WorldToVirtual(p);
        return new(v.X - Monitor.X, v.Y - Monitor.Y);
    }
    public CanvasPoint LocalToDip(CanvasPoint p) => new(p.X / DpiX, p.Y / DpiY);
    public CanvasPoint DipToLocal(CanvasPoint p) => new(p.X * DpiX, p.Y * DpiY);
    public CanvasRect WorldToLocal(CanvasRect r)
    {
        var p = WorldToLocal(new CanvasPoint(r.X, r.Y));
        return new(p.X, p.Y, r.Width * Zoom, r.Height * Zoom);
    }
    public CanvasRect LocalToDip(CanvasRect r) => new(r.X / DpiX, r.Y / DpiY, r.Width / DpiX, r.Height / DpiY);
    public CanvasRect WorldToDip(CanvasRect r) => LocalToDip(WorldToLocal(r));
}
