using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using CanvasDesktop.CanvasView;

namespace CanvasDesktop;

internal sealed class AiOperationsSectorManager
{
    private readonly WorkspaceService _workspace;
    private readonly AnnotationDocument _annotations;
    private readonly AnnotationPersistence? _annotationStore;
    private readonly ITeamTelemetryProvider _telemetry;

    public CanvasPoint SectorOrigin { get; } = new(8000, 1000);
    public const double PodHeight = 1200;
    public const double PodSpacing = 400;

    private readonly Dictionary<string, SpawnedPod> _spawnedPods = new(StringComparer.OrdinalIgnoreCase);

    public sealed record SpawnedPod(
        string TaskId,
        CanvasPoint Origin,
        IReadOnlyList<Guid> ZoneIds,
        IReadOnlyList<Guid> AnnotationIds,
        IReadOnlyDictionary<Guid, string> CardRoleMap)
    {
        public WorldRect GetBounds(WorkspaceDocument workspaceDoc, AnnotationDocument? annotations, Guid desktopId)
        {
            double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
            double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;

            foreach (var zoneId in ZoneIds)
            {
                var zone = workspaceDoc.Zones.FirstOrDefault(z => z.Id == zoneId);
                if (zone != null)
                {
                    minX = Math.Min(minX, zone.Bounds.X);
                    minY = Math.Min(minY, zone.Bounds.Y);
                    maxX = Math.Max(maxX, zone.Bounds.X + zone.Bounds.W);
                    maxY = Math.Max(maxY, zone.Bounds.Y + zone.Bounds.H);
                }
            }

            if (annotations != null)
            {
                foreach (var annotId in AnnotationIds)
                {
                    var annot = annotations.Items.FirstOrDefault(a => a.DesktopId == desktopId && a.Id == annotId);
                    if (annot != null)
                    {
                        minX = Math.Min(minX, annot.Bounds.X);
                        minY = Math.Min(minY, annot.Bounds.Y);
                        maxX = Math.Max(maxX, annot.Bounds.X + annot.Bounds.Width);
                        maxY = Math.Max(maxY, annot.Bounds.Y + annot.Bounds.Height);
                    }
                }
            }

            if (double.IsFinite(minX) && double.IsFinite(maxX) && maxX > minX)
            {
                return new WorldRect { X = minX, Y = minY, W = maxX - minX, H = maxY - minY };
            }

            return new WorldRect { X = Origin.X, Y = Origin.Y, W = 2800, H = 1200 };
        }
    }

    public AiOperationsSectorManager(
        WorkspaceService workspace,
        AnnotationDocument annotations,
        AnnotationPersistence? annotationStore,
        ITeamTelemetryProvider telemetry)
    {
        _workspace = workspace;
        _annotations = annotations;
        _annotationStore = annotationStore;
        _telemetry = telemetry;
    }

    public bool HasPod(string taskId) => _spawnedPods.ContainsKey(taskId);

    public IReadOnlyDictionary<string, SpawnedPod> SpawnedPods => _spawnedPods;

    public SpawnedPod? SpawnPod(Guid desktopId, TeamInstanceTelemetry team, string cvlTemplateJson, CanvasPoint? customOrigin = null)
    {
        if (_spawnedPods.TryGetValue(team.TaskId, out var existing)) return existing;

        int slotIndex = _spawnedPods.Count;
        var podOrigin = customOrigin ?? new CanvasPoint(SectorOrigin.X, SectorOrigin.Y + slotIndex * (PodHeight + PodSpacing));

        var basePlan = JsonSerializer.Deserialize<ComposerPlan>(cvlTemplateJson, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
        if (basePlan == null || basePlan.Nodes == null) return null;

        // Specialize Plan for Task
        string prefix = $"pod_{team.TaskId}_";
        var specializedNodes = basePlan.Nodes.Select(n => n with
        {
            Id = prefix + n.Id,
            Title = $"[{team.TaskId}] {n.Title}",
            Subtitle = !string.IsNullOrEmpty(team.Worktree) ? $"Worktree: {Path.GetFileName(team.Worktree)}" : n.Subtitle,
            Text = $"[{team.TaskId}] {n.Title}\n{(string.IsNullOrEmpty(team.Worktree) ? n.Subtitle : Path.GetFileName(team.Worktree))}",
            Zone = !string.IsNullOrEmpty(n.Zone) ? prefix + n.Zone : ""
        }).ToArray();

        var specializedZones = basePlan.Zones?.Select(z => z with
        {
            Id = prefix + z.Id,
            Name = $"[{team.TaskId}] {z.Name}"
        }).ToArray();

        var specializedEdges = (basePlan.Edges ?? []).Select(e => e with
        {
            From = prefix + e.From,
            To = prefix + e.To
        }).ToArray();

        var specializedPlan = basePlan with
        {
            Summary = $"Team Pod: {team.Name} ({team.TaskId})",
            Nodes = specializedNodes,
            Zones = specializedZones,
            Edges = specializedEdges,
            Edits = basePlan.Edits ?? []
        };

        var context = new ComposerContext(desktopId, _annotations.Revision, podOrigin, [], []);
        var preview = CanvasComposer.Prepare(context, specializedPlan);

        var workflowService = new WorkflowService(_workspace, _annotations, _annotationStore);
        bool applied = workflowService.ApplyComposerPlan(desktopId, specializedPlan, preview);
        if (!applied) return null;

        var cardRoleMap = new Dictionary<Guid, string>();
        var spawnedAnnotIds = preview.Items.Select(i => i.Id).ToList();
        var spawnedZoneIds = specializedZones?.Select(z => _workspace.Document.Zones.FirstOrDefault(wz => wz.DesktopId == desktopId && wz.Name == z.Name)?.Id ?? Guid.Empty)
            .Where(g => g != Guid.Empty).ToList() ?? new List<Guid>();

        foreach (var node in basePlan.Nodes)
        {
            string specializedId = prefix + node.Id;
            if (preview.NodeIdMap != null && preview.NodeIdMap.TryGetValue(specializedId, out var annotId))
            {
                cardRoleMap[annotId] = node.Id; // e.g. "dev", "arch", "rev", "mrg", "cln"
            }
        }

        var spawned = new SpawnedPod(team.TaskId, podOrigin, spawnedZoneIds, spawnedAnnotIds, cardRoleMap);
        _spawnedPods[team.TaskId] = spawned;
        return spawned;
    }

    public bool CleanPod(Guid desktopId, string taskId)
    {
        if (!_spawnedPods.TryGetValue(taskId, out var pod)) return false;

        // 1. Remove all annotations
        foreach (var annotId in pod.AnnotationIds)
        {
            _annotations.Delete(desktopId, annotId);
        }

        // 2. Remove all workspace zones
        foreach (var zoneId in pod.ZoneIds)
        {
            var zone = _workspace.Document.Zones.FirstOrDefault(z => z.Id == zoneId);
            if (zone != null) _workspace.Document.Zones.Remove(zone);
        }

        _workspace.SaveNow();
        _annotationStore?.Save();
        _spawnedPods.Remove(taskId, out _);
        return true;
    }
}
