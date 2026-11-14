using System;
using System.Linq;

namespace CanvasDesktop;

/// <summary>Owns persisted mission frames and their task-scoped role layout.</summary>
internal sealed class TeamPodService(WorkspaceService workspace, AnnotationDocument annotations)
{
    private const double Margin = 24;
    private const double CardWidth = 240;
    private const double CardHeight = 112;
    private const double CardGap = 16;
    private const double ModularWidth = 2 * (Margin + CardWidth) + CardGap;
    private const double ModularHeight = 2 * Margin + CardHeight;
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
    {
        name = name.Trim();
        if (!CanCreate(desktop) || !ZoneService.ValidName(name)) return null;
        return SaveMission(new WorkspaceZone
        {
            DesktopId = desktop, AgentTaskId = Guid.NewGuid().ToString("N"), IsModularMission = true,
            Name = name, Color = "#A78BFA",
            Bounds = new() { X = anchor.X - ModularWidth / 2, Y = anchor.Y - ModularHeight / 2,
                W = ModularWidth, H = ModularHeight }
        }, []);
    }

    public bool AddAgent(Guid missionId, string name)
    {
        name = name.Trim();
        var desktop = workspace.Zones.DesktopId;
        var zone = workspace.Document.Zones.FirstOrDefault(z => z.Id == missionId && z.DesktopId == desktop && z.IsMission);
        if (zone == null || !ZoneService.ValidName(name)) return false;
        var previous = zone.Bounds;
        var occupied = annotations.Items.Where(a => a.DesktopId == desktop && a.AgentTaskId == zone.AgentTaskId
            && a.AgentRole.Length > 0 && a.Kind != AnnotationKind.Arrow && !a.InBackground).Select(a => a.Bounds).ToArray();
        double x = previous.X + Margin;
        double y = zone.IsModularMission ? previous.Y + Margin
            : Math.Max(previous.Y + previous.H, occupied.Select(b => b.Y + b.Height).DefaultIfEmpty(previous.Y).Max()) + CardGap;
        while (occupied.Any(b => CanvasSelection.Intersects(b, new CanvasRect(x, y, CardWidth, CardHeight))))
        {
            if (x == previous.X + Margin && previous.W >= ModularWidth)
                x += CardWidth + CardGap;
            else
            {
                x = previous.X + Margin;
                y += CardHeight + CardGap;
            }
        }
        var next = previous;
        next.W = Math.Max(next.W, x + CardWidth + Margin - next.X);
        next.H = Math.Max(next.H, y + CardHeight + Margin - next.Y);
        if (!ZoneGeometry.ValidBounds(next)) return false;
        var id = Guid.NewGuid();
        var card = new CanvasAnnotation(id, desktop, AnnotationKind.Text, new(x, y), new(x + CardWidth, y + CardHeight), Text: name)
        {
            AgentTaskId = zone.AgentTaskId, AgentRole = id.ToString("N")
        };
        bool Resize(bool redo)
        {
            if (workspace.Zones.DesktopId != desktop || !workspace.Document.Zones.Contains(zone)
                || !ZoneLayoutPlanner.SamePlacement(zone.Bounds, redo ? previous : next)) return false;
            zone.Bounds = redo ? next : previous;
            if (!workspace.SaveNow())
            {
                zone.Bounds = redo ? previous : next;
                return false;
            }
            workspace.Zones.NotifyDesktopChanged();
            return true;
        }
        try { return annotations.ApplyBatch(annotations.Revision, desktop, [card], Resize); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return false; }
    }

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
            CreateTelemetryMission(desktop, team.TaskId, team.Name);
        }
    }

    private void RestoreLayout(Guid desktop)
    {
        foreach (var zone in workspace.Document.Zones.Where(z => z.IsMission && !z.IsModularMission && z.DesktopId == desktop).ToArray())
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

    private bool CanCreate(Guid desktop) => desktop == workspace.Zones.DesktopId
        && workspace.Document.Zones.Count(z => !z.IsTemporaryArea) < ZoneService.MaximumZones;

    private Guid? CreateTelemetryMission(Guid desktop, string taskId, string name)
    {
        if (!CanCreate(desktop)) return null;
        name = string.IsNullOrWhiteSpace(name) ? taskId[..Math.Min(120, taskId.Length)] : name.Trim();
        if (!ZoneService.ValidName(name)) return null;

        var pod = TeamPodLayout.Create(desktop, taskId, new(0, 0));
        var right = annotations.Items.Where(a => a.DesktopId == desktop).Select(a => a.Bounds.X + a.Bounds.Width)
            .Concat(workspace.Document.Zones.Where(z => z.DesktopId == desktop).Select(z => z.Bounds.X + z.Bounds.W))
            .Concat(workspace.Document.Items.Where(i => i.DesktopId == desktop).Select(i => i.Bounds.X + i.Bounds.W));
        double dx = right.DefaultIfEmpty(-60).Max() + 160 - pod.Bounds.X;
        double dy = 100 - pod.Bounds.Y;

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
        var items = pod.Items.Where(a => !annotations.Items.Any(e => e.Id == a.Id)).Select(a => a.Translate(dx, dy)).ToArray();
        return SaveMission(zone, items);
    }

    private Guid? SaveMission(WorkspaceZone zone, CanvasAnnotation[] items)
    {
        if (!ZoneGeometry.ValidBounds(zone.Bounds)) return null;
        var desktop = zone.DesktopId;
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
