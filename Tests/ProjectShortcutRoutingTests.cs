using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using CanvasDesktop;

namespace CanvasDesktop.Tests;

[Collection("ShellViews")]
public class ProjectShortcutRoutingTests
{
    [Theory]
    [InlineData(Keys.P)]
    [InlineData(Keys.Z)]
    public void ShortcutSuppressesCharacterAndDefersProjectRequestUntilKeyHandlerReturns(Keys key)
    {
        WorkspaceViewTests.OnSta(() =>
        {
            using var rig = new WorkspaceHarness();
            rig.Workspace.Start();
            using var overview = new OverviewManager(rig.Canvas, rig.Wm, rig.Api, rig.Input, new FakeAppConfig { EnableStartupIntro = false });
            overview.AttachZones(rig.Workspace.Zones);
            EnableKeyboardRouting(overview);
            using var pass = new OverviewOverlay(Screen.PrimaryScreen!);
            _ = pass.Handle; // A hidden handle supplies a WinForms queue without activating a window.
            using var navigation = new ZoneNavigation(rig.Workspace.Zones, rig.Workspace, _ => { }, () => { });
            ZoneWorkspaceWindow? window = null;
            int requests = 0;
            overview.ZonePanelRequested += () =>
            {
                requests++;
                window = new ZoneWorkspaceWindow(rig.Workspace.Zones, navigation, () => { }, () => null);
            };
            try
            {
                var args = new KeyEventArgs(key);
                Press(overview, pass, args);
                Assert.True(args.Handled);
                Assert.True(args.SuppressKeyPress);
                Assert.Equal(0, requests);
                System.Windows.Forms.Application.DoEvents();
                Assert.Equal(1, requests);
                Assert.NotNull(window);
                var search = Descendants(window!).OfType<System.Windows.Controls.TextBox>().Single(t => t.Name == "ProjectSearch");
                Assert.Equal("", search.Text);
                search.Text = "project";
                Assert.Equal("project", search.Text);
            }
            finally { window?.Close(); }
        });
    }

    [Theory]
    [InlineData(Keys.P)]
    [InlineData(Keys.Z)]
    public void QueuedRequestDoesNotRunAfterOverlayIsDisposed(Keys key)
    {
        WorkspaceViewTests.OnSta(() =>
        {
            using var rig = new WorkspaceHarness();
            rig.Workspace.Start();
            using var overview = new OverviewManager(rig.Canvas, rig.Wm, rig.Api, rig.Input, new FakeAppConfig { EnableStartupIntro = false });
            overview.AttachZones(rig.Workspace.Zones);
            EnableKeyboardRouting(overview);
            using var pass = new OverviewOverlay(Screen.PrimaryScreen!);
            _ = pass.Handle;
            int requests = 0;
            overview.ZonePanelRequested += () => requests++;
            var args = new KeyEventArgs(key);
            Press(overview, pass, args);
            Assert.True(args.SuppressKeyPress);
            pass.Dispose();
            System.Windows.Forms.Application.DoEvents();
            Assert.Equal(0, requests);
        });
    }

    private static void EnableKeyboardRouting(OverviewManager overview)
    {
        // Enable the normal keyboard path without showing or rendering an overview on the desktop.
        var state = (OverviewState)typeof(OverviewManager).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(overview)!;
        state.SetMode(OverviewMode.Zooming);
    }

    private static void Press(OverviewManager overview, OverviewOverlay pass, KeyEventArgs args)
        => typeof(OverviewManager).GetMethod("HandleKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(overview, [pass, args]);
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
