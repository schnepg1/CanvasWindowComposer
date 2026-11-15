using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Media;
using CanvasDesktop;
using CanvasDesktop.CanvasView;
using Xunit;

namespace CanvasDesktop.Tests;

public class SubagentPlacementTests
{
    [Fact]
    public void SubagentPlacement_AmpleSpace_PlacesFullCardRightOfParent()
    {
        var parent = new CanvasRect(100, 100, 280, 140);
        var placement = SubagentPlacementPlanner.PlanPlacement(parent, []);

        Assert.Equal(400, placement.X); // parent.Right (380) + 20
        Assert.Equal(100, placement.Y); // parent.Y
        Assert.Equal(280, placement.Width);
        Assert.Equal(140, placement.Height);
    }

    [Fact]
    public void SubagentPlacement_OccupiedRightSlot_MovesToBelowSlot()
    {
        var parent = new CanvasRect(100, 100, 280, 140);
        // Obstacle blocking right slot (400, 100, 280, 140)
        var obstacleRight = new CanvasRect(400, 100, 280, 140);
        var placement = SubagentPlacementPlanner.PlanPlacement(parent, [obstacleRight]);

        Assert.Equal(100, placement.X); // parent.X
        Assert.Equal(256, placement.Y); // parent.Bottom (240) + 16
        Assert.Equal(280, placement.Width);
        Assert.Equal(140, placement.Height);
    }

    [Fact]
    public void SubagentPlacement_OccupiedRightAndBelow_MovesToLeftSlot()
    {
        var parent = new CanvasRect(400, 300, 280, 140);
        var obstacleRight = new CanvasRect(700, 300, 280, 140);
        var obstacleBelow = new CanvasRect(400, 456, 280, 140);

        var placement = SubagentPlacementPlanner.PlanPlacement(parent, [obstacleRight, obstacleBelow]);

        Assert.Equal(100, placement.X); // parent.X - 280 - 20 = 100
        Assert.Equal(300, placement.Y);
        Assert.Equal(280, placement.Width);
        Assert.Equal(140, placement.Height);
    }

    [Fact]
    public void SubagentPlacement_HeavilyConstrainedSpace_FallsBackToCompactSatellitePill()
    {
        var parent = new CanvasRect(100, 100, 280, 140);
        // Container zone fits compact pill below parent, but cannot fit any full card (280x140)
        var containerZone = new CanvasRect(80, 80, 300, 260); // X: 80..380, Y: 80..340

        var placement = SubagentPlacementPlanner.PlanPlacement(parent, [], containerZone);

        // Subagent planner should adaptively select a 140x40 satellite pill
        Assert.Equal(140, placement.Width);
        Assert.Equal(40, placement.Height);
        Assert.Equal(100, placement.X);
        Assert.Equal(256, placement.Y); // 256 + 40 = 296 <= 340
    }

    [Fact]
    public void AgentCard_ClickingCompactPill_ExpandsToFullCardAndToggles()
    {
        WorkspaceViewTests.OnSta(() =>
        {
            var desktop = Guid.NewGuid();
            var folder = Path.Combine(Path.GetTempPath(), "subagent_click_test_" + Guid.NewGuid().ToString("N"));
            var pill = new CanvasAnnotation(Guid.NewGuid(), desktop, AnnotationKind.Text,
                new(100, 100), new(240, 140), Text: "Subagent Alpha", AgentTaskId: "task-01", AgentRole: "codex");
            Assert.Equal(140, pill.Bounds.Width);
            Assert.Equal(40, pill.Bounds.Height);

            Directory.CreateDirectory(folder);
            try
            {
                var canvas = new Canvas();
                var api = new FakeWindowApi();
                var input = new FakeInputRouter();
                var config = new FakeAppConfig { EnableStartupIntro = false };
                var desktops = new FakeVirtualDesktops { CurrentDesktopId = desktop };
                var wm = new WindowManager(canvas, api, config, input, new FakeClock());
                using var overview = new OverviewManager(canvas, wm, api, input, config);
                overview.HerdrSessions = new MockAgentSessionManager();
                overview.AttachAnnotations(desktops, Path.Combine(folder, "annotations.json"));
                overview.TransitionTo(OverviewMode.Zooming);
                overview.Annotations!.Put(pill);

                // Simulate clicking inside the compact pill at (120, 120)
                var pointer = new CanvasPointer(0, new(120, 120), CanvasPointerButton.Left,
                    CanvasModifiers.None, new CanvasViewTransform(0, 0, 1.0, new CanvasRect(0, 0, 1920, 1080)));
                bool handled = overview.TryHandleAgentCardClick(pointer);
                Assert.True(handled);

                var expanded = overview.Annotations!.Items.Single(i => i.Id == pill.Id);
                Assert.Equal(280, expanded.Bounds.Width);
                Assert.Equal(140, expanded.Bounds.Height);

                // Toggle back to compact satellite pill
                overview.ToggleAgentCardSize(pill.Id);
                var collapsed = overview.Annotations!.Items.Single(i => i.Id == pill.Id);
                Assert.Equal(140, collapsed.Bounds.Width);
                Assert.Equal(40, collapsed.Bounds.Height);
            }
            finally
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        });
    }

    [Fact]
    public void AgentCard_ClickingCompactPill_RelocatesIfInPlaceCollides()
    {
        WorkspaceViewTests.OnSta(() =>
        {
            var desktop = Guid.NewGuid();
            var folder = Path.Combine(Path.GetTempPath(), "subagent_relocate_test_" + Guid.NewGuid().ToString("N"));
            var pill = new CanvasAnnotation(Guid.NewGuid(), desktop, AnnotationKind.Text,
                new(100, 100), new(240, 140), Text: "Subagent Beta", AgentTaskId: "task-01", AgentRole: "codex");
            // Obstacle placed directly where in-place expansion (100..380, 100..240) would overlap
            var obstacle = new CanvasAnnotation(Guid.NewGuid(), desktop, AnnotationKind.Rectangle,
                new(200, 100), new(350, 200));

            Directory.CreateDirectory(folder);
            try
            {
                var canvas = new Canvas();
                var api = new FakeWindowApi();
                var input = new FakeInputRouter();
                var config = new FakeAppConfig { EnableStartupIntro = false };
                var desktops = new FakeVirtualDesktops { CurrentDesktopId = desktop };
                var wm = new WindowManager(canvas, api, config, input, new FakeClock());
                using var overview = new OverviewManager(canvas, wm, api, input, config);
                overview.HerdrSessions = new MockAgentSessionManager();
                overview.AttachAnnotations(desktops, Path.Combine(folder, "annotations.json"));
                overview.TransitionTo(OverviewMode.Zooming);
                overview.Annotations!.Put(pill);
                overview.Annotations!.Put(obstacle);

                var pointer = new CanvasPointer(0, new(120, 120), CanvasPointerButton.Left,
                    CanvasModifiers.None, new CanvasViewTransform(0, 0, 1.0, new CanvasRect(0, 0, 1920, 1080)));
                bool handled = overview.TryHandleAgentCardClick(pointer);
                Assert.True(handled);

                var expanded = overview.Annotations!.Items.Single(i => i.Id == pill.Id);
                Assert.Equal(280, expanded.Bounds.Width);
                Assert.Equal(140, expanded.Bounds.Height);
                // Must not intersect with the obstacle
                Assert.False(expanded.Bounds.IntersectsWith(obstacle.Bounds));
            }
            finally
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        });
    }

    [Fact]
    public void AgentCardContextMenu_ShowsExpandOrCollapse_BasedOnCardHeight()
    {
        var desktop = Guid.NewGuid();
        // 1. Compact card (height <= 60)
        var compactCard = new CanvasAnnotation(Guid.NewGuid(), desktop, AnnotationKind.Text,
            new(100, 100), new(240, 140), Text: "Compact Subagent", AgentTaskId: "task-01", AgentRole: "codex");
        Assert.True(compactCard.Bounds.Height <= 60);

        bool toggleCalled = false;
        using var compactMenu = AgentCardContextMenuBuilder.Build(
            compactCard,
            isLive: false,
            _ => { }, () => { }, () => { }, () => { }, () => { }, () => { }, () => { },
            onToggleExpandCollapse: () => toggleCalled = true);

        var expandItem = Assert.Single(compactMenu.Items.OfType<ToolStripMenuItem>(), i => i.Text == "Expand to Full Card");
        expandItem.PerformClick();
        Assert.True(toggleCalled);

        // 2. Full card (height = 140 > 60)
        var fullCard = new CanvasAnnotation(Guid.NewGuid(), desktop, AnnotationKind.Text,
            new(100, 100), new(380, 240), Text: "Full Subagent", AgentTaskId: "task-01", AgentRole: "codex");
        Assert.True(fullCard.Bounds.Height > 60);

        bool collapseCalled = false;
        using var fullMenu = AgentCardContextMenuBuilder.Build(
            fullCard,
            isLive: false,
            _ => { }, () => { }, () => { }, () => { }, () => { }, () => { }, () => { },
            onToggleExpandCollapse: () => collapseCalled = true);

        var collapseItem = Assert.Single(fullMenu.Items.OfType<ToolStripMenuItem>(), i => i.Text == "Collapse to Satellite Pill");
        collapseItem.PerformClick();
        Assert.True(collapseCalled);
    }

    [Fact]
    public void OverviewManager_SpawnSubagent_InheritsParentMissionTaskIdAndWorktree()
    {
        WorkspaceViewTests.OnSta(() =>
        {
            var desktop = Guid.NewGuid();
            var folder = Path.Combine(Path.GetTempPath(), "spawn_subagent_test_" + Guid.NewGuid().ToString("N"));
            var parentCard = new CanvasAnnotation(Guid.NewGuid(), desktop, AnnotationKind.Text,
                new(100, 100), new(380, 240), Text: "Lead Architect", AgentTaskId: "mission-omega", AgentRole: "claude")
            {
                AgentWorkingFolder = @"C:\repos\my-project"
            };

            Directory.CreateDirectory(folder);
            try
            {
                var canvas = new Canvas();
                var api = new FakeWindowApi();
                var input = new FakeInputRouter();
                var config = new FakeAppConfig { EnableStartupIntro = false };
                var desktops = new FakeVirtualDesktops { CurrentDesktopId = desktop };
                var wm = new WindowManager(canvas, api, config, input, new FakeClock());
                using var overview = new OverviewManager(canvas, wm, api, input, config);
                overview.HerdrSessions = new MockAgentSessionManager();
                overview.AttachAnnotations(desktops, Path.Combine(folder, "annotations.json"));
                overview.TransitionTo(OverviewMode.Zooming);
                overview.Annotations!.Put(parentCard);

                // 1. Spawn subagent with inherited properties
                var subagent = overview.SpawnSubagent(parentCard.Id, "Subagent Coder");

                Assert.NotNull(subagent);
                Assert.Equal("Subagent Coder", subagent.Text);
                Assert.Equal("mission-omega", subagent.AgentTaskId);
                Assert.Equal("claude", subagent.AgentRole);
                Assert.Equal(@"C:\repos\my-project", subagent.AgentWorkingFolder);
                Assert.Equal(desktop, subagent.DesktopId);
                Assert.Equal(AnnotationKind.Text, subagent.Kind);

                // Right slot: 380 + 20 = 400
                Assert.Equal(400, subagent.Bounds.X);
                Assert.Equal(100, subagent.Bounds.Y);
                Assert.Equal(280, subagent.Bounds.Width);
                Assert.Equal(140, subagent.Bounds.Height);

                var inDoc = overview.Annotations!.Items.Single(a => a.Id == subagent.Id);
                Assert.Equal("Subagent Coder", inDoc.Text);

                // 2. Spawn another subagent with overridden profile and worktree
                var subagent2 = overview.SpawnSubagent(parentCard.Id, "Subagent Reviewer", profileId: "gemini", worktreeFolder: @"C:\repos\worktree-rev");
                Assert.Equal("Subagent Reviewer", subagent2.Text);
                Assert.Equal("mission-omega", subagent2.AgentTaskId);
                Assert.Equal("gemini", subagent2.AgentRole);
                Assert.Equal(@"C:\repos\worktree-rev", subagent2.AgentWorkingFolder);
                // Right slot occupied, so subagent2 moves to below slot
                Assert.Equal(100, subagent2.Bounds.X);
                Assert.Equal(256, subagent2.Bounds.Y);
            }
            finally
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        });
    }

    [Fact]
    public void MissionCardLayer_RendersCompactSatellitePill()
    {
        WorkspaceViewTests.OnSta(() =>
        {
            var desktop = Guid.NewGuid();
            var pill = new CanvasAnnotation(Guid.NewGuid(), desktop, AnnotationKind.Text,
                new(100, 100), new(240, 140), Text: "Scout Pill", AgentTaskId: "task-01", AgentRole: "gemini");
            var card = new MissionAgentCard(pill);
            var layer = new MissionCardLayer(() => [card], _ => new(card.Role, AgentExecutionState.Working), _ => null, _ => @"C:\test", _ => true);

            var snapshot = OverviewAnnotationSnapshot.Create([], 0, 0, 1, new(0, 0, 1920, 1080), null, false);
            var transform = new CanvasViewTransform(0, 0, 1.0, new CanvasRect(0, 0, 1920, 1080));
            var context = new OverviewAnnotationContext(snapshot, transform, new DpiScale(1, 1), new Size(1920, 1080));
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                layer.Draw(dc, context);
            }
            Assert.NotNull(visual.Drawing);
        });
    }
}
