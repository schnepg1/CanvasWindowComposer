using CanvasDesktop;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using System.Windows.Interop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Xunit;

namespace CanvasDesktop.Tests;

[Collection("ShellViews")]
public class ZoneOverviewIntegrationTests
{
    [Fact]
    [Trait("Category", "NativeRendering")]
    public void ProjectsWindowRemainsAboveCanvasAndSurvivesCanvasToggles()
        => WorkspaceViewTests.OnSta(() =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "zone-nav-overview-" + Guid.NewGuid().ToString("N"));
            try
            {
                var config = new FakeAppConfig { EnableStartupIntro = false };
                using var rig = new WorkspaceHarness(new WorkspaceDocument { PlacementPolicyInitialized = true }, config: config);
                rig.Workspace.Start();
                var input = rig.Input;
                var desktops = rig.Desktops;
                using var overview = new OverviewManager(rig.Canvas, rig.Wm, rig.Api, input, config);
                overview.AttachPlaceholderWorkspace(rig.Workspace);
                overview.AttachAnnotations(desktops, Path.Combine(folder, "annotations.json"));

                using var zoneUi = new ZoneUi(rig.Workspace, overview);
                zoneUi.Show();

                var navigator = zoneUi.Window!;
                try
                {
                    var search = Descendants(navigator).OfType<System.Windows.Controls.TextBox>().Single(t => t.Name == "ProjectSearch");
                    search.Text = "ActiveProject";

                    for (int i = 0; i < 3; i++)
                    {
                        input.RaiseOverviewHotkey();
                        Assert.Equal(OverviewMode.Zooming, overview.CurrentMode);
                        var pass = (OverviewOverlay)Control.FromHandle(overview.MonitorHandles[0])!;
                        pass.Activate();
                        Application.DoEvents();

                        Assert.Contains(new WindowInteropHelper(navigator).Owner, overview.MonitorHandles);
                        Assert.True(navigator.Topmost);
                        AssertPointerHits(navigator);

                        input.RaiseOverviewHotkey();
                        Assert.Equal(OverviewMode.Hidden, overview.CurrentMode);
                        Assert.Equal(IntPtr.Zero, new WindowInteropHelper(navigator).Owner);
                        Assert.True(navigator.IsVisible);
                        Assert.False(navigator.Topmost);
                        Assert.Equal("ActiveProject", search.Text);
                    }

                    input.RaiseOverviewHotkey();
                    typeof(OverviewManager).GetMethod("OnDisplaySettingsChanged", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                        .Invoke(overview, [null, System.EventArgs.Empty]);
                    Application.DoEvents();
                    AssertPointerHits(navigator);
                    Assert.Contains(new WindowInteropHelper(navigator).Owner, overview.MonitorHandles);
                    overview.TransitionTo(OverviewMode.Hidden);
                }
                finally
                {
                    navigator.Close();
                }
            }
            finally
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        });

    [Fact]
    [Trait("Category", "NativeRendering")]
    public void MoreMenuActionOpensProjectsWindowOverCanvas()
        => WorkspaceViewTests.OnSta(() =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "zone-menu-overview-" + Guid.NewGuid().ToString("N"));
            try
            {
                var config = new FakeAppConfig { EnableStartupIntro = false };
                using var rig = new WorkspaceHarness(new WorkspaceDocument { PlacementPolicyInitialized = true }, config: config);
                rig.Workspace.Start();
                var input = rig.Input;
                var desktops = rig.Desktops;
                using var overview = new OverviewManager(rig.Canvas, rig.Wm, rig.Api, input, config);
                overview.AttachPlaceholderWorkspace(rig.Workspace);
                overview.AttachAnnotations(desktops, Path.Combine(folder, "annotations.json"));
                using var zoneUi = new ZoneUi(rig.Workspace, overview);

                overview.TransitionTo(OverviewMode.Zooming);
                Assert.NotNull(overview.UnifiedDock);
                Assert.True(overview.UnifiedDock.IsDockVisible);

                var menu = overview.UnifiedDock.Panel.OpenMoreMenu();
                Assert.NotNull(menu);
                var projectsItem = menu!.Items.OfType<System.Windows.Controls.MenuItem>()
                    .Single(i => (string)i.Header == "Projects…");
                projectsItem.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));

                Assert.NotNull(zoneUi.Window);
                Assert.True(zoneUi.Window.IsVisible);
                Assert.True(zoneUi.Window.Topmost);
                Assert.Contains(new WindowInteropHelper(zoneUi.Window).Owner, overview.MonitorHandles);
                zoneUi.Window.Close();

                overview.TransitionTo(OverviewMode.Hidden);
            }
            finally
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        });

    [Fact]
    [Trait("Category", "NativeRendering")]
    public void ProjectsWindowKeepsUnsavedEditsAcrossCanvasToggles()
        => WorkspaceViewTests.OnSta(() =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "zone-window-overview-" + Guid.NewGuid().ToString("N"));
            try
            {
                var config = new FakeAppConfig { EnableStartupIntro = false };
                using var rig = new WorkspaceHarness(new WorkspaceDocument { PlacementPolicyInitialized = true }, config: config);
                rig.Workspace.Start();
                var input = rig.Input;
                var desktops = rig.Desktops;
                using var overview = new OverviewManager(rig.Canvas, rig.Wm, rig.Api, input, config);
                overview.AttachPlaceholderWorkspace(rig.Workspace);
                overview.AttachAnnotations(desktops, Path.Combine(folder, "annotations.json"));

                using var zoneUi = new ZoneUi(rig.Workspace, overview);
                zoneUi.Show();

                var window = zoneUi.Window!;
                try
                {
                    var name = Descendants(window).OfType<System.Windows.Controls.TextBox>().Single(t => t.Name == "AreaName");
                    name.Text = "Frontend Area";

                    for (int i = 0; i < 3; i++)
                    {
                        input.RaiseOverviewHotkey();
                        Assert.Equal(OverviewMode.Zooming, overview.CurrentMode);
                        var pass = (OverviewOverlay)Control.FromHandle(overview.MonitorHandles[0])!;
                        pass.Activate();
                        Application.DoEvents();

                        Assert.Contains(new WindowInteropHelper(window).Owner, overview.MonitorHandles);
                        Assert.True(window.Topmost);
                        AssertPointerHits(window);

                        input.RaiseOverviewHotkey();
                        Assert.Equal(OverviewMode.Hidden, overview.CurrentMode);
                        Assert.Equal(IntPtr.Zero, new WindowInteropHelper(window).Owner);
                        Assert.True(window.IsVisible);
                        Assert.False(window.Topmost);
                        Assert.Equal("Frontend Area", name.Text);
                    }

                    input.RaiseOverviewHotkey();
                    typeof(OverviewManager).GetMethod("OnDisplaySettingsChanged", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                        .Invoke(overview, [null, System.EventArgs.Empty]);
                    Application.DoEvents();
                    AssertPointerHits(window);
                    Assert.Contains(new WindowInteropHelper(window).Owner, overview.MonitorHandles);
                    overview.TransitionTo(OverviewMode.Hidden);
                }
                finally
                {
                    window.Close();
                }
            }
            finally
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        });

    private static void AssertPointerHits(System.Windows.Window window)
    {
        window.UpdateLayout();
        var handle = new WindowInteropHelper(window).Handle;
        Assert.NotEqual(System.IntPtr.Zero, handle);
        bool hasRect = PInvoke.GetWindowRect((HWND)handle, out var rect);
        Assert.True(hasRect, $"GetWindowRect failed for handle {handle:X}, IsVisible={window.IsVisible}, IsLoaded={window.IsLoaded}");
        var pt = new System.Drawing.Point((rect.left + rect.right) / 2, (rect.top + rect.bottom) / 2);
        var hit = (System.IntPtr)PInvoke.WindowFromPoint(pt);
        Assert.Equal(handle, hit);
    }

    private static System.Collections.Generic.IEnumerable<System.Windows.DependencyObject> Descendants(System.Windows.DependencyObject root)
    {
        yield return root;
        foreach (var child in System.Windows.LogicalTreeHelper.GetChildren(root).OfType<System.Windows.DependencyObject>())
            foreach (var descendant in Descendants(child)) yield return descendant;
    }
}
