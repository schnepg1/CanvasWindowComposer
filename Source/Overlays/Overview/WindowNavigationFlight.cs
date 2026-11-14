using System;

namespace CanvasDesktop;

internal sealed class WindowNavigationFlight
{
    public const double Duration = 3.0;
    internal const double MinimumFlightDuration = 0.25;
    internal const double DefaultFullDurationDistance = 3500;
    internal const double DefaultFullClimbDistance = 3000;
    internal const double MinimumRampDistance = 500;
    internal const double MaximumRampDistance = 10000;
    internal const double NoClimbDistance = 300;
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
    private readonly double _climbFraction;
    private readonly double _flightDuration;
    private readonly double _cruisingScale;
    private readonly string _profile;

    public double Distance => _distance;
    public double CruisingZoom => _cruisingZoom;
    public double FlightDuration => _flightDuration;
    public double CruisingScale => _cruisingScale;
    public string Profile => _profile;
    public CanvasPoint FromFocalPoint => new(_fromFocalX, _fromFocalY);
    public CanvasPoint ToFocalPoint => new(_toFocalX, _toFocalY);

    public WindowNavigationFlight(
        CanvasCamera from,
        CanvasCamera to,
        ScreenRect? viewport = null,
        double? duration = null,
        double? cruisingScale = null,
        string? profile = null,
        double fullDurationDistance = DefaultFullDurationDistance,
        double fullClimbDistance = DefaultFullClimbDistance,
        bool fixedDuration = false)
    {
        _from = from;
        _to = to;
        _cruisingScale = Math.Clamp(cruisingScale ?? 1.0, 0.4, 2.5);
        _profile = string.IsNullOrWhiteSpace(profile) ? "Helicopter" : profile.Trim();

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
        fullDurationDistance = Math.Clamp(fullDurationDistance, MinimumRampDistance, MaximumRampDistance);
        fullClimbDistance = Math.Clamp(fullClimbDistance, MinimumRampDistance, MaximumRampDistance);
        double maximumDuration = Math.Clamp(duration ?? Duration, 0.1, 10.0);
        if (fixedDuration)
        {
            _flightDuration = maximumDuration;
        }
        else
        {
            double durationFraction = Math.Min(_distance / fullDurationDistance, 1);
            _flightDuration = MinimumFlightDuration + (maximumDuration - MinimumFlightDuration) * durationFraction;
        }

        _climbFraction = string.Equals(_profile, "Planar", StringComparison.OrdinalIgnoreCase)
            ? 0
            : Math.Clamp((_distance - NoClimbDistance) / (fullClimbDistance - NoClimbDistance), 0, 1);
        if (_climbFraction > 0)
        {
            // Calculate cruising altitude in logarithmic zoom space.
            // Ground level: Zoom = 1.0 (log = 0). Overview: Zoom < 1.0 (log < 0).
            double baseZoom = Math.Min(from.Zoom, to.Zoom);
            double maxFactor = string.Equals(_profile, "Cinematic", StringComparison.OrdinalIgnoreCase) ? 4.5
                : string.Equals(_profile, "Brisk", StringComparison.OrdinalIgnoreCase) ? 2.5 : 3.5;
            // Ramp towards the allowed full climb so it reaches the zoom floor only at the configured distance.
            double minZoom = Math.Max(0.05, 0.18 / _cruisingScale);
            double fullClimbLog = Math.Max(0, Math.Min(Math.Log(maxFactor) * _cruisingScale, Math.Log(baseZoom / minZoom)));
            _cruisingZoom = baseZoom * Math.Exp(-_climbFraction * fullClimbLog);
        }
        else
        {
            _cruisingZoom = Math.Min(from.Zoom, to.Zoom);
        }
    }

    public CanvasPoint SampleFocalPoint(double seconds)
    {
        double u = double.IsFinite(seconds) ? Math.Clamp(seconds / _flightDuration, 0, 1) : 0;
        if (u <= 0) return FromFocalPoint;
        if (u >= 1) return ToFocalPoint;

        double pXy = PanningSmoothstep(u, _profile);
        double curFocalX = _fromFocalX + (_toFocalX - _fromFocalX) * pXy;
        double curFocalY = _fromFocalY + (_toFocalY - _fromFocalY) * pXy;
        return new CanvasPoint(curFocalX, curFocalY);
    }

    public CanvasCamera Sample(double seconds)
    {
        double u = double.IsFinite(seconds) ? Math.Clamp(seconds / _flightDuration, 0, 1) : 0;
        if (u <= 0) return _from;
        if (u >= 1) return _to;

        // --- Decoupled 2D Canvas Panning Progress P_xy(u) across full [0, 1] ---
        // Uses smoothstep continuous curves:
        // Liftoff begins much slower (<2% lateral distance during initial climb),
        // accelerates into rapid cruising translation, flares over the landing zone (>98%),
        // and softly eases into final alignment.
        // The focal center moves in a strictly straight 2D line from start to target.
        double pXy = PanningSmoothstep(u, _profile);
        double curFocalX = _fromFocalX + (_toFocalX - _fromFocalX) * pXy;
        double curFocalY = _fromFocalY + (_toFocalY - _fromFocalY) * pXy;

        // --- Altitude / Zoom Curve Z(u) across full [0, 1] ---
        // Decoupled verticality:
        // Climbs smoothly and strongly in the first phase (u <= 0.42), crests at cruising altitude (0.42 - 0.50),
        // and smoothly begins descent from u = 0.50 onwards, gently decelerating into touchdown as horizontal
        // motion finishes simultaneously.
        double directZoom = Math.Abs(_from.Zoom - _to.Zoom) > 1e-12
            ? Math.Exp(Math.Log(_from.Zoom) + (Math.Log(_to.Zoom) - Math.Log(_from.Zoom)) * pXy)
            : _from.Zoom;
        double curZoom = directZoom;
        if (_climbFraction > 0)
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

                if (u <= 0.42)
                {
                    double tAscent = u / 0.42;
                    double easedAscent = SmoothQuintic(tAscent);
                    curZoom = Math.Exp(logFrom + (logCruise - logFrom) * easedAscent);
                }
                else if (u <= 0.50)
                {
                    curZoom = _cruisingZoom;
                }
                else
                {
                    double tDescent = (u - 0.50) / 0.50;
                    double easedDescent = SmoothQuintic(tDescent);
                    curZoom = Math.Exp(logCruise + (logTo - logCruise) * easedDescent);
                }
            }
            // Ease from direct endpoint zoom changes into the vertical flight profile as the route grows.
            if (_from.Zoom != _to.Zoom && _climbFraction < 1)
                curZoom = directZoom + (curZoom - directZoom) * _climbFraction;
        }

        // Reconstruct top-left camera corner from the straight-line world focal center
        double curX = curFocalX - _vpCenterX / curZoom;
        double curY = curFocalY - _vpCenterY / curZoom;

        return new CanvasCamera(curX, curY, curZoom);
    }

    private static double SmoothQuintic(double t)
        => t * t * t * (t * (t * 6.0 - 15.0) + 10.0);

    private static double PanningSmoothstep(double t, string profile)
    {
        if (string.Equals(profile, "Brisk", StringComparison.OrdinalIgnoreCase))
        {
            // Septic smoothstep (C3 continuous): t^4 (35 - 84t + 70t^2 - 20t^3)
            return t * t * t * t * (35.0 - 84.0 * t + 70.0 * t * t - 20.0 * t * t * t);
        }
        if (string.Equals(profile, "Cinematic", StringComparison.OrdinalIgnoreCase))
        {
            // Undecic smoothstep (C5 continuous): t^6 (462 - 1980t + 3465t^2 - 3080t^3 + 1386t^4 - 252t^5)
            double t2 = t * t;
            double t3 = t2 * t;
            double t4 = t2 * t2;
            double t5 = t4 * t;
            double t6 = t3 * t3;
            return t6 * (462.0 - 1980.0 * t + 3465.0 * t2 - 3080.0 * t3 + 1386.0 * t4 - 252.0 * t5);
        }
        // Default: Helicopter / Planar nonic smoothstep (C4 continuous)
        return t * t * t * t * t * (126.0 - 420.0 * t + 540.0 * t * t - 315.0 * t * t * t + 70.0 * t * t * t * t);
    }

    internal static CanvasCamera Target(WorldRect world, double zoom, ScreenRect viewport)
        => new(world.X + world.W / 2.0 - (viewport.X + viewport.Width / 2.0) / zoom,
            world.Y + world.H / 2.0 - (viewport.Y + viewport.Height / 2.0) / zoom, zoom);
}
