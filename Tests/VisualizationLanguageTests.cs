using System;
using System.IO;
using System.Linq;
using CanvasDesktop;
using CanvasDesktop.CanvasView;
using Xunit;

namespace CanvasDesktop.Tests;

public class VisualizationLanguageTests
{
    private static readonly Guid Desktop = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Fact]
    public void CanvasComposer_Prepare_SupportsMultikindNodesAndCurvedArrows()
    {
        var context = new ComposerContext(Desktop, 1, new CanvasPoint(100, 100), [], []);

        var nodes = new[]
        {
            new ComposerNode("client", "web", "React Web", "#38BDF8", Title: "Storefront UI", Subtitle: "https://store.example.com", Url: "https://store.example.com"),
            new ComposerNode("api", "service", "API Gateway", "#10B981", Title: "Gateway :8080", Subtitle: "Go Reverse Proxy"),
            new ComposerNode("db", "database", "PostgreSQL", "#3B82F6", Title: "Orders DB", Subtitle: "Postgres 16 Primary")
        };

        var edges = new[]
        {
            new ComposerEdge("client", "api", "#60A5FA", Label: "REST POST /checkout", Stroke: 16.0, Routing: "curved"),
            new ComposerEdge("api", "db", "#10B981", Label: "SQL INSERT orders", Stroke: 16.0, Routing: "curved")
        };

        var plan = new ComposerPlan("E-Commerce Architecture", nodes, edges, [], Direction: "LR");
        Assert.True(plan.HasWorkflowEntities());

        var preview = CanvasComposer.Prepare(context, plan);
        Assert.Equal("E-Commerce Architecture", preview.Summary);
        Assert.NotNull(preview.NodeIdMap);
        Assert.Equal(3, preview.NodeIdMap.Count);

        // Verify 3 nodes + 2 arrows = 5 items
        Assert.Equal(5, preview.Items.Count);

        var nodeAnnotations = preview.Items.Where(i => i.Kind == AnnotationKind.Rectangle).ToList();
        Assert.Equal(3, nodeAnnotations.Count);
        Assert.All(nodeAnnotations, a => Assert.Equal(AnnotationCardFill.Solid, a.CardFill));
        Assert.Contains(nodeAnnotations, a => a.Text.Contains("Storefront UI") && a.Text.Contains("https://store.example.com"));

        var arrowAnnotations = preview.Items.Where(i => i.Kind == AnnotationKind.Arrow).ToList();
        Assert.Equal(2, arrowAnnotations.Count);
        Assert.Equal("REST POST /checkout", arrowAnnotations[0].Text);
        Assert.Equal(16.0, arrowAnnotations[0].Stroke);
        Assert.Equal(ConnectorRouting.Curved, arrowAnnotations[0].Routing);
        Assert.NotNull(arrowAnnotations[0].SourceId);
        Assert.NotNull(arrowAnnotations[0].TargetId);
    }

    [Fact]
    public void CanvasComposer_Prepare_SupportsVerticalTopDownLayout()
    {
        var context = new ComposerContext(Desktop, 1, new CanvasPoint(500, 500), [], []);

        var nodes = new[]
        {
            new ComposerNode("step1", "step", "Stage 1", "#38BDF8", Title: "Ingest Data"),
            new ComposerNode("step2", "step", "Stage 2", "#10B981", Title: "Transform ETL"),
            new ComposerNode("step3", "step", "Stage 3", "#F59E0B", Title: "Publish Dashboard")
        };

        var edges = new[]
        {
            new ComposerEdge("step1", "step2", "#38BDF8", Label: "Raw JSON", Stroke: 12.0),
            new ComposerEdge("step2", "step3", "#10B981", Label: "Parquet Tables", Stroke: 12.0)
        };

        var plan = new ComposerPlan("ETL Pipeline", nodes, edges, [], Direction: "TD");
        var preview = CanvasComposer.Prepare(context, plan);

        var step1 = preview.Items.First(i => i.Text.Contains("Ingest Data"));
        var step2 = preview.Items.First(i => i.Text.Contains("Transform ETL"));
        var step3 = preview.Items.First(i => i.Text.Contains("Publish Dashboard"));

        // In TD layout, rows advance downwards (Y increases with each rank)
        Assert.True(step1.Bounds.Y < step2.Bounds.Y, "step1 should be placed above step2 in TD layout");
        Assert.True(step2.Bounds.Y < step3.Bounds.Y, "step2 should be placed above step3 in TD layout");
    }

    [Fact]
    public void CanvasComposer_Prepare_GeneratesZoneBackgroundPreview()
    {
        var context = new ComposerContext(Desktop, 1, new CanvasPoint(200, 200), [], []);

        var zones = new[]
        {
            new ComposerZone("z_cloud", "AWS Cloud Infrastructure", "#38BDF8")
        };

        var nodes = new[]
        {
            new ComposerNode("api", "service", "API Service", "#10B981", Title: "API Server", Zone: "z_cloud"),
            new ComposerNode("db", "database", "Database", "#3B82F6", Title: "Primary DB", Zone: "z_cloud")
        };

        var edges = new[]
        {
            new ComposerEdge("api", "db", "#60A5FA", Label: "Query")
        };

        var plan = new ComposerPlan("Cloud Setup", nodes, edges, [], zones, Direction: "LR");
        var preview = CanvasComposer.Prepare(context, plan);

        // A background zone rectangle should have been inserted
        var zoneRect = preview.Items.FirstOrDefault(i => i.InBackground && i.Text == "AWS Cloud Infrastructure");
        Assert.NotNull(zoneRect);
        Assert.Equal(AnnotationCardFill.Tinted, zoneRect.CardFill);

        var apiNode = preview.Items.First(i => i.Text.Contains("API Server"));
        var dbNode = preview.Items.First(i => i.Text.Contains("Primary DB"));

        // Zone rectangle bounds must encompass member nodes
        Assert.True(zoneRect.Bounds.X <= apiNode.Bounds.X);
        Assert.True(zoneRect.Bounds.Y <= apiNode.Bounds.Y);
        Assert.True(zoneRect.Bounds.X + zoneRect.Bounds.Width >= dbNode.Bounds.X + dbNode.Bounds.Width);
        Assert.True(zoneRect.Bounds.Y + zoneRect.Bounds.Height >= dbNode.Bounds.Y + dbNode.Bounds.Height);
    }

    [Fact]
    public void WorkflowService_ApplyComposerPlan_MaterializesZonesItemsAndAnnotations()
    {
        using var harness = new WorkspaceHarness();
        var annotationDoc = new AnnotationDocument();
        var workflowService = new WorkflowService(harness.Workspace, annotationDoc);

        var zones = new[]
        {
            new ComposerZone("z_prod", "Production Cluster", "#38BDF8")
        };

        var nodes = new[]
        {
            new ComposerNode("app1", "web", "Frontend", "#38BDF8", Title: "Dashboard", App: "chrome.exe", Url: "https://dash.example.com", ChromeProfile: "Profile 1", Zone: "z_prod"),
            new ComposerNode("svc1", "service", "Backend", "#10B981", Title: "Payment Microservice", Subtitle: "Port 5000", Zone: "z_prod"),
            new ComposerNode("db1", "database", "Database", "#3B82F6", Title: "Postgres Main", Zone: "z_prod")
        };

        var edges = new[]
        {
            new ComposerEdge("app1", "svc1", "#60A5FA", Label: "charge.create", Stroke: 16.0),
            new ComposerEdge("svc1", "db1", "#10B981", Label: "ledger entry", Stroke: 16.0)
        };

        var plan = new ComposerPlan("FinTech System", nodes, edges, [], zones, Direction: "LR");
        var context = new ComposerContext(Desktop, 1, new CanvasPoint(100, 100), [], []);
        var preview = CanvasComposer.Prepare(context, plan);

        bool applied = workflowService.ApplyComposerPlan(Desktop, plan, preview);
        Assert.True(applied);

        // 1. Verify Zone created
        var zone = Assert.Single(harness.Workspace.Document.Zones);
        Assert.Equal("Production Cluster", zone.Name);
        Assert.Single(zone.ItemIds); // The web item app1

        // 2. Verify WorkspaceItem created for web node
        var item = Assert.Single(harness.Workspace.Document.Items);
        Assert.Equal("Dashboard", item.Title);
        Assert.Equal("https://dash.example.com", item.Target);
        Assert.Equal("Profile 1", item.ChromeProfilePath);

        // 3. Verify conceptual cards and arrows stored in annotations
        // svc1 + db1 (2 cards) + 2 arrows = 4 annotations
        Assert.Equal(4, annotationDoc.Items.Count);

        var arrows = annotationDoc.Items.Where(a => a.Kind == AnnotationKind.Arrow).ToList();
        Assert.Equal(2, arrows.Count);
        Assert.Contains(arrows, a => a.Text == "charge.create" && a.Stroke == 16.0);
        Assert.Contains(arrows, a => a.Text == "ledger entry" && a.Stroke == 16.0);
    }

    [Fact]
    public void WorkflowService_ImportWorkflowJson_ComposerPlanJson_SuccessfullyImports()
    {
        using var harness = new WorkspaceHarness();
        var annotationDoc = new AnnotationDocument();
        var workflowService = new WorkflowService(harness.Workspace, annotationDoc);

        var plan = new ComposerPlan(
            Summary: "CI/CD Pipeline",
            Nodes:
            [
                new ComposerNode("git", "web", "GitHub Repo", "#38BDF8", Title: "Repository", Url: "https://github.com/myorg/app"),
                new ComposerNode("ci", "service", "Actions Runner", "#10B981", Title: "Build Runner"),
                new ComposerNode("k8s", "service", "Kubernetes", "#3B82F6", Title: "Production Pods")
            ],
            Edges:
            [
                new ComposerEdge("git", "ci", "#60A5FA", Label: "Webhook: push", Stroke: 16.0),
                new ComposerEdge("ci", "k8s", "#10B981", Label: "Helm Deploy", Stroke: 16.0)
            ],
            Edits: []);

        string json = System.Text.Json.JsonSerializer.Serialize(plan, new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            WriteIndented = true
        });

        bool imported = workflowService.ImportWorkflowJson(Desktop, json, new CanvasPoint(300, 300));
        Assert.True(imported);

        Assert.Single(harness.Workspace.Document.Items);
        Assert.Equal("https://github.com/myorg/app", harness.Workspace.Document.Items[0].Target);

        var arrows = annotationDoc.Items.Where(a => a.Kind == AnnotationKind.Arrow).ToList();
        Assert.Equal(2, arrows.Count);
        Assert.Equal("Webhook: push", arrows[0].Text);
    }

    [Fact]
    public void WorkflowPlan_ToComposerPlan_ConvertsCleanly()
    {
        var plan = new WorkflowPlan(
            ProjectName: "Marketing Funnel",
            Windows:
            [
                new WorkflowPlanWindow("ig", "Instagram Profile", "chrome.exe", "https://instagram.com/mybrand", "Profile 1"),
                new WorkflowPlanWindow("mc", "ManyChat Flow", "chrome.exe", "https://manychat.com", "Profile 1")
            ],
            Links:
            [
                new WorkflowPlanLink("ig", "mc", "DM Automation", 16.0, "#60A5FA")
            ],
            LayoutDirection: "LR");

        var composerPlan = plan.ToComposerPlan();
        Assert.Equal("Marketing Funnel", composerPlan.Summary);
        Assert.Equal("LR", composerPlan.Direction);
        Assert.Single(composerPlan.Zones!);
        Assert.Equal("Marketing Funnel", composerPlan.Zones![0].Name);
        Assert.Equal(2, composerPlan.Nodes.Length);
        Assert.Single(composerPlan.Edges);
        Assert.Equal("DM Automation", composerPlan.Edges[0].Label);
        Assert.Equal(16.0, composerPlan.Edges[0].Stroke);
    }

    [Fact]
    public void SpaceshipBridge_TeamArchitecture_MaterializesOnCanvasViaCVL()
    {
        using var harness = new WorkspaceHarness();
        var annotationDoc = new AnnotationDocument();
        var workflowService = new WorkflowService(harness.Workspace, annotationDoc);

        string repoRoot = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", ".."));
        string jsonPath = Path.Combine(repoRoot, ".agents", "teams", "team_architecture_cvl.json");
        if (!File.Exists(jsonPath))
        {
            jsonPath = Path.Combine(Environment.CurrentDirectory, ".agents", "teams", "team_architecture_cvl.json");
        }
        string json = File.ReadAllText(jsonPath);

        bool imported = workflowService.ImportWorkflowJson(Desktop, json, new CanvasPoint(100, 100));
        Assert.True(imported);

        // 1. Verify 3 pods / zones were created in Workspace
        Assert.Equal(3, harness.Workspace.Document.Zones.Count);
        Assert.Contains(harness.Workspace.Document.Zones, z => z.Name == "Spaceship Bridge: Mission Command");
        Assert.Contains(harness.Workspace.Document.Zones, z => z.Name == "Engineering Pod: Build & Quality Loop");
        Assert.Contains(harness.Workspace.Document.Zones, z => z.Name == "Release Pod: Integration & Recovery");

        // 2. Verify all 7 role cards were materialized as annotations
        var roleCards = annotationDoc.Items.Where(i => i.Kind == AnnotationKind.Rectangle && !i.InBackground).ToList();
        Assert.Equal(7, roleCards.Count);
        Assert.Contains(roleCards, c => c.Text.Contains("Mission Orchestrator"));
        Assert.Contains(roleCards, c => c.Text.Contains("System Architect"));
        Assert.Contains(roleCards, c => c.Text.Contains("Worktree Programmer"));
        Assert.Contains(roleCards, c => c.Text.Contains("Quality Reviewer"));
        Assert.Contains(roleCards, c => c.Text.Contains("Branch Merger"));
        Assert.Contains(roleCards, c => c.Text.Contains("Conflict Resolver"));
        Assert.Contains(roleCards, c => c.Text.Contains("Branch Cleaner"));

        // 3. Verify all 9 labeled curved arrows were materialized
        var arrows = annotationDoc.Items.Where(i => i.Kind == AnnotationKind.Arrow).ToList();
        Assert.Equal(9, arrows.Count);
        Assert.All(arrows, a => Assert.Equal(ConnectorRouting.Curved, a.Routing));
        Assert.Contains(arrows, a => a.Text == "1. Dispatch Task Request" && a.Stroke == 16.0);
        Assert.Contains(arrows, a => a.Text == "3. Submit Diff for Review" && a.Stroke == 16.0);
        Assert.Contains(arrows, a => a.Text == "3b. Feedback Loop (CHANGES_REQUESTED)" && a.Stroke == 12.0);
        Assert.Contains(arrows, a => a.Text == "4. Verdict: APPROVED" && a.Stroke == 16.0);
        Assert.Contains(arrows, a => a.Text == "4b. On Merge Conflict" && a.Stroke == 12.0);
        Assert.Contains(arrows, a => a.Text == "6. Task Finished & Cleaned" && a.Stroke == 16.0);
    }
}
