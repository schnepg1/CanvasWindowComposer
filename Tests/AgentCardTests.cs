using CanvasDesktop;
using CanvasDesktop.CanvasView;
using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Media;
using Xunit;

namespace CanvasDesktop.Tests;

public class AgentCardTests
{
    private static CanvasPointer Pointer(double x, double y) => new(0, new(x, y), CanvasPointerButton.Left,
        CanvasModifiers.None, new CanvasViewTransform(0, 0, 1, new(0, 0, 1920, 1080)));

    [Fact]
    public void ClickPlacesIndependentAgentWithUndoRedoAndPersistence()
    {
        var desktop = Guid.NewGuid();
        var document = new AnnotationDocument();
        var tool = new AnnotationCanvasTool(document, () => desktop);
        tool.EditTextRequested += _ => throw new Exception("Agents must not open the note editor.");
        tool.SetMode(AnnotationMode.Agent);
        Assert.True(tool.PointerDown(Pointer(120, 180)));
        Assert.Empty(document.Items);
        tool.PointerUp(Pointer(120, 180));
        var card = Assert.Single(document.Items);
        Assert.True(MissionAgentCard.IsCard(card));
        Assert.Equal(new CanvasRect(120, 180, 280, 140), card.Bounds);
        Assert.Equal("codex", card.AgentRole);
        Assert.Equal(card.Id, tool.SelectedId);
        Assert.Equal(AnnotationMode.Select, tool.Mode);
        Assert.True(document.Undo(desktop));
        Assert.Empty(document.Items);
        Assert.True(document.Undo(desktop, true));
        Assert.Equal(card, Assert.Single(document.Items));
        tool.SetMode(AnnotationMode.Agent);
        tool.PointerDown(Pointer(500, 180)); tool.PointerUp(Pointer(500, 180));
        Assert.Equal(2, document.Items.Select(item => item.AgentTaskId).Distinct().Count());
        var saved = card with { AgentWorkingFolder = Path.GetTempPath() };
        Assert.Equal(saved, System.Text.Json.JsonSerializer.Deserialize<CanvasAnnotation>(System.Text.Json.JsonSerializer.Serialize(saved)));
    }

    [Fact]
    public void CancelAndDesktopSwitchDoNotPlaceAgents()
    {
        var desktop = Guid.NewGuid();
        var document = new AnnotationDocument();
        var tool = new AnnotationCanvasTool(document, () => desktop);
        tool.SetMode(AnnotationMode.Agent);
        tool.PointerDown(Pointer(1, 2)); tool.Cancel(); tool.PointerUp(Pointer(1, 2));
        Assert.Empty(document.Items);
        tool.PointerDown(Pointer(1, 2)); desktop = Guid.NewGuid(); tool.PointerUp(Pointer(1, 2));
        Assert.Empty(document.Items);
    }

    [Fact]
    public void ContextUsesSmallestProjectOnSameDesktopAndExcludesMissionsAndTemporaryAreas()
    {
        var desktop = Guid.NewGuid();
        var card = new CanvasAnnotation(Guid.NewGuid(), desktop, AnnotationKind.Text, new(50, 50), new(290, 162));
        var outer = new WorkspaceZone { DesktopId = desktop, Bounds = new() { W = 1000, H = 1000 }, ProjectFolderPath = "outer" };
        var inner = new WorkspaceZone { DesktopId = desktop, Bounds = new() { W = 400, H = 400 }, ProjectFolderPath = "inner" };
        var mission = new WorkspaceZone { DesktopId = desktop, AgentTaskId = "mission", Bounds = new() { W = 100, H = 100 } };
        var temporary = new WorkspaceZone { DesktopId = desktop, IsTemporaryArea = true, Bounds = mission.Bounds };
        var otherDesktop = new WorkspaceZone { DesktopId = Guid.NewGuid(), Bounds = mission.Bounds };
        Assert.Same(inner, AgentCardContext.ProjectAt([outer, mission, temporary, otherDesktop, inner], card));
        Assert.Null(AgentCardContext.ProjectAt([outer, inner], card.Translate(2000, 0)));
    }

    [Fact]
    public void MissionCardLayerRendersAtBothNormalAndLowZoomLevels()
        => WorkspaceViewTests.OnSta(() =>
        {
            var desktop = Guid.NewGuid();
            var annotation = new CanvasAnnotation(Guid.NewGuid(), desktop, AnnotationKind.Text,
                new(100, 100), new(380, 240), Text: "Scout Agent", AgentTaskId: "task-99", AgentRole: "codex");
            var card = new MissionAgentCard(annotation);
            var layer = new MissionCardLayer(() => [card], _ => null, _ => null);

            var snapshot = OverviewAnnotationSnapshot.Create([], 0, 0, 1, new(0, 0, 1920, 1080), null, false);

            // Zoom 1.0 (280x140 DIPs): HasCardControls is true
            var highTransform = new CanvasViewTransform(0, 0, 1.0, new CanvasRect(0, 0, 1920, 1080));
            var highContext = new OverviewAnnotationContext(snapshot, highTransform, new DpiScale(1, 1), new Size(1920, 1080));
            var highVisual = new DrawingVisual();
            using (var dc = highVisual.RenderOpen())
            {
                layer.Draw(dc, highContext);
            }
            Assert.NotNull(highVisual.Drawing);
            Assert.True(MissionCardLayer.HasCardControls(280, 140));

            // Zoom 0.25 (70x35 DIPs): HasCardControls is false, renders mini label
            var lowTransform = new CanvasViewTransform(0, 0, 0.25, new CanvasRect(0, 0, 1920, 1080));
            var lowContext = new OverviewAnnotationContext(snapshot, lowTransform, new DpiScale(1, 1), new Size(1920, 1080));
            var lowVisual = new DrawingVisual();
            using (var dc = lowVisual.RenderOpen())
            {
                layer.Draw(dc, lowContext);
            }
            Assert.NotNull(lowVisual.Drawing);
            Assert.False(MissionCardLayer.HasCardControls(60, 28));
        });

    [Fact]
    public void AgentCardContextMenu_HasAllRequiredItemsAndFiveProfiles()
    {
        var desktop = Guid.NewGuid();
        var cardAnnotation = new CanvasAnnotation(Guid.NewGuid(), desktop, AnnotationKind.Text,
            new(100, 100), new(380, 240), Text: "Codex", AgentTaskId: "task-01", AgentRole: "codex");

        using var menu = AgentCardContextMenuBuilder.Build(
            cardAnnotation,
            isLive: false,
            onSelectProfile: _ => { },
            onToggleAgent: () => { },
            onSetWorkingDirectory: () => { },
            onEditPrompt: () => { },
            onToggleLiveConsole: () => { },
            onPutInBackpack: () => { },
            onDeleteAgentCard: () => { });

        var items = menu.Items.OfType<ToolStripMenuItem>().ToArray();

        // 1. Agent Profile menu with 5 submenus
        var profileMenu = Assert.Single(items, i => i.Text == "Agent Profile");
        var subItems = profileMenu.DropDownItems.OfType<ToolStripMenuItem>().ToArray();
        Assert.Equal(5, subItems.Length);

        Assert.Equal("OpenAI Codex", subItems[0].Text);
        Assert.True(subItems[0].Checked);

        Assert.Equal("Claude Code", subItems[1].Text);
        Assert.False(subItems[1].Checked);

        Assert.Equal("Google Gemini CLI", subItems[2].Text);
        Assert.False(subItems[2].Checked);

        Assert.Equal("Aider", subItems[3].Text);
        Assert.False(subItems[3].Checked);

        Assert.Equal("OpenCode", subItems[4].Text);
        Assert.False(subItems[4].Checked);

        // 2. Start Agent (when not live)
        Assert.Contains(items, i => i.Text == "Start Agent");

        // 3. Set Working Directory…
        Assert.Contains(items, i => i.Text == "Set Working Directory…");

        // 4. Edit Prompt…
        Assert.Contains(items, i => i.Text == "Edit Prompt…");

        // 5. Toggle Live Console
        Assert.Contains(items, i => i.Text == "Toggle Live Console");

        // 6. Put in backpack
        Assert.Contains(items, i => i.Text == "Put in backpack");

        // 7. Delete agent card
        Assert.Contains(items, i => i.Text == "Delete agent card");

        // Obsolete "Open agent" must NOT exist
        Assert.DoesNotContain(items, i => (i.Text ?? "").Contains("Open agent", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AgentCardContextMenu_TogglesStartAndStopBasedOnLiveState()
    {
        var cardAnnotation = new CanvasAnnotation(Guid.NewGuid(), Guid.NewGuid(), AnnotationKind.Text,
            new(100, 100), new(380, 240), Text: "Codex", AgentTaskId: "task-01", AgentRole: "codex");

        using var notLiveMenu = AgentCardContextMenuBuilder.Build(
            cardAnnotation, isLive: false, _ => { }, () => { }, () => { }, () => { }, () => { }, () => { }, () => { });
        Assert.Contains(notLiveMenu.Items.OfType<ToolStripMenuItem>(), i => i.Text == "Start Agent");
        Assert.DoesNotContain(notLiveMenu.Items.OfType<ToolStripMenuItem>(), i => i.Text == "Stop Agent");

        using var liveMenu = AgentCardContextMenuBuilder.Build(
            cardAnnotation, isLive: true, _ => { }, () => { }, () => { }, () => { }, () => { }, () => { }, () => { });
        Assert.Contains(liveMenu.Items.OfType<ToolStripMenuItem>(), i => i.Text == "Stop Agent");
        Assert.DoesNotContain(liveMenu.Items.OfType<ToolStripMenuItem>(), i => i.Text == "Start Agent");
    }

    [Fact]
    public void AgentCardContextMenu_ChangingProfileUpdatesAgentRoleOnDocumentItem()
    {
        var desktop = Guid.NewGuid();
        var document = new AnnotationDocument();
        var cardAnnotation = new CanvasAnnotation(Guid.NewGuid(), desktop, AnnotationKind.Text,
            new(100, 100), new(380, 240), Text: "Codex", AgentTaskId: "task-01", AgentRole: "codex");
        document.Put(cardAnnotation);

        AgentProfile? chosenProfile = null;
        using var menu = AgentCardContextMenuBuilder.Build(
            cardAnnotation,
            isLive: false,
            onSelectProfile: p =>
            {
                chosenProfile = p;
                var current = document.Items.FirstOrDefault(i => i.Id == cardAnnotation.Id);
                if (current != null)
                {
                    document.Put(current with { AgentRole = p.Id });
                }
            },
            () => { }, () => { }, () => { }, () => { }, () => { }, () => { });

        var profileMenu = menu.Items.OfType<ToolStripMenuItem>().Single(i => i.Text == "Agent Profile");
        var claudeItem = profileMenu.DropDownItems.OfType<ToolStripMenuItem>().Single(i => i.Text == "Claude Code");
        claudeItem.PerformClick();

        Assert.NotNull(chosenProfile);
        Assert.Equal("claude", chosenProfile.Id);
        Assert.Equal("claude", document.Items.Single().AgentRole);

        // Select Google Gemini CLI
        var geminiItem = profileMenu.DropDownItems.OfType<ToolStripMenuItem>().Single(i => i.Text == "Google Gemini CLI");
        geminiItem.PerformClick();
        Assert.Equal("gemini", chosenProfile.Id);
        Assert.Equal("gemini", document.Items.Single().AgentRole);

        // Select Aider
        var aiderItem = profileMenu.DropDownItems.OfType<ToolStripMenuItem>().Single(i => i.Text == "Aider");
        aiderItem.PerformClick();
        Assert.Equal("aider", chosenProfile.Id);
        Assert.Equal("aider", document.Items.Single().AgentRole);

        // Select OpenCode
        var openCodeItem = profileMenu.DropDownItems.OfType<ToolStripMenuItem>().Single(i => i.Text == "OpenCode");
        openCodeItem.PerformClick();
        Assert.Equal("opencode", chosenProfile.Id);
        Assert.Equal("opencode", document.Items.Single().AgentRole);
    }

    [Fact]
    public void GitWorktreeHelper_FormatEnvironmentLabel_FormatsAllDirectoryKinds()
    {
        var tempBase = Path.Combine(Path.GetTempPath(), "git_env_format_" + Guid.NewGuid().ToString("N"));
        try
        {
            // 1. Linked worktree
            var mainRepo = Path.Combine(tempBase, "my_repo");
            var linkedWt = Path.Combine(tempBase, "my_worktree");
            var gitdirWorktree = Path.Combine(mainRepo, ".git", "worktrees", "my_worktree");
            Directory.CreateDirectory(gitdirWorktree);
            Directory.CreateDirectory(linkedWt);
            File.WriteAllText(Path.Combine(gitdirWorktree, "HEAD"), "ref: refs/heads/feature/in-canvas-card\n");
            File.WriteAllText(Path.Combine(gitdirWorktree, "commondir"), "../..\n");
            File.WriteAllText(Path.Combine(linkedWt, ".git"), $"gitdir: {gitdirWorktree}\n");

            var wtLabel = GitWorktreeHelper.FormatEnvironmentLabel(linkedWt);
            Assert.Equal("🌿 my_repo ↳ feature/in-canvas-card", wtLabel);

            // 2. Standard Git repo
            var standardRepo = Path.Combine(tempBase, "standard_repo");
            var stdDotGit = Path.Combine(standardRepo, ".git");
            Directory.CreateDirectory(stdDotGit);
            File.WriteAllText(Path.Combine(stdDotGit, "HEAD"), "ref: refs/heads/main\n");

            var stdLabel = GitWorktreeHelper.FormatEnvironmentLabel(standardRepo);
            Assert.Equal("📁 standard_repo [main]", stdLabel);

            // 3. Normal folder
            var normalDir = Path.Combine(tempBase, "normal_folder");
            Directory.CreateDirectory(normalDir);
            var normalLabel = GitWorktreeHelper.FormatEnvironmentLabel(normalDir);
            Assert.Equal("📁 normal_folder", normalLabel);

            // 4. Null / whitespace
            Assert.Equal("📁 (no directory)", GitWorktreeHelper.FormatEnvironmentLabel(null));
            Assert.Equal("📁 (no directory)", GitWorktreeHelper.FormatEnvironmentLabel("   "));
        }
        finally
        {
            if (Directory.Exists(tempBase)) Directory.Delete(tempBase, true);
        }
    }

    [Fact]
    public void AgentCardLayout_ReservesTopRightCornerForCloseAndKeepsControlsApart()
    {
        var card = new Rect(100, 100, 280, 140);
        var close = MissionCardLayout.CloseRect(card);
        var console = MissionCardLayout.ConsoleToggleRect(card);
        var play = MissionCardLayout.PlayStopRect(card);

        Assert.Equal(card.Right - 8, close.Right);
        Assert.Equal(card.Y + 11, close.Y);
        Assert.False(close.IntersectsWith(console));
        Assert.False(console.IntersectsWith(play));
        Assert.All([close, console, play], r => Assert.True(r.Left >= card.Left && r.Right <= card.Right));
    }

    [Fact]
    public void MissionCardLayer_RendersFluentStyleWithoutOpenAgentText()
        => WorkspaceViewTests.OnSta(() =>
        {
            var desktop = Guid.NewGuid();
            var annotation = new CanvasAnnotation(Guid.NewGuid(), desktop, AnnotationKind.Text,
                new(100, 100), new(380, 240), Text: "Codex", AgentTaskId: "task-01", AgentRole: "codex");
            var card = new MissionAgentCard(annotation);
            var layer = new MissionCardLayer(() => [card], _ => null, _ => null, _ => @"C:\test", _ => false);

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

    [Fact]
    public void AgentCardInteraction_DoesNotLaunchDetachedPopupWindow_AndHandlesInPlaceControls()
    {
        WorkspaceViewTests.OnSta(() =>
        {
            var desktop = Guid.NewGuid();
            var folder = Path.Combine(Path.GetTempPath(), "agent_card_test_" + Guid.NewGuid().ToString("N"));
            var card = new CanvasAnnotation(Guid.NewGuid(), desktop, AnnotationKind.Text,
                new(100, 100), new(380, 240), Text: "Codex", AgentTaskId: "standalone-task", AgentRole: "codex");

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
                overview.Annotations!.Put(card);

                var missionCard = new MissionAgentCard(card);

                // 1. Verify cycling profile via CycleAgentProfile
                overview.CycleAgentProfile(missionCard);
                var updatedAfterCycle = overview.Annotations!.Items.Single(i => i.Id == card.Id);
                Assert.Equal("claude", updatedAfterCycle.AgentRole);
                Assert.Equal("Claude Code", updatedAfterCycle.Text);
                Assert.Null(overview.MissionControl); // No detached window launched

                // 2. Verify console drawer expand / collapse
                overview.ToggleAgentConsoleDrawer(card.Id);
                var updatedAfterExpand = overview.Annotations!.Items.Single(i => i.Id == card.Id);
                Assert.Equal(340, updatedAfterExpand.Bounds.Height);
                Assert.Null(overview.MissionControl); // No detached window launched

                overview.ToggleAgentConsoleDrawer(card.Id);
                var updatedAfterCollapse = overview.Annotations!.Items.Single(i => i.Id == card.Id);
                Assert.Equal(140, updatedAfterCollapse.Bounds.Height);
                Assert.Null(overview.MissionControl); // No detached window launched

                // 3. Verify in-place click on avatar cycles profile without detached popup
                // Card is at world (100, 100), Bounds: 280x140. Avatar is at offset (10, 10) -> (115, 115)
                var avatarClickPointer = new CanvasPointer(0, new(115, 115), CanvasPointerButton.Left,
                    CanvasModifiers.None, new CanvasViewTransform(0, 0, 1.0, new CanvasRect(0, 0, 1920, 1080)));
                bool handled = overview.TryHandleAgentCardClick(avatarClickPointer);
                Assert.True(handled);
                Assert.Null(overview.MissionControl); // Still no popup
                var afterAvatarClick = overview.Annotations!.Items.Single(i => i.Id == card.Id);
                Assert.Equal("gemini", afterAvatarClick.AgentRole);

                // 4. Verify in-place click on console toggle expands bounds without popup
                // Console toggle is at offset (Right - 56 + 11, 11 + 11) = (380 - 45, 100 + 22) = (335, 122)
                var consoleClickPointer = new CanvasPointer(0, new(335, 122), CanvasPointerButton.Left,
                    CanvasModifiers.None, new CanvasViewTransform(0, 0, 1.0, new CanvasRect(0, 0, 1920, 1080)));
                handled = overview.TryHandleAgentCardClick(consoleClickPointer);
                Assert.True(handled);
                Assert.Null(overview.MissionControl);
                var afterConsoleClick = overview.Annotations!.Items.Single(i => i.Id == card.Id);
                Assert.Equal(340, afterConsoleClick.Bounds.Height);

                // 5. Verify the top-right close button removes the card without a popup
                var closeClickPointer = new CanvasPointer(0, new(361, 122), CanvasPointerButton.Left,
                    CanvasModifiers.None, new CanvasViewTransform(0, 0, 1.0, new CanvasRect(0, 0, 1920, 1080)));
                handled = overview.TryHandleAgentCardClick(closeClickPointer);
                Assert.True(handled);
                Assert.DoesNotContain(overview.Annotations!.Items, i => i.Id == card.Id);
                Assert.Null(overview.MissionControl);
            }
            finally
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        });
    }
}
