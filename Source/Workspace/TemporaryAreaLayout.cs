using System;
using System.Collections.Generic;
using System.Linq;

namespace CanvasDesktop;

/// <summary>Append arrivals without moving existing content or changing application sizes.</summary>
internal static class TemporaryAreaLayout
{
    public const double Gap = 64;
    public const double Width = 3600;
    public const double Header = 120;

    public static bool Overlaps(WorldRect a, WorldRect b, double gap = Gap)
        => a.X < b.X + b.W + gap && a.X + a.W + gap > b.X
            && a.Y < b.Y + b.H + gap && a.Y + a.H + gap > b.Y;

    public static bool ContainsCenter(WorldRect area, WorldRect window)
        => window.X + window.W / 2 >= area.X && window.X + window.W / 2 <= area.X + area.W
            && window.Y + window.H / 2 >= area.Y && window.Y + window.H / 2 <= area.Y + area.H;

    public static WorldRect Place(WorkspaceTemporaryArea anchor, WorldRect window, IReadOnlyList<WorldRect> occupied)
    {
        double left = anchor.X + Gap, top = anchor.Y + Header;
        double right = left + Math.Max(Width - Gap * 2, window.W);
        var xs = occupied.Select(b => b.X + b.W + Gap).Append(left)
            .Where(x => x >= left && x + window.W <= right).Distinct().OrderBy(x => x).ToArray();
        var ys = occupied.Select(b => b.Y + b.H + Gap).Append(top)
            .Where(y => y >= top).Distinct().OrderBy(y => y);
        foreach (double y in ys)
            foreach (double x in xs)
            {
                var candidate = window;
                candidate.X = x; candidate.Y = y; candidate.PinnedToScreen = false;
                if (WorkspaceGeometry.ValidBounds(candidate) && !occupied.Any(b => Overlaps(candidate, b)))
                    return candidate;
            }
        throw new InvalidOperationException("There is no free space within the canvas coordinate limits.");
    }
}
