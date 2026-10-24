using System;

namespace CanvasDesktop;

/// <summary>An abort joins the current position, velocity and acceleration to a resting landing.</summary>
internal sealed class StartupIntroLanding
{
    public const double DurationSeconds = 1.4;
    private readonly StartupIntroFrame _from;
    private readonly CanvasCamera _target;
    private readonly double _screenX, _screenY;
    private readonly double[] _start, _end, _velocity, _acceleration;
    private readonly bool _reduced;

    public StartupIntroLanding(StartupIntroFlight flight, double elapsed, CanvasCamera target,
        double screenX, double screenY, bool reduced)
    {
        _from = flight.Sample(elapsed); _target = target;
        _screenX = screenX; _screenY = screenY; _reduced = reduced;
        _start = Coordinates(_from);
        _end = Coordinates(new(target.X, target.Y, target.Zoom, 0, 4, 1, true));
        const double h = 0.001;
        var before = Coordinates(flight.Sample(elapsed - h));
        var after = Coordinates(flight.Sample(elapsed + h));
        _velocity = new double[3]; _acceleration = new double[3];
        for (int i = 0; i < 3; i++)
        {
            _velocity[i] = (after[i] - before[i]) / (2 * h);
            _acceleration[i] = (after[i] - 2 * _start[i] + before[i]) / (h * h);
        }
    }

    public StartupIntroFrame Sample(double elapsed)
    {
        double t = double.IsNaN(elapsed) ? 0 : Math.Clamp(elapsed, 0, DurationSeconds);
        if (_reduced || t >= DurationSeconds)
            return new(_target.X, _target.Y, _target.Zoom, 0, 4, 1, true);
        if (t <= 0) return _from with { Complete = false };
        double progress = t / DurationSeconds, ease = StartupIntroMotion.Ease(progress);
        double Coordinate(int i) => StartupIntroMotion.Interpolate(_start[i], _end[i],
            _velocity[i], 0, _acceleration[i], DurationSeconds, progress);
        double zoom = Math.Clamp(Math.Exp(Coordinate(2)), OverviewCamera.ZoomMin, OverviewCamera.ZoomMax);
        return new(Coordinate(0) - _screenX / zoom, Coordinate(1) - _screenY / zoom, zoom,
            _from.TitleOpacity * (1 - ease), _from.TitleSpacing + (4 - _from.TitleSpacing) * ease,
            _from.RevealOpacity + (1 - _from.RevealOpacity) * ease, false);
    }

    private double[] Coordinates(StartupIntroFrame frame)
        => [frame.X + _screenX / frame.Zoom, frame.Y + _screenY / frame.Zoom, Math.Log(frame.Zoom)];
}
