using CanvasDesktop;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using Forms = System.Windows.Forms;

namespace CanvasDesktop.Tests;

[Collection("ShellViews")]
public class CanvasPanelNativeTests
{
    [Fact]
    [Trait("Category", "NativeRendering")]
    public void PanelsStayClickableAfterModeChangesAndClosingBackpackWithoutWaitingForARefresh()
        => WorkspaceViewTests.OnSta(() =>
        {
            Assert.True(GridRenderer.CompileShaders());
            var config = new FakeAppConfig { EnableStartupIntro = false, ReduceStartupMotion = true };
            using var rig = new WorkspaceHarness(new WorkspaceDocument { PlacementPolicyInitialized = true }, config: config);
            rig.Workspace.Start();
            using var overview = new OverviewManager(rig.Canvas, rig.Wm, rig.Api, rig.Input, config);
            overview.AttachPlaceholderWorkspace(rig.Workspace);
            overview.TransitionTo(OverviewMode.Zooming);
            var bag = overview.InventoryView!;
            var drawer = overview.TemporaryDrawer!;
            AssertToolWindow(bag); AssertToolWindow(drawer);

            for (int i = 0; i < 3; i++)
            {
                // ApplyConfig used to raise the fullscreen pass AFTER these panels.
                overview.TransitionTo(OverviewMode.Panning);
                overview.TransitionTo(OverviewMode.Zooming);
                AssertPointerHits(bag); AssertPointerHits(drawer);
                Assert.Contains(new WindowInteropHelper(bag).Owner, overview.MonitorHandles);
                Assert.Contains(new WindowInteropHelper(drawer).Owner, overview.MonitorHandles);
                var folded = bag.ScreenBounds;
                var work = Forms.Screen.PrimaryScreen!.WorkingArea;
                Assert.Equal(work.Top, folded.Top);
                Assert.InRange(Math.Abs(folded.Left + folded.Width / 2.0 - (work.Left + work.Width / 2.0)), 0, 1);

                overview.ToggleInventory();
                AssertPointerHits(bag);
                drawer.State.Open(Environment.TickCount64); drawer.Advance(Environment.TickCount64);
                AssertPointerHits(drawer);
                var expanded = bag.ScreenBounds;
                drawer.ClosePeek();
                Assert.Equal(expanded, bag.ScreenBounds); // Drawer animation must not move the centered backpack.
                bag.Close(); // Closing focuses the canvas; neither button may go behind it.
                Assert.False(bag.Panel.IsOpen);
                Assert.Equal(folded, bag.ScreenBounds);
                AssertPointerHits(bag); AssertPointerHits(drawer);

                overview.TransitionTo(OverviewMode.Hidden);
                Assert.DoesNotContain(new WindowInteropHelper(bag).Owner, overview.MonitorHandles);
                Assert.DoesNotContain(new WindowInteropHelper(drawer).Owner, overview.MonitorHandles);
                Assert.True(drawer.IsVisible);
                overview.TransitionTo(OverviewMode.Zooming);
            }
        });

    [Fact]
    [Trait("Category", "NativePreview")]
    public void BackpackRefreshDuringCaptureAndCancellationKeepsTilesCurrentAndAllowsAnotherDrag()
        => WorkspaceViewTests.OnSta(() =>
        {
            var work = Forms.Screen.PrimaryScreen!.WorkingArea;
            var screen = new ScreenRect(work.X, work.Y, work.Width, work.Height);
            using var bag = new BackpackWindow(screen);
            var first = TemporaryDrawerTests.Row("Original item");
            var added = TemporaryDrawerTests.Row("Arrived during capture");
            bag.Panel.UpdateItems([first]); bag.Present(true, true, screen);
            PreparePointer(bag);
            var panel = bag.Panel;
            int drops = 0;
            panel.DropRequested += (id, point) => { Assert.Equal(first.Id, id); drops++; };
            panel.Drag.Begin(first.Id, new Point(100, 100));
            Assert.True(panel.Drag.IsHeld);
            panel.Drag.Move(new Point(150, 150), true);
            panel.UpdateItems([first, added]);
            Assert.DoesNotContain(Descendants(panel).OfType<TextBlock>(), t => t.Text == added.Title);
            panel.Drag.Cancel();
            Assert.Contains(Descendants(panel).OfType<TextBlock>(), t => t.Text == added.Title);
            Assert.Equal(0, drops);

            panel.Drag.Begin(first.Id, new Point(100, 100));
            Assert.True(panel.Drag.IsHeld);
            panel.Drag.Move(new Point(-200, 100), true);
            panel.Drag.Release(new Point(-200, 100));
            Assert.Equal(1, drops);
            Assert.False(panel.Drag.IsHeld);
            panel.Drag.Begin(first.Id, new Point(100, 100));
            Assert.True(panel.Drag.IsHeld);
            int closes = 0;
            panel.OpenRequested += open => { if (!open) closes++; };
            panel.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(panel), 0, System.Windows.Input.Key.Escape)
                { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
            Assert.False(panel.Drag.IsHeld);
            Assert.Equal(0, closes); // Escape cancels capture before it can fold the backpack.
            panel.Drag.Begin(first.Id, new Point(100, 100));
            Assert.True(panel.Drag.IsHeld);
            bag.Present(false, true, screen);
            Assert.False(panel.Drag.IsHeld);
            Assert.Equal(1, drops);
        });

    private static void AssertToolWindow(Window window)
    {
        int style = PInvoke.GetWindowLong((HWND)new WindowInteropHelper(window).Handle, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        Assert.NotEqual(0, style & 0x80);
        Assert.Equal(0, style & (0x40000 | 0x20 | 0x08000000));
    }

    private static void AssertPointerHits(Window window)
    {
        window.UpdateLayout();
        var handle = new WindowInteropHelper(window).Handle;
        Assert.True(PInvoke.GetWindowRect((HWND)handle, out var rect));
        var point = new System.Drawing.Point((rect.left + rect.right) / 2, (rect.top + rect.bottom) / 2);
        Assert.Equal(handle, (IntPtr)PInvoke.WindowFromPoint(point));
    }

    private static void PreparePointer(Window window)
    {
        window.Activate(); Forms.Application.DoEvents(); window.UpdateLayout();
        SendMessageW(new WindowInteropHelper(window).Handle, 0x0200, IntPtr.Zero, new IntPtr(100 | (100 << 16)));
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        { yield return child; foreach (var descendant in Descendants(child)) yield return descendant; }
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr SendMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
