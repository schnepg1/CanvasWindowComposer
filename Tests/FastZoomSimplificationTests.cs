using System;
using System.Linq;
using System.Windows.Forms;
using CanvasDesktop;
using Xunit;

namespace CanvasDesktop.Tests;

[Collection("ShellViews")]
[Trait("Category", "NativeRendering")]
public class FastZoomSimplificationTests
{
    [Fact]
    public void ImmediateZoomAdvancesOnFirstWheelEventWithoutStall()
        => WorkspaceViewTests.OnSta(() =>
        {
            using var source = new Form();
            var canvas = new Canvas(); var api = new FakeWindowApi(); var input = new FakeInputRouter();
            var clock = new FakeClock(1000); var dwm = new FakeDwmThumbnailApi();
            var config = new FakeAppConfig { EnableStartupIntro = false, LowQualityMode = false, SmoothZoom = true };
            api.AddWindow(source.Handle, 100, 100, 400, 300, title: "App");
            canvas.SetWindow(source.Handle, 100, 100, 400, 300);
            using var wm = new WindowManager(canvas, api, config, input, clock);
            using var overview = new OverviewManager(canvas, wm, api, input, config, gestureClock: clock, dwm: dwm);
            overview.TransitionTo(OverviewMode.Zooming);
            var pass = (OverviewOverlay)Control.FromHandle(overview.MonitorHandles[0])!;

            double initialZoom = overview.ViewCamera.Zoom;

            // 1 single wheel notch (-120 delta) at t=1000
            pass.OnWheel!(pass, 200, 200, -120, false, false, false);

            // On the exact message of the first wheel notch (0ms elapsed), smooth zoom is active
            // and the camera has ALREADY advanced immediately toward the target
            Assert.True(overview.SmoothZoomActive);
            Assert.True(overview.IsZoomSettling);
            Assert.NotEqual(initialZoom, overview.ViewCamera.Zoom);
            Assert.True(overview.ViewCamera.Zoom < initialZoom); // zoomed out
        });

    [Fact]
    public void LiveWindowsNeverReplacedWithTextCardsDuringFastZoomInNormalQuality()
        => WorkspaceViewTests.OnSta(() =>
        {
            using var source = new Form();
            var canvas = new Canvas(); var api = new FakeWindowApi(); var input = new FakeInputRouter();
            var clock = new FakeClock(1000); var dwm = new FakeDwmThumbnailApi();
            var config = new FakeAppConfig { EnableStartupIntro = false, LowQualityMode = false, SmoothZoom = true };
            api.AddWindow(source.Handle, 100, 100, 400, 300, title: "App");
            canvas.SetWindow(source.Handle, 100, 100, 400, 300);
            using var wm = new WindowManager(canvas, api, config, input, clock);
            using var overview = new OverviewManager(canvas, wm, api, input, config, gestureClock: clock, dwm: dwm);
            overview.TransitionTo(OverviewMode.Zooming);
            var pass = (OverviewOverlay)Control.FromHandle(overview.MonitorHandles[0])!;

            // Fast scroll burst: multiple notches within 50ms
            pass.OnWheel!(pass, 200, 200, -120, false, false, false);
            clock.Advance(25);
            pass.OnWheel!(pass, 200, 200, -120, false, false, false);

            // In normal quality, live windows must REMAIN live throughout zooming!
            // No text-card simplification, no visual flickering.
            Assert.False(overview.GesturePreviewsActive);
            Assert.Empty(overview.GesturePreviewCards);
            Assert.Contains(source.Handle, dwm.VisibleSources);

            // Automatic parking is suspended during zoom motion
            Assert.True(wm.SuspendAutomaticParking);

            // Finish animation and settle after 180ms
            clock.Advance(250);
            overview.AdvanceSmoothZoom(clock.TickCount64);
            clock.Advance(180);
            overview.TickGesturePreviews();

            // Settle restores normal state and automatic parking
            Assert.False(overview.IsZoomSettling);
            Assert.False(wm.SuspendAutomaticParking);
            Assert.Contains(source.Handle, dwm.VisibleSources);
        });

    [Fact]
    public void ThresholdCardModeSwitchingIsDeferredWhileZooming()
        => WorkspaceViewTests.OnSta(() =>
        {
            using var source = new Form();
            var canvas = new Canvas(); var api = new FakeWindowApi(); var input = new FakeInputRouter();
            var clock = new FakeClock(1000); var dwm = new FakeDwmThumbnailApi();
            var config = new FakeAppConfig
            {
                EnableStartupIntro = false,
                LowQualityMode = false,
                SmoothZoom = true,
                OverviewCardEnterZoom = 0.40,
                OverviewCardExitZoom = 0.50
            };
            api.AddWindow(source.Handle, 100, 100, 400, 300, title: "App");
            canvas.SetWindow(source.Handle, 100, 100, 400, 300);
            using var wm = new WindowManager(canvas, api, config, input, clock);
            using var overview = new OverviewManager(canvas, wm, api, input, config, gestureClock: clock, dwm: dwm);
            overview.TransitionTo(OverviewMode.Zooming);
            var pass = (OverviewOverlay)Control.FromHandle(overview.MonitorHandles[0])!;

            // Rapidly scroll out across the 0.40 card threshold
            for (int i = 0; i < 4; i++)
            {
                pass.OnWheel!(pass, 200, 200, -120, false, false, false);
                clock.Advance(25);
            }

            // Smooth zoom is active and settling
            Assert.True(overview.SmoothZoomActive);
            Assert.True(overview.IsZoomSettling);
            Assert.True(wm.SuspendAutomaticParking);

            // Live windows are not replaced by text cards during active motion
            Assert.False(overview.GesturePreviewsActive);

            // Finish animation and settle
            clock.Advance(250);
            overview.AdvanceSmoothZoom(clock.TickCount64);
            clock.Advance(180);
            overview.TickGesturePreviews();

            // Settled: motion ended, parking resumed
            Assert.False(overview.IsZoomSettling);
            Assert.False(wm.SuspendAutomaticParking);
        });

    [Fact]
    public void MiddleMousePanTracksInstantlyAndReconcilesOnRelease()
        => WorkspaceViewTests.OnSta(() =>
        {
            using var source = new Form();
            var canvas = new Canvas(); var api = new FakeWindowApi(); var input = new FakeInputRouter();
            var clock = new FakeClock(1000); var dwm = new FakeDwmThumbnailApi();
            var config = new FakeAppConfig { EnableStartupIntro = false, LowQualityMode = false };
            api.AddWindow(source.Handle, 100, 100, 400, 300, title: "App");
            canvas.SetWindow(source.Handle, 100, 100, 400, 300);
            using var wm = new WindowManager(canvas, api, config, input, clock);
            using var overview = new OverviewManager(canvas, wm, api, input, config, gestureClock: clock, dwm: dwm);
            overview.TransitionTo(OverviewMode.Zooming);
            var pass = (OverviewOverlay)Control.FromHandle(overview.MonitorHandles[0])!;

            double initialCamX = overview.ViewCamera.X;
            double initialCamY = overview.ViewCamera.Y;

            // Begin middle mouse pan at (200, 200)
            pass.OnMouseButtonDown!(pass, new MouseEventArgs(MouseButtons.Middle, 1, 200, 200, 0));
            Assert.True(overview.MotionActive);

            // Move by (50, 30) - camera should pan immediately
            pass.OnMouseMoved!(pass, new MouseEventArgs(MouseButtons.Middle, 0, 250, 230, 0));
            Assert.NotEqual(initialCamX, overview.ViewCamera.X);
            Assert.NotEqual(initialCamY, overview.ViewCamera.Y);
            Assert.True(overview.MotionActive);

            // Mouse stops moving for 50ms before releasing middle button
            clock.Advance(50);
            pass.OnMouseButtonUp!(pass, new MouseEventArgs(MouseButtons.Middle, 1, 250, 230, 0));

            // Pan ended: motion is no longer active, no phantom inertia coasting
            Assert.False(overview.MotionActive);
            Assert.False(overview.MomentumActive);
        });
}
