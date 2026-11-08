using System;
using System.Collections.Generic;
using System.Linq;

namespace CanvasDesktop.CanvasView;

public sealed record GeneratedWorkflow(
    CanvasRect ZoneBounds,
    string ZoneName,
    IReadOnlyList<(WorkflowPlanWindow Spec, Guid Id, CanvasRect Bounds)> Windows,
    IReadOnlyList<CanvasAnnotation> Arrows);

public static class WorkflowLayoutEngine
{
    public const double DefaultWindowWidth = 800;
    public const double DefaultWindowHeight = 600;
    public const double ColumnSpacing = 320;
    public const double RowSpacing = 160;
    public const double ZonePadding = 80;

    public static GeneratedWorkflow Generate(WorkflowPlan plan, CanvasPoint origin, Guid desktopId)
    {
        if (plan is null || plan.Windows is null || plan.Windows.Count == 0)
            throw new ArgumentException("Plan must contain at least one window.");

        var inDegree = plan.Windows.ToDictionary(w => w.Key, _ => 0);
        foreach (var link in plan.Links ?? [])
        {
            if (inDegree.ContainsKey(link.To)) inDegree[link.To]++;
        }

        var rank = plan.Windows.ToDictionary(w => w.Key, _ => 0);
        var queue = new Queue<string>(plan.Windows.Where(w => inDegree[w.Key] == 0).Select(w => w.Key));
        int visited = 0;
        while (queue.TryDequeue(out var key))
        {
            visited++;
            foreach (var link in (plan.Links ?? []).Where(l => l.From == key))
            {
                rank[link.To] = Math.Max(rank[link.To], rank[key] + 1);
                if (--inDegree[link.To] == 0) queue.Enqueue(link.To);
            }
        }
        if (visited != plan.Windows.Count)
        {
            for (int i = 0; i < plan.Windows.Count; i++) rank[plan.Windows[i].Key] = i;
        }

        bool horizontal = string.Equals(plan.LayoutDirection, "LR", StringComparison.OrdinalIgnoreCase);
        var columnCounts = new Dictionary<int, int>();
        var positions = new Dictionary<string, (Guid Id, CanvasRect Bounds)>();

        foreach (var w in plan.Windows)
        {
            int col = rank[w.Key];
            columnCounts.TryGetValue(col, out int row);
            columnCounts[col] = row + 1;

            double x = horizontal
                ? origin.X + ZonePadding + col * (DefaultWindowWidth + ColumnSpacing)
                : origin.X + ZonePadding + row * (DefaultWindowWidth + ColumnSpacing);

            double y = horizontal
                ? origin.Y + ZonePadding + row * (DefaultWindowHeight + RowSpacing)
                : origin.Y + ZonePadding + col * (DefaultWindowHeight + RowSpacing);

            positions[w.Key] = (Guid.NewGuid(), new CanvasRect(x, y, DefaultWindowWidth, DefaultWindowHeight));
        }

        double minX = positions.Values.Min(p => p.Bounds.X) - ZonePadding;
        double minY = positions.Values.Min(p => p.Bounds.Y) - ZonePadding;
        double maxX = positions.Values.Max(p => p.Bounds.X + p.Bounds.Width) + ZonePadding;
        double maxY = positions.Values.Max(p => p.Bounds.Y + p.Bounds.Height) + ZonePadding;
        var zoneBounds = new CanvasRect(minX, minY, maxX - minX, maxY - minY);

        var arrows = new List<CanvasAnnotation>();
        foreach (var link in plan.Links ?? [])
        {
            if (!positions.TryGetValue(link.From, out var src) || !positions.TryGetValue(link.To, out var tgt))
                continue;

            var (a, b) = AnnotationGeometry.ComputePortAnchors(
                src.Bounds, ConnectionPort.Auto,
                tgt.Bounds, ConnectionPort.Auto);

            arrows.Add(new CanvasAnnotation(
                Id: Guid.NewGuid(),
                DesktopId: desktopId,
                Kind: AnnotationKind.Arrow,
                A: a,
                B: b,
                Color: link.Color,
                Stroke: Math.Clamp(link.Stroke, AnnotationDocument.MinimumStroke, AnnotationDocument.MaximumStroke),
                Text: link.Label ?? "",
                FontSize: 20,
                Routing: ConnectorRouting.Curved,
                SourceId: src.Id,
                TargetId: tgt.Id));
        }

        var windowList = plan.Windows.Select(w => (w, positions[w.Key].Id, positions[w.Key].Bounds)).ToList();
        return new GeneratedWorkflow(zoneBounds, plan.ProjectName, windowList, arrows);
    }
}
