using System;
using System.Linq;

namespace CanvasDesktop;

/// <summary>Owns persisted mission frames and their task-scoped role layout.</summary>
internal sealed class TeamPodService(WorkspaceService workspace, AnnotationDocument annotations)
{
    private readonly System.Collections.Generic.HashSet<Guid> _recovered = [];

    public bool SetPositionLocked(Guid zoneId, bool locked)
    {
        var zone = workspace.Document.Zones.FirstOrDefault(z => z.Id == zoneId
            && z.DesktopId == workspace.Zones.DesktopId && z.IsMission);
        if (zone == null) return false;
        if (zone.MissionPositionLocked == locked) return true;
        zone.MissionPositionLocked = locked;
        if (!workspace.SaveNow())
        {
            zone.MissionPositionLocked = !locked;
            return false;
        }
        workspace.Zones.NotifyDesktopChanged();
        return true;
    }

    public Guid? CreateMission(Guid desktop, string name, CanvasPoint anchor)
        => ZoneService.ValidName(name?.Trim()) ? Create(desktop, Guid.NewGuid().ToString("N"), name!, anchor) : null;

    public bool SetWorkingFolder(Guid zoneId, string folder)
    {
        var zone = workspace.Document.Zones.FirstOrDefault(z => z.Id == zoneId
            && z.DesktopId == workspace.Zones.DesktopId && z.IsMission);
        if (zone == null || !System.IO.Path.IsPathFullyQualified(folder) || !System.IO.Directory.Exists(folder)) return false;
        string? previous = zone.MissionWorkingFolder;
        zone.MissionWorkingFolder = folder;
        if (workspace.SaveNow()) return true;
        zone.MissionWorkingFolder = previous;
        return false;
    }

    public void Reconcile(TeamTelemetrySnapshot snapshot, Guid desktop)
    {
        if (_recovered.Add(desktop)) RestoreLayout(desktop);
        foreach (var team in snapshot.Teams.Values)
        {
            if (workspace.Document.Zones.Any(z => z.AgentTaskId == team.TaskId)) continue;
            Create(desktop, team.TaskId, team.Name, null);
        }
    }

    private void RestoreLayout(Guid desktop)
    {
        foreach (var zone in workspace.Document.Zones.Where(z => z.IsMission && z.DesktopId == desktop).ToArray())
        {
            var layout = TeamPodLayout.Create(desktop, zone.AgentTaskId, new(0, 0));
            var existing = annotations.Items.ToDictionary(item => item.Id);
            var savedAnchor = layout.Items.FirstOrDefault(item => item.AgentRole.Length > 0 && existing.ContainsKey(item.Id));
            double dx = zone.Bounds.X - layout.Bounds.X;
            double dy = zone.Bounds.Y - layout.Bounds.Y;
            double offsetX = 0, offsetY = 0;
            // Match a saved workspace move even if the annotation save was interrupted.
            // A legacy frame resized independently has a different size: keep its existing artwork positions.
            if (savedAnchor != null && Math.Abs(zone.Bounds.W - layout.Bounds.Width) < 0.01
                && Math.Abs(zone.Bounds.H - layout.Bounds.Height) < 0.01)
            {
                offsetX = dx + savedAnchor.A.X - existing[savedAnchor.Id].A.X;
                offsetY = dy + savedAnchor.A.Y - existing[savedAnchor.Id].A.Y;
            }
            var repairs = layout.Items.Select(template => existing.TryGetValue(template.Id, out var saved)
                    ? Math.Abs(offsetX) < 0.01 && Math.Abs(offsetY) < 0.01 ? saved : saved.Translate(offsetX, offsetY)
                    : template.Translate(dx, dy))
                .Where(item => !existing.TryGetValue(item.Id, out var saved) || item != saved).ToArray();
            if (repairs.Length > 0)
                annotations.ApplyDerivedChange(desktop, repairs, addMissing: true);
        }
    }

    private Guid? Create(Guid desktop, string taskId, string name, CanvasPoint? anchor)
    {
        if (desktop != workspace.Zones.DesktopId
            || workspace.Document.Zones.Count(z => !z.IsTemporaryArea) >= ZoneService.MaximumZones)
            return null;
        name = string.IsNullOrWhiteSpace(name) ? taskId[..Math.Min(120, taskId.Length)] : name.Trim();
        if (!ZoneService.ValidName(name)) return null;

        var pod = TeamPodLayout.Create(desktop, taskId, new(0, 0));
        double dx, dy;
        if (anchor is { } center)
        {
            dx = center.X - pod.Bounds.Width / 2 - pod.Bounds.X;
            dy = center.Y - pod.Bounds.Height / 2 - pod.Bounds.Y;
        }
        else
        {
            var right = annotations.Items.Where(a => a.DesktopId == desktop).Select(a => a.Bounds.X + a.Bounds.Width)
                .Concat(workspace.Document.Zones.Where(z => z.DesktopId == desktop).Select(z => z.Bounds.X + z.Bounds.W))
                .Concat(workspace.Document.Items.Where(i => i.DesktopId == desktop).Select(i => i.Bounds.X + i.Bounds.W));
            dx = right.DefaultIfEmpty(-60).Max() + 160 - pod.Bounds.X;
            dy = 100 - pod.Bounds.Y;
        }

        // Recover saved cards if workspace persistence lagged behind annotation persistence.
        var existing = annotations.Items.Where(a => a.AgentTaskId == taskId && a.DesktopId == desktop).ToArray();
        if (existing.FirstOrDefault(a => pod.Items.Any(p => p.Id == a.Id)) is { } savedCard)
        {
            var original = pod.Items.First(a => a.Id == savedCard.Id);
            dx = savedCard.A.X - original.A.X;
            dy = savedCard.A.Y - original.A.Y;
        }
        var zone = new WorkspaceZone
        {
            Id = pod.ZoneId, DesktopId = desktop, AgentTaskId = taskId,
            Name = name, Color = "#A78BFA",
            Bounds = new WorldRect { X = pod.Bounds.X + dx, Y = pod.Bounds.Y + dy, W = pod.Bounds.Width, H = pod.Bounds.Height }
        };
        if (!ZoneGeometry.ValidBounds(zone.Bounds)) return null;
        var items = pod.Items.Where(a => !annotations.Items.Any(e => e.Id == a.Id)).Select(a => a.Translate(dx, dy)).ToArray();
        bool Place(bool redo)
        {
            if (workspace.Zones.DesktopId != desktop) return false;
            if (redo)
            {
                if (workspace.Document.Zones.Any(z => z.Id == zone.Id)) return false;
                workspace.Document.Zones.Add(zone);
            }
            else
            {
                if (!workspace.Document.Zones.Contains(zone) || zone.ItemIds.Count > 0 || zone.MissionPositionLocked) return false;
                workspace.Document.Zones.Remove(zone);
            }
            if (!workspace.SaveNow())
            {
                if (redo) workspace.Document.Zones.Remove(zone);
                else workspace.Document.Zones.Add(zone);
                return false;
            }
            workspace.Zones.NotifyDesktopChanged();
            return true;
        }
        try
        {
            // Validate before Place modifies the workspace half.
            return annotations.ApplyBatch(annotations.Revision, desktop, items, Place) ? zone.Id : null;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }
}
