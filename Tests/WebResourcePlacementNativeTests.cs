using CanvasDesktop;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace CanvasDesktop.Tests;

[Collection("ShellViews")]
public class WebResourcePlacementNativeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "NativeRendering")]
    public void EscapeCancelsPlacementBeforeClosingTheBackpackOrCanvas(bool global)
        => WorkspaceViewTests.OnSta(() =>
        {
            var config = new FakeAppConfig { EnableStartupIntro = false, ReduceStartupMotion = true };
            using var rig = new WorkspaceHarness(config: config); rig.Workspace.Start();
            using var overview = new OverviewManager(rig.Canvas, rig.Wm, rig.Api, rig.Input, config, new FakeScreens());
            overview.AttachPlaceholderWorkspace(rig.Workspace); overview.TransitionTo(OverviewMode.Zooming);
            overview.ToggleInventory();
            var pass = (OverviewOverlay)Forms.Control.FromHandle(overview.MonitorHandles[0])!;
            int saves = rig.Store.Saves;
            overview.ShowAddWebResource();
            Assert.True(overview.PlacingWebResource); Assert.True(overview.CanvasInputSuspended);
            Assert.Null(overview.WebResourceDialog); Assert.Empty(rig.Workspace.Document.Items);
            // Opening the workflow and moving the preview must not save an unfinished resource.
            pass.OnMouseMoved!(pass, new(Forms.MouseButtons.None, 0, 800, 500, 0));
            Assert.Equal(saves, rig.Store.Saves);
            if (global) rig.Input.RaiseEscPressed(); else pass.OnKey!(pass, new(Forms.Keys.Escape));
            Assert.False(overview.PlacingWebResource); Assert.False(overview.CanvasInputSuspended);
            Assert.Null(overview.WebResourcePlacementBounds); Assert.Null(overview.WebResourceDialog);
            Assert.True(overview.InventoryView!.Panel.IsOpen); Assert.Equal(OverviewMode.Zooming, overview.CurrentMode);
            pass.OnMouseButtonUp!(pass, new(Forms.MouseButtons.Left, 1, 800, 500, 0));
            Assert.Empty(rig.Workspace.Document.Items); Assert.Null(overview.WebResourceDialog);
            overview.ShowAddWebResource(); overview.TransitionTo(OverviewMode.Hidden);
            Assert.False(overview.PlacingWebResource); Assert.Null(overview.WebResourcePlacementBounds);
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "NativeRendering")]
    public void ClickOpensTheDialogAndSaveKeepsTheChosenZoomedCanvasPosition(bool save)
        => WorkspaceViewTests.OnSta(() =>
        {
            string directory = Path.Combine(Path.GetTempPath(), "canvas-placement-ui-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string browser = Path.Combine(directory, "chrome.exe"); File.WriteAllText(browser, "test browser");
                var config = new FakeAppConfig { EnableStartupIntro = false, ReduceStartupMotion = true, ShortcutZoomOutAmount = .8 };
                using var rig = new WorkspaceHarness(config: config); rig.Workspace.Start();
                using var overview = new OverviewManager(rig.Canvas, rig.Wm, rig.Api, rig.Input, config, new FakeScreens());
                overview.AttachPlaceholderWorkspace(rig.Workspace); overview.TransitionTo(OverviewMode.Zooming, zoomOutOnOpen: true);
                overview.CenterViewOnMinimap(-8000, 5000);
                var camera = overview.ViewCamera; Assert.True(camera.Zoom < .5);
                var pass = (OverviewOverlay)Forms.Control.FromHandle(overview.MonitorHandles[0])!;
                int x = 900 - pass.OriginX, y = 550 - pass.OriginY;
                overview.ShowAddWebResource();
                pass.OnMouseMoved!(pass, new(Forms.MouseButtons.None, 0, x, y, 0));
                Assert.True(overview.PlacingWebResource); Assert.Null(overview.WebResourceDialog);
                // A release without a canvas press cannot accept the toolbar/menu selection itself.
                pass.OnMouseButtonUp!(pass, new(Forms.MouseButtons.Left, 1, x, y, 0));
                Assert.True(overview.PlacingWebResource); Assert.Null(overview.WebResourceDialog);
                pass.OnMouseButtonDown!(pass, new(Forms.MouseButtons.Left, 1, x, y, 0));
                Assert.Null(overview.WebResourceDialog); Assert.Empty(rig.Workspace.Document.Items);
                Exception? failure = null; bool opened = false;
                Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() =>
                {
                    var window = overview.WebResourceDialog;
                    try
                    {
                        Assert.NotNull(window); opened = true;
                        Assert.False(overview.PlacingWebResource); Assert.True(overview.CanvasInputSuspended);
                        Assert.Empty(rig.Workspace.Document.Items);
                        var fields = WorkspaceViewTests.Descendants(window!).ToArray();
                        T Field<T>(string name) where T : DependencyObject
                            => fields.OfType<T>().Single(c => AutomationProperties.GetName(c) == name);
                        Field<ComboBox>("Browser").ItemsSource = new[] { new BrowserInstallation(browser, "Chrome") };
                        Field<ComboBox>("Browser").SelectedIndex = 0;
                        Field<TextBox>("Website URL").Text = "https://example.com/";
                        if (save) fields.OfType<Button>().Single(b => Equals(b.Content, "Save"))
                            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        else window!.DialogResult = false;
                        Assert.False(window!.IsVisible);
                    }
                    catch (Exception ex) { failure = ex; if (window?.IsVisible == true) window.DialogResult = false; }
                }));
                // Move since the last throttled preview: release must query this new position.
                pass.OnMouseButtonUp!(pass, new(Forms.MouseButtons.Left, 1, x + 120, y + 30, 0));
                Assert.Null(failure); Assert.True(opened);
                Assert.False(overview.PlacingWebResource); Assert.Null(overview.WebResourceDialog);
                Assert.False(overview.CanvasInputSuspended); Assert.Equal(camera, overview.ViewCamera);
                if (save)
                {
                    var item = Assert.Single(rig.Workspace.Document.Items);
                    Assert.Equal(camera.X + 1020 / camera.Zoom - 500, item.Bounds.X, 5);
                    Assert.Equal(camera.Y + 580 / camera.Zoom - 350, item.Bounds.Y, 5);
                    Assert.Equal(1000, item.Bounds.W); Assert.Equal(700, item.Bounds.H);
                    Assert.Equal(WorkspacePlacement.Deliberate, item.Placement);
                }
                else Assert.Empty(rig.Workspace.Document.Items);
            }
            finally { Directory.Delete(directory, recursive: true); }
        });

    [Fact]
    [Trait("Category", "NativeRendering")]
    public void BlockedSpaceAndReservedPanelsCannotOpenTheDialogAndRightClickCancels()
        => WorkspaceViewTests.OnSta(() =>
        {
            var config = new FakeAppConfig { EnableStartupIntro = false, ReduceStartupMotion = true };
            using var rig = new WorkspaceHarness(config: config); rig.Workspace.Start();
            using var overview = new OverviewManager(rig.Canvas, rig.Wm, rig.Api, rig.Input, config, new FakeScreens());
            overview.AttachPlaceholderWorkspace(rig.Workspace); overview.TransitionTo(OverviewMode.Zooming);
            var pass = (OverviewOverlay)Forms.Control.FromHandle(overview.MonitorHandles[0])!;
            var camera = overview.ViewCamera;
            rig.Workspace.AdditionalPlacementObstacles = () => [new() { X = camera.X, Y = camera.Y, W = 10000, H = 10000 }];
            overview.ShowAddWebResource();
            foreach (var point in new[] { new System.Drawing.Point(800, 500), new(100, 1070) })
            {
                pass.OnMouseButtonDown!(pass, new(Forms.MouseButtons.Left, 1, point.X, point.Y, 0));
                pass.OnMouseButtonUp!(pass, new(Forms.MouseButtons.Left, 1, point.X, point.Y, 0));
                Assert.True(overview.PlacingWebResource); Assert.Null(overview.WebResourcePlacementBounds);
                Assert.Null(overview.WebResourceDialog); Assert.Empty(rig.Workspace.Document.Items);
            }
            pass.OnMouseButtonDown!(pass, new(Forms.MouseButtons.Right, 1, 800, 500, 0));
            Assert.False(overview.PlacingWebResource); Assert.Equal(OverviewMode.Zooming, overview.CurrentMode);
        });
}
