using System;
using System.Collections.Generic;
using System.Linq;

namespace CanvasDesktop;

internal sealed record ArrivalViewport(CanvasCamera Camera, IReadOnlyList<ScreenRect> Screens,
    IReadOnlyList<ScreenRect>? Reserved = null);

/// <summary>Find space inside the visible working areas without moving or resizing other content.</summary>
internal static class NearbyWindowPlacement
{
    public static bool TryPlace(WorldRect window, ArrivalViewport view, IReadOnlyList<WorldRect> obstacles,
        out WorldRect placed, double? preferredX = null, double? preferredY = null)
    {
        placed = default;
        if (!WorkspaceGeometry.ValidBounds(window) || !double.IsFinite(view.Camera.Zoom) || view.Camera.Zoom <= 0) return false;
        double gap = 24 / view.Camera.Zoom, best = double.PositiveInfinity;
        WorldRect World(ScreenRect r) => new() { X = view.Camera.X + r.X / view.Camera.Zoom,
            Y = view.Camera.Y + r.Y / view.Camera.Zoom, W = r.Width / view.Camera.Zoom, H = r.Height / view.Camera.Zoom };
        var blocked = obstacles.Where(b => !b.InInventory && ZoneGeometry.ValidBounds(b))
            .Concat((view.Reserved ?? Array.Empty<ScreenRect>()).Select(World)).ToArray();
        foreach (var screen in view.Screens)
        {
            var bounds = World(screen);
            double left = bounds.X + gap, top = bounds.Y + gap;
            double right = bounds.X + bounds.W - gap - window.W, bottom = bounds.Y + bounds.H - gap - window.H;
            if (right < left || bottom < top) continue;
            double aimX = preferredX ?? bounds.X + (bounds.W - window.W) / 2;
            double aimY = preferredY ?? bounds.Y + (bounds.H - window.H) / 2;
            var local = blocked.Where(b => TemporaryAreaLayout.Overlaps(bounds, b, gap)).ToArray();
            var xs = local.SelectMany(b => new[] { b.X - window.W - gap, b.X + b.W + gap })
                .Concat(new[] { left, right, Math.Clamp(aimX, left, right) }).Where(x => x >= left && x <= right).Distinct().ToArray();
            var ys = local.SelectMany(b => new[] { b.Y - window.H - gap, b.Y + b.H + gap })
                .Concat(new[] { top, bottom, Math.Clamp(aimY, top, bottom) }).Where(y => y >= top && y <= bottom).Distinct().ToArray();
            foreach (double x in xs)
                foreach (double y in ys)
                {
                    var candidate = window; candidate.X = x; candidate.Y = y;
                    candidate.InInventory = candidate.PinnedToScreen = false;
                    if (candidate.State == WindowState.Maximized) candidate.State = WindowState.Normal;
                    double distance = (x - aimX) * (x - aimX) + (y - aimY) * (y - aimY);
                    if (distance >= best || !WorkspaceGeometry.ValidBounds(candidate)
                        || local.Any(b => TemporaryAreaLayout.Overlaps(candidate, b, gap))) continue;
                    placed = candidate; best = distance;
                }
        }
        return double.IsFinite(best);
    }
}
