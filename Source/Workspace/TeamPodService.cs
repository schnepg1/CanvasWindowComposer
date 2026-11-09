using System;
using System.Linq;

namespace CanvasDesktop;

/// <summary>UI-thread reconciliation. Telemetry updates never reposition an existing pod.</summary>
internal sealed class TeamPodService(WorkspaceService workspace, AnnotationDocument annotations)
{
    public void Reconcile(TeamTelemetrySnapshot snapshot, Guid desktop)
    {
        foreach (var team in snapshot.Teams.Values)
        {
            if (workspace.Document.Zones.Any(z => z.AgentTaskId == team.TaskId)) continue;
            if (workspace.Document.Zones.Count(z => !z.IsTemporaryArea) >= ZoneService.MaximumZones) return;
            var obstacles = annotations.Items.Where(a => a.DesktopId == desktop).Select(a => a.Bounds)
                .Concat(workspace.Document.Zones.Where(z => z.DesktopId == desktop).Select(z => new CanvasRect(z.Bounds.X, z.Bounds.Y, z.Bounds.W, z.Bounds.H)))
                .Concat(workspace.Document.Items.Where(i => i.DesktopId == desktop).Select(i => new CanvasRect(i.Bounds.X, i.Bounds.Y, i.Bounds.W, i.Bounds.H))).ToArray();
            var pod = TeamPodLayout.Create(desktop, team.TaskId, new(0, 0));
            double x = obstacles.Length == 0 ? 100 : obstacles.Max(b => b.X + b.Width) + 160;
            double dx = x - pod.Bounds.X, dy = 100 - pod.Bounds.Y;
            // Recover saved annotations if workspace persistence lagged behind the annotation save.
            var existing = annotations.Items.Where(a => a.AgentTaskId == team.TaskId && a.DesktopId == desktop).ToArray();
            var anchor = existing.FirstOrDefault(a => pod.Items.Any(p => p.Id == a.Id));
            if (anchor != null)
            {
                dx = anchor.A.X - pod.Items.First(a => a.Id == anchor.Id).A.X;
                dy = anchor.A.Y - pod.Items.First(a => a.Id == anchor.Id).A.Y;
            }
            var items = pod.Items.Select(a => a.Translate(dx, dy)).Where(a => !annotations.Items.Any(e => e.Id == a.Id)).ToArray();
            AnnotationDocument.Validate(annotations.Items.Concat(items));
            var zone = new WorkspaceZone
            {
                Id = pod.ZoneId, DesktopId = desktop, AgentTaskId = team.TaskId,
                Name = "AI Mission Control: " + team.Name, Color = "#38BDF8",
                Bounds = new WorldRect { X = pod.Bounds.X + dx, Y = pod.Bounds.Y + dy, W = pod.Bounds.Width, H = pod.Bounds.Height }
            };
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
                    if (!workspace.Document.Zones.Contains(zone) || zone.ItemIds.Count > 0) return false;
                    workspace.Document.Zones.Remove(zone);
                }
                workspace.SaveNow();
                workspace.Zones.NotifyDesktopChanged();
                return true;
            }
            annotations.ApplyBatch(annotations.Revision, desktop, items, Place);
        }
    }
}
