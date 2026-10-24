using CanvasDesktop;
using CanvasDesktop.CanvasView;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Media;

namespace CanvasDesktop.Tests;

[Collection("ShellViews")]
public class PresentationFrameTests
{
    [Fact]
    public void GridSnapshotsDetachCameraPanAndMonitorDataFromLaterUpdates()
    {
        using var grid = new GridRenderer();
        grid.SetScreenLayout(-1920, 0, false, [new(-1920, 0, 1920, 1080), new(0, 0, 1280, 720)]);
        grid.UpdateCamera(10, 20, .5, 3, 4);
        var monitors = new float[64];
        var before = grid.CaptureSnapshot(monitors);
        grid.UpdateCamera(30, 40, .25, 5, 6);
        grid.SetScreenLayout(0, 0, true, [new(0, 0, 2560, 1440)]);
        var next = grid.CaptureSnapshot(new float[64]);
        Assert.Equal(10, before.CamX); Assert.Equal(20, before.CamY); Assert.Equal(.5f, before.Zoom);
        Assert.Equal(3, before.PanAccumX); Assert.Equal(4, before.PanAccumY);
        Assert.Equal(2, before.MonitorCount); Assert.Equal(-1920, monitors[0]); Assert.Equal(1920, monitors[2]);
        Assert.Equal(30, next.CamX); Assert.Equal(8, next.PanAccumX); Assert.Equal(10, next.PanAccumY);
        Assert.Equal(1, next.MonitorCount);
    }

    [Fact]
    public async Task CameraPublishingContinuesWhileThePreviousSnapshotIsHeldByASlowConsumer()
    {
        using var grid = new GridRenderer();
        grid.UpdateCamera(1, -1, .5);
        using var captured = new ManualResetEventSlim(); using var finish = new ManualResetEventSlim();
        var consumer = Task.Run(() =>
        {
            var frame = grid.CaptureSnapshot(new float[64]);
            captured.Set(); finish.Wait();
            Assert.Equal(1, frame.CamX); Assert.Equal(-1, frame.CamY);
        });
        try
        {
            Assert.True(captured.Wait(5000));
            await Task.Run(() => grid.UpdateCamera(2, -2, .25)).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(2, grid.CaptureSnapshot(new float[64]).CamX);
        }
        finally { finish.Set(); await consumer.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Fact]
    public void MinimapSnapshotsDoNotBorrowMutableWindowRectangles()
    {
        using var map = new MinimapRenderer();
        map.UpdateSnapshot([new() { X = 20, Y = 30, W = 100, H = 80 }], null, (0, 0, 800, 600));
        var rectangles = new float[4];
        var before = map.CaptureSnapshot(rectangles);
        var saved = rectangles.ToArray();
        map.UpdateSnapshot([], null, (100, 100, 800, 600));
        Assert.Equal(1, before.WindowCount);
        Assert.Equal(saved, rectangles);
        Assert.Equal(0, map.CaptureSnapshot([]).WindowCount);
    }

    private sealed class Recorder : IOverviewAnnotationLayer
    {
        public CanvasViewTransform? View;
        public Action? OnDraw;
        public void Draw(DrawingContext dc, OverviewAnnotationContext context) { View = context.View; OnDraw?.Invoke(); }
    }

    [Fact]
    public void TwoAnnotationWindowsCanStageAndPrepareBeforeOneSharedDispatcherFlush()
        => WorkspaceViewTests.OnSta(() =>
        {
            var diagnostics = new PerformanceDiagnostics();
            var first = new OverviewAnnotationWindow(IntPtr.Zero, diagnostics);
            var second = new OverviewAnnotationWindow(IntPtr.Zero, diagnostics);
            try
            {
                var a = new Recorder(); var b = new Recorder();
                first.SetDrawingLayers([a]); second.SetDrawingLayers([b]);
                OverviewAnnotationSnapshot Frame(int x, int screenX) => OverviewAnnotationSnapshot.Create([], x, 20, .5,
                    new(screenX, 0, 300, 220), null, false, showHints: false);
                first.Update(Frame(0, 0)); second.Update(Frame(0, 300));
                diagnostics.Reset();
                first.StageUpdate(Frame(100, 0)); second.StageUpdate(Frame(100, 300));
                first.PrepareFrame(); second.PrepareFrame(); first.FlushFrame();
                Assert.Equal(100, a.View!.Value.CameraX); Assert.Equal(100, b.View!.Value.CameraX);
                Assert.Equal(1, Assert.Single(diagnostics.Snapshot().Operations).Calls);
            }
            finally { first.Close(); second.Close(); }
        });

    [Fact]
    public void ThumbnailCullingAndDestinationUseTheSameCapturedCamera()
        => WorkspaceViewTests.OnSta(() =>
        {
            using var pass = new OverviewOverlay(Screen.PrimaryScreen!);
            var canvas = new Canvas(); var api = new FakeWindowApi(); var screens = new FakeScreens();
            var camera = new OverviewCamera(screens); var dwm = new FakeDwmThumbnailApi();
            var b = pass.Screen.Bounds;
            for (int i = 1; i <= 2; i++)
            {
                api.AddWindow((IntPtr)i, b.X + 100 * i, b.Y + 100, 80, 80);
                canvas.SetWindow((IntPtr)i, b.X + 100 * i, b.Y + 100, 80, 80);
            }
            var windows = new OverviewWindowList(canvas, api); windows.Refresh();
            var thumbnails = new OverviewThumbnails([pass], windows, camera, new(), api, screens, dwm: dwm,
                prepareLiveSources: _ => camera.SetTo(9000, 0, 1));
            try
            {
                thumbnails.Reconcile();
                foreach (int i in new[] { 1, 2 })
                {
                    var update = Assert.Single(dwm.Updates, u => u.Source == (IntPtr)i
                        && (u.Properties.dwFlags & Windows.Win32.PInvoke.DWM_TNP_RECTDESTINATION) != 0);
                    Assert.Equal(100 * i, update.Properties.rcDestination.left);
                }
                thumbnails.Reconcile(); Assert.False(thumbnails.HasLiveSource((IntPtr)1));
            }
            finally { thumbnails.Hide(); }
        });

    [Fact]
    [Trait("Category", "NativeRendering")]
    public void ReentrantCameraInputPublishesTheCurrentFrameThenTheNextCamera()
        => WorkspaceViewTests.OnSta(() =>
        {
            using var source = new Form();
            var canvas = new Canvas(); var api = new FakeWindowApi(); var input = new FakeInputRouter();
            var clock = new FakeClock(); var config = new FakeAppConfig { EnableStartupIntro = false, LowQualityMode = true };
            api.AddWindow(source.Handle, 100, 100, 400, 300); canvas.SetWindow(source.Handle, 100, 100, 400, 300);
            using var wm = new WindowManager(canvas, api, config, input, clock);
            using var overview = new OverviewManager(canvas, wm, api, input, config, gestureClock: clock, dwm: new FakeDwmThumbnailApi());
            overview.TransitionTo(OverviewMode.Zooming);
            var pass = (OverviewOverlay)Control.FromHandle(overview.MonitorHandles[0])!;
            var layer = new Recorder(); bool once = true;
            layer.OnDraw = () => { if (once) { once = false; canvas.SetCamera(800, 200); } };
            pass.SetDrawingLayers([layer]);
            pass.OnWheel!(pass, 200, 200, -30, false, true, false);
            var requested = overview.ViewCamera;
            overview.TickGesturePreviews();
            var published = pass.Grid!.CaptureSnapshot(new float[64]);
            Assert.Equal((float)requested.X, published.CamX); Assert.Equal((float)requested.Zoom, published.Zoom);
            Assert.Equal(requested.X, layer.View!.Value.CameraX);
            Assert.Equal(800, overview.ViewCamera.X);
            overview.TickGesturePreviews();
            Assert.Equal(800, pass.Grid.CaptureSnapshot(new float[64]).CamX);
            Assert.Equal(800, layer.View!.Value.CameraX);
        });
}
