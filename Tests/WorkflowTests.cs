using System;
using System.Collections.Generic;
using System.Linq;
using CanvasDesktop;
using CanvasDesktop.CanvasView;
using Xunit;

namespace CanvasDesktop.Tests;

public class WorkflowTests
{
    private static readonly Guid Desktop = Guid.NewGuid();

    [Fact]
    public void WorkflowGraph_JsonSerializationAndDeserialization_PreservesAllProperties()
    {
        var node1 = new WorkflowNode(
            Id: Guid.NewGuid(),
            Kind: "window",
            Title: "Instagram Profile - Chrome",
            CustomName: "IG Profile",
            AppExecutable: @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            TargetUrl: "https://instagram.com/mygirlfriend",
            ChromeProfile: "Profile 2",
            Zone: "Marketing Funnel",
            Bounds: new CanvasRect(100, 100, 480, 360));

        var node2 = new WorkflowNode(
            Id: Guid.NewGuid(),
            Kind: "window",
            Title: "ManyChat Automation",
            CustomName: "ManyChat Flow",
            AppExecutable: @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            TargetUrl: "https://manychat.com",
            ChromeProfile: "Profile 2",
            Zone: "Marketing Funnel",
            Bounds: new CanvasRect(680, 100, 480, 360));

        var projectNode = new WorkflowNode(
            Id: Guid.NewGuid(),
            Kind: "project",
            Title: "Marketing Funnel",
            CustomName: "Marketing Funnel",
            AppExecutable: "",
            TargetUrl: null,
            ChromeProfile: null,
            Zone: null,
            Bounds: new CanvasRect(50, 50, 1200, 500));

        var edge = new WorkflowEdge(
            Id: Guid.NewGuid(),
            SourceId: node1.Id,
            TargetId: node2.Id,
            Description: "IG DM Trigger",
            Color: "#60A5FA",
            Stroke: 16.0,
            Routing: "curved");

        var graph = new WorkflowGraph("Marketing Funnel", Desktop, [node1, node2, projectNode], [edge]);

        string json = WorkflowExport.ToJson(graph);
        Assert.NotNull(json);
        Assert.Contains("Marketing Funnel", json);
        Assert.Contains("https://instagram.com/mygirlfriend", json);
        Assert.Contains("IG DM Trigger", json);
        Assert.Contains("curved", json);

        var restored = WorkflowExport.FromJson(json);
        Assert.NotNull(restored);
        Assert.Equal(graph.Name, restored.Name);
        Assert.Equal(graph.DesktopId, restored.DesktopId);
        Assert.Equal(3, restored.Nodes.Count);
        Assert.Single(restored.Edges);

        var rNode1 = restored.Nodes.First(n => n.Id == node1.Id);
        Assert.Equal(node1.Title, rNode1.Title);
        Assert.Equal(node1.CustomName, rNode1.CustomName);
        Assert.Equal(node1.TargetUrl, rNode1.TargetUrl);
        Assert.Equal(node1.ChromeProfile, rNode1.ChromeProfile);
        Assert.Equal(node1.Zone, rNode1.Zone);
        Assert.Equal(node1.Bounds.X, rNode1.Bounds.X);
        Assert.Equal(node1.Bounds.Y, rNode1.Bounds.Y);

        var rEdge = restored.Edges[0];
        Assert.Equal(edge.Id, rEdge.Id);
        Assert.Equal(edge.SourceId, rEdge.SourceId);
        Assert.Equal(edge.TargetId, rEdge.TargetId);
        Assert.Equal(edge.Description, rEdge.Description);
        Assert.Equal(edge.Stroke, rEdge.Stroke);
        Assert.Equal("curved", rEdge.Routing);
    }

    [Fact]
    public void WorkflowLayoutEngine_MarketingFunnelPlan_GeneratesNonOverlappingLayoutAndThickCurvedArrows()
    {
        var plan = new WorkflowPlan(
            ProjectName: "Marketing Funnel",
            Windows:
            [
                new WorkflowPlanWindow("ig", "IG Chrome Profile", "chrome.exe", "https://instagram.com/mygirlfriend", "Profile 2"),
                new WorkflowPlanWindow("manychat", "ManyChat Automation", "chrome.exe", "https://manychat.com", "Profile 2"),
                new WorkflowPlanWindow("landing", "Funnel Landing Page", "chrome.exe", "https://funnel.example.com", "Profile 2"),
                new WorkflowPlanWindow("ads", "Meta Ads Manager", "chrome.exe", "https://adsmanager.facebook.com", "Profile 2")
            ],
            Links:
            [
                new WorkflowPlanLink("ig", "manychat", "DM Trigger", 16.0, "#3B82F6"),
                new WorkflowPlanLink("manychat", "landing", "Chat Link", 16.0, "#3B82F6"),
                new WorkflowPlanLink("ads", "landing", "Traffic Ad", 16.0, "#3B82F6")
            ]);

        var origin = new CanvasPoint(200, 300);
        var layout = WorkflowLayoutEngine.Generate(plan, origin, Desktop);

        Assert.Equal("Marketing Funnel", layout.ZoneName);
        Assert.Equal(4, layout.Windows.Count);
        Assert.Equal(3, layout.Arrows.Count);

        // Verify non-overlapping window bounding boxes
        for (int i = 0; i < layout.Windows.Count; i++)
        {
            var b1 = layout.Windows[i].Bounds;
            for (int j = i + 1; j < layout.Windows.Count; j++)
            {
                var b2 = layout.Windows[j].Bounds;
                bool intersects = b1.X < b2.X + b2.Width && b1.X + b1.Width > b2.X &&
                                  b1.Y < b2.Y + b2.Height && b1.Y + b1.Height > b2.Y;
                Assert.False(intersects, $"Windows {layout.Windows[i].Spec.Key} and {layout.Windows[j].Spec.Key} overlap!");
            }
        }

        // Verify zone bounds fully encloses all windows
        foreach (var (_, _, b) in layout.Windows)
        {
            Assert.True(b.X >= layout.ZoneBounds.X, "Window left outside zone");
            Assert.True(b.Y >= layout.ZoneBounds.Y, "Window top outside zone");
            Assert.True(b.X + b.Width <= layout.ZoneBounds.X + layout.ZoneBounds.Width, "Window right outside zone");
            Assert.True(b.Y + b.Height <= layout.ZoneBounds.Y + layout.ZoneBounds.Height, "Window bottom outside zone");
        }

        // Verify arrows
        foreach (var arrow in layout.Arrows)
        {
            Assert.Equal(AnnotationKind.Arrow, arrow.Kind);
            Assert.Equal(16.0, arrow.Stroke);
            Assert.Equal(ConnectorRouting.Curved, arrow.Routing);
            Assert.False(string.IsNullOrWhiteSpace(arrow.Text));
            Assert.NotNull(arrow.SourceId);
            Assert.NotNull(arrow.TargetId);
            Assert.NotEqual(arrow.SourceId, arrow.TargetId);
            Assert.NotEqual(arrow.A, arrow.B);
        }
    }

    [Fact]
    public void WorkflowService_ApplyWorkflow_AddsZoneItemsAndAnnotations()
    {
        using var harness = new WorkspaceHarness();
        var annotationDoc = new AnnotationDocument();
        var workflowService = new WorkflowService(harness.Workspace, annotationDoc);

        var plan = new WorkflowPlan(
            ProjectName: "Marketing Funnel",
            Windows:
            [
                new WorkflowPlanWindow("w1", "IG Profile", "chrome.exe", "https://instagram.com/mygirlfriend", "Profile 2"),
                new WorkflowPlanWindow("w2", "ManyChat", "chrome.exe", "https://manychat.com", "Profile 2")
            ],
            Links:
            [
                new WorkflowPlanLink("w1", "w2", "DM link", 16.0, "#38BDF8")
            ]);

        bool applied = workflowService.ApplyWorkflow(Desktop, plan, new CanvasPoint(500, 500));
        Assert.True(applied);

        // Verify zone added to workspace
        var zone = Assert.Single(harness.Workspace.Document.Zones);
        Assert.Equal("Marketing Funnel", zone.Name);
        Assert.Equal(2, zone.ItemIds.Count);

        // Verify workspace items added
        Assert.Equal(2, harness.Workspace.Document.Items.Count);
        var item1 = harness.Workspace.Document.Items.First(i => i.Name == "IG Profile");
        Assert.Equal("https://instagram.com/mygirlfriend", item1.Target);
        Assert.Equal("Profile 2", item1.ChromeProfilePath);

        // Verify annotation arrow added
        var arrow = Assert.Single(annotationDoc.Items);
        Assert.Equal(AnnotationKind.Arrow, arrow.Kind);
        Assert.Equal(16.0, arrow.Stroke);
        Assert.Equal(ConnectorRouting.Curved, arrow.Routing);
        Assert.Equal("DM link", arrow.Text);
        Assert.Equal(item1.Id, arrow.SourceId);

        // Verify GetWorkflow export
        var exported = workflowService.GetWorkflow(Desktop, zone.Id);
        Assert.Equal("Marketing Funnel", exported.Name);
        Assert.Equal(3, exported.Nodes.Count); // 2 windows + 1 project zone
        Assert.Single(exported.Edges);
        Assert.Equal("DM link", exported.Edges[0].Description);
        Assert.Equal(16.0, exported.Edges[0].Stroke);
    }

    [Fact]
    public void ConnectedArrows_TranslateEndpoints_WhenMovedInSelection()
    {
        var srcId = Guid.NewGuid();
        var tgtId = Guid.NewGuid();

        var arrow = new CanvasAnnotation(
            Id: Guid.NewGuid(),
            DesktopId: Desktop,
            Kind: AnnotationKind.Arrow,
            A: new CanvasPoint(100, 100),
            B: new CanvasPoint(300, 100),
            Stroke: 16.0,
            SourceId: srcId,
            SourcePort: ConnectionPort.Right,
            TargetId: tgtId,
            TargetPort: ConnectionPort.Left,
            Routing: ConnectorRouting.Curved);

        // Case 1: Only Source moves by (+50, +20)
        double dx = 50, dy = 20;
        var movedSrcOnly = arrow with { A = new(arrow.A.X + dx, arrow.A.Y + dy) };
        Assert.Equal(150, movedSrcOnly.A.X);
        Assert.Equal(120, movedSrcOnly.A.Y);
        Assert.Equal(300, movedSrcOnly.B.X);
        Assert.Equal(100, movedSrcOnly.B.Y);

        // Case 2: Only Target moves by (-30, +40)
        double tdx = -30, tdy = 40;
        var movedTgtOnly = arrow with { B = new(arrow.B.X + tdx, arrow.B.Y + tdy) };
        Assert.Equal(100, movedTgtOnly.A.X);
        Assert.Equal(100, movedTgtOnly.A.Y);
        Assert.Equal(270, movedTgtOnly.B.X);
        Assert.Equal(140, movedTgtOnly.B.Y);

        // Case 3: Both move by (+100, +50) - rigid translation
        var translated = arrow.Translate(100, 50);
        Assert.Equal(200, translated.A.X);
        Assert.Equal(150, translated.A.Y);
        Assert.Equal(400, translated.B.X);
        Assert.Equal(150, translated.B.Y);
        Assert.Equal(arrow.SourceId, translated.SourceId);
        Assert.Equal(arrow.TargetId, translated.TargetId);
    }

    [Fact]
    public void ConnectedArrows_PreservedWhenWindowClosedOrMinimized_CleanedWhenRemoved()
    {
        using var harness = new WorkspaceHarness();
        var doc = harness.Workspace.Document;

        var window1 = new WorkspaceItem
        {
            Id = Guid.NewGuid(),
            DesktopId = Desktop,
            Title = "Window 1",
            Name = "Window 1",
            Bounds = new WorldRect { X = 0, Y = 0, W = 400, H = 300 }
        };
        var window2 = new WorkspaceItem
        {
            Id = Guid.NewGuid(),
            DesktopId = Desktop,
            Title = "Window 2",
            Name = "Window 2",
            Bounds = new WorldRect { X = 600, Y = 0, W = 400, H = 300 }
        };
        doc.Items.Add(window1);
        doc.Items.Add(window2);

        var arrow = new CanvasAnnotation(
            Id: Guid.NewGuid(),
            DesktopId: Desktop,
            Kind: AnnotationKind.Arrow,
            A: new CanvasPoint(400, 150),
            B: new CanvasPoint(600, 150),
            Stroke: 16.0,
            SourceId: window1.Id,
            TargetId: window2.Id);

        var annotationDoc = new AnnotationDocument([arrow]);
        Assert.Single(annotationDoc.Items);

        // Emulate checking valid IDs on canvas
        var validIds = doc.Items.Where(i => i.DesktopId == Desktop).Select(i => i.Id).ToHashSet();

        // 1. When window 1 is minimized or closed: its WorkspaceItem remains in doc.Items as placeholder
        Assert.Contains(window1.Id, validIds);
        bool sourceValid = validIds.Contains(arrow.SourceId!.Value);
        bool targetValid = validIds.Contains(arrow.TargetId!.Value);
        Assert.True(sourceValid && targetValid, "Arrow should stay attached when window is minimized/closed placeholder");

        // 2. When window 1 is explicitly forgotten / deleted from workspace
        doc.Items.Remove(window1);
        var updatedValidIds = doc.Items.Where(i => i.DesktopId == Desktop).Select(i => i.Id).ToHashSet();
        Assert.DoesNotContain(window1.Id, updatedValidIds);

        // Arrow source is now invalid
        bool orphaned = !updatedValidIds.Contains(arrow.SourceId.Value) || !updatedValidIds.Contains(arrow.TargetId.Value);
        Assert.True(orphaned, "Arrow should be recognized as orphaned when window is removed from workspace");
        if (orphaned)
        {
            annotationDoc.Delete(Desktop, arrow.Id);
        }
        Assert.Empty(annotationDoc.Items);
    }

    [Fact]
    public void WorkflowService_ImportWorkflowJson_PlanFormat_SuccessfullyApplies()
    {
        using var harness = new WorkspaceHarness();
        var annotationDoc = new AnnotationDocument();
        var workflowService = new WorkflowService(harness.Workspace, annotationDoc);

        var plan = new WorkflowPlan(
            ProjectName: "Imported Funnel",
            Windows:
            [
                new WorkflowPlanWindow("w1", "IG Profile", "chrome.exe", "https://instagram.com/mygirlfriend", "Profile 2"),
                new WorkflowPlanWindow("w2", "ManyChat", "chrome.exe", "https://manychat.com", "Profile 2")
            ],
            Links:
            [
                new WorkflowPlanLink("w1", "w2", "Lead Trigger", 16.0, "#38BDF8")
            ]);

        string json = System.Text.Json.JsonSerializer.Serialize(plan);
        bool imported = workflowService.ImportWorkflowJson(Desktop, json, new CanvasPoint(200, 200));

        Assert.True(imported);
        var zone = Assert.Single(harness.Workspace.Document.Zones);
        Assert.Equal("Imported Funnel", zone.Name);
        Assert.Equal(2, harness.Workspace.Document.Items.Count);
        var arrow = Assert.Single(annotationDoc.Items);
        Assert.Equal("Lead Trigger", arrow.Text);
        Assert.Equal(16.0, arrow.Stroke);
    }

    [Fact]
    public void WorkflowService_ImportWorkflowJson_GraphFormat_SuccessfullyAppliesAndRemapsIds()
    {
        using var harness = new WorkspaceHarness();
        var annotationDoc = new AnnotationDocument();
        var workflowService = new WorkflowService(harness.Workspace, annotationDoc);

        var node1 = new WorkflowNode(
            Id: Guid.NewGuid(),
            Kind: "window",
            Title: "IG Chrome",
            CustomName: "IG Chrome",
            AppExecutable: "chrome.exe",
            TargetUrl: "https://instagram.com",
            ChromeProfile: "Profile 1",
            Zone: "Marketing Zone",
            Bounds: new CanvasRect(100, 100, 400, 300));

        var node2 = new WorkflowNode(
            Id: Guid.NewGuid(),
            Kind: "window",
            Title: "ManyChat",
            CustomName: "ManyChat Flow",
            AppExecutable: "chrome.exe",
            TargetUrl: "https://manychat.com",
            ChromeProfile: "Profile 1",
            Zone: "Marketing Zone",
            Bounds: new CanvasRect(600, 100, 400, 300));

        var projectNode = new WorkflowNode(
            Id: Guid.NewGuid(),
            Kind: "project",
            Title: "Marketing Zone",
            CustomName: "Marketing Zone",
            AppExecutable: "",
            TargetUrl: null,
            ChromeProfile: null,
            Zone: null,
            Bounds: new CanvasRect(50, 50, 1000, 450));

        var edge = new WorkflowEdge(
            Id: Guid.NewGuid(),
            SourceId: node1.Id,
            TargetId: node2.Id,
            Description: "Bio Link",
            Color: "#60A5FA",
            Stroke: 16.0,
            Routing: "curved");

        var graph = new WorkflowGraph("Marketing Zone", Desktop, [node1, node2, projectNode], [edge]);
        string json = WorkflowExport.ToJson(graph);

        bool imported = workflowService.ImportWorkflowJson(Desktop, json, new CanvasPoint(500, 500));
        Assert.True(imported);

        var zone = Assert.Single(harness.Workspace.Document.Zones);
        Assert.Equal("Marketing Zone", zone.Name);
        Assert.Equal(2, harness.Workspace.Document.Items.Count);

        var arrow = Assert.Single(annotationDoc.Items);
        Assert.Equal("Bio Link", arrow.Text);
        Assert.NotEqual(node1.Id, arrow.SourceId); // Verify remapped ID
        Assert.NotEqual(node2.Id, arrow.TargetId);
        Assert.Contains(harness.Workspace.Document.Items, i => i.Id == arrow.SourceId);
        Assert.Contains(harness.Workspace.Document.Items, i => i.Id == arrow.TargetId);
    }

    [Fact]
    public void WorkflowService_ImportWorkflowJson_InvalidData_ReturnsFalse()
    {
        using var harness = new WorkspaceHarness();
        var annotationDoc = new AnnotationDocument();
        var workflowService = new WorkflowService(harness.Workspace, annotationDoc);

        Assert.False(workflowService.ImportWorkflowJson(Desktop, "", new CanvasPoint(0, 0)));
        Assert.False(workflowService.ImportWorkflowJson(Desktop, "not json", new CanvasPoint(0, 0)));
        Assert.False(workflowService.ImportWorkflowJson(Desktop, "{}", new CanvasPoint(0, 0)));
    }
}
