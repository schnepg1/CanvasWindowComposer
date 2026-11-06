using System;
using System.Collections.Generic;
using System.Linq;
using CanvasDesktop.CanvasView;

namespace CanvasDesktop;

internal interface IWorkflowService
{
    WorkflowGraph GetWorkflow(Guid desktopId, Guid? zoneId = null);
    string ExportWorkflowJson(Guid desktopId, Guid? zoneId = null);
    bool ApplyWorkflow(Guid desktopId, WorkflowPlan plan, CanvasPoint origin);
    bool ImportWorkflowJson(Guid desktopId, string json, CanvasPoint origin);
    bool ApplyGraph(Guid desktopId, WorkflowGraph graph, CanvasPoint origin);
}

internal sealed class WorkflowService : IWorkflowService
{
    private readonly WorkspaceService _workspace;
    private readonly AnnotationDocument _annotations;
    private readonly AnnotationPersistence? _annotationStore;

    public WorkflowService(WorkspaceService workspace, AnnotationDocument annotations, AnnotationPersistence? annotationStore = null)
    {
        _workspace = workspace;
        _annotations = annotations;
        _annotationStore = annotationStore;
    }

    public WorkflowGraph GetWorkflow(Guid desktopId, Guid? zoneId = null)
    {
        var doc = _workspace.Document;
        var zones = doc.Zones.Where(z => z.DesktopId == desktopId).ToList();
        if (zoneId.HasValue) zones = zones.Where(z => z.Id == zoneId.Value).ToList();

        var zoneItemIds = zones.SelectMany(z => z.ItemIds).ToHashSet();
        var zoneById = zones.ToDictionary(z => z.Id);

        var items = doc.Items.Where(i => i.DesktopId == desktopId && !i.Bounds.PinnedToScreen && !i.Bounds.InInventory);
        if (zoneId.HasValue) items = items.Where(i => zoneItemIds.Contains(i.Id));
        var itemList = items.ToList();

        var nodes = new List<WorkflowNode>();

        foreach (var item in itemList)
        {
            var enclosingZone = zones.FirstOrDefault(z => z.ItemIds.Contains(item.Id));
            string url = item.WebResource?.Url ?? (item.AutoDetectedTarget ? "" : item.Target);
            string profile = !string.IsNullOrEmpty(item.ChromeProfilePath)
                ? item.ChromeProfilePath
                : (item.WebResource?.ProfilePath ?? "");

            nodes.Add(new WorkflowNode(
                Id: item.Id,
                Kind: "window",
                Title: item.Title,
                CustomName: string.IsNullOrWhiteSpace(item.Name) ? item.Title : item.Name,
                AppExecutable: item.ExecutablePath,
                TargetUrl: string.IsNullOrEmpty(url) ? null : url,
                ChromeProfile: string.IsNullOrEmpty(profile) ? null : profile,
                Zone: enclosingZone?.Name,
                Bounds: new CanvasRect(item.Bounds.X, item.Bounds.Y, item.Bounds.W, item.Bounds.H)));
        }

        foreach (var zone in zones)
        {
            nodes.Add(new WorkflowNode(
                Id: zone.Id,
                Kind: "project",
                Title: zone.Name,
                CustomName: zone.Name,
                AppExecutable: "",
                TargetUrl: null,
                ChromeProfile: null,
                Zone: null,
                Bounds: new CanvasRect(zone.Bounds.X, zone.Bounds.Y, zone.Bounds.W, zone.Bounds.H)));
        }

        var nodeIds = nodes.Select(n => n.Id).ToHashSet();

        var arrows = _annotations.Items
            .Where(a => a.DesktopId == desktopId && a.Kind == AnnotationKind.Arrow)
            .Where(a => a.SourceId.HasValue && nodeIds.Contains(a.SourceId.Value) &&
                        a.TargetId.HasValue && nodeIds.Contains(a.TargetId.Value))
            .ToList();

        var edges = arrows.Select(a => new WorkflowEdge(
            Id: a.Id,
            SourceId: a.SourceId!.Value,
            TargetId: a.TargetId!.Value,
            Description: a.Text,
            Color: a.Color,
            Stroke: a.Stroke,
            Routing: a.Routing.ToString().ToLowerInvariant())).ToList();

        string graphName = zones.Count == 1 ? zones[0].Name : "Canvas Workflow";
        return new WorkflowGraph(graphName, desktopId, nodes, edges);
    }

    public string ExportWorkflowJson(Guid desktopId, Guid? zoneId = null)
        => WorkflowExport.ToJson(GetWorkflow(desktopId, zoneId));

    public bool ApplyWorkflow(Guid desktopId, WorkflowPlan plan, CanvasPoint origin)
    {
        var generated = WorkflowLayoutEngine.Generate(plan, origin, desktopId);

        var zone = new WorkspaceZone
        {
            Id = Guid.NewGuid(),
            DesktopId = desktopId,
            Name = generated.ZoneName,
            Color = "#60A5FA",
            Bounds = new WorldRect
            {
                X = generated.ZoneBounds.X,
                Y = generated.ZoneBounds.Y,
                W = generated.ZoneBounds.Width,
                H = generated.ZoneBounds.Height
            },
            ItemIds = generated.Windows.Select(w => w.Id).ToList()
        };

        _workspace.Document.Zones.Add(zone);

        foreach (var (spec, id, bounds) in generated.Windows)
        {
            string url = spec.Url ?? "";
            string profile = spec.ChromeProfile ?? "";
            var item = new WorkspaceItem
            {
                Id = id,
                DesktopId = desktopId,
                Placement = WorkspacePlacement.Deliberate,
                Title = spec.CustomName,
                Name = spec.CustomName,
                ExecutablePath = string.IsNullOrWhiteSpace(spec.App) ? "chrome.exe" : spec.App,
                Target = url,
                ChromeProfilePath = profile,
                WebResource = !string.IsNullOrEmpty(url) ? new WebResource(url, profile) : null,
                Bounds = new WorldRect
                {
                    X = bounds.X,
                    Y = bounds.Y,
                    W = bounds.Width,
                    H = bounds.Height
                }
            };

            _workspace.Document.Items.Add(item);
        }

        foreach (var arrow in generated.Arrows)
        {
            _annotations.Put(arrow);
        }

        _workspace.SaveNow();
        _annotationStore?.Save();

        return true;
    }

    public bool ImportWorkflowJson(Guid desktopId, string json, CanvasPoint origin)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;

        // Try WorkflowPlan first
        try
        {
            var plan = System.Text.Json.JsonSerializer.Deserialize<WorkflowPlan>(json, new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            if (plan != null && plan.Windows != null && plan.Windows.Count > 0)
            {
                return ApplyWorkflow(desktopId, plan, origin);
            }
        }
        catch { }

        // Try WorkflowGraph
        try
        {
            var graph = WorkflowExport.FromJson(json);
            if (graph != null && graph.Nodes != null && graph.Nodes.Count > 0)
            {
                return ApplyGraph(desktopId, graph, origin);
            }
        }
        catch { }

        return false;
    }

    public bool ApplyGraph(Guid desktopId, WorkflowGraph graph, CanvasPoint origin)
    {
        var idMap = new Dictionary<Guid, Guid>();
        var windowNodes = graph.Nodes.Where(n => n.Kind == "window").ToList();
        var projectNodes = graph.Nodes.Where(n => n.Kind == "project").ToList();

        double minX = graph.Nodes.Min(n => n.Bounds.X);
        double minY = graph.Nodes.Min(n => n.Bounds.Y);
        double dx = origin.X - minX;
        double dy = origin.Y - minY;

        foreach (var pNode in projectNodes)
        {
            var newId = Guid.NewGuid();
            idMap[pNode.Id] = newId;

            var zone = new WorkspaceZone
            {
                Id = newId,
                DesktopId = desktopId,
                Name = pNode.CustomName,
                Color = "#60A5FA",
                Bounds = new WorldRect
                {
                    X = pNode.Bounds.X + dx,
                    Y = pNode.Bounds.Y + dy,
                    W = pNode.Bounds.Width,
                    H = pNode.Bounds.Height
                },
                ItemIds = new List<Guid>()
            };
            _workspace.Document.Zones.Add(zone);
        }

        foreach (var wNode in windowNodes)
        {
            var newId = Guid.NewGuid();
            idMap[wNode.Id] = newId;

            string url = wNode.TargetUrl ?? "";
            string profile = wNode.ChromeProfile ?? "";
            var item = new WorkspaceItem
            {
                Id = newId,
                DesktopId = desktopId,
                Placement = WorkspacePlacement.Deliberate,
                Title = wNode.Title,
                Name = wNode.CustomName,
                ExecutablePath = string.IsNullOrWhiteSpace(wNode.AppExecutable) ? "chrome.exe" : wNode.AppExecutable,
                Target = url,
                ChromeProfilePath = profile,
                WebResource = !string.IsNullOrEmpty(url) ? new WebResource(url, profile) : null,
                Bounds = new WorldRect
                {
                    X = wNode.Bounds.X + dx,
                    Y = wNode.Bounds.Y + dy,
                    W = wNode.Bounds.Width,
                    H = wNode.Bounds.Height
                }
            };
            _workspace.Document.Items.Add(item);

            if (!string.IsNullOrEmpty(wNode.Zone))
            {
                var zone = _workspace.Document.Zones.FirstOrDefault(z => z.DesktopId == desktopId && z.Name == wNode.Zone);
                zone?.ItemIds.Add(newId);
            }
        }

        foreach (var edge in graph.Edges ?? [])
        {
            if (!idMap.TryGetValue(edge.SourceId, out var newSrcId) || !idMap.TryGetValue(edge.TargetId, out var newTgtId))
                continue;

            var srcNode = windowNodes.FirstOrDefault(n => n.Id == edge.SourceId);
            var tgtNode = windowNodes.FirstOrDefault(n => n.Id == edge.TargetId);
            if (srcNode == null || tgtNode == null) continue;

            var srcBounds = new CanvasRect(srcNode.Bounds.X + dx, srcNode.Bounds.Y + dy, srcNode.Bounds.Width, srcNode.Bounds.Height);
            var tgtBounds = new CanvasRect(tgtNode.Bounds.X + dx, tgtNode.Bounds.Y + dy, tgtNode.Bounds.Width, tgtNode.Bounds.Height);

            var (a, b) = AnnotationGeometry.ComputePortAnchors(srcBounds, ConnectionPort.Auto, tgtBounds, ConnectionPort.Auto);

            var routing = string.Equals(edge.Routing, "orthogonal", StringComparison.OrdinalIgnoreCase)
                ? ConnectorRouting.Orthogonal
                : string.Equals(edge.Routing, "straight", StringComparison.OrdinalIgnoreCase)
                    ? ConnectorRouting.Straight
                    : ConnectorRouting.Curved;

            var arrow = new CanvasAnnotation(
                Id: Guid.NewGuid(),
                DesktopId: desktopId,
                Kind: AnnotationKind.Arrow,
                A: a,
                B: b,
                Color: string.IsNullOrEmpty(edge.Color) ? "#60A5FA" : edge.Color,
                Stroke: edge.Stroke > 0 ? edge.Stroke : 16.0,
                Text: edge.Description ?? "",
                FontSize: 20,
                Routing: routing,
                SourceId: newSrcId,
                TargetId: newTgtId);

            _annotations.Put(arrow);
        }

        _workspace.SaveNow();
        _annotationStore?.Save();

        return true;
    }
}
