using System;
using System.Collections.Generic;

namespace CanvasDesktop;

internal readonly record struct StartupIntroFrame(
    double X, double Y, double Zoom, double TitleOpacity,
    double TitleSpacing, double RevealOpacity, bool Complete);

/// <summary>A deterministic tour of project areas and window clusters. Owns no clocks or canvas state.</summary>
internal sealed class StartupIntroFlight
{
    public const double MinimumDurationSeconds = 12;
    public const double ReducedMotionDurationSeconds = 0.45;
    private const double RevealSeconds = 0.6;
    private const double CruiseSwellPeriodSeconds = 14;
    private readonly CanvasCamera _saved, _landing;
    private readonly bool _reducedMotion;
    private readonly double _screenCenterX, _screenCenterY, _cruiseZoom, _worldWidth, _worldHeight;
    private readonly StartupIntroRoute _route;

    public double DurationSeconds { get; }

    public StartupIntroFlight(CanvasCamera savedCamera, ScreenRect primaryScreen,
        IReadOnlyList<WorldRect> nearbyWindows, bool reducedMotion = false, CanvasCamera? landing = null,
        IReadOnlyList<WorldRect>? projectRegions = null)
    {
        ValidateCamera(savedCamera, nameof(savedCamera));
        _saved = savedCamera;
        _landing = landing ?? savedCamera;
        ValidateCamera(_landing, nameof(landing));
        _reducedMotion = reducedMotion;
        double width = primaryScreen.Width > 0 ? primaryScreen.Width : 1920;
        double height = primaryScreen.Height > 0 ? primaryScreen.Height : 1080;
        _screenCenterX = primaryScreen.X + width / 2;
        _screenCenterY = primaryScreen.Y + height / 2;

        var destinations = StartupIntroRoute.Destinations(nearbyWindows, projectRegions,
            width / savedCamera.Zoom, height / savedCamera.Zoom);
        _cruiseZoom = Math.Min(savedCamera.Zoom, 0.2);
        foreach (var area in destinations)
            _cruiseZoom = Math.Min(_cruiseZoom, Math.Min(width * 0.8 / area.W, height * 0.65 / area.H));
        _cruiseZoom = Math.Clamp(_cruiseZoom, OverviewCamera.ZoomMin, OverviewCamera.ZoomMax);
        _worldWidth = width / _cruiseZoom;
        _worldHeight = height / _cruiseZoom;
        _route = new StartupIntroRoute(
            new(_saved.X + _screenCenterX / _saved.Zoom, _saved.Y + _screenCenterY / _saved.Zoom),
            new(_landing.X + _screenCenterX / _landing.Zoom, _landing.Y + _screenCenterY / _landing.Zoom),
            destinations, _worldWidth, _worldHeight, MinimumDurationSeconds - RevealSeconds);
        DurationSeconds = reducedMotion ? ReducedMotionDurationSeconds : RevealSeconds + _route.Duration;
    }

    public StartupIntroFrame Sample(double elapsedSeconds)
    {
        double t = double.IsNaN(elapsedSeconds) ? 0 : Math.Clamp(elapsedSeconds, 0, DurationSeconds);
        bool complete = t >= DurationSeconds;
        double reveal = StartupIntroMotion.Ease(t / (_reducedMotion ? 0.12 : RevealSeconds));
        double title = _reducedMotion
            ? StartupIntroMotion.Ease(t / 0.12) * (1 - StartupIntroMotion.Ease((t - 0.2) / 0.25))
            : StartupIntroMotion.Ease((t - 0.6) / 0.5) * (1 - StartupIntroMotion.Ease((t - 1.8) / 0.6));
        double spacing = _reducedMotion ? 1 : 1 + 3 * StartupIntroMotion.Ease(t / 3.6);
        if (_reducedMotion || complete)
            return new(_landing.X, _landing.Y, _landing.Zoom, title, spacing, reveal, complete);
        if (t <= RevealSeconds)
            return new(_saved.X, _saved.Y, _saved.Zoom, title, spacing, reveal, false);

        double travelTime = t - RevealSeconds;
        var (center, leg, progress) = _route.Sample(travelTime);
        double envelope = StartupIntroMotion.Ease(travelTime / _route.TakeoffDuration)
            * (1 - StartupIntroMotion.Ease((travelTime - _route.LandingStart) / _route.LandingDuration));
        // A subtle, slow swell follows elapsed time rather than restarting at every
        // region. Short route legs must not accelerate the camera's breathing.
        // The smooth envelope joins takeoff and landing with no added velocity or acceleration.
        double phase = 2 * Math.PI * travelTime / CruiseSwellPeriodSeconds;
        double wave = Math.Sin(phase);
        double lift = _worldHeight * 0.015 * wave * envelope;
        double bank = _worldWidth * 0.003 * Math.Sin(phase / 2) * envelope;
        double zoom = leg == 0
            ? StartupIntroMotion.Geometric(_saved.Zoom, _cruiseZoom, StartupIntroMotion.Ease(progress))
            : travelTime >= _route.LandingStart
                ? StartupIntroMotion.Geometric(_cruiseZoom, _landing.Zoom, StartupIntroMotion.Ease(progress))
                : _cruiseZoom;
        double breathing = Math.Max(0, Math.Min(0.006, Math.Min(Math.Log(zoom / OverviewCamera.ZoomMin),
            Math.Log(Math.Max(_saved.Zoom, _landing.Zoom) / zoom))));
        zoom *= Math.Exp(breathing * wave * envelope);
        return new(center.X + bank - _screenCenterX / zoom,
            center.Y + lift - _screenCenterY / zoom, zoom, title, spacing, reveal, false);
    }

    public StartupIntroLanding CreateLanding(double elapsedSeconds)
        => new(this, elapsedSeconds, _landing, _screenCenterX, _screenCenterY, _reducedMotion);

    private static void ValidateCamera(CanvasCamera camera, string name)
    {
        if (!double.IsFinite(camera.X) || !double.IsFinite(camera.Y) || !double.IsFinite(camera.Zoom)
            || camera.Zoom < OverviewCamera.ZoomMin || camera.Zoom > OverviewCamera.ZoomMax)
            throw new ArgumentOutOfRangeException(name);
    }
}

internal static class StartupIntroMotion
{
    public static double Ease(double progress)
    {
        double p = Math.Clamp(progress, 0, 1);
        return Math.Clamp(p * p * p * (p * (p * 6 - 15) + 10), 0, 1);
    }

    public static double Geometric(double from, double to, double amount)
        => Math.Exp(Math.Log(from) + (Math.Log(to) - Math.Log(from)) * amount);

    // Quintic Hermite interpolation: shared velocity and acceleration give C2 joins.
    public static double Interpolate(double from, double to, double velocity, double endVelocity,
        double acceleration, double duration, double progress)
    {
        double p = Math.Clamp(progress, 0, 1), p2 = p * p, p3 = p2 * p, p4 = p3 * p, p5 = p4 * p;
        return from + (to - from) * Ease(p)
            + velocity * duration * (p - 6 * p3 + 8 * p4 - 3 * p5)
            + endVelocity * duration * (-4 * p3 + 7 * p4 - 3 * p5)
            + acceleration * duration * duration * (p2 - 3 * p3 + 3 * p4 - p5) / 2;
    }
}
