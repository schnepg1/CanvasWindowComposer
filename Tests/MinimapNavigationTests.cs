using System.Drawing;
using System.Windows.Forms;
using CanvasDesktop;

namespace CanvasDesktop.Tests;

public class MinimapNavigationTests
{
    [Fact]
    public void ProjectionMapsTheWorldAndStaysFixedWhileViewportMoves()
    {
        var projection = MinimapProjection.Create((-1000, -500, 3000, 1500), (0, 0, 800, 600), 240, 160);
        Assert.True(projection.IsValid);
        var world = projection.WorldFromMap(new PointF((float)(projection.OffsetX + (1250 - projection.MinX) * projection.Scale),
            (float)(projection.OffsetY + (-250 - projection.MinY) * projection.Scale)));
        Assert.InRange(world.x, 1249.99, 1250.01);
        Assert.InRange(world.y, -250.01, -249.99);
        var moved = MinimapProjection.Create((-1000, -500, 3000, 1500), (8000, 0, 800, 600), 240, 160);
        Assert.NotEqual(projection, moved);
        Assert.Equal(world, projection.WorldFromMap(new PointF((float)(projection.OffsetX + (1250 - projection.MinX) * projection.Scale),
            (float)(projection.OffsetY + (-250 - projection.MinY) * projection.Scale))));
    }

    [Fact]
    public void OverviewViewportUsesPrimaryMonitorAndCurrentZoom()
    {
        var result = MinimapOverlay.OverviewViewport(new CanvasCamera(100, -50, .5), new ScreenRect(200, -100, 1000, 600));
        Assert.Equal((500d, -250d, 2000d, 1200d), result);
    }

    [Fact]
    [Trait("Category", "NativeRendering")]
    public void DesktopClickAndDragNavigateWithOneFixedProjection()
        => WorkspaceViewTests.OnSta(() =>
        {
            var canvas = new Canvas(); var input = new FakeInputRouter(); var api = new FakeWindowApi();
            var screens = new FakeScreens();
            using var wm = new WindowManager(canvas, api, new FakeAppConfig(), input);
            using var cache = new DesktopStateCache(canvas, wm, new FakeOverviewController(), new FakeVirtualDesktops());
            using var map = new MinimapOverlay(canvas, input, cache, screens);
            canvas.SetWindow((IntPtr)1, -2000, -1000, 5000, 3000);
            map.SetPinned(true);
            Assert.True(map.Pinned);
            Assert.Equal((nint)3, (nint)Windows.Win32.PInvoke.SendMessage((Windows.Win32.Foundation.HWND)map.Handle, 0x21, 0, 0).Value);
            var viewport = canvas.GetViewport(screens.PrimaryBounds.Width, screens.PrimaryBounds.Height);
            var projection = MinimapProjection.Create(canvas.GetWorldExtents(), viewport, 240, 160);
            var first = projection.WorldFromMap(new PointF(80, 60));
            var second = projection.WorldFromMap(new PointF(150, 100));
            int commits = 0; canvas.Committed += () => commits++;
            Mouse(map, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, 88, 68, 0));
            Assert.InRange(canvas.CamX + viewport.w / 2, first.x - .01, first.x + .01);
            Mouse(map, "OnMouseMove", new MouseEventArgs(MouseButtons.Left, 0, 158, 108, 0));
            Assert.InRange(canvas.CamX + viewport.w / 2, second.x - .01, second.x + .01);
            Assert.InRange(canvas.CamY + viewport.h / 2, second.y - .01, second.y + .01);
            Mouse(map, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, 158, 108, 0));
            Assert.Equal(1, canvas.Zoom);
            Assert.Equal(1, commits);
            input.Enabled = false;
            var stopped = canvas.CamX;
            Mouse(map, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, 50, 50, 0));
            Mouse(map, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, 50, 50, 0));
            Assert.Equal(stopped, canvas.CamX);
            Assert.Equal(1, commits);
        });

    [Fact]
    [Trait("Category", "NativeRendering")]
    public void RaisingMinimapDoesNotTakeKeyboardFocus()
        => WorkspaceViewTests.OnSta(() =>
        {
            var canvas = new Canvas(); var input = new FakeInputRouter(); var api = new FakeWindowApi();
            using var wm = new WindowManager(canvas, api, new FakeAppConfig(), input);
            using var cache = new DesktopStateCache(canvas, wm, new FakeOverviewController(), new FakeVirtualDesktops());
            using var map = new MinimapOverlay(canvas, input, cache, new FakeScreens());
            using var focused = new Form();
            focused.Show(); focused.Activate(); Application.DoEvents();
            map.NotifyCanvasChanged();
            focused.Activate(); Application.DoEvents();
            Assert.Equal(focused.Handle, (IntPtr)Windows.Win32.PInvoke.GetForegroundWindow());
            input.RaiseDragStarted(); Application.DoEvents();
            Assert.Equal(focused.Handle, (IntPtr)Windows.Win32.PInvoke.GetForegroundWindow());
        });

    private static void Mouse(Control control, string method, MouseEventArgs args)
        => typeof(Control).GetMethod(method, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(control, [args]);
}
