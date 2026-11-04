using CanvasDesktop;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using Forms = System.Windows.Forms;

namespace CanvasDesktop.Tests;

[Collection("ShellViews")]
public class DrawerDragFeedbackNativeTests
{
    [Fact]
    [Trait("Category", "NativePreview")]
    public void FeedbackWindowsNeverTakeFocusOrMouseCaptureAndHideTogether()
        => WorkspaceViewTests.OnSta(() =>
        {
            var work = Forms.Screen.PrimaryScreen!.WorkingArea;
            using var bag = new BackpackWindow(new(work.X, work.Y, work.Width, work.Height));
            bag.Present(true, true, new(work.X, work.Y, work.Width, work.Height)); PreparePointer(bag);
            Assert.True(bag.Panel.CaptureMouse());
            try
            {
                var foreground = PInvoke.GetForegroundWindow();
                using var feedback = new CanvasPlacementFeedbackView(WinFormsScreens.Instance);
                feedback.ShowLanding(new(work.X + 100, work.Y + 200, 300, 200));
                feedback.ShowPointer(new(work.X + 130, work.Y + 230), "Project notes", true, true);
                Assert.Equal(foreground, PInvoke.GetForegroundWindow()); Assert.Same(bag.Panel, Mouse.Captured);
                foreach (var window in new[] { feedback.PointerWindow!, feedback.LandingWindow! })
                {
                    window.UpdateLayout(); Assert.True(window.IsVisible); Assert.False(window.IsHitTestVisible);
                    var handle = (HWND)new WindowInteropHelper(window).Handle;
                    int style = PInvoke.GetWindowLong(handle, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
                    Assert.Equal(0x080000A0, style & 0x080000A0); Assert.Equal(0, style & 0x40000);
                    Assert.True(window.ActualWidth > 100); Assert.True(window.ActualHeight > 30);
                }
                feedback.Hide(); Assert.False(feedback.PointerWindow!.IsVisible); Assert.False(feedback.LandingWindow!.IsVisible);
            }
            finally { bag.Panel.ReleaseMouseCapture(); }
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "NativeRendering")]
    public void BothDrawersKeepCaptureAndDropAtThePreviewedPositionWhenZoomedOut(bool inventory)
        => WorkspaceViewTests.OnSta(() =>
        {
            using var source = new Forms.Form();
            var config = new FakeAppConfig { EnableStartupIntro = false, ReduceStartupMotion = true, ShortcutZoomOutAmount = .8 };
            using var rig = new WorkspaceHarness(new WorkspaceDocument { PlacementPolicyInitialized = true }, config: config);
            rig.Api.AddWindow(source.Handle, 100, 100, 400, 300, title: "Preview this window"); rig.Workspace.Start();
            var item = Assert.Single(rig.Workspace.Document.Items);
            if (inventory) Assert.True(rig.Workspace.CollectInInventory(item.Id));
            Assert.True(GridRenderer.CompileShaders());
            using var overview = new OverviewManager(rig.Canvas, rig.Wm, rig.Api, rig.Input, config, new FakeScreens());
            overview.AttachPlaceholderWorkspace(rig.Workspace); overview.TransitionTo(OverviewMode.Zooming, zoomOutOnOpen: true);
            overview.CenterViewOnMinimap(-8000, -4000);
            Window host; FrameworkElement panel; DrawerRowDrag drag;
            if (inventory)
            {
                overview.ToggleInventory(); host = overview.InventoryView!; panel = overview.InventoryView!.Panel; drag = overview.InventoryView.Panel.Drag;
            }
            else
            {
                var drawer = overview.TemporaryDrawer!; drawer.State.Open(Environment.TickCount64); drawer.Advance(Environment.TickCount64);
                host = drawer; panel = drawer.Panel; drag = drawer.Panel.Drag;
            }
            PreparePointer(host);
            var camera = overview.ViewCamera; Assert.True(camera.Zoom < .5);
            var point = panel.PointFromScreen(new Point(350.25, 700.5));
            var screen = panel.PointToScreen(point); // Use the physical pointer coordinates WPF reports to both move and release.
            var worldX = camera.X + screen.X / camera.Zoom; var worldY = camera.Y + screen.Y / camera.Zoom;
            var obstacle = new WorldRect { X = worldX - (inventory ? 0 : 200), Y = worldY - (inventory ? 0 : 150), W = 400, H = 300 };
            rig.Workspace.AdditionalPlacementObstacles = () => [obstacle];
            var preview = overview.PreviewDrawerLanding(item.Id, inventory, screen);
            Assert.NotNull(preview); Assert.InRange(preview.Value.W, 1, 199);
            Assert.Null(overview.PreviewDrawerLanding(item.Id, inventory, new Point(100, 1070))); // taskbar
            var bagBounds = overview.InventoryView!.ScreenBounds;
            Assert.Null(overview.PreviewDrawerLanding(item.Id, inventory, new Point(bagBounds.Left + 20, bagBounds.Top + 20)));
            drag.Begin(item.Id, new Point(100, 100)); Assert.True(drag.IsHeld);
            drag.Move(point, true); Assert.True(drag.IsDragging); Assert.Same(panel, Mouse.Captured);
            drag.Release(point);
            Assert.False(drag.IsHeld); Assert.Equal(camera, overview.ViewCamera);
            Assert.False(item.Bounds.InInventory); Assert.Equal(WorkspacePlacement.Deliberate, item.Placement);
            Assert.Equal(preview.Value.X, (int)((item.Bounds.X - camera.X) * camera.Zoom));
            Assert.Equal(preview.Value.Y, (int)((item.Bounds.Y - camera.Y) * camera.Zoom));
            Assert.False(TemporaryAreaLayout.Overlaps(item.Bounds, obstacle, 24 / camera.Zoom));
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "NativePreview")]
    public void DrawerRowsTransferBetweenTheBackpackAndTemporaryArea(bool fromBackpack)
        => WorkspaceViewTests.OnSta(() =>
        {
            using var source = new Forms.Form();
            var config = new FakeAppConfig { EnableStartupIntro = false, ReduceStartupMotion = true };
            using var rig = new WorkspaceHarness(new WorkspaceDocument { PlacementPolicyInitialized = true }, config: config);
            rig.Api.AddWindow(source.Handle, 100, 100, 400, 300, title: "Transfer this window");
            rig.Workspace.Start();
            Assert.True(GridRenderer.CompileShaders());
            using var overview = new OverviewManager(rig.Canvas, rig.Wm, rig.Api, rig.Input, config, new FakeScreens());
            overview.AttachPlaceholderWorkspace(rig.Workspace);
            overview.TransitionTo(OverviewMode.Zooming);
            var item = Assert.Single(rig.Workspace.Document.Items);
            Window host; FrameworkElement panel; DrawerRowDrag drag;
            if (fromBackpack)
            {
                Assert.True(rig.Workspace.CollectInInventory(item.Id));
                overview.ToggleInventory();
                host = overview.InventoryView!; panel = overview.InventoryView!.Panel; drag = overview.InventoryView.Panel.Drag;
            }
            else
            {
                var drawer = overview.TemporaryDrawer!;
                drawer.State.Open(Environment.TickCount64); drawer.Advance(Environment.TickCount64);
                host = drawer; panel = drawer.Panel; drag = drawer.Panel.Drag;
            }
            host.UpdateLayout(); PreparePointer(host);
            var target = fromBackpack ? Center(overview.TemporaryDrawer!.ScreenBounds) : Center(overview.InventoryView!.ScreenBounds);
            Assert.Null(overview.PreviewDrawerLanding(item.Id, fromBackpack, target));

            drag.Begin(item.Id, new Point(80, 60));
            Assert.True(drag.IsHeld);
            drag.Move(panel.PointFromScreen(target), true);
            drag.Release(panel.PointFromScreen(target));

            Assert.False(drag.IsHeld);
            if (fromBackpack)
            {
                Assert.Empty(rig.Workspace.InventoryItems);
                Assert.Equal(item.Id, Assert.Single(rig.Workspace.TemporaryAreaItems).Id);
                Assert.Equal(WorkspacePlacement.Temporary, item.Placement);
            }
            else
            {
                Assert.Empty(rig.Workspace.TemporaryAreaItems);
                Assert.Equal(item.Id, Assert.Single(rig.Workspace.InventoryItems).Id);
                Assert.True(item.Bounds.InInventory);
            }
        });

    [Fact]
    [Trait("Category", "NativePreview")]
    public void BackpackDragHoldsTheReceivingDrawerOpenUntilItLeaves()
        => WorkspaceViewTests.OnSta(() =>
        {
            using var source = new Forms.Form();
            var config = new FakeAppConfig { EnableStartupIntro = false, ReduceStartupMotion = true };
            using var rig = new WorkspaceHarness(new WorkspaceDocument { PlacementPolicyInitialized = true }, config: config);
            rig.Api.AddWindow(source.Handle, 100, 100, 400, 300);
            rig.Workspace.Start();
            var item = Assert.Single(rig.Workspace.Document.Items);
            Assert.True(rig.Workspace.CollectInInventory(item.Id));
            Assert.True(GridRenderer.CompileShaders());
            using var overview = new OverviewManager(rig.Canvas, rig.Wm, rig.Api, rig.Input, config, new FakeScreens());
            overview.AttachPlaceholderWorkspace(rig.Workspace);
            overview.TransitionTo(OverviewMode.Zooming);
            overview.ToggleInventory();
            var drawer = overview.TemporaryDrawer!;
            drawer.ClosePeek();
            drawer.State.SetHovered(false, Environment.TickCount64);
            var bag = overview.InventoryView!;
            PreparePointer(bag);
            var drag = bag.Panel.Drag;
            drag.Begin(item.Id, new Point(80, 60));
            Assert.True(drag.IsHeld);
            drag.Move(bag.Panel.PointFromScreen(Center(drawer.ScreenBounds)), true);
            Assert.True(drawer.State.IsOpen);
            drawer.Advance(Environment.TickCount64 + TemporaryDrawerState.PeekMilliseconds + 1);
            Assert.True(drawer.State.IsOpen);
            Assert.Same(bag.Panel, Mouse.Captured);
            drag.Cancel();
            drawer.Advance(Environment.TickCount64 + TemporaryDrawerState.PeekMilliseconds + 1);
            Assert.False(drawer.State.IsOpen);
        });

    private static Point Center(ScreenRect rect) => new(rect.X + rect.Width / 2.0, rect.Y + rect.Height / 2.0);

    private static void PreparePointer(Window window)
    {
        window.Activate(); Forms.Application.DoEvents(); window.UpdateLayout();
        SendMessageW(new WindowInteropHelper(window).Handle, 0x0200, IntPtr.Zero, new IntPtr(100 | (100 << 16)));
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr SendMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
