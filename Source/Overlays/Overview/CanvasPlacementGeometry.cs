using System.Linq;
using System.Windows;

namespace CanvasDesktop;

internal static class CanvasPlacementGeometry
{
    internal static bool TryWorldAnchor(ArrivalViewport view, Point screen, WorldRect size, bool centered,
        out double x, out double y)
    {
        x = y = 0;
        bool Contains(ScreenRect r) => screen.X >= r.Left && screen.X < r.Right && screen.Y >= r.Top && screen.Y < r.Bottom;
        if (!view.Screens.Any(Contains) || view.Reserved?.Any(Contains) == true) return false;
        x = view.Camera.X + screen.X / view.Camera.Zoom - (centered ? size.W / 2 : 0);
        y = view.Camera.Y + screen.Y / view.Camera.Zoom - (centered ? size.H / 2 : 0);
        return true;
    }

    // Canvas thumbnails use the actual zoomed size; native windows enforce a minimum size.
    internal static WindowRect Project(WorldRect placed, CanvasCamera camera, bool desktop)
        => desktop ? camera.Project(placed)
            : new((int)((placed.X - camera.X) * camera.Zoom), (int)((placed.Y - camera.Y) * camera.Zoom),
                System.Math.Max(1, (int)(placed.W * camera.Zoom)), System.Math.Max(1, (int)(placed.H * camera.Zoom)));
}
