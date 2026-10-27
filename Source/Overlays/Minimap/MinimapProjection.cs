using System;
using System.Drawing;

namespace CanvasDesktop;

/// <summary>The shared world-to-minimap transform used for drawing and input.</summary>
internal readonly record struct MinimapProjection(double MinX, double MinY, double Scale, double OffsetX, double OffsetY)
{
    public static MinimapProjection Create((double minX, double minY, double maxX, double maxY)? extents,
        (double x, double y, double w, double h) viewport, int width, int height, double padding = 0.10)
    {
        double minX = viewport.x, minY = viewport.y, maxX = viewport.x + viewport.w, maxY = viewport.y + viewport.h;
        if (extents is var (ex1, ey1, ex2, ey2))
        {
            minX = Math.Min(minX, ex1); minY = Math.Min(minY, ey1);
            maxX = Math.Max(maxX, ex2); maxY = Math.Max(maxY, ey2);
        }
        double w = maxX - minX, h = maxY - minY;
        minX -= w * padding; minY -= h * padding;
        w *= 1 + 2 * padding; h *= 1 + 2 * padding;
        if (w < 1 || h < 1 || width <= 2 || height <= 2) return default;
        double scale = Math.Min((width - 2) / w, (height - 2) / h);
        return new(minX, minY, scale, (width - w * scale) / 2, (height - h * scale) / 2);
    }

    public bool IsValid => double.IsFinite(Scale) && Scale > 0;
    public (double x, double y) WorldFromMap(PointF point) =>
        (MinX + (point.X - OffsetX) / Scale, MinY + (point.Y - OffsetY) / Scale);
}
