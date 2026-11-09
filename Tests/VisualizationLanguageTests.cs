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

        // 4. Verify generous horizontal arrow clearance (>= 400px between rank columns)
        var handoffArrow = arrows.First(a => a.Text.Contains("ARCHITECTURE_PLAN.md Handoff"));
        double arrowSpanX = Math.Abs(handoffArrow.B.X - handoffArrow.A.X);
        Assert.True(arrowSpanX >= 400, $"Arrow span ({arrowSpanX}px) must be >= 400px to allow generous label clearance.");
    }

    [Fact]
    public void CanvasComposer_Prepare_ProvidesGenerousLabelClearance()
    {
        var context = new ComposerContext(Desktop, 1, new CanvasPoint(100, 100), [], []);

        var zones = new[]
        {
            new ComposerZone("z1", "Zone 1", "#38BDF8")
        };

        var nodes = new[]
        {
            new ComposerNode("n1", "service", "Service 1", "#10B981", Title: "Service 1", Zone: "z1"),
            new ComposerNode("n2", "service", "Service 2", "#3B82F6", Title: "Service 2", Zone: "z1"),
            new ComposerNode("n3", "service", "Service 3", "#F59E0B", Title: "Service 3")
        };

        var edges = new[]
        {
            new ComposerEdge("n1", "n3", "#60A5FA", Label: "A very long descriptive label between cards")
        };

        // In LR direction, n1 and n2 are in rank 0 (different rows), n3 is in rank 1 (next column)
        var plan = new ComposerPlan("Spacing Plan", nodes, edges, [], zones, Direction: "LR");
        var preview = CanvasComposer.Prepare(context, plan);

        var item1 = preview.Items.First(i => i.Text.Contains("Service 1"));
        var item2 = preview.Items.First(i => i.Text.Contains("Service 2"));
        var item3 = preview.Items.First(i => i.Text.Contains("Service 3"));
        var arrow = preview.Items.First(i => i.Kind == AnnotationKind.Arrow);

        // Column gap: X distance between n1 right edge and n3 left edge should be >= 400
        double colGap = item3.Bounds.X - (item1.Bounds.X + item1.Bounds.Width);
        Assert.True(colGap >= 400, $"Column gap ({colGap}) must be >= 400px for generous label clearance.");

        // Row gap: Y distance between n1 bottom and n2 top should be >= 140
        double rowGap = item2.Bounds.Y - (item1.Bounds.Y + item1.Bounds.Height);
        Assert.True(rowGap >= 140, $"Row gap ({rowGap}) must be >= 140px for comfortable vertical card clearance.");

        // Zone padding: Zone rect around n1 and n2 should have at least 60px horizontal margin and 80px top margin
        var zoneRect = preview.Items.First(i => i.InBackground);
        Assert.True(item1.Bounds.X - zoneRect.Bounds.X >= 60, "Zone left padding must be >= 60px.");
        Assert.True(item1.Bounds.Y - zoneRect.Bounds.Y >= 80, "Zone top padding must be >= 80px for header room.");
    }

    [Fact]
    public void ConnectedArrowAnchors_RecomputeDynamically_WhenEndpointsOrZonesMove()
    {
        // Simulate two nodes and a connecting arrow
        var srcBounds = new CanvasRect(100, 100, 320, 180);
        var tgtBounds = new CanvasRect(800, 100, 320, 180);

        var (initialA, initialB) = AnnotationGeometry.ComputePortAnchors(srcBounds, ConnectionPort.Auto, tgtBounds, ConnectionPort.Auto);
        // Initially src is left of tgt, so Right port to Left port
        Assert.Equal(new CanvasPoint(420, 190), initialA);
        Assert.Equal(new CanvasPoint(800, 190), initialB);

        // Move target below source: new tgtBounds at (100, 600)
        var movedTgtBounds = new CanvasRect(100, 600, 320, 180);
        var (updatedA, updatedB) = AnnotationGeometry.ComputePortAnchors(srcBounds, ConnectionPort.Auto, movedTgtBounds, ConnectionPort.Auto);

        // Now target is directly below source: ports recompute to Bottom port (260, 280) to Top port (260, 600)
        Assert.Equal(new CanvasPoint(260, 280), updatedA);
        Assert.Equal(new CanvasPoint(260, 600), updatedB);

        // Verify that moving target by (dx, dy) when enclosed in a zone shifts anchors cleanly
        double dx = 200, dy = 100;
        var translatedTgt = new CanvasRect(movedTgtBounds.X + dx, movedTgtBounds.Y + dy, movedTgtBounds.Width, movedTgtBounds.Height);
        var (zoneA, zoneB) = AnnotationGeometry.ComputePortAnchors(srcBounds, ConnectionPort.Auto, translatedTgt, ConnectionPort.Auto);
        Assert.Equal(new CanvasPoint(260, 280), zoneA);
        Assert.Equal(new CanvasPoint(translatedTgt.X + translatedTgt.Width / 2, translatedTgt.Y), zoneB);
    }

    private static string LoadTeamCvlTemplate()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, ".agents", "teams", "team_architecture_cvl.json"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".agents", "teams", "team_architecture_cvl.json"),
            Path.Combine(Environment.CurrentDirectory, ".agents", "teams", "team_architecture_cvl.json")
        };
        foreach (var c in candidates)
        {
            var full = Path.GetFullPath(c);
            if (File.Exists(full)) return File.ReadAllText(full);
        }
        throw new FileNotFoundException("team_architecture_cvl.json not found in test search paths.");
    }

    [Fact]
    public void AiOperationsSector_AllocatesDedicatedCoordinates()
    {
        using var harness = new WorkspaceHarness();
        var annotations = new AnnotationDocument();
        using var telemetry = new WindowsNativeTelemetryProvider(mutexProbe: () => false);
        var sectorManager = new AiOperationsSectorManager(harness.Workspace, annotations, null, telemetry);

        Assert.Equal(new CanvasPoint(8000, 1000), sectorManager.SectorOrigin);
        Assert.True(sectorManager.SectorOrigin.X >= 8000);
        Assert.True(sectorManager.SectorOrigin.Y >= 1000);
    }

    [Fact]
    public void AiOperationsSector_SpawnTeamPod_MaterializesNodesZonesArrows()
    {
        using var harness = new WorkspaceHarness();
        var annotations = new AnnotationDocument();
        using var telemetry = new WindowsNativeTelemetryProvider(mutexProbe: () => false);
        var sectorManager = new AiOperationsSectorManager(harness.Workspace, annotations, null, telemetry);

        string cvlJson = LoadTeamCvlTemplate();
        var team = new TeamInstanceTelemetry(
            TaskId: "pod-alpha",
            Name: "Alpha Feature Team",
            ConversationId: "conv-101",
            Worktree: @"C:\worktrees\CanvasWindowComposer-alpha",
            ActiveRole: "programmer",
            Status: "Active",
            Roles: new Dictionary<string, AgentStatusReport>(),
            UpdatedAt: DateTimeOffset.UtcNow);

        var spawned = sectorManager.SpawnPod(Desktop, team, cvlJson);
        Assert.NotNull(spawned);
        Assert.Equal("pod-alpha", spawned.TaskId);
        Assert.Equal(new CanvasPoint(8000, 1000), spawned.Origin);
        Assert.True(sectorManager.HasPod("pod-alpha"));

        // Verify zones materialized with task specialization
        Assert.True(spawned.ZoneIds.Count >= 3);
        var docZones = harness.Workspace.Document.Zones;
        Assert.Contains(docZones, z => z.Name.Contains("[pod-alpha]") && z.Name.Contains("Mission Command"));
        Assert.Contains(docZones, z => z.Name.Contains("[pod-alpha]") && z.Name.Contains("Engineering Pod"));
        Assert.Contains(docZones, z => z.Name.Contains("[pod-alpha]") && z.Name.Contains("Release Pod"));

        // Verify annotations materialized (nodes + arrows)
        var items = annotations.Items.Where(a => a.DesktopId == Desktop).ToList();
        Assert.True(items.Count > 0);

        var devCard = items.FirstOrDefault(i => i.Text.Contains("[pod-alpha]") && i.Text.Contains("Worktree Programmer"));
        Assert.NotNull(devCard);
        Assert.Contains("CanvasWindowComposer-alpha", devCard.Text);

        var arrows = items.Where(i => i.Kind == AnnotationKind.Arrow).ToList();
        Assert.True(arrows.Count >= 5);
        Assert.All(arrows, a =>
        {
            Assert.Equal(ConnectorRouting.Curved, a.Routing);
            Assert.NotNull(a.SourceId);
            Assert.NotNull(a.TargetId);
        });
    }

    [Fact]
    public void AiOperationsSector_BindsSpawnedCardsToTeamTelemetry()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "vis-telemetry-1-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            using var harness = new WorkspaceHarness();
            var annotations = new AnnotationDocument();
            using var telemetry = new WindowsNativeTelemetryProvider(tempFile, mutexProbe: () => false);
            var sectorManager = new AiOperationsSectorManager(harness.Workspace, annotations, null, telemetry);

            string cvlJson = LoadTeamCvlTemplate();

            var teamAlpha = new TeamInstanceTelemetry("alpha", "Alpha Team", "", "", "programmer", "Active", new Dictionary<string, AgentStatusReport>(), DateTimeOffset.UtcNow);
            var teamBeta = new TeamInstanceTelemetry("beta", "Beta Team", "", "", "programmer", "Active", new Dictionary<string, AgentStatusReport>(), DateTimeOffset.UtcNow);

            sectorManager.SpawnPod(Desktop, teamAlpha, cvlJson);
            sectorManager.SpawnPod(Desktop, teamBeta, cvlJson);

            telemetry.SetTeamState("alpha", "programmer", AgentExecutionState.Working, "Alpha Dev is working");
            telemetry.SetTeamState("beta", "programmer", AgentExecutionState.Done, "Beta Dev is done");

            var alphaDev = annotations.Items.First(item => item.Text.Contains("[alpha]") && item.Text.Contains("Worktree Programmer"));
            var betaDev = annotations.Items.First(item => item.Text.Contains("[beta]") && item.Text.Contains("Worktree Programmer"));

            Assert.Equal(AgentExecutionState.Working, telemetry.GetStateForAnnotation(alphaDev));
            Assert.Equal(AgentExecutionState.Done, telemetry.GetStateForAnnotation(betaDev));
        }
        finally
        {
            if (File.Exists(tempFile)) try { File.Delete(tempFile); } catch { }
        }
    }

    [Fact]
    public void AiOperationsSector_CleansUpPod_WhenBranchCleanerDone()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "vis-telemetry-2-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            using var harness = new WorkspaceHarness();
            var annotations = new AnnotationDocument();
            using var telemetry = new WindowsNativeTelemetryProvider(tempFile, mutexProbe: () => false);
            var sectorManager = new AiOperationsSectorManager(harness.Workspace, annotations, null, telemetry);

            string cvlJson = LoadTeamCvlTemplate();
            var team = new TeamInstanceTelemetry("pod-cleanup", "Cleanup Team", "", "", "branch-cleaner", "Active", new Dictionary<string, AgentStatusReport>(), DateTimeOffset.UtcNow);

            var spawned = sectorManager.SpawnPod(Desktop, team, cvlJson);
            Assert.NotNull(spawned);
            Assert.True(sectorManager.HasPod("pod-cleanup"));
            Assert.Contains(annotations.Items, a => a.Text.Contains("[pod-cleanup]"));
            Assert.Contains(harness.Workspace.Document.Zones, z => z.Name.Contains("[pod-cleanup]"));

            bool cleaned = sectorManager.CleanPod(Desktop, "pod-cleanup");
            Assert.True(cleaned);
            Assert.False(sectorManager.HasPod("pod-cleanup"));
            Assert.DoesNotContain(annotations.Items, a => a.Text.Contains("[pod-cleanup]"));
            Assert.DoesNotContain(harness.Workspace.Document.Zones, z => z.Name.Contains("[pod-cleanup]"));
        }
        finally
        {
            if (File.Exists(tempFile)) try { File.Delete(tempFile); } catch { }
        }
    }

    [Fact]
    public void AiOperationsSector_SpawnPod_SupportsCustomAnchorAndCalculatesBounds()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "vis-telemetry-3-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            using var harness = new WorkspaceHarness();
            var annotations = new AnnotationDocument();
            using var telemetry = new WindowsNativeTelemetryProvider(tempFile, mutexProbe: () => false);
            var sectorManager = new AiOperationsSectorManager(harness.Workspace, annotations, null, telemetry);

            string cvlJson = LoadTeamCvlTemplate();
            var team = new TeamInstanceTelemetry("pod-custom", "Custom Team", "", "", "programmer", "Active", new Dictionary<string, AgentStatusReport>(), DateTimeOffset.UtcNow);

            var anchor = new CanvasPoint(2500, 3500);
            var spawned = sectorManager.SpawnPod(Desktop, team, cvlJson, anchor);

            Assert.NotNull(spawned);
            Assert.Equal(anchor, spawned.Origin);

            var bounds = spawned.GetBounds(harness.Workspace.Document, annotations, Desktop);
            Assert.True(bounds.W > 0);
            Assert.True(bounds.H > 0);
            Assert.True(bounds.X >= anchor.X - 500);
            Assert.True(bounds.Y >= anchor.Y - 500);
        }
        finally
        {
            if (File.Exists(tempFile)) try { File.Delete(tempFile); } catch { }
        }
    }
}