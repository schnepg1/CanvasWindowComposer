using CanvasDesktop;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Windows.Win32;

namespace CanvasDesktop.Tests;

[Collection("ShellViews")]
public class InventoryNativeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "NativePreview")]
    public void OnlyForegroundOverflowOpensTheBackpack(bool sourceHadFocus)
        => WorkspaceViewTests.OnSta(() =>
        {
            using var source = new Form();
            var config = new FakeAppConfig { EnableStartupIntro = false, ReduceStartupMotion = true };
            using var rig = new WorkspaceHarness(new WorkspaceDocument { PlacementPolicyInitialized = true }, config: config);
            rig.Workspace.Start();
            using var overview = new OverviewManager(rig.Canvas, rig.Wm, rig.Api, rig.Input, config, new FakeScreens());
            overview.AttachPlaceholderWorkspace(rig.Workspace);
            rig.Workspace.AdditionalPlacementObstacles = () => [new() { W = 1920, H = 1080 }];
            rig.Api.AddWindow(source.Handle, 10, 20, 400, 300, title: "Overflow");
            rig.Api.Foreground = sourceHadFocus ? source.Handle : IntPtr.Zero;
            rig.Workspace.SaveNow();
            Assert.True(Assert.Single(rig.Workspace.Items).Bounds.InInventory);
            Assert.Equal(sourceHadFocus, rig.Workspace.Document.InventoryOpen);
        });

    [Fact]
    [Trait("Category", "NativePlacement")]
    public void NativeInventoryClippingSurvivesPreviewAndCameraChangesUntilUnpacked()
        => WorkspaceViewTests.OnSta(() =>
        {
            using var source = new Form { FormBorderStyle = FormBorderStyle.None, AutoScaleMode = AutoScaleMode.None };
            var api = new Win32WindowApi(new FakeScreens()); var canvas = new Canvas();
            using var wm = new WindowManager(canvas, api, new FakeAppConfig(), new FakeInputRouter());
            var world = new WorldRect { X = 100, Y = 200, W = 500, H = 350 };
            Assert.True(wm.RestorePlacement(source.Handle, world)); Assert.Equal(0, RegionType(source.Handle));
            Assert.True(wm.TryCarryWindow(source.Handle, world)); Assert.Equal(1, RegionType(source.Handle));
            canvas.SetCamera(5000, 1000); wm.UnclipAll(); wm.SetLivePreviewWindows([source.Handle]); wm.ReprojectSync();
            Assert.Equal(1, RegionType(source.Handle)); Assert.True(canvas.Windows[source.Handle].InInventory);
            world.X = 5100; world.Y = 1200;
            Assert.True(wm.RestorePlacement(source.Handle, world));
            Assert.Equal(0, RegionType(source.Handle)); Assert.Equal((100, 200, 500, 350), api.GetWindowRect(source.Handle));
            Assert.False(source.Visible); // Only this unshown, test-owned HWND was used.
        });

    [Fact]
    [Trait("Category", "NativePreview")]
    public void BackpackAndPickupEffectAppearWithoutTakingForegroundFocus()
        => WorkspaceViewTests.OnSta(() =>
        {
            var bounds = Screen.PrimaryScreen!.Bounds; var work = Screen.PrimaryScreen.WorkingArea;
            using var backpack = new BackpackWindow(new(work.X, work.Y, work.Width, work.Height));
            var foreground = PInvoke.GetForegroundWindow();
            backpack.Present(true, true, new(work.X, work.Y, work.Width, work.Height));
            Assert.Equal(foreground, PInvoke.GetForegroundWindow());
            var screen = backpack.ScreenBounds;
            Assert.InRange(screen.Right, work.Right - 100, work.Right);
            Assert.True(backpack.ContainsScreenPoint(screen.X + screen.Width / 2, screen.Y + screen.Height / 2));
            Assert.False(backpack.ContainsScreenPoint(screen.X - 20, screen.Y - 20));
            using var flight = new InventoryFlightWindow();
            flight.Fly("Test window", new(100, 100, 500, 350), screen, new(bounds.X, bounds.Y, bounds.Width, bounds.Height), false);
            Assert.Equal(foreground, PInvoke.GetForegroundWindow());
        });

    [Fact]
    [Trait("Category", "NativeRendering")]
    public void InventoryShortcutAndCanvasDropCollectAWindowAndTravelWithoutMovingThePanel()
        => WorkspaceViewTests.OnSta(() =>
        {
            Assert.True(GridRenderer.CompileShaders());
            using var source = new Form();
            var config = new FakeAppConfig { EnableStartupIntro = false, ReduceStartupMotion = true };
            using var rig = new WorkspaceHarness(new WorkspaceDocument { PlacementPolicyInitialized = true }, config: config);
            rig.Api.AddWindow(source.Handle, 100, 100, 400, 300, title: "Carry me"); rig.Workspace.Start();
            using var overview = new OverviewManager(rig.Canvas, rig.Wm, rig.Api, rig.Input, config, new FakeScreens());
            overview.AttachPlaceholderWorkspace(rig.Workspace); overview.TransitionTo(OverviewMode.Zooming);
            var pass = (OverviewOverlay)Control.FromHandle(overview.MonitorHandles[0])!;
            pass.OnKey!(pass, new KeysEvent(Keys.I));
            Assert.True(rig.Workspace.Document.InventoryOpen);
            var bag = overview.InventoryView!; var target = bag.ScreenBounds;
            var item = rig.Workspace.Document.Items.Single(); var world = item.Bounds;
            var projected = overview.ViewCamera.Project(world);
            int x = projected.X + projected.W / 2 - pass.OriginX, y = projected.Y + projected.H / 2 - pass.OriginY;
            pass.OnMouseButtonDown!(pass, new(MouseButtons.Left, 1, x, y, 0));
            int bx = target.X + target.Width / 2 - pass.OriginX, by = target.Y + target.Height / 2 - pass.OriginY;
            pass.OnMouseMoved!(pass, new(MouseButtons.Left, 0, bx, by, 0));
            pass.OnMouseButtonUp!(pass, new(MouseButtons.Left, 1, bx, by, 0));
            Assert.True(item.Bounds.InInventory); Assert.Equal(WorkspacePlacement.Deliberate, item.Placement);
            var position = bag.ScreenBounds;
            overview.CenterViewOnMinimap(-8000, -4000);
            Assert.Equal(position, bag.ScreenBounds);
            Assert.True(rig.Workspace.DropFromInventory(item.Id)); Assert.False(item.Bounds.InInventory);
        });

    // A named helper avoids any dependency on keyboard injection into the desktop.
    private sealed class KeysEvent(Keys key) : KeyEventArgs(key);
    private static int RegionType(IntPtr window)
    {
        var region = CreateRectRgn(0, 0, 0, 0);
        try { return GetWindowRgn(window, region); }
        finally { DeleteObject(region); }
    }
    [DllImport("gdi32.dll", ExactSpelling = true)] private static extern IntPtr CreateRectRgn(int l, int t, int r, int b);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern int GetWindowRgn(IntPtr window, IntPtr region);
    [DllImport("gdi32.dll", ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr region);
}
