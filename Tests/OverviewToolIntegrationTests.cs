using CanvasDesktop;
using CanvasDesktop.CanvasView;
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

    [Fact]
    [Trait("Category", "NativeRendering")]
    public void RightClickingCanvasProjectAreaHeaderOpensContextMenuWithChooseProjectFolder()
        => WorkspaceViewTests.OnSta(() =>
        {
            var config = new FakeAppConfig { EnableStartupIntro = false };
            var zone = new WorkspaceZone
            {
                Name = "Test Project",
                Bounds = new WorldRect { X = 200, Y = 300, W = 800, H = 600 }
            };
            using var rig = new WorkspaceHarness(new WorkspaceDocument { Zones = [zone] }, config: config);
            rig.Workspace.Start();

            using var overview = new OverviewManager(rig.Canvas, rig.Wm, rig.Api, rig.Input, config);
            overview.AttachPlaceholderWorkspace(rig.Workspace);
            overview.AttachZones(rig.Workspace.Zones);
            overview.TransitionTo(OverviewMode.Zooming);

            string targetFolder = System.IO.Path.GetTempPath();
            overview.ProjectFolderPickerOverride = _ => targetFolder;

            var pass = (OverviewOverlay)Control.FromHandle(overview.MonitorHandles[0])!;
            var b = pass.Screen.Bounds;
            double dpi = pass.DeviceDpi / 96.0;
            var view = new CanvasViewTransform(overview.Camera.X, overview.Camera.Y, overview.Camera.Zoom,
                new(b.X, b.Y, b.Width, b.Height), dpi, dpi);
            var label = ZoneLabelLayout.Get(zone.Bounds, view);
            Assert.NotNull(label);
            int clickX = (int)(label.Value.X + label.Value.Width / 2);
            int clickY = (int)(label.Value.Y + label.Value.Height / 2);

            pass.OnMouseButtonDown!(pass, new MouseEventArgs(MouseButtons.Right, 1, clickX, clickY, 0));

            var field = typeof(OverviewManager).GetField("_annotationContextMenu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var menu = Assert.IsType<ContextMenuStrip>(field.GetValue(overview));
            Assert.True(menu.Visible);

            var items = menu.Items.OfType<ToolStripMenuItem>().ToArray();
            var chooseFolderItem = Assert.Single(items, i => i.Text == "Choose project folder…");
            Assert.Contains(items, i => i.Text == "Put in backpack");
            Assert.Contains(items, i => i.Text == "Delete area…");

            chooseFolderItem.PerformClick();
            Application.DoEvents();

            Assert.Equal(System.IO.Path.TrimEndingDirectorySeparator(targetFolder), zone.ProjectFolderPath);
            var item = Assert.Single(rig.Workspace.Document.Items);
            Assert.Equal(item.Id, Assert.Single(zone.ItemIds));

            Assert.False(menu.Visible);
            Assert.False(overview.CloseActiveMenus());
        });

    [Theory]
    [InlineData(ZoneDeleteMode.BorderOnly)]
    [InlineData(ZoneDeleteMode.Everything)]
    [Trait("Category", "NativeRendering")]
    public void RightClickingCanvasProjectAreaHeaderDeleteAreaExecutesSelectedMode(ZoneDeleteMode mode)
        => WorkspaceViewTests.OnSta(() =>
        {
            var config = new FakeAppConfig { EnableStartupIntro = false };
            var zone = new WorkspaceZone
            {
                Name = "Project To Delete",
                Bounds = new WorldRect { X = 200, Y = 300, W = 800, H = 600 }
            };
            var item = new WorkspaceItem
            {
                Title = "Contained App",
                ExecutablePath = @"C:\Apps\App.exe",
                Bounds = new WorldRect { X = 250, Y = 350, W = 300, H = 200 }
            };
            using var rig = new WorkspaceHarness(new WorkspaceDocument { Zones = [zone], Items = [item] }, config: config);
            rig.Workspace.Start();
            zone.ItemIds.Add(item.Id);

            var annotFolder = Path.Combine(Path.GetTempPath(), "zone-delete-overview-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(annotFolder);
            try
            {
                using var overview = new OverviewManager(rig.Canvas, rig.Wm, rig.Api, rig.Input, config);
                overview.AttachPlaceholderWorkspace(rig.Workspace);
                overview.AttachZones(rig.Workspace.Zones);
                overview.AttachAnnotations(rig.Desktops, Path.Combine(annotFolder, "annotations.json"));
                var note = new CanvasAnnotation(
                    Guid.NewGuid(),
                    rig.Workspace.Zones.DesktopId,
                    AnnotationKind.Text,
                    new CanvasPoint(250, 350),
                    new CanvasPoint(450, 450),
                    Text: "Contained Note"
                );
                overview.Annotations!.Put(note);
                overview.TransitionTo(OverviewMode.Zooming);

                overview.ConfirmDeleteZoneOverride = _ => mode;

                var pass = (OverviewOverlay)Control.FromHandle(overview.MonitorHandles[0])!;
                var b = pass.Screen.Bounds;
                double dpi = pass.DeviceDpi / 96.0;
                var view = new CanvasViewTransform(overview.Camera.X, overview.Camera.Y, overview.Camera.Zoom,
                    new(b.X, b.Y, b.Width, b.Height), dpi, dpi);
                var label = ZoneLabelLayout.Get(zone.Bounds, view);
                Assert.NotNull(label);
                int clickX = (int)(label.Value.X + label.Value.Width / 2);
                int clickY = (int)(label.Value.Y + label.Value.Height / 2);

                pass.OnMouseButtonDown!(pass, new MouseEventArgs(MouseButtons.Right, 1, clickX, clickY, 0));

                var field = typeof(OverviewManager).GetField("_annotationContextMenu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                var menu = Assert.IsType<ContextMenuStrip>(field.GetValue(overview));
                var deleteItem = Assert.Single(menu.Items.OfType<ToolStripMenuItem>(), i => i.Text == "Delete area…");

                deleteItem.PerformClick();
                Application.DoEvents();

                Assert.DoesNotContain(rig.Workspace.Zones.Items, z => z.Id == zone.Id);
                if (mode == ZoneDeleteMode.Everything)
                {
                    Assert.DoesNotContain(rig.Workspace.Document.Items, i => i.Id == item.Id);
                    Assert.DoesNotContain(overview.Annotations!.Items, a => a.Id == note.Id);
                }
                else
                {
                    Assert.Contains(rig.Workspace.Document.Items, i => i.Id == item.Id);
                    Assert.Contains(overview.Annotations!.Items, a => a.Id == note.Id);
                }
            }
            finally
            {
                try { Directory.Delete(annotFolder, true); } catch { }
            }
        });

    [Fact]
    [Trait("Category", "NativeRendering")]
    public void RightClickingCanvasProjectAreaHeaderDeleteAreaCancelKeepsEverything()
        => WorkspaceViewTests.OnSta(() =>
        {
            var config = new FakeAppConfig { EnableStartupIntro = false };
            var zone = new WorkspaceZone
            {
                Name = "Canceled Delete Project",
                Bounds = new WorldRect { X = 200, Y = 300, W = 800, H = 600 }
            };
            var item = new WorkspaceItem
            {
                Title = "Kept App",
                ExecutablePath = @"C:\Apps\App.exe",
                Bounds = new WorldRect { X = 250, Y = 350, W = 300, H = 200 }
            };
            using var rig = new WorkspaceHarness(new WorkspaceDocument { Zones = [zone], Items = [item] }, config: config);
            rig.Workspace.Start();
            zone.ItemIds.Add(item.Id);

            using var overview = new OverviewManager(rig.Canvas, rig.Wm, rig.Api, rig.Input, config);
            overview.AttachPlaceholderWorkspace(rig.Workspace);
            overview.AttachZones(rig.Workspace.Zones);
            overview.TransitionTo(OverviewMode.Zooming);

            overview.ConfirmDeleteZoneOverride = _ => null;

            var pass = (OverviewOverlay)Control.FromHandle(overview.MonitorHandles[0])!;
            var b = pass.Screen.Bounds;
            double dpi = pass.DeviceDpi / 96.0;
            var view = new CanvasViewTransform(overview.Camera.X, overview.Camera.Y, overview.Camera.Zoom,
                new(b.X, b.Y, b.Width, b.Height), dpi, dpi);
            var label = ZoneLabelLayout.Get(zone.Bounds, view);
            Assert.NotNull(label);
            int clickX = (int)(label.Value.X + label.Value.Width / 2);
            int clickY = (int)(label.Value.Y + label.Value.Height / 2);

            pass.OnMouseButtonDown!(pass, new MouseEventArgs(MouseButtons.Right, 1, clickX, clickY, 0));

            var field = typeof(OverviewManager).GetField("_annotationContextMenu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var menu = Assert.IsType<ContextMenuStrip>(field.GetValue(overview));
            var deleteItem = Assert.Single(menu.Items.OfType<ToolStripMenuItem>(), i => i.Text == "Delete area…");

            deleteItem.PerformClick();
            Application.DoEvents();

            Assert.Contains(rig.Workspace.Zones.Items, z => z.Id == zone.Id);
            Assert.Contains(rig.Workspace.Document.Items, i => i.Id == item.Id);
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
