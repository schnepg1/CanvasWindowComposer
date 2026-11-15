using System;
using System.Collections.Generic;
using System.Linq;
using CanvasDesktop.CanvasView;

namespace CanvasDesktop;

internal static class AgentCardContext
{
    // Nested projects use the smallest enclosing area; mission frames are not projects.
    internal static WorkspaceZone? ProjectAt(IEnumerable<WorkspaceZone> zones, CanvasAnnotation card)
        => ProjectAt(zones, card.DesktopId, card.A);

    internal static WorkspaceZone? ProjectAt(IEnumerable<WorkspaceZone> zones, Guid desktopId, CanvasPoint point)
        => zones.Where(zone => zone.DesktopId == desktopId && !zone.IsMission && !zone.IsTemporaryArea
            && CanvasSelection.Contains(new(zone.Bounds.X, zone.Bounds.Y, zone.Bounds.W, zone.Bounds.H), point))
            .OrderBy(zone => zone.Bounds.W * zone.Bounds.H).ThenBy(zone => zone.Id).FirstOrDefault();

    /// <summary>
    /// Resolves the working folder according to the 3-tier working environment hierarchy:
    /// Tier 1: Live Runtime Worktree (telemetry report worktree or workflow worktree if non-empty)
    /// Tier 2: Card-explicit setting (card.AgentWorkingFolder if non-empty)
    /// Tier 3: Mission/Project Zone baseline (missionWorkingFolder ?? projectFolderPath)
    /// Tier 4: User profile directory fallback.
    /// </summary>
    internal static string ResolveWorkingFolder(
        string? runtimeWorktree,
        string? cardWorkingFolder,
        string? missionWorkingFolder,
        string? projectFolderPath,
        string? fallback = null)
    {
        if (!string.IsNullOrWhiteSpace(runtimeWorktree))
            return runtimeWorktree;
        if (!string.IsNullOrWhiteSpace(cardWorkingFolder))
            return cardWorkingFolder;
        if (!string.IsNullOrWhiteSpace(missionWorkingFolder))
            return missionWorkingFolder;
        if (!string.IsNullOrWhiteSpace(projectFolderPath))
            return projectFolderPath;
        if (!string.IsNullOrWhiteSpace(fallback))
            return fallback;
        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    /// <summary>
    /// Resolves the working folder for an agent card using live telemetry, card settings, and workspace context.
    /// </summary>
    internal static string ResolveWorkingFolder(
        CanvasAnnotation card,
        ITeamTelemetryProvider? telemetryProvider = null,
        string? missionWorkingFolder = null,
        string? projectFolderPath = null,
        string? fallback = null)
    {
        string? runtimeWorktree = telemetryProvider?.GetReportForCard(card)?.Worktree;
        if (string.IsNullOrWhiteSpace(runtimeWorktree) && telemetryProvider != null && !string.IsNullOrWhiteSpace(card.AgentTaskId))
        {
            runtimeWorktree = telemetryProvider.Current.Teams.GetValueOrDefault(card.AgentTaskId)?.Worktree;
        }

        return ResolveWorkingFolder(
            runtimeWorktree,
            card.AgentWorkingFolder,
            missionWorkingFolder,
            projectFolderPath,
            fallback);
    }

    /// <summary>
    /// Resolves the working folder for an agent card given the workspace zones and telemetry provider.
    /// </summary>
    internal static string ResolveWorkingFolder(
        CanvasAnnotation card,
        IEnumerable<WorkspaceZone>? zones,
        ITeamTelemetryProvider? telemetryProvider = null,
        WorkspaceZone? mission = null,
        string? fallback = null)
    {
        var project = zones != null ? ProjectAt(zones, card) : null;
        return ResolveWorkingFolder(
            card,
            telemetryProvider,
            mission?.MissionWorkingFolder,
            project?.ProjectFolderPath,
            fallback);
    }
}
