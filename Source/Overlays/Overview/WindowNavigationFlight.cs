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

        // --- Continuous Horizontal Progress P_xy(u) across full [0, 1] ---
        // Uses SmoothQuintic across the entire journey:
        // Starts with zero velocity/acceleration (liftoff is predominantly vertical),
        // smoothly accelerates horizontally, cruises, and decelerates to a stop
        // at the destination right as vertical descent completes.
        double easedH = SmoothQuintic(u);
        double curX = _from.X + (_to.X - _from.X) * easedH;
        double curY = _from.Y + (_to.Y - _from.Y) * easedH;

        // --- Continuous Vertical Altitude / Zoom Curve Z(u) across full [0, 1] ---
        double curZoom;
        if (_hasClimb)
        {
            if (Math.Abs(_from.Zoom - _cruisingZoom) < 1e-12 && Math.Abs(_to.Zoom - _cruisingZoom) < 1e-12)
            {
                curZoom = _from.Zoom;
            }
            else
            {
                double logFrom = Math.Log(_from.Zoom);
                double logTo = Math.Log(_to.Zoom);
                double logCruise = Math.Log(_cruisingZoom);

                // Smooth continuous curve:
                // First half (u in [0, 0.5]): climbs smoothly from logFrom to logCruise,
                //   starting with zero vertical speed, accelerating upward, and slowing down upward speed near apex.
                // Second half (u in [0.5, 1.0]): crests at logCruise, initiates decline,
                //   and eases vertical descent to stop simultaneously when the horizontal arrival stops at u = 1.0.
                if (u <= 0.5)
                {
                    double tAscent = u * 2.0;
                    double easedAscent = SmoothQuintic(tAscent);
                    curZoom = Math.Exp(logFrom + (logCruise - logFrom) * easedAscent);
                }
                else
                {
                    double tDescent = (u - 0.5) * 2.0;
                    double easedDescent = SmoothQuintic(tDescent);
                    curZoom = Math.Exp(logCruise + (logTo - logCruise) * easedDescent);
                }
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
