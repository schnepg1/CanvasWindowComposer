using System;

namespace CanvasDesktop;

/// <summary>
/// World-space camera for the overview overlays. Pure math — owns position +
/// zoom, exposes pan / zoom-to-cursor / center / world-from-virtual operations.
/// Reads <see cref="IScreens.VirtualScreen"/> for the centering math.
/// </summary>
internal sealed class OverviewCamera
{
    public const double ZoomMin = 0.02;
    public const double ZoomMax = 1.0;
    public const double AbsoluteZoomMax = 10.0;
    public const double ZoomStep = 0.1;
    public const double ZoomEpsilon = 0.0001;

    private readonly IScreens _screens;

    public double X { get; private set; }
    public double Y { get; private set; }
    public double Zoom { get; private set; } = 1.0;
    public double MaxZoom { get; set; } = ZoomMax;

    public OverviewCamera(IScreens screens)
    {
        _screens = screens;
    }

    public void SetTo(double x, double y, double zoom)
    {
        X = x;
        Y = y;
        Zoom = zoom;
    }

    /// <summary>Copy camera state from the main canvas.</summary>
    public void SyncFrom(Canvas canvas)
    {
        X = canvas.CamX;
        Y = canvas.CamY;
        Zoom = canvas.Zoom;
    }

    /// <summary>Pan in virtual-screen pixels; converted to world via current zoom.</summary>
    public void PanByVirtual(double virtualDx, double virtualDy)
    {
        X -= virtualDx / Zoom;
        Y -= virtualDy / Zoom;
    }

    /// <summary>
    /// Sets zoom keeping the specified world point fixed under the virtual screen cursor.
    /// Returns true if zoom changed.
    /// </summary>
    public bool SetZoomAroundWorldPoint(int virtualX, int virtualY, double worldX, double worldY, double targetZoom)
    {
        double clamped = Math.Clamp(targetZoom, ZoomMin, MaxZoom);
        if (Math.Abs(clamped - Zoom) < ZoomEpsilon) return false;

        Zoom = clamped;
        X = worldX - virtualX / Zoom;
        Y = worldY - virtualY / Zoom;
        return true;
    }

    /// <summary>
    /// Zoom by <paramref name="notches"/> (positive = in) keeping the world
    /// point under the cursor fixed. Returns true if zoom changed.
    /// </summary>
    public bool ZoomToCursor(int virtualX, int virtualY, double notches)
    {
        var (wx, wy) = WorldFromVirtual(virtualX, virtualY);
        return SetZoomAroundWorldPoint(virtualX, virtualY, wx, wy, Zoom + notches * ZoomStep * Zoom);
    }

    /// <summary>Reduce zoom immediately while preserving the main screen's center.</summary>
    public bool ZoomOutForShortcut(double amount)
    {
        if (!double.IsFinite(amount) || amount <= 0) return false;
        var primary = _screens.PrimaryBounds;
        return ZoomToCursor(primary.X + primary.Width / 2, primary.Y + primary.Height / 2,
            -Math.Clamp(amount, 0, 0.80) / ZoomStep);
    }

    /// <summary>Center on a world rect (does not change zoom).</summary>
    public void CenterOnWorld(double worldX, double worldY, double worldW, double worldH)
    {
        var pb = _screens.PrimaryBounds;
        X = worldX + worldW / 2 - pb.Width / (2 * Zoom);
        Y = worldY + worldH / 2 - pb.Height / (2 * Zoom);
    }

    /// <summary>World coordinates corresponding to a virtual-screen pixel.</summary>
    public (double x, double y) WorldFromVirtual(int virtualX, int virtualY)
    {
        var world = new CanvasViewTransform(X, Y, Zoom, default).VirtualToWorld(new(virtualX, virtualY));
        return (world.X, world.Y);
    }

    /// <summary>
    /// Camera position for the main canvas on close. Uses primary monitor
    /// dimensions so the result is independent of how many monitors are attached.
    /// </summary>
    public (double x, double y) ViewportCamera
    {
        get
        {
            var pb = _screens.PrimaryBounds;
            double ox = pb.Width * (1.0 / Zoom - 1.0) / 2.0;
            double oy = pb.Height * (1.0 / Zoom - 1.0) / 2.0;
            return (X + ox, Y + oy);
        }
    }
}
