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
}
