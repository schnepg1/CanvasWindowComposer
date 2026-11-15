using System;
using System.Collections.Generic;
using System.Linq;
using CanvasDesktop.CanvasView;

namespace CanvasDesktop;

/// <summary>
/// Plans collision-free placement for subagents near their parent agent card,
/// dynamically adapting from full-size cards (280x140) to compact satellite pills (140x40)
/// under spatial constraints or obstacle proximity.
/// </summary>
public static class SubagentPlacementPlanner
{
    public const double FullCardWidth = 280;
    public const double FullCardHeight = 140;
    public const double CompactPillWidth = 140;
    public const double CompactPillHeight = 40;

    private const double GapHorizontal = 20;
    private const double GapVertical = 16;
    private const double MicroGap = 12;

    public static CanvasRect PlanPlacement(
        CanvasRect parentBounds,
        IEnumerable<CanvasRect> obstacles,
        CanvasRect? containerZone = null)
    {
        var obstacleList = obstacles?.Where(o => o.Width > 0 && o.Height > 0).ToList() ?? [];

        // 1. Evaluates candidate slots near parent for Full Card (280x140)
        var fullCandidates = GenerateFullCandidates(parentBounds);
        foreach (var candidate in fullCandidates)
        {
            if (IsSlotValid(candidate, obstacleList, containerZone))
                return candidate;
        }

        // 2. If no 280x140 slot fits (space is constrained): fallback to finding a micro-slot for a Compact Satellite Pill (140x40) along parent's perimeter
        var compactCandidates = GenerateCompactCandidates(parentBounds);
        foreach (var candidate in compactCandidates)
        {
            if (IsSlotValid(candidate, obstacleList, containerZone))
                return candidate;
        }

        // Default fallback to compact satellite pill at primary offset
        return new CanvasRect(parentBounds.Right + GapHorizontal, parentBounds.Y, CompactPillWidth, CompactPillHeight);
    }

    private static IEnumerable<CanvasRect> GenerateFullCandidates(CanvasRect parent)
    {
        // 1. Right of parent (parent.Right + 20, parent.Y)
        yield return new CanvasRect(parent.Right + GapHorizontal, parent.Y, FullCardWidth, FullCardHeight);

        // 2. Below parent (parent.X, parent.Bottom + 16)
        yield return new CanvasRect(parent.X, parent.Bottom + GapVertical, FullCardWidth, FullCardHeight);

        // 3. Left of parent (parent.X - 280 - 20, parent.Y)
        yield return new CanvasRect(parent.X - FullCardWidth - GapHorizontal, parent.Y, FullCardWidth, FullCardHeight);

        // 4. Above parent (parent.X, parent.Y - 140 - 16)
        yield return new CanvasRect(parent.X, parent.Y - FullCardHeight - GapVertical, FullCardWidth, FullCardHeight);

        // 5. Diagonal / secondary offsets
        yield return new CanvasRect(parent.Right + GapHorizontal, parent.Bottom + GapVertical, FullCardWidth, FullCardHeight);
        yield return new CanvasRect(parent.Right + GapHorizontal, parent.Y - FullCardHeight - GapVertical, FullCardWidth, FullCardHeight);
        yield return new CanvasRect(parent.X - FullCardWidth - GapHorizontal, parent.Bottom + GapVertical, FullCardWidth, FullCardHeight);
        yield return new CanvasRect(parent.X - FullCardWidth - GapHorizontal, parent.Y - FullCardHeight - GapVertical, FullCardWidth, FullCardHeight);

        yield return new CanvasRect(parent.Right + (GapHorizontal * 2) + FullCardWidth, parent.Y, FullCardWidth, FullCardHeight);
        yield return new CanvasRect(parent.X, parent.Bottom + (GapVertical * 2) + FullCardHeight, FullCardWidth, FullCardHeight);
        yield return new CanvasRect(parent.X - (FullCardWidth + GapHorizontal) * 2, parent.Y, FullCardWidth, FullCardHeight);
        yield return new CanvasRect(parent.X, parent.Y - (FullCardHeight + GapVertical) * 2, FullCardWidth, FullCardHeight);
    }

    private static IEnumerable<CanvasRect> GenerateCompactCandidates(CanvasRect parent)
    {
        // Right perimeter
        yield return new CanvasRect(parent.Right + GapHorizontal, parent.Y, CompactPillWidth, CompactPillHeight);
        yield return new CanvasRect(parent.Right + MicroGap, parent.Y, CompactPillWidth, CompactPillHeight);
        yield return new CanvasRect(parent.Right + MicroGap, parent.Y + (parent.Height - CompactPillHeight) / 2, CompactPillWidth, CompactPillHeight);
        yield return new CanvasRect(parent.Right + MicroGap, parent.Bottom - CompactPillHeight, CompactPillWidth, CompactPillHeight);

        // Below perimeter
        yield return new CanvasRect(parent.X, parent.Bottom + GapVertical, CompactPillWidth, CompactPillHeight);
        yield return new CanvasRect(parent.X, parent.Bottom + MicroGap, CompactPillWidth, CompactPillHeight);
        yield return new CanvasRect(parent.X + (parent.Width - CompactPillWidth) / 2, parent.Bottom + MicroGap, CompactPillWidth, CompactPillHeight);
        yield return new CanvasRect(parent.Right - CompactPillWidth, parent.Bottom + MicroGap, CompactPillWidth, CompactPillHeight);

        // Left perimeter
        yield return new CanvasRect(parent.X - CompactPillWidth - GapHorizontal, parent.Y, CompactPillWidth, CompactPillHeight);
        yield return new CanvasRect(parent.X - CompactPillWidth - MicroGap, parent.Y, CompactPillWidth, CompactPillHeight);
        yield return new CanvasRect(parent.X - CompactPillWidth - MicroGap, parent.Y + (parent.Height - CompactPillHeight) / 2, CompactPillWidth, CompactPillHeight);
        yield return new CanvasRect(parent.X - CompactPillWidth - MicroGap, parent.Bottom - CompactPillHeight, CompactPillWidth, CompactPillHeight);

        // Above perimeter
        yield return new CanvasRect(parent.X, parent.Y - CompactPillHeight - GapVertical, CompactPillWidth, CompactPillHeight);
        yield return new CanvasRect(parent.X, parent.Y - CompactPillHeight - MicroGap, CompactPillWidth, CompactPillHeight);
        yield return new CanvasRect(parent.X + (parent.Width - CompactPillWidth) / 2, parent.Y - CompactPillHeight - MicroGap, CompactPillWidth, CompactPillHeight);
        yield return new CanvasRect(parent.Right - CompactPillWidth, parent.Y - CompactPillHeight - MicroGap, CompactPillWidth, CompactPillHeight);

        // Perimeter diagonals / corners
        yield return new CanvasRect(parent.Right + MicroGap, parent.Bottom + MicroGap, CompactPillWidth, CompactPillHeight);
        yield return new CanvasRect(parent.X - CompactPillWidth - MicroGap, parent.Bottom + MicroGap, CompactPillWidth, CompactPillHeight);
        yield return new CanvasRect(parent.Right + MicroGap, parent.Y - CompactPillHeight - MicroGap, CompactPillWidth, CompactPillHeight);
        yield return new CanvasRect(parent.X - CompactPillWidth - MicroGap, parent.Y - CompactPillHeight - MicroGap, CompactPillWidth, CompactPillHeight);
    }

    private static bool IsSlotValid(CanvasRect slot, IReadOnlyList<CanvasRect> obstacles, CanvasRect? containerZone)
    {
        if (containerZone.HasValue)
        {
            var zone = containerZone.Value;
            if (slot.X < zone.X || slot.Right > zone.Right || slot.Y < zone.Y || slot.Bottom > zone.Bottom)
                return false;
        }

        for (int i = 0; i < obstacles.Count; i++)
        {
            if (slot.IntersectsWith(obstacles[i]))
                return false;
        }

        return true;
    }
}
