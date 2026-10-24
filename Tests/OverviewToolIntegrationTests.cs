using CanvasDesktop;
using System.Windows.Forms;

namespace CanvasDesktop.Tests;

[Collection("ShellViews")]
public class OverviewToolIntegrationTests
{
    [Fact]
    [Trait("Category", "NativeRendering")]
    public void WindowDoubleClickStillNavigatesAfterTheSecondMouseDownAcquiresOwnership()
        => WorkspaceViewTests.OnSta(() =>
        {
            Assert.True(GridRenderer.CompileShaders());
            using var source = new Form();
            var canvas = new Canvas(); var api = new FakeWindowApi(); var input = new FakeInputRouter();
            var config = new FakeAppConfig { EnableStartupIntro = false };
            api.AddWindow(source.Handle, 100, 100, 400, 300);
            canvas.SetWindow(source.Handle, 100, 100, 400, 300);
            using var wm = new WindowManager(canvas, api, config, input, new FakeClock());
            using var overview = new OverviewManager(canvas, wm, api, input, config);
            overview.TransitionTo(OverviewMode.Zooming);
            var pass = (OverviewOverlay)Control.FromHandle(overview.MonitorHandles[0])!;
            int x = 150 - pass.OriginX, y = 150 - pass.OriginY;
            pass.OnMouseButtonDown!(pass, new(MouseButtons.Left, 1, x, y, 0));
            pass.OnMouseButtonUp!(pass, new(MouseButtons.Left, 1, x, y, 0));
            pass.OnMouseButtonDown!(pass, new(MouseButtons.Left, 2, x, y, 0));
            pass.OnMouseDoubleClicked!(pass, new(MouseButtons.Left, 2, x, y, 0));
            Assert.Equal(OverviewMode.Hidden, overview.CurrentMode);
        });

    [Fact]
    [Trait("Category", "NativeRendering")]
    public void RightClickingCanvasPlaceholderCardOpensItsSavedPlaceMenu()
        => WorkspaceViewTests.OnSta(() =>
        {
            Assert.True(GridRenderer.CompileShaders());
            var config = new FakeAppConfig { EnableStartupIntro = false };
            var item = new WorkspaceItem
            {
                Title = "Editor", ExecutablePath = @"C:\Apps\Editor.exe",
                Bounds = new WorldRect { X = 100, Y = 100, W = 400, H = 300 }
            };
            using var rig = new WorkspaceHarness(new WorkspaceDocument { Items = [item] }, config: config,
                placeholders: new WinFormsPlaceholderFactory());
            rig.Workspace.Start();
            var card = Assert.IsType<PlaceholderForm>(Control.FromHandle(
                Assert.Single(rig.Workspace.GetPlaceholderTitles()).Key));
            rig.Api.AddWindow(card.Handle, 100, 100, 400, 300, pid: (uint)Environment.ProcessId, manageable: false);
            using var overview = new OverviewManager(rig.Canvas, rig.Wm, rig.Api, rig.Input, config);
            overview.AttachPlaceholderWorkspace(rig.Workspace);
            overview.TransitionTo(OverviewMode.Zooming);
            bool opened = false; card.ContextMenuStrip!.Opened += (_, _) => opened = true;
            var pass = (OverviewOverlay)Control.FromHandle(overview.MonitorHandles[0])!;
            pass.OnMouseButtonDown!(pass, new(MouseButtons.Right, 1, 300, 250, 0));
            Assert.True(opened);
            Assert.Contains(card.ContextMenuStrip.Items.OfType<ToolStripMenuItem>(), i => i.Text == "Rename…");
            card.ContextMenuStrip.Close();
        });

    [Fact]
    [Trait("Category", "NativeRendering")]
    public void RightClickingSavedLivePreviewAlsoOffersRenameWithoutCreatingAPlaceholder()
        => WorkspaceViewTests.OnSta(() =>
        {
            using var source = new Form();
            var config = new FakeAppConfig { EnableStartupIntro = false };
            var item = new WorkspaceItem { Title = "Editor", ExecutablePath = @"C:\Apps\Editor.exe",
                Bounds = new WorldRect { X = 100, Y = 100, W = 400, H = 300 } };
            using var rig = new WorkspaceHarness(new WorkspaceDocument { Items = [item] }, config: config);
            rig.Api.AddWindow(source.Handle, 100, 100, 400, 300, title: item.Title);
            rig.Workspace.Start(); Assert.True(rig.Workspace.IsOpen(item));
            using var overview = new OverviewManager(rig.Canvas, rig.Wm, rig.Api, rig.Input, config);
            overview.AttachPlaceholderWorkspace(rig.Workspace); overview.TransitionTo(OverviewMode.Zooming);
            var pass = (OverviewOverlay)Control.FromHandle(overview.MonitorHandles[0])!;
            pass.OnMouseButtonDown!(pass, new(MouseButtons.Right, 1, 300, 250, 0));
            var field = typeof(OverviewManager).GetField("_windowContextMenu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var menu = Assert.IsType<WorkspaceContextMenu>(field.GetValue(overview));
            Assert.True(menu.Visible);
            // Releasing the right mouse button MUST NOT close the menu
            pass.OnMouseButtonUp!(pass, new(MouseButtons.Right, 1, 300, 250, 0));
            Assert.True(menu.Visible); Assert.Empty(rig.Workspace.GetPlaceholderTitles());
            Assert.Contains(menu.Items.OfType<ToolStripMenuItem>(), i => i.Text == "Rename…");
            // CloseActiveMenus properly closes it
            Assert.True(overview.CloseActiveMenus());
            Assert.False(menu.Visible);
        });

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [Trait("Category", "NativeRendering")]
    public void LocalAndGlobalEscapeCancelArmedOrCapturedZoneBeforeClosing(bool global, bool dragging)
        => WorkspaceViewTests.OnSta(() =>
        {
            Assert.True(GridRenderer.CompileShaders());
            var canvas = new Canvas(); var api = new FakeWindowApi(); var input = new FakeInputRouter();
            var config = new FakeAppConfig { EnableStartupIntro = false };
            using var wm = new WindowManager(canvas, api, config, input, new FakeClock());
            using var overview = new OverviewManager(canvas, wm, api, input, config);
            var desktop = Guid.NewGuid();
            overview.AttachZones(new ZoneService(new WorkspaceDocument(), () => desktop, () => true));
            int commits = 0; overview.ZoneDrawn += _ => commits++;
            overview.BeginZone();
            var pass = (OverviewOverlay)Control.FromHandle(overview.MonitorHandles[0])!;
            if (dragging) pass.OnMouseButtonDown!(pass, new(MouseButtons.Left, 1, 200, 200, 0));
            void Escape()
            {
                if (global) input.RaiseEscPressed();
                else pass.OnKey!(pass, new(Keys.Escape));
            }
            Escape();
            Assert.Equal(OverviewMode.Zooming, overview.CurrentMode);
            Assert.False(pass.Capture);
            pass.OnMouseButtonUp!(pass, new(MouseButtons.Left, 1, 400, 400, 0));
            Assert.Equal(0, commits);
            Escape();
            Assert.Equal(OverviewMode.Hidden, overview.CurrentMode);
        });
}
