using System;
using System.Collections.Generic;

namespace CanvasDesktop;

internal enum ZoneResizeCorner { TopLeft, TopRight, BottomRight, BottomLeft }
internal readonly record struct ZoneResizeHandle(ZoneResizeCorner Corner, ZoneLabelRect Rect);

/// <summary>One physical-pixel layout shared by corner drawing and pointer hit testing.</summary>
internal static class ZoneResizeLayout
{
    public static IReadOnlyList<ZoneResizeHandle> Get(WorldRect bounds, double camX, double camY,
        double zoom, ScreenRect monitor, double dpiX, double dpiY, bool hitTarget = false)
        => Get(bounds, new CanvasViewTransform(camX, camY, zoom,
            new(monitor.X, monitor.Y, monitor.Width, monitor.Height), dpiX, dpiY), hitTarget);

    public static IReadOnlyList<ZoneResizeHandle> Get(WorldRect bounds, CanvasViewTransform view, bool hitTarget = false)
    {
        var handles = new List<ZoneResizeHandle>(4);
        var rect = view.WorldToLocal(new CanvasRect(bounds.X, bounds.Y, bounds.W, bounds.H));
        double x = rect.X, y = rect.Y, w = rect.Width, h = rect.Height;
        var monitor = view.Monitor;
        double dpiX = view.DpiX, dpiY = view.DpiY;
        if (w < 24 * dpiX || h < 24 * dpiY) return handles;
        double size = hitTarget ? 18 : 8;
        void Add(ZoneResizeCorner corner, double cx, double cy)
        {
            if (cx < 0 || cy < 0 || cx >= monitor.Width || cy >= monitor.Height) return;
            handles.Add(new(corner, new(cx - size * dpiX / 2, cy - size * dpiY / 2, size * dpiX, size * dpiY)));
        }
        Add(ZoneResizeCorner.TopLeft, x, y);
        Add(ZoneResizeCorner.TopRight, x + w, y);
        Add(ZoneResizeCorner.BottomRight, x + w, y + h);
        Add(ZoneResizeCorner.BottomLeft, x, y + h);
        return handles;
    }

    public static bool TryResize(WorldRect original, ZoneResizeCorner corner, double dx, double dy, out WorldRect bounds)
    {
        bounds = original;
        if (!double.IsFinite(dx) || !double.IsFinite(dy)) return false;
        // Keep the opposite corner fixed and prevent crossing it. Small pre-existing
        // areas may grow without an initial jump when a handle is pressed.
        double minW = Math.Min(64, original.W), minH = Math.Min(64, original.H);
        bool left = corner is ZoneResizeCorner.TopLeft or ZoneResizeCorner.BottomLeft;
        bool top = corner is ZoneResizeCorner.TopLeft or ZoneResizeCorner.TopRight;
        bounds.W = Math.Max(minW, original.W + (left ? -dx : dx));
        bounds.H = Math.Max(minH, original.H + (top ? -dy : dy));
        if (left) bounds.X = original.X + original.W - bounds.W;
        if (top) bounds.Y = original.Y + original.H - bounds.H;
        return ZoneGeometry.ValidBounds(bounds);
    }
}
