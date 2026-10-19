using CanvasDesktop;
using System.Windows.Forms;

namespace CanvasDesktop.Tests;

[Collection("ShellViews")]
[Trait("Category", "NativeRendering")]
public class CanvasPerformanceIntegrationTests
{
    private sealed class CaptureCounter : IWindowPreviewCapture
    {
        public int Calls;
        public System.Drawing.Bitmap? Capture(IntPtr handle, System.Drawing.Size sourceSize, System.Drawing.Size targetSize)
        { Interlocked.Increment(ref Calls); return new(targetSize.Width, targetSize.Height); }
    }
    private sealed class CachedSurface : ICachedPreviewSurface
    {
        public IntPtr Handle => (IntPtr)987;
        public void Dispose() { }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ZoomBurstsOnlySimplifyInLowQualityAndRestoreAfterTheLastMotion(bool lowQuality)
        => WorkspaceViewTests.OnSta(() =>
        {
            using var source = new Form();
            var canvas = new Canvas(); var api = new FakeWindowApi(); var input = new FakeInputRouter();
            var clock = new FakeClock(); var dwm = new FakeDwmThumbnailApi(); var captures = new CaptureCounter();
            var config = new FakeAppConfig { EnableStartupIntro = false, LowQualityMode = lowQuality };
            using var previews = new WindowPreviewCache(captures);
            api.AddWindow(source.Handle, 100, 100, 400, 300, title: "Notes");
            canvas.SetWindow(source.Handle, 100, 100, 400, 300);
            using var wm = new WindowManager(canvas, api, config, input, clock, previews: previews);
            using var overview = new OverviewManager(canvas, wm, api, input, config, gestureClock: clock, dwm: dwm);
            overview.TransitionTo(OverviewMode.Zooming);
            var pass = (OverviewOverlay)Control.FromHandle(overview.MonitorHandles[0])!;
            Assert.Contains(source.Handle, dwm.Sources);
            void Zoom() { pass.OnWheel!(pass, 200, 200, -30, false, true); overview.TickGesturePreviews(); }
            Zoom();
            Assert.Equal(lowQuality, overview.GesturePreviewsActive);
            Assert.Equal(!lowQuality, dwm.Sources.Contains(source.Handle));
            if (lowQuality)
            {
                var card = Assert.Single(overview.GesturePreviewCards);
                Assert.Equal("Notes", card.Title); Assert.Equal(0d, card.ImageOpacity);
            }
            clock.Advance(100); Zoom();
            clock.Advance(179); overview.TickGesturePreviews();
            Assert.Equal(lowQuality, overview.GesturePreviewsActive);
            clock.Advance(1); overview.TickGesturePreviews();
            Assert.False(overview.GesturePreviewsActive);
            Assert.Contains(source.Handle, dwm.Sources);
            Assert.Equal(0, captures.Calls); Assert.Empty(api.MinimizeCalls);
            Assert.Equal(100, canvas.Windows[source.Handle].X);
            Assert.Equal(400, canvas.Windows[source.Handle].W);
        });

    [Fact]
    public void HeldWindowDragStaysSimpleAndDisablingQualityOrClosingRestoresTheSession()
        => WorkspaceViewTests.OnSta(() =>
        {
            using var source = new Form();
            var canvas = new Canvas(); var api = new FakeWindowApi(); var input = new FakeInputRouter();
            var clock = new FakeClock(); var dwm = new FakeDwmThumbnailApi();
            var config = new FakeAppConfig { EnableStartupIntro = false, LowQualityMode = true };
            api.AddWindow(source.Handle, 100, 100, 400, 300, title: "Notes");
            canvas.SetWindow(source.Handle, 100, 100, 400, 300);
            using var wm = new WindowManager(canvas, api, config, input, clock);
            using var overview = new OverviewManager(canvas, wm, api, input, config, gestureClock: clock, dwm: dwm);
            overview.TransitionTo(OverviewMode.Zooming);
            var pass = (OverviewOverlay)Control.FromHandle(overview.MonitorHandles[0])!;
            pass.OnMouseButtonDown!(pass, new(MouseButtons.Left, 1, 150, 150, 0));
            pass.OnMouseMoved!(pass, new(MouseButtons.Left, 0, 250, 150, 0));
            overview.TickGesturePreviews();
            Assert.True(overview.GesturePreviewsActive);
            clock.Advance(1000); overview.TickGesturePreviews();
            Assert.True(overview.GesturePreviewsActive);
            Assert.Equal(canvas.Windows[source.Handle], Assert.Single(overview.GesturePreviewCards).Bounds);
            pass.OnMouseButtonUp!(pass, new(MouseButtons.Left, 1, 250, 150, 0));
            overview.TickGesturePreviews();
            Assert.False(overview.GesturePreviewsActive); Assert.Contains(source.Handle, dwm.Sources);
            pass.OnWheel!(pass, 200, 200, -30, false, true); overview.TickGesturePreviews();
            Assert.True(overview.GesturePreviewsActive);
            config.LowQualityMode = false; overview.RefreshConfig();
            Assert.False(overview.GesturePreviewsActive); Assert.Contains(source.Handle, dwm.Sources);
            config.LowQualityMode = true; overview.RefreshConfig();
            pass.OnWheel!(pass, 200, 200, -30, false, true); overview.TickGesturePreviews();
            Assert.True(overview.GesturePreviewsActive);
            overview.TransitionTo(OverviewMode.Hidden); clock.Advance(1000); overview.TickGesturePreviews();
            Assert.False(overview.GesturePreviewsActive);
            overview.TransitionTo(OverviewMode.Zooming);
            Assert.False(overview.GesturePreviewsActive); Assert.Contains(source.Handle, dwm.Sources);
            Assert.Equal(200, canvas.Windows[source.Handle].X);
        });

    [Fact]
    public void FailedLiveRestorationRetainsATitleCardUntilRegistrationCanBeRetried()
        => WorkspaceViewTests.OnSta(() =>
        {
            using var source = new Form();
            var canvas = new Canvas(); var api = new FakeWindowApi(); var input = new FakeInputRouter();
            var clock = new FakeClock(); var dwm = new FakeDwmThumbnailApi();
            var config = new FakeAppConfig { EnableStartupIntro = false, LowQualityMode = true };
            api.AddWindow(source.Handle, 100, 100, 400, 300, title: "Notes");
            canvas.SetWindow(source.Handle, 100, 100, 400, 300);
            using var wm = new WindowManager(canvas, api, config, input, clock);
            using var overview = new OverviewManager(canvas, wm, api, input, config, gestureClock: clock, dwm: dwm);
            overview.TransitionTo(OverviewMode.Zooming);
            var pass = (OverviewOverlay)Control.FromHandle(overview.MonitorHandles[0])!;
            pass.OnWheel!(pass, 200, 200, -30, false, true); overview.TickGesturePreviews();
            dwm.FailedSources.Add(source.Handle);
            clock.Advance(180); overview.TickGesturePreviews();
            Assert.False(overview.GesturePreviewsActive);
            Assert.Equal("Notes", Assert.Single(overview.GesturePreviewCards).Title);
            dwm.FailedSources.Clear(); clock.Advance(250); overview.TickGesturePreviews();
            Assert.Contains(source.Handle, dwm.Sources); Assert.Empty(overview.GesturePreviewCards);
        });

    [Fact]
    public void PreviouslyCapturedPreviewCanBeReusedWithoutCapturingOrMinimizingDuringMotion()
        => WorkspaceViewTests.OnSta(() =>
        {
            using var source = new Form();
            var canvas = new Canvas(); var api = new FakeWindowApi(); var input = new FakeInputRouter();
            var clock = new FakeClock(); var dwm = new FakeDwmThumbnailApi(); var captures = new CaptureCounter();
            var config = new FakeAppConfig { EnableStartupIntro = false, LowQualityMode = true };
            using var previews = new WindowPreviewCache(captures, _ => new CachedSurface());
            api.AddWindow(source.Handle, 100, 100, 400, 300, title: "Notes");
            canvas.SetWindow(source.Handle, 10000, 100, 400, 300);
            using var wm = new WindowManager(canvas, api, config, input, clock,
                inspectProcess: pid => new(pid, 100, "test.exe"), previews: previews);
            wm.Tick(); clock.Advance(1500); wm.Tick();
            Assert.True(wm.CapturePreviewBeforeParking(source.Handle));
            Assert.True(SpinWait.SpinUntil(() => { previews.DrainCompleted(); return !previews.CapturePending; }, 5000));
            Assert.True(wm.HasCachedPreview(source.Handle));
            canvas.SetWindow(source.Handle, 100, 100, 400, 300);
            using var overview = new OverviewManager(canvas, wm, api, input, config, gestureClock: clock, dwm: dwm);
            overview.TransitionTo(OverviewMode.Zooming);
            var pass = (OverviewOverlay)Control.FromHandle(overview.MonitorHandles[0])!;
            pass.OnWheel!(pass, 200, 200, -30, false, true); overview.TickGesturePreviews();
            Assert.True(overview.GesturePreviewsActive);
            Assert.Contains((IntPtr)987, dwm.Sources); Assert.DoesNotContain(source.Handle, dwm.Sources);
            Assert.Equal(1d, Assert.Single(overview.GesturePreviewCards).ImageOpacity);
            clock.Advance(180); overview.TickGesturePreviews();
            Assert.Contains(source.Handle, dwm.Sources);
            overview.TransitionTo(OverviewMode.Hidden);
            Assert.True(wm.SetWindowPinnedToScreen(source.Handle, true));
            overview.TransitionTo(OverviewMode.Panning);
            Assert.True(overview.GesturePreviewsActive);
            Assert.Contains(source.Handle, dwm.Sources); Assert.DoesNotContain((IntPtr)987, dwm.Sources);
            Assert.Equal(1, captures.Calls); Assert.Empty(api.MinimizeCalls);
        });

    [Fact]
    [Trait("Category", "NativeRendering")]
    public void QualityChangesApplyToExistingPassesAndSurviveReopeningWithoutRestart()
        => WorkspaceViewTests.OnSta(() =>
        {
            Assert.True(GridRenderer.CompileShaders());
            var canvas = new Canvas(); var api = new FakeWindowApi(); var input = new FakeInputRouter();
            var config = new FakeAppConfig { EnableStartupIntro = false };
            using var wm = new WindowManager(canvas, api, config, input, new FakeClock());
            using var overview = new OverviewManager(canvas, wm, api, input, config);
            overview.TransitionTo(OverviewMode.Zooming);
            var grids = overview.MonitorHandles.Select(h => ((OverviewOverlay)Control.FromHandle(h)!).Grid!).ToArray();
            Assert.All(grids, grid => { Assert.NotNull(grid); Assert.False(grid.LowQuality); });
            config.LowQualityMode = true; overview.RefreshConfig();
            Assert.All(grids, grid => Assert.True(grid.LowQuality));
            overview.TransitionTo(OverviewMode.Hidden);
            config.LowQualityMode = false; overview.RefreshConfig();
            overview.TransitionTo(OverviewMode.Zooming);
            Assert.Equal(grids, overview.MonitorHandles.Select(h => ((OverviewOverlay)Control.FromHandle(h)!).Grid!).ToArray());
            Assert.All(grids, grid => Assert.False(grid.LowQuality));
        });
}
