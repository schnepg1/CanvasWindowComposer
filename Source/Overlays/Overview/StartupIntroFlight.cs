using System;
using System.Collections.Generic;

namespace CanvasDesktop;

internal readonly record struct StartupIntroFrame(
    double X, double Y, double Zoom, double TitleOpacity,
    double TitleSpacing, double RevealOpacity, bool Complete);

/// <summary>
/// A sampled, screen-centered overview flight. It owns no clocks, native windows,
/// or canvas state; sampling out of order is safe, including after completion.
/// </summary>
internal sealed class StartupIntroFlight
{
    public const double DurationSeconds = 3.5;
    public const double ReducedMotionDurationSeconds = 0.45;

    private readonly CanvasCamera _saved, _landing;
    private readonly bool _reducedMotion;
    private readonly double _screenCenterX, _screenCenterY;
    private readonly double _pullbackZoom;
    private readonly double _panX, _panY, _arcX, _arcY;

    public StartupIntroFlight(CanvasCamera savedCamera, ScreenRect primaryScreen,
        IReadOnlyList<WorldRect> nearbyWindows, bool reducedMotion = false, CanvasCamera? landing = null)
    {
        if (!double.IsFinite(savedCamera.X) || !double.IsFinite(savedCamera.Y) ||
            !double.IsFinite(savedCamera.Zoom) || savedCamera.Zoom < OverviewCamera.ZoomMin ||
            savedCamera.Zoom > OverviewCamera.ZoomMax)
            throw new ArgumentOutOfRangeException(nameof(savedCamera),
                "The saved camera must be finite and within overview zoom limits.");

        _saved = savedCamera;
        _landing = landing ?? savedCamera;
        if (!double.IsFinite(_landing.X) || !double.IsFinite(_landing.Y) || !double.IsFinite(_landing.Zoom)
            || _landing.Zoom < OverviewCamera.ZoomMin || _landing.Zoom > OverviewCamera.ZoomMax)
            throw new ArgumentOutOfRangeException(nameof(landing));
        _reducedMotion = reducedMotion;
        _pullbackZoom = Math.Min(savedCamera.Zoom, 0.4);

        double width = primaryScreen.Width > 0 ? primaryScreen.Width : 1920;
        double height = primaryScreen.Height > 0 ? primaryScreen.Height : 1080;
        // Virtual screen coordinates include monitor origins. Using only half the
        // dimensions would drift toward (0,0) on a negative-origin monitor.
        _screenCenterX = primaryScreen.X + width / 2;
        _screenCenterY = primaryScreen.Y + height / 2;

        if (reducedMotion || nearbyWindows is null) return;

        double worldWidth = width / savedCamera.Zoom;
        double worldHeight = height / savedCamera.Zoom;
        double centerX = savedCamera.X + _screenCenterX / savedCamera.Zoom;
        double centerY = savedCamera.Y + _screenCenterY / savedCamera.Zoom;
        var candidates = new List<(double Distance, double X, double Y)>();
        foreach (WorldRect window in nearbyWindows)
        {
            if (window.PinnedToScreen || !double.IsFinite(window.X) ||
                !double.IsFinite(window.Y) || !double.IsFinite(window.W) ||
                !double.IsFinite(window.H) || window.W <= 0 || window.H <= 0)
                continue;

            double dx = window.X + window.W / 2 - centerX;
            double dy = window.Y + window.H / 2 - centerY;
            if (!double.IsFinite(dx) || !double.IsFinite(dy) ||
                Math.Abs(dx) > worldWidth * 1.5 || Math.Abs(dy) > worldHeight * 1.5)
                continue;

            double distance = Square(dx / worldWidth) + Square(dy / worldHeight);
            if (distance <= 2.25) candidates.Add((distance, dx, dy));
        }

        if (candidates.Count == 0) return;
        candidates.Sort((a, b) =>
        {
            int distance = a.Distance.CompareTo(b.Distance);
            if (distance != 0) return distance;
            int x = a.X.CompareTo(b.X);
            return x != 0 ? x : a.Y.CompareTo(b.Y);
        });

        double targetX = 0, targetY = 0, weightSum = 0;
        for (int i = 0; i < Math.Min(3, candidates.Count); i++)
        {
            var candidate = candidates[i];
            double weight = 1 / (1 + candidate.Distance);
            targetX += candidate.X * weight;
            targetY += candidate.Y * weight;
            weightSum += weight;
        }

        _panX = Math.Clamp(targetX / weightSum * 0.3, -worldWidth * 0.18, worldWidth * 0.18);
        _panY = Math.Clamp(targetY / weightSum * 0.3, -worldHeight * 0.14, worldHeight * 0.14);
        _arcX = worldWidth * 0.035 * (_panX < 0 ? -1 : 1);
        _arcY = worldHeight * 0.035;
    }

    public StartupIntroFrame Sample(double elapsedSeconds)
    {
        double duration = _reducedMotion ? ReducedMotionDurationSeconds : DurationSeconds;
        double t = double.IsNaN(elapsedSeconds) ? 0 : Math.Clamp(elapsedSeconds, 0, duration);
        bool complete = t >= duration;
        double reveal = Ease(t / (_reducedMotion ? 0.12 : 0.5));
        double title = _reducedMotion
            ? Ease(t / 0.12) * (1 - Ease((t - 0.2) / 0.25))
            : Ease((t - 0.5) / 0.45) * (1 - Ease((t - 1.55) / 0.55));
        double spacing = _reducedMotion ? 1 : 1 + 3 * Ease(t / 2.1);

        // Keep exact endpoints. Reduced motion stays at the reading target throughout
        // the fade; ordinary intros keep the saved camera as their target.
        if (_reducedMotion || complete)
            return new(_landing.X, _landing.Y, _landing.Zoom, title, spacing, reveal, complete);
        if (t <= 0) return new(_saved.X, _saved.Y, _saved.Zoom, title, spacing, reveal, false);

        double zoom = t <= 2.6
            ? Lerp(_saved.Zoom, _pullbackZoom, Ease((t - 0.5) / 0.7))
            : Lerp(_pullbackZoom, _landing.Zoom, Ease((t - 2.6) / 0.9));
        zoom = Math.Clamp(zoom, OverviewCamera.ZoomMin, OverviewCamera.ZoomMax);

        // Each segment has zero first and second derivatives at its endpoints.
        // The shallow arc never rotates the horizon and keeps a fixed zoom while
        // surveying. The outgoing envelope joins the final centered settle.
        double excursion = Ease((t - 1.2) / 0.7) * (1 - Ease((t - 2.6) / 0.9));
        double angle = 0.7 * Ease((t - 1.2) / 1.4);
        double dx = excursion * (_panX + _arcX * Math.Sin(angle));
        double dy = excursion * (_panY + _arcY * (1 - Math.Cos(angle)));
        double centerScale = 1 / _saved.Zoom - 1 / zoom;
        double x = _saved.X + _screenCenterX * centerScale + dx;
        double y = _saved.Y + _screenCenterY * centerScale + dy;
        double settle = Ease((t - 2.1) / 1.4);
        x += (_landing.X - _saved.X - _screenCenterX * (1 / _saved.Zoom - 1 / _landing.Zoom)) * settle;
        y += (_landing.Y - _saved.Y - _screenCenterY * (1 / _saved.Zoom - 1 / _landing.Zoom)) * settle;
        return new(x, y, zoom, title, spacing, reveal, false);
    }

    private static double Square(double value) => value * value;
    private static double Lerp(double from, double to, double amount)
        => from + (to - from) * amount;

    // Quintic smootherstep has zero velocity AND acceleration at each join.
    private static double Ease(double progress)
    {
        double p = Math.Clamp(progress, 0, 1);
        return Math.Clamp(p * p * p * (p * (p * 6 - 15) + 10), 0, 1);
    }
}
