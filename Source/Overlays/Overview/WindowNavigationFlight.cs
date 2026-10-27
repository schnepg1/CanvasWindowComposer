using System;

namespace CanvasDesktop;

internal sealed class WindowNavigationFlight(CanvasCamera from, CanvasCamera to)
{
    public const double Duration = .45;
    public CanvasCamera Sample(double seconds)
    {
        double t = double.IsFinite(seconds) ? Math.Clamp(seconds / Duration, 0, 1) : 0;
        double eased = t * t * t * (t * (t * 6 - 15) + 10);
        return new(from.X + (to.X - from.X) * eased, from.Y + (to.Y - from.Y) * eased,
            from.Zoom + (to.Zoom - from.Zoom) * eased);
    }

    internal static CanvasCamera Target(WorldRect world, double zoom, ScreenRect viewport)
        => new(world.X + world.W / 2 - (viewport.X + viewport.Width / 2.0) / zoom,
            world.Y + world.H / 2 - (viewport.Y + viewport.Height / 2.0) / zoom, zoom);
}
