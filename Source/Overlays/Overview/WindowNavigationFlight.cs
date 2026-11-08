using System;

namespace CanvasDesktop;

internal sealed class WindowNavigationFlight
{
    public const double Duration = .45;
    private readonly CanvasCamera _from;
    private readonly CanvasCamera _to;
    private readonly double _fromFocalX;
    private readonly double _fromFocalY;
    private readonly double _toFocalX;
    private readonly double _toFocalY;
    private readonly double _vpCenterX;
    private readonly double _vpCenterY;
    private readonly double _distance;
    private readonly double _cruisingZoom;
    private readonly bool _hasClimb;

    public double Distance => _distance;
    public double CruisingZoom => _cruisingZoom;
    public CanvasPoint FromFocalPoint => new(_fromFocalX, _fromFocalY);
    public CanvasPoint ToFocalPoint => new(_toFocalX, _toFocalY);

    public WindowNavigationFlight(CanvasCamera from, CanvasCamera to, ScreenRect? viewport = null)
    {
        _from = from;
        _to = to;

        var vp = viewport ?? new ScreenRect(0, 0, 1920, 1080);
        _vpCenterX = vp.X + vp.Width / 2.0;
        _vpCenterY = vp.Y + vp.Height / 2.0;

        // Compute world focal center: the world-space coordinate displayed at the screen center.
        _fromFocalX = from.X + _vpCenterX / from.Zoom;
        _fromFocalY = from.Y + _vpCenterY / from.Zoom;
        _toFocalX = to.X + _vpCenterX / to.Zoom;
        _toFocalY = to.Y + _vpCenterY / to.Zoom;

        double dx = _toFocalX - _fromFocalX;
        double dy = _toFocalY - _fromFocalY;
        _distance = Math.Sqrt(dx * dx + dy * dy);

        // If distance < 300px (nearby window or small recenter), no cruising altitude climb is added.
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

    public CanvasPoint SampleFocalPoint(double seconds)
    {
        double u = double.IsFinite(seconds) ? Math.Clamp(seconds / Duration, 0, 1) : 0;
        if (u <= 0) return FromFocalPoint;
        if (u >= 1) return ToFocalPoint;

        double pXy = SepticSmoothstep(u);
        double curFocalX = _fromFocalX + (_toFocalX - _fromFocalX) * pXy;
        double curFocalY = _fromFocalY + (_toFocalY - _fromFocalY) * pXy;
        return new CanvasPoint(curFocalX, curFocalY);
    }

    public CanvasCamera Sample(double seconds)
    {
        double u = double.IsFinite(seconds) ? Math.Clamp(seconds / Duration, 0, 1) : 0;
        if (u <= 0) return _from;
        if (u >= 1) return _to;

        // --- Straight 2D Canvas Panning Progress P_xy(u) across full [0, 1] ---
        // Uses SepticSmoothstep (C3 continuous):
        // Liftoff accelerates slowly at first (allowing zoom-out altitude to climb strongly first),
        // surges across the canvas at peak cruising speed, and smoothly decelerates before landing.
        // The focal center moves in a strictly straight 2D line from start to target.
        double pXy = SepticSmoothstep(u);
        double curFocalX = _fromFocalX + (_toFocalX - _fromFocalX) * pXy;
        double curFocalY = _fromFocalY + (_toFocalY - _fromFocalY) * pXy;

        // --- Altitude / Zoom Curve Z(u) across full [0, 1] ---
        // Altitude climbs upward strongly first (zoom-out), starting smoothly without exploding,
        // crests at cruising altitude at midpoint, initiates decline, and slows down descent
        // to touch down gently at destination.
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
            double eased = SepticSmoothstep(u);
            curZoom = _from.Zoom + (_to.Zoom - _from.Zoom) * eased;
        }

        // Reconstruct top-left camera corner from the straight-line world focal center
        double curX = curFocalX - _vpCenterX / curZoom;
        double curY = curFocalY - _vpCenterY / curZoom;

        return new CanvasCamera(curX, curY, curZoom);
    }

    private static double SmoothQuintic(double t)
        => t * t * t * (t * (t * 6.0 - 15.0) + 10.0);

    private static double SepticSmoothstep(double t)
        => t * t * t * t * (35.0 - 84.0 * t + 70.0 * t * t - 20.0 * t * t * t);

    internal static CanvasCamera Target(WorldRect world, double zoom, ScreenRect viewport)
        => new(world.X + world.W / 2.0 - (viewport.X + viewport.Width / 2.0) / zoom,
            world.Y + world.H / 2.0 - (viewport.Y + viewport.Height / 2.0) / zoom, zoom);
}
