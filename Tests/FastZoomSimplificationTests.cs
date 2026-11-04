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
    public void SlowZoomRetainsLivePreviewsInNormalQuality()
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

            // 1 slow notch (-120 delta) at t=1000
            pass.OnWheel!(pass, 200, 200, -120, false, false, false);
            overview.TickGesturePreviews();

            // In normal quality, a single slow notch does not activate gesture preview simplification
            Assert.False(overview.FastZoomActive);
            Assert.False(overview.GesturePreviewsActive);
            Assert.Contains(source.Handle, dwm.VisibleSources);
        });

    [Fact]
    public void FastZoomWheelBurstActivatesSimplificationAndSuspendsParkingInNormalQuality()
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

            // Notch 1 at t=1000
            pass.OnWheel!(pass, 200, 200, -120, false, false, false);

            // Notch 2 arrives 25ms later (fast scroll flick, cadence = 40 notches/sec)
            clock.Advance(25);
            pass.OnWheel!(pass, 200, 200, -120, false, false, false);
            overview.TickGesturePreviews();

            // Fast zoom acceleration must activate gesture previews even in normal quality!
            Assert.True(overview.FastZoomActive);
            Assert.True(overview.GesturePreviewsActive);
            Assert.True(wm.SuspendAutomaticParking);

            // DWM live source is suppressed/simplified into lightweight gesture cards during motion
            Assert.DoesNotContain(source.Handle, dwm.VisibleSources);
            var card = Assert.Single(overview.GesturePreviewCards);
            Assert.Equal("App", card.Title);

            // Advance through smooth zoom animation and wait for the 180ms settle boundary
            clock.Advance(180);
            overview.TickGesturePreviews();

            // Settle restores normal rendering and automatic parking
            Assert.False(overview.GesturePreviewsActive);
            Assert.False(wm.SuspendAutomaticParking);
            Assert.Contains(source.Handle, dwm.VisibleSources);
        });

    [Fact]
    public void FastZoomOutAcrossCardThresholdDefersCardModeUntilSettled()
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
            overview.TickGesturePreviews();

            // Fast zoom is active and target zoom is deep in card territory
            Assert.True(overview.FastZoomActive);
            Assert.True(overview.GesturePreviewsActive);
            Assert.True(wm.SuspendAutomaticParking);

            // While animating, lightweight gesture cards represent the window
            Assert.Single(overview.GesturePreviewCards);

            // Finish animation and settle
            clock.Advance(250);
            overview.AdvanceSmoothZoom(clock.TickCount64);
            clock.Advance(180);
            overview.TickGesturePreviews();

            // Settled: motion ended, parking resumed
            Assert.False(overview.GesturePreviewsActive);
            Assert.False(wm.SuspendAutomaticParking);
        });
}
