using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Interop;
using CanvasDesktop;
using CanvasDesktop.CanvasView;
using Xunit;

namespace CanvasDesktop.Tests;

[Collection("ShellViews")]
public class AgentTerminalOverlayTests
{
    [Fact]
    public void PlacementMath_TransformsWorldCardBoundsToScreenDrawerPosition()
    {
        WorkspaceViewTests.OnSta(() =>
        {
            var herdr = new MockAgentSessionManager();
            var cardId = Guid.NewGuid();
            var profile = herdr.DefaultAgent; // Codex

            var cardBounds = new CanvasRect(100, 200, 280, 340);
            var expectedDrawerWorld = MissionCardLayout.TerminalDrawerRect(cardBounds);

            // Verify world drawer rect math:
            // X: 100 + 10 = 110, Y: 200 + 108 = 308, Width: 280 - 20 = 260, Height: 340 - 118 = 222
            Assert.Equal(110, expectedDrawerWorld.X);
            Assert.Equal(308, expectedDrawerWorld.Y);
            Assert.Equal(260, expectedDrawerWorld.Width);
            Assert.Equal(222, expectedDrawerWorld.Height);

            // Transform: camera at (50, 100), zoom 1.5, monitor 1920x1080
            var transform = new CanvasViewTransform(50, 100, 1.5, new CanvasRect(0, 0, 1920, 1080));
            var expectedScreenRect = transform.WorldToScreen(expectedDrawerWorld);

            // Screen coords:
            // Screen X: (110 - 50) * 1.5 = 90
            // Screen Y: (308 - 100) * 1.5 = 312
            // Screen Width: 260 * 1.5 = 390
            // Screen Height: 222 * 1.5 = 333
            Assert.Equal(90, expectedScreenRect.X);
            Assert.Equal(312, expectedScreenRect.Y);
            Assert.Equal(390, expectedScreenRect.Width);
            Assert.Equal(333, expectedScreenRect.Height);

            using var overlay = new AgentTerminalOverlayWindow(herdr, cardId, profile);
            var dpi = new DpiScale(1.0, 1.0);
            var workingArea = new System.Drawing.Rectangle(0, 0, 1920, 1080);

            overlay.UpdatePlacement(transform, cardBounds, dpi, workingArea);

            Assert.Equal(expectedDrawerWorld, overlay.LastDrawerWorld);
            Assert.Equal(expectedScreenRect, overlay.LastScreenRect);
            Assert.Equal(90, overlay.Left);
            Assert.Equal(312, overlay.Top);
            Assert.Equal(390, overlay.Width);
            Assert.Equal(333, overlay.Height);
            Assert.Equal(Visibility.Visible, overlay.Visibility);
            Assert.Equal(1.0, overlay.Opacity);
            Assert.False(overlay.IsBelowLodThreshold);
        });
    }

    [Fact]
    public void PlacementMath_NonIdentityDpi_ScalesCoordinatesProperly()
    {
        WorkspaceViewTests.OnSta(() =>
        {
            var herdr = new MockAgentSessionManager();
            var cardId = Guid.NewGuid();
            var profile = herdr.DefaultAgent;

            var cardBounds = new CanvasRect(100, 200, 280, 340);
            var transform = new CanvasViewTransform(50, 100, 1.5, new CanvasRect(0, 0, 1920, 1080));
            var dpi = new DpiScale(1.5, 1.5);
            var workingArea = new System.Drawing.Rectangle(0, 0, 1920, 1080);

            using var overlay = new AgentTerminalOverlayWindow(herdr, cardId, profile);
            overlay.UpdatePlacement(transform, cardBounds, dpi, workingArea);

            // Screen pixels: 90, 312, 390, 333
            // DIPs (divided by 1.5):
            // Left: 90 / 1.5 = 60
            // Top: 312 / 1.5 = 208
            // Width: 390 / 1.5 = 260
            // Height: 333 / 1.5 = 222
            Assert.Equal(60, overlay.Left);
            Assert.Equal(208, overlay.Top);
            Assert.Equal(260, overlay.Width);
            Assert.Equal(222, overlay.Height);
        });
    }

    [Fact]
    public void LodThreshold_HidesWhenZoomIsBelowMinimum()
    {
        WorkspaceViewTests.OnSta(() =>
        {
            var herdr = new MockAgentSessionManager();
            var cardId = Guid.NewGuid();
            var profile = herdr.DefaultAgent;

            var cardBounds = new CanvasRect(100, 200, 280, 340);
            // Zoom = 0.3 (< 0.35 threshold)
            var transform = new CanvasViewTransform(0, 0, 0.3, new CanvasRect(0, 0, 1920, 1080));
            var dpi = new DpiScale(1.0, 1.0);
            var workingArea = new System.Drawing.Rectangle(0, 0, 1920, 1080);

            using var overlay = new AgentTerminalOverlayWindow(herdr, cardId, profile);
            overlay.UpdatePlacement(transform, cardBounds, dpi, workingArea);

            Assert.True(overlay.IsBelowLodThreshold);
            Assert.Equal(Visibility.Hidden, overlay.Visibility);
            Assert.Equal(0.0, overlay.Opacity);
        });
    }

    [Fact]
    public void LodThreshold_HidesWhenScreenHeightDipsIsBelowMinimum()
    {
        WorkspaceViewTests.OnSta(() =>
        {
            var herdr = new MockAgentSessionManager();
            var cardId = Guid.NewGuid();
            var profile = herdr.DefaultAgent;

            // Small card height -> drawer height world = 150 - 118 = 32
            // At zoom 0.5, screen height = 32 * 0.5 = 16 DIPs (< 60)
            var cardBounds = new CanvasRect(100, 200, 280, 150);
            var transform = new CanvasViewTransform(0, 0, 0.5, new CanvasRect(0, 0, 1920, 1080));
            var dpi = new DpiScale(1.0, 1.0);
            var workingArea = new System.Drawing.Rectangle(0, 0, 1920, 1080);

            using var overlay = new AgentTerminalOverlayWindow(herdr, cardId, profile);
            overlay.UpdatePlacement(transform, cardBounds, dpi, workingArea);

            Assert.True(overlay.IsBelowLodThreshold);
            Assert.Equal(Visibility.Hidden, overlay.Visibility);
            Assert.Equal(0.0, overlay.Opacity);
        });
    }

    [Fact]
    public void Placement_HidesWhenDrawerIsOutsideWorkingArea()
    {
        WorkspaceViewTests.OnSta(() =>
        {
            var herdr = new MockAgentSessionManager();
            var cardId = Guid.NewGuid();
            var profile = herdr.DefaultAgent;

            // Off-screen card
            var cardBounds = new CanvasRect(5000, 5000, 280, 340);
            var transform = new CanvasViewTransform(0, 0, 1.0, new CanvasRect(0, 0, 1920, 1080));
            var dpi = new DpiScale(1.0, 1.0);
            var workingArea = new System.Drawing.Rectangle(0, 0, 1920, 1080);

            using var overlay = new AgentTerminalOverlayWindow(herdr, cardId, profile);
            overlay.UpdatePlacement(transform, cardBounds, dpi, workingArea);

            Assert.Equal(Visibility.Hidden, overlay.Visibility);
            Assert.Equal(0.0, overlay.Opacity);
        });
    }

    [Fact]
    public void OverlayLifecycle_CreationPropertiesAndDisposal()
    {
        WorkspaceViewTests.OnSta(() =>
        {
            var herdr = new MockAgentSessionManager();
            var cardId = Guid.NewGuid();
            var claudeProfile = HerdrSessions.FindAgent("claude");

            bool collapsed = false;
            var overlay = new AgentTerminalOverlayWindow(herdr, cardId, claudeProfile, onCollapse: () => collapsed = true);

            try
            {
                Assert.Equal(WindowStyle.None, overlay.WindowStyle);
                Assert.True(overlay.AllowsTransparency);
                Assert.True(overlay.Topmost);
                Assert.False(overlay.ShowInTaskbar);
                Assert.Equal(System.Windows.Media.Brushes.Transparent, overlay.Background);
                Assert.Equal("Agent Terminal (Claude Code)", overlay.Title);
                Assert.Equal(cardId, overlay.CardId);
                Assert.Equal("claude", overlay.Profile.Id);
                Assert.NotNull(overlay.TerminalPane);
                Assert.Contains(">_ Live Terminal Console", overlay.StatusText);
                Assert.False(collapsed);

                // Trigger collapse action
                overlay.CollapseRequested += () => { };
                overlay.DetachRequested += () => { };
                overlay.ClearRequested += () => { };
            }
            finally
            {
                overlay.Dispose();
            }
        });
    }

    [Fact]
    public void WindowOwnership_Section31_SafeInteropWithoutThrowing()
    {
        WorkspaceViewTests.OnSta(() =>
        {
            var herdr = new MockAgentSessionManager();
            var cardId = Guid.NewGuid();
            var profile = herdr.DefaultAgent;

            using var overlay = new AgentTerminalOverlayWindow(herdr, cardId, profile);
            using var ownerForm = new System.Windows.Forms.Form();

            // SetOwner with IntPtr.Zero is a no-op
            overlay.SetOwner(IntPtr.Zero);

            // SetOwner with valid handle does not throw
            overlay.SetOwner(ownerForm.Handle);

            // Safe ownership interop verification: WindowInteropHelper.Owner
            var interop = new WindowInteropHelper(overlay);
            Assert.Equal(ownerForm.Handle, interop.Owner);
        });
    }

    [Fact]
    public void ContextMenu_WatchTerminalForLiveAgentAndToggleConsoleForNonLive()
    {
        var desktop = Guid.NewGuid();
        var cardAnnotation = new CanvasAnnotation(Guid.NewGuid(), desktop, AnnotationKind.Text,
            new(100, 100), new(380, 240), Text: "Codex", AgentTaskId: "task-01", AgentRole: "codex");

        // Non-live menu
        using var notLiveMenu = AgentCardContextMenuBuilder.Build(
            cardAnnotation, isLive: false, _ => { }, () => { }, () => { }, () => { }, () => { }, () => { }, () => { });
        var notLiveItems = notLiveMenu.Items.OfType<ToolStripMenuItem>().ToArray();
        var notLiveConsoleItem = Assert.Single(notLiveItems, i => i.Text == "Toggle Live Console");
        Assert.Equal("Toggle In-Canvas Console", notLiveConsoleItem.AccessibleName);

        // Live menu
        using var liveMenu = AgentCardContextMenuBuilder.Build(
            cardAnnotation, isLive: true, _ => { }, () => { }, () => { }, () => { }, () => { }, () => { }, () => { });
        var liveItems = liveMenu.Items.OfType<ToolStripMenuItem>().ToArray();
        var liveConsoleItem = Assert.Single(liveItems, i => i.Text == "Watch Terminal");
        Assert.Equal("Toggle In-Canvas Console", liveConsoleItem.AccessibleName);
    }

    [Fact]
    public void OverviewManager_ToggleAgentConsoleDrawer_OpensAndClosesActiveTerminalOverlay()
    {
        WorkspaceViewTests.OnSta(() =>
        {
            var desktop = Guid.NewGuid();
            var folder = Path.Combine(Path.GetTempPath(), "overlay_test_" + Guid.NewGuid().ToString("N"));
            var card = new CanvasAnnotation(Guid.NewGuid(), desktop, AnnotationKind.Text,
                new(100, 100), new(380, 240), Text: "Codex", AgentTaskId: "task-01", AgentRole: "codex");

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

                Assert.Empty(overview.ActiveTerminalOverlays);
                Assert.Null(overview.ActiveTerminalOverlay);

                // 1. Expand console drawer (height becomes 340 >= 180) -> opens overlay
                overview.ToggleAgentConsoleDrawer(card.Id);
                var expandedCard = overview.Annotations!.Items.Single(i => i.Id == card.Id);
                Assert.Equal(340, expandedCard.Bounds.Height);

                Assert.Single(overview.ActiveTerminalOverlays);
                Assert.NotNull(overview.ActiveTerminalOverlay);
                Assert.Equal(card.Id, overview.ActiveTerminalOverlay.CardId);

                // 2. Collapse console drawer (height becomes 140 < 180) -> closes and disposes overlay
                overview.ToggleAgentConsoleDrawer(card.Id);
                var collapsedCard = overview.Annotations!.Items.Single(i => i.Id == card.Id);
                Assert.Equal(140, collapsedCard.Bounds.Height);

                Assert.Empty(overview.ActiveTerminalOverlays);
                Assert.Null(overview.ActiveTerminalOverlay);
            }
            finally
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        });
    }

    [Fact]
    public void OverviewManager_CloseMissions_CleansUpAllActiveOverlays()
    {
        WorkspaceViewTests.OnSta(() =>
        {
            var desktop = Guid.NewGuid();
            var folder = Path.Combine(Path.GetTempPath(), "overlay_close_test_" + Guid.NewGuid().ToString("N"));
            var card = new CanvasAnnotation(Guid.NewGuid(), desktop, AnnotationKind.Text,
                new(100, 100), new(380, 240), Text: "Codex", AgentTaskId: "task-01", AgentRole: "codex");

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

                overview.ToggleAgentConsoleDrawer(card.Id);
                Assert.Single(overview.ActiveTerminalOverlays);

                overview.CloseMissions();
                Assert.Empty(overview.ActiveTerminalOverlays);
            }
            finally
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        });
    }
}
