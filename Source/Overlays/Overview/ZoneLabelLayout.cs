using System;

namespace CanvasDesktop;

internal readonly record struct ZoneLabelRect(double X, double Y, double Width, double Height)
{
    public bool Contains(double x, double y) => x >= X && y >= Y && x < X + Width && y < Y + Height;
}

/// <summary>Shared physical-pixel layout for rendering and hit testing zone headers.</summary>
internal static class ZoneLabelLayout
{
    public static ZoneLabelRect? Get(WorldRect bounds, double camX, double camY, double zoom,
        ScreenRect monitor, double dpiX, double dpiY)
        => Get(bounds, new CanvasViewTransform(camX, camY, zoom,
            new(monitor.X, monitor.Y, monitor.Width, monitor.Height), dpiX, dpiY));

    public static ZoneLabelRect? Get(WorldRect bounds, CanvasViewTransform view)
    {
        var rect = view.WorldToLocal(new CanvasRect(bounds.X, bounds.Y, bounds.W, bounds.H));
        double x = rect.X, y = rect.Y, width = rect.Width, height = rect.Height;
        var monitor = view.Monitor;
        double dpiX = view.DpiX, dpiY = view.DpiY;
        if (width < 40 * dpiX || x >= monitor.Width || y >= monitor.Height || x + width <= 0 || y + height <= 0) return null;
        double labelX = Math.Max(4 * dpiX, x + 10 * dpiX);
        double labelY = Math.Max(64 * dpiY, y + 10 * dpiY);
        if (labelY + 28 * dpiY > y + height) return null;
        return new(labelX, labelY, Math.Min(284 * dpiX, width - 16 * dpiX), 28 * dpiY);
    }
}
