using System;
using System.Collections.Generic;
using System.Linq;

namespace CanvasDesktop;

internal sealed class StartupIntroRoute
{
    internal readonly record struct Point(double X, double Y);
    private readonly Point[] _points, _velocities;
    private readonly double[] _times;
    public double Duration => _times[^1];
    public double TakeoffDuration => _times[1];
    public double LandingStart => _times[^2];
    public double LandingDuration => Duration - LandingStart;

    public StartupIntroRoute(Point start, Point landing, IReadOnlyList<WorldRect> destinations,
        double width, double height, double minimumDuration)
    {
        var targets = OrderTargets(destinations, start, width, height);
        int entryDirection = targets.Count < 2 || targets[1].X >= targets[0].X ? 1 : -1;
        int exitDirection = targets.Count < 2 || targets[^1].X >= targets[^2].X ? 1 : -1;
        double firstX = targets.Count == 0 ? start.X : targets[0].X;
        double lastX = targets.Count == 0 ? start.X : targets[^1].X;
        double entryX = entryDirection > 0 ? Math.Min(start.X, firstX) - width * 0.42 : Math.Max(start.X, firstX) + width * 0.42;
        double exitX = exitDirection > 0 ? Math.Max(landing.X, lastX) + width * 0.42 : Math.Min(landing.X, lastX) - width * 0.42;
        var points = new List<Point> { start,
            new(entryX, (targets.Count == 0 ? start.Y : targets[0].Y) - height * 0.12) };
        points.AddRange(targets);
        points.Add(new(exitX, (targets.Count == 0 ? start.Y : targets[^1].Y) + height * 0.12));
        points.Add(landing);
        _points = points.ToArray();
        _times = new double[_points.Length];
        for (int i = 1; i < _points.Length; i++)
        {
            double dx = (_points[i].X - _points[i - 1].X) / width;
            double dy = (_points[i].Y - _points[i - 1].Y) / height;
            // Pace by visible distance, with time to read each region and ease off/on.
            double seconds = Math.Max(i == 1 || i == _points.Length - 1 ? 3.2 : 1.8,
                Math.Sqrt(dx * dx + dy * dy) / 0.24);
            _times[i] = _times[i - 1] + seconds;
        }
        double stretch = Math.Max(1, minimumDuration / Duration);
        for (int i = 1; i < _times.Length; i++) _times[i] *= stretch;
        _velocities = new Point[_points.Length];
        for (int i = 1; i < _points.Length - 1; i++)
        {
            double seconds = _times[i + 1] - _times[i - 1];
            _velocities[i] = new((_points[i + 1].X - _points[i - 1].X) / seconds,
                (_points[i + 1].Y - _points[i - 1].Y) / seconds);
        }
    }

    private static List<Point> OrderTargets(IReadOnlyList<WorldRect> destinations, Point start, double width, double height)
    {
        var remaining = destinations.Select(b => new Point(b.X + b.W / 2, b.Y + b.H / 2))
            .Distinct().OrderBy(p => p.Y).ThenBy(p => p.X).ToArray();
        var tour = new List<Point>();
        bool reverse = false;
        for (int i = 0; i < remaining.Length;)
        {
            int end = i + 1;
            while (end < remaining.Length && remaining[end].Y - remaining[i].Y <= height * 0.4) end++;
            var row = remaining[i..end].OrderBy(p => p.X).ThenBy(p => p.Y).ToArray();
            if (reverse) Array.Reverse(row);
            tour.AddRange(row);
            reverse = !reverse;
            i = end;
        }
        double Distance(Point p) => Math.Pow((p.X - start.X) / width, 2) + Math.Pow((p.Y - start.Y) / height, 2);
        if (tour.Count > 1 && Distance(tour[^1]) < Distance(tour[0])) tour.Reverse();
        return tour;
    }

    public (Point Center, int Leg, double Progress) Sample(double time)
    {
        int index = Array.BinarySearch(_times, time);
        int leg = Math.Clamp(index < 0 ? ~index - 1 : index, 0, _times.Length - 2);
        double duration = _times[leg + 1] - _times[leg];
        double progress = Math.Clamp((time - _times[leg]) / duration, 0, 1);
        var from = _points[leg]; var to = _points[leg + 1];
        var velocity = _velocities[leg]; var endVelocity = _velocities[leg + 1];
        return (new(StartupIntroMotion.Interpolate(from.X, to.X, velocity.X, endVelocity.X, 0, duration, progress),
            StartupIntroMotion.Interpolate(from.Y, to.Y, velocity.Y, endVelocity.Y, 0, duration, progress)), leg, progress);
    }

    public static IReadOnlyList<WorldRect> Destinations(IReadOnlyList<WorldRect>? windows,
        IReadOnlyList<WorldRect>? regions, double viewportWidth, double viewportHeight)
    {
        var areas = (regions ?? []).Where(Valid).OrderBy(b => b.X).ThenBy(b => b.Y).ThenBy(b => b.W).ThenBy(b => b.H).ToList();
        var loose = (windows ?? []).Where(Valid).Where(w => !areas.Any(a => ContainsCenter(a, w)))
            .OrderBy(w => w.X).ThenBy(w => w.Y).ThenBy(w => w.W).ThenBy(w => w.H).ToArray();
        var parents = Enumerable.Range(0, loose.Length).ToArray();
        int Root(int i)
        {
            while (parents[i] != i) { parents[i] = parents[parents[i]]; i = parents[i]; }
            return i;
        }
        // Connected components keep adjacent windows together, independently of enumeration order.
        double gapX = viewportWidth * 0.35, gapY = viewportHeight * 0.35;
        for (int i = 0; i < loose.Length; i++)
            for (int j = i + 1; j < loose.Length; j++)
            {
                var a = loose[i]; var b = loose[j];
                if (b.X > a.X + a.W + gapX) break;
                if (b.Y <= a.Y + a.H + gapY && a.Y <= b.Y + b.H + gapY)
                    parents[Root(j)] = Root(i);
            }
        foreach (var cluster in loose.Select((bounds, i) => (bounds, root: Root(i))).GroupBy(w => w.root))
        {
            double x = cluster.Min(w => w.bounds.X), y = cluster.Min(w => w.bounds.Y);
            areas.Add(new() { X = x, Y = y, W = cluster.Max(w => w.bounds.X + w.bounds.W) - x,
                H = cluster.Max(w => w.bounds.Y + w.bounds.H) - y });
        }
        return areas;
    }

    private static bool Valid(WorldRect bounds) => !bounds.PinnedToScreen && WorkspaceGeometry.ValidBounds(bounds);
    private static bool ContainsCenter(WorldRect area, WorldRect window)
        => window.X + window.W / 2 >= area.X && window.X + window.W / 2 <= area.X + area.W
            && window.Y + window.H / 2 >= area.Y && window.Y + window.H / 2 <= area.Y + area.H;
}
