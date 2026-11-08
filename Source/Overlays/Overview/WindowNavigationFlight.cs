using System;

namespace CanvasDesktop;

internal sealed class WindowNavigationFlight
{
    public const double Duration = .45;
    private readonly CanvasCamera _from;
    private readonly CanvasCamera _to;
    private readonly double _distance;
    private readonly double _cruisingZoom;
    private readonly bool _hasClimb;

    public WindowNavigationFlight(CanvasCamera from, CanvasCamera to)
    {
        _from = from;
        _to = to;
        double dx = to.X - from.X;
        double dy = to.Y - from.Y;
        _distance = Math.Sqrt(dx * dx + dy * dy);

        // Option A: If distance < 300px (nearby window or small recenter), no cruising altitude climb is added.
        _hasClimb = _distance >= 300;
        if (_hasClimb)
        {
            // Calculate cruising altitude in logarithmic zoom space.
            // Ground level: Zoom = 1.0 (log = 0). Overview: Zoom < 1.0 (log < 0).
            double logFrom = Math.Log(from.Zoom);
            double logTo = Math.Log(to.Zoom);
            double logMin = Math.Min(logFrom, logTo);

            // Scale climb with distance: up to ~2.8x zoom-out factor.
            double distanceFactor = Math.Clamp((_distance - 300.0) / 3700.0, 0.0, 1.0);
            double climbLog = distanceFactor * Math.Log(2.8);

            // Cruising zoom clamped so it does not zoom out further than 0.18
            double targetLog = Math.Max(Math.Log(0.18), logMin - climbLog);
            _cruisingZoom = Math.Exp(targetLog);
        }
        else
        {
            _cruisingZoom = Math.Min(from.Zoom, to.Zoom);
        }
    }

    public CanvasCamera Sample(double seconds)
    {
        double u = double.IsFinite(seconds) ? Math.Clamp(seconds / Duration, 0, 1) : 0;
        if (u <= 0) return _from;
        if (u >= 1) return _to;

        // --- Horizontal Progress P_xy(u) ---
        // Helicopter lateral motion:
        // u in [0, 0.12]: Vertical takeoff only; lateral shift is 0.
        // u in [0.12, 0.88]: Smooth lateral acceleration, cruise, and deceleration.
        // u in [0.88, 1.0]: Vertical landing only; lateral shift is 100% complete.
        // Symmetric around u = 0.50 so P_xy(0.50) == 0.50 exactly.
        const double u1 = 0.12;
        const double u2 = 0.88;
        double tH = Math.Clamp((u - u1) / (u2 - u1), 0.0, 1.0);
        double easedH = SmoothQuintic(tH);

        double curX = _from.X + (_to.X - _from.X) * easedH;
        double curY = _from.Y + (_to.Y - _from.Y) * easedH;

        // --- Vertical Zoom / Altitude Profile Z(u) ---
        double curZoom;
        if (_hasClimb)
        {
            if (Math.Abs(_from.Zoom - _cruisingZoom) < 1e-12 && Math.Abs(_to.Zoom - _cruisingZoom) < 1e-12)
            {
                curZoom = _from.Zoom;
            }
            // Rapid vertical climb during takeoff: u in [0, 0.35]
            // Cruising altitude plateau: u in [0.35, 0.65]
            // Emphasized vertical descent during landing: u in [0.65, 1.0]
            else if (u <= 0.65)
            {
                double tClimb = Math.Clamp(u / 0.35, 0.0, 1.0);
                double easedClimb = SmoothQuintic(tClimb);
                curZoom = Math.Exp(Math.Log(_from.Zoom) + (Math.Log(_cruisingZoom) - Math.Log(_from.Zoom)) * easedClimb);
            }
            else
            {
                double tDesc = Math.Clamp((u - 0.65) / 0.35, 0.0, 1.0);
                double easedDesc = SmoothQuintic(tDesc);
                curZoom = Math.Exp(Math.Log(_cruisingZoom) + (Math.Log(_to.Zoom) - Math.Log(_cruisingZoom)) * easedDesc);
            }
        }
        else
        {
            double eased = SmoothQuintic(u);
            curZoom = _from.Zoom + (_to.Zoom - _from.Zoom) * eased;
        }

        return new CanvasCamera(curX, curY, curZoom);
    }

    private static double SmoothQuintic(double t)
        => t * t * t * (t * (t * 6.0 - 15.0) + 10.0);

    internal static CanvasCamera Target(WorldRect world, double zoom, ScreenRect viewport)
        => new(world.X + world.W / 2 - (viewport.X + viewport.Width / 2.0) / zoom,
            world.Y + world.H / 2 - (viewport.Y + viewport.Height / 2.0) / zoom, zoom);
}
