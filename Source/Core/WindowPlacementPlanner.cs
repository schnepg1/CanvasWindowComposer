using System;
using System.Collections.Generic;

namespace CanvasDesktop;

/// <summary>Immutable camera snapshot; all coordinates are in the existing screen-pixel space.</summary>
internal readonly record struct CanvasCamera(double X, double Y, double Zoom)
{
    public (int x, int y) ProjectPosition(double x, double y)
        => ((int)((x - X) * Zoom), (int)((y - Y) * Zoom));

    public (int w, int h) ProjectSize(double w, double h)
        => (Math.Max(200, (int)Math.Ceiling(w * Zoom)), Math.Max(100, (int)Math.Ceiling(h * Zoom)));

    public WindowRect Project(WorldRect world)
    {
        var (x, y) = ProjectPosition(world.X, world.Y);
        var (w, h) = ProjectSize(world.W, world.H);
        return new WindowRect(x, y, w, h);
    }
}

internal enum WindowRegionChange { None, Clip, Unclip }
internal enum PlacementIntent { Project, RestoreLayout }

/// <summary>
/// A decision, not an applied native state. IntersectsViewport describes the logical
/// projected rectangle, not whether the application is rendering or consuming resources.
/// TargetRect is only applied when ShouldMove is true.
/// </summary>
internal readonly record struct WindowPlacementPlan(
    WindowRect ProjectedRect, WindowRect TargetRect, bool IntersectsViewport,
    bool ShouldMove, WindowRegionChange RegionChange);

/// <summary>
/// Pure placement policy. No HWNDs, Win32 calls, configuration access or mutable canvas.
/// Monitor working areas intentionally exclude taskbars, matching the existing policy.
/// </summary>
internal static class WindowPlacementPlanner
{
    private static readonly (int x, int y, int w, int h) FallbackScreen = (0, 0, 1920, 1080);
    private const int ClipEdgeOffsetPx = 1;

    public static WindowPlacementPlan Plan(WorldRect world, CanvasCamera camera,
        IReadOnlyList<(int x, int y, int w, int h)> screens, bool clipOffScreen,
        bool wasClipped, PlacementIntent intent = PlacementIntent.Project, bool repositionClipped = false)
    {
        var projected = camera.Project(world);
        bool visible = IntersectsAnyScreen(projected, screens);
        if (world.State != WindowState.Normal || (world.PinnedToScreen && intent == PlacementIntent.Project))
            return new(projected, projected, visible, false, WindowRegionChange.None);

        // Explicit saved-layout placement may move a pinned window, but must not hide it.
        if (clipOffScreen && !world.PinnedToScreen && !visible)
        {
            // Already-parked windows need no new edge search until the display changes.
            if (wasClipped && !repositionClipped)
                return new(projected, projected, false, false, WindowRegionChange.None);
            return new(projected, ParkAtScreenEdge(projected, screens), false,
                !wasClipped || repositionClipped, wasClipped ? WindowRegionChange.None : WindowRegionChange.Clip);
        }

        return new(projected, projected, visible, true,
            wasClipped || intent == PlacementIntent.RestoreLayout ? WindowRegionChange.Unclip : WindowRegionChange.None);
    }

    public static bool IntersectsAnyScreen(WindowRect rect, IReadOnlyList<(int x, int y, int w, int h)> screens)
    {
        if (rect.W <= 0 || rect.H <= 0) return false;
        foreach (var screen in screens)
        {
            if (screen.w <= 0 || screen.h <= 0) continue;
            if ((long)rect.X + rect.W > screen.x && rect.X < (long)screen.x + screen.w &&
                (long)rect.Y + rect.H > screen.y && rect.Y < (long)screen.y + screen.h)
                return true;
        }
        return false;
    }

    public static WindowRect ResolvePinnedPlacement(WorldRect world, CanvasCamera camera, WindowRect actual,
        bool wasClipped, IReadOnlyList<(int x, int y, int w, int h)> screens)
    {
        if (!wasClipped && IntersectsAnyScreen(actual, screens)) return actual;
        var projected = camera.Project(world);
        if (IntersectsAnyScreen(projected, screens)) return projected;
        return MoveIntoNearestScreen(projected, screens);
    }

    /// <summary>
    /// Final desktop recovery must fit fully inside a working area, including resizing
    /// oversized windows. It deliberately differs from pinning, which preserves size.
    /// </summary>
    public static WindowRect FitDesktop(WindowRect rect, IReadOnlyList<(int x, int y, int w, int h)> screens)
        => DesktopViewport.Fit(rect, screens);

    private static WindowRect MoveIntoNearestScreen(WindowRect rect, IReadOnlyList<(int x, int y, int w, int h)> screens)
    {
        var nearest = FallbackScreen;
        double bestDistance = double.MaxValue;
        foreach (var screen in screens)
        {
            if (screen.w <= 0 || screen.h <= 0) continue;
            double dx = (double)rect.X + rect.W / 2 - ((double)screen.x + screen.w / 2);
            double dy = (double)rect.Y + rect.H / 2 - ((double)screen.y + screen.h / 2);
            double distance = dx * dx + dy * dy;
            if (distance < bestDistance) { bestDistance = distance; nearest = screen; }
        }

        int w = rect.W > 0 ? rect.W : Math.Min(FallbackScreen.w, nearest.w);
        int h = rect.H > 0 ? rect.H : Math.Min(FallbackScreen.h, nearest.h);
        int x = (int)Math.Clamp((long)rect.X, nearest.x, (long)nearest.x + Math.Max(0, nearest.w - w));
        int y = (int)Math.Clamp((long)rect.Y, nearest.y, (long)nearest.y + Math.Max(0, nearest.h - h));
        return new WindowRect(x, y, w, h);
    }

    private static WindowRect ParkAtScreenEdge(WindowRect rect, IReadOnlyList<(int x, int y, int w, int h)> screens)
    {
        var nearest = FallbackScreen;
        double bestDistance = double.MaxValue;
        foreach (var screen in screens)
        {
            if (screen.w <= 0 || screen.h <= 0) continue;
            double dx = (double)rect.X + rect.W / 2 - ((double)screen.x + screen.w / 2);
            double dy = (double)rect.Y + rect.H / 2 - ((double)screen.y + screen.h / 2);
            double distance = Math.Abs(dx) + Math.Abs(dy);
            if (distance < bestDistance) { bestDistance = distance; nearest = screen; }
        }

        // Compatibility policy retained: keep a one-pixel geometric overlap so apps
        // that reject fully off-screen placements do not repeatedly reposition themselves.
        long x = rect.X, y = rect.Y;
        if ((long)rect.X + rect.W <= nearest.x) x = (long)nearest.x - rect.W + ClipEdgeOffsetPx;
        else if (rect.X >= (long)nearest.x + nearest.w) x = (long)nearest.x + nearest.w - ClipEdgeOffsetPx;
        if ((long)rect.Y + rect.H <= nearest.y) y = (long)nearest.y - rect.H + ClipEdgeOffsetPx;
        else if (rect.Y >= (long)nearest.y + nearest.h) y = (long)nearest.y + nearest.h - ClipEdgeOffsetPx;
        return new WindowRect((int)Math.Clamp(x, int.MinValue, int.MaxValue),
            (int)Math.Clamp(y, int.MinValue, int.MaxValue), rect.W, rect.H);
    }
}
