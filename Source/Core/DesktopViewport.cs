using System;
using System.Collections.Generic;

namespace CanvasDesktop;

/// <summary>Screen-space recovery policy, independent of the canvas camera and Win32.</summary>
internal static class DesktopViewport
{
    public static WindowRect Fit(WindowRect rect, IReadOnlyList<(int x, int y, int w, int h)> screens)
    {
        (int x, int y, int w, int h) nearest = (0, 0, 1920, 1080);
        double bestDistance = double.MaxValue;
        long bestOverlap = -1;
        foreach (var screen in screens)
        {
            if (screen.w <= 0 || screen.h <= 0) continue;
            // Preserve windows already contained in any monitor, including negative origins.
            if (rect.W > 0 && rect.H > 0 && rect.X >= screen.x && rect.Y >= screen.y
                && (long)rect.X + rect.W <= (long)screen.x + screen.w
                && (long)rect.Y + rect.H <= (long)screen.y + screen.h)
                return rect;

            long overlapW = Math.Max(0, Math.Min((long)rect.X + rect.W, (long)screen.x + screen.w)
                - Math.Max((long)rect.X, screen.x));
            long overlapH = Math.Max(0, Math.Min((long)rect.Y + rect.H, (long)screen.y + screen.h)
                - Math.Max((long)rect.Y, screen.y));
            long overlap = overlapW * overlapH;
            double cx = rect.X + rect.W / 2.0, cy = rect.Y + rect.H / 2.0;
            double dx = cx - Math.Clamp(cx, screen.x, (double)screen.x + screen.w);
            double dy = cy - Math.Clamp(cy, screen.y, (double)screen.y + screen.h);
            double distance = dx * dx + dy * dy;
            if (overlap > bestOverlap || (overlap == bestOverlap && distance < bestDistance))
            {
                nearest = screen;
                bestOverlap = overlap;
                bestDistance = distance;
            }
        }

        int w = Math.Clamp(rect.W, 1, nearest.w), h = Math.Clamp(rect.H, 1, nearest.h);
        int x = (int)Math.Clamp((long)rect.X, nearest.x, (long)nearest.x + nearest.w - w);
        int y = (int)Math.Clamp((long)rect.Y, nearest.y, (long)nearest.y + nearest.h - h);
        return new WindowRect(x, y, w, h);
    }
}
