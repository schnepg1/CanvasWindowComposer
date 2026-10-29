using System;
using System.Linq;

namespace CanvasDesktop;

/// <summary>Keep a live arrival visible at the active camera, allowing overlap and preserving its size/state.</summary>
internal static class LiveWindowPlacement
{
    public static WorldRect Place(WorldRect window, CanvasCamera sourceCamera, ArrivalViewport view)
    {
        if (!double.IsFinite(view.Camera.Zoom) || view.Camera.Zoom <= 0) return window;
        // Native geometry was enrolled against the main camera. The user may be
        // viewing a different camera in overview; retain the app's screen position there.
        double x = (window.X - sourceCamera.X) * sourceCamera.Zoom;
        double y = (window.Y - sourceCamera.Y) * sourceCamera.Zoom;
        var (width, height) = view.Camera.ProjectSize(window.W, window.H);
        var screens = view.Screens.Where(s => s.Width > 0 && s.Height > 0).ToArray();
        double Overlap(ScreenRect screen) => Math.Max(0, Math.Min(x + width, screen.Right) - Math.Max(x, screen.X))
            * Math.Max(0, Math.Min(y + height, screen.Bottom) - Math.Max(y, screen.Y));
        if (screens.Length > 0)
        {
            var screen = screens.OrderByDescending(Overlap).First();
            if (Overlap(screen) == 0)
            {
                x = screen.X + (screen.Width - width) / 2.0;
                y = screen.Y + (screen.Height - height) / 2.0;
            }
            // Oversized dialogs keep their size, with the top-left controls reachable.
            // Never reject a live arrival because it cannot fit without overlapping.
            x = Math.Clamp(x, screen.X, Math.Max(screen.X, screen.Right - width));
            y = Math.Clamp(y, screen.Y, Math.Max(screen.Y, screen.Bottom - height));
        }
        window.X = view.Camera.X + x / view.Camera.Zoom;
        window.Y = view.Camera.Y + y / view.Camera.Zoom;
        window.InInventory = window.PinnedToScreen = false;
        return window;
    }
}
