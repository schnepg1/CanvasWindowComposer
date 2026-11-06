using System;
using System.Collections.Generic;
using System.Linq;

namespace CanvasDesktop;

internal sealed class StartupIntroRoute
{
    internal readonly record struct Point(double X, double Y);

    private const double TravelSpeed = 0.24; // Viewport lengths per second.
    private const double TransitionSeconds = 3.2;
    private const double SpiralRadius = 0.24; // Fraction of the cruise viewport.
    private readonly Point _start, _focus, _landing;
    private readonly double _width, _height, _spiralDuration;

    public double Duration { get; }
    public double TakeoffDuration => TransitionSeconds;
    public double LandingStart => Duration - LandingDuration;
    public double LandingDuration { get; }

    public StartupIntroRoute(Point start, Point landing, IReadOnlyList<WorldRect> windows,
        IReadOnlyList<WorldRect>? regions, double width, double height, double minimumDuration)
    {
        _start = start;
        _landing = landing;
        _width = width;
        _height = height;
        _spiralDuration = minimumDuration;
        _focus = FindFocus(windows.Concat(regions ?? []));

        LandingDuration = Math.Max(TransitionSeconds, Distance(_focus, _landing) / TravelSpeed);
        Duration = minimumDuration + (LandingDuration - TransitionSeconds);
    }

    private Point FindFocus(IEnumerable<WorldRect> areas)
    {
        var focus = _start;
        double bestDistance = double.PositiveInfinity;
        foreach (var area in areas)
        {
            if (area.PinnedToScreen || !WorkspaceGeometry.ValidBounds(area)) continue;
            // A large region can reach the current view even when its center is far away.
            var candidate = new Point(Math.Clamp(_start.X, area.X, area.X + area.W),
                Math.Clamp(_start.Y, area.Y, area.Y + area.H));
            // Ignore areas outside the viewport centered on the restored view at cruise zoom.
            if (Math.Abs(candidate.X - _start.X) > _width / 2 ||
                Math.Abs(candidate.Y - _start.Y) > _height / 2) continue;
            double distance = Distance(_start, candidate);
            if (distance > bestDistance) continue;
            if (distance == bestDistance &&
                (candidate.X > focus.X || (candidate.X == focus.X && candidate.Y >= focus.Y))) continue;
            focus = candidate;
            bestDistance = distance;
        }
        return focus;
    }

    private double Distance(Point from, Point to)
    {
        double dx = (to.X - from.X) / _width, dy = (to.Y - from.Y) / _height;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    public Point Sample(double time, double takeoff, double landing)
    {
        double progress = Math.Min(time / _spiralDuration, 1);
        double angle = 2 * Math.Tau * progress;
        // Two turns with a radius that grows and shrinks, at rest at both ends.
        double radius = SpiralRadius * Math.Pow(Math.Sin(Math.PI * progress), 3);
        double x = _start.X + (_focus.X - _start.X) * takeoff + (_landing.X - _focus.X) * landing;
        double y = _start.Y + (_focus.Y - _start.Y) * takeoff + (_landing.Y - _focus.Y) * landing;
        return new(x + _width * radius * Math.Cos(angle), y + _height * radius * Math.Sin(angle));
    }
}
