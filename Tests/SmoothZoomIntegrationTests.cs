using System;
using System.Windows.Forms;
using CanvasDesktop;
using Xunit;

namespace CanvasDesktop.Tests;

[Collection("ShellViews")]
[Trait("Category", "NativeRendering")]
public class SmoothZoomIntegrationTests
{
    private static void Run(Action<Canvas, FakeWindowApi, FakeInputRouter, FakeClock, FakeAppConfig, WindowManager, OverviewManager, OverviewOverlay> check)
        => WorkspaceViewTests.OnSta(() =>
        {
            var canvas = new Canvas(); var api = new FakeWindowApi(); var input = new FakeInputRouter();
            var clock = new FakeClock(1000); var config = new FakeAppConfig { EnableStartupIntro = false, SmoothZoom = true };
            using var wm = new WindowManager(canvas, api, config, input, clock);
            using var overview = new OverviewManager(canvas, wm, api, input, config, gestureClock: clock, dwm: new FakeDwmThumbnailApi());
            overview.TransitionTo(OverviewMode.Zooming);
            var pass = (OverviewOverlay)Control.FromHandle(overview.MonitorHandles[0])!;
            check(canvas, api, input, clock, config, wm, overview, pass);
        });

    [Fact]
    public void WheelZoom_SmoothZoomEnabled_AnimatesAcrossFrames()
        => Run((canvas, api, input, clock, config, wm, overview, pass) =>
        {
            config.SmoothZoom = true;
            var before = overview.ViewCamera;

            // Scroll down 1 notch (-120)
            pass.OnWheel!(pass, 300, 300, -120, false, false, false);
            Assert.True(overview.SmoothZoomActive, "Smooth zoom should be active after wheel scroll");
            Assert.Equal(before.Zoom * 0.9, overview.SmoothZoomTarget, 5);

            // Advance clock by 16ms and tick animation
            clock.Advance(16);
            overview.AdvanceSmoothZoom(clock.TickCount64);
            var frame1 = overview.ViewCamera;
            Assert.True(frame1.Zoom < before.Zoom, "Zoom should have started decreasing");
            Assert.True(frame1.Zoom > overview.SmoothZoomTarget, "Zoom should not jump to target immediately");

            // Advance to completion
            for (int i = 0; i < 30; i++)
            {
                clock.Advance(16);
                overview.AdvanceSmoothZoom(clock.TickCount64);
                if (!overview.SmoothZoomActive) break;
            }

            Assert.False(overview.SmoothZoomActive, "Smooth zoom should complete");
            Assert.Equal(before.Zoom * 0.9, overview.ViewCamera.Zoom, 5);
        });

    [Fact]
    public void WheelZoom_SmoothZoomDisabled_SnapsImmediately()
        => Run((canvas, api, input, clock, config, wm, overview, pass) =>
        {
            config.SmoothZoom = false;
            var before = overview.ViewCamera;

            // Scroll down 1 notch (-120)
            pass.OnWheel!(pass, 300, 300, -120, false, false, false);
            Assert.False(overview.SmoothZoomActive, "Smooth zoom should NOT be active when disabled");
            Assert.Equal(before.Zoom * 0.9, overview.ViewCamera.Zoom, 5);
        });

    [Fact]
    public void PanGesture_CancelsActiveSmoothZoom()
        => Run((canvas, api, input, clock, config, wm, overview, pass) =>
        {
            config.SmoothZoom = true;
            pass.OnWheel!(pass, 300, 300, -120, false, false, false);
            Assert.True(overview.SmoothZoomActive);

            // Start pan gesture with middle click
            pass.OnMouseButtonDown!(pass, new(MouseButtons.Middle, 1, 500, 500, 0));
            Assert.False(overview.SmoothZoomActive, "Middle-click pan should cancel smooth zoom");
        });

    [Fact]
    public void CursorFocalPoint_RemainsPinnedDuringSmoothZoom()
        => Run((canvas, api, input, clock, config, wm, overview, pass) =>
        {
            config.SmoothZoom = true;
            int cursorX = 400, cursorY = 300;
            var before = overview.ViewCamera;
            double focalWx = before.X + cursorX / before.Zoom;
            double focalWy = before.Y + cursorY / before.Zoom;

            pass.OnWheel!(pass, cursorX, cursorY, -120, false, false, false);

            for (int i = 0; i < 20; i++)
            {
                clock.Advance(16);
                overview.AdvanceSmoothZoom(clock.TickCount64);

                var cam = overview.ViewCamera;
                double currentWx = cam.X + cursorX / cam.Zoom;
                double currentWy = cam.Y + cursorY / cam.Zoom;

                Assert.InRange(Math.Abs(focalWx - currentWx), 0, 1e-4);
                Assert.InRange(Math.Abs(focalWy - currentWy), 0, 1e-4);

                if (!overview.SmoothZoomActive) break;
            }
        });

    [Fact]
    public void TouchpadPinch_SmoothZoomEnabled_AnimatesZoomTowardCursorFocalPoint()
        => Run((canvas, api, input, clock, config, wm, overview, pass) =>
        {
            config.SmoothZoom = true;
            var before = overview.ViewCamera;

            // Touchpad pinch gesture: isTouchpad = true, control = true, delta = -120
            pass.OnWheel!(pass, 300, 300, -120, false, true, false, isTouchpad: true);
            Assert.True(overview.SmoothZoomActive, "Touchpad pinch should activate smooth zoom");
            Assert.Equal(before.Zoom * 0.9, overview.SmoothZoomTarget, 5);

            // Animate
            clock.Advance(16);
            overview.AdvanceSmoothZoom(clock.TickCount64);
            Assert.True(overview.ViewCamera.Zoom < before.Zoom);
        });

    [Fact]
    public void TouchpadScroll_PansCameraSmoothly()
        => Run((canvas, api, input, clock, config, wm, overview, pass) =>
        {
            var before = overview.ViewCamera;

            // Touchpad two-finger vertical scroll: isTouchpad = true, control = false, delta = 50
            pass.OnWheel!(pass, 300, 300, 50, false, false, false, isTouchpad: true);
            Assert.False(overview.SmoothZoomActive, "Touchpad 2-finger scroll should NOT activate zoom");
            Assert.Equal(before.Zoom, overview.ViewCamera.Zoom, 5);
            Assert.NotEqual(before.Y, overview.ViewCamera.Y);
        });

    [Fact]
    public void ZoomAcceleration_WhenDisabledInConfig_ExecutesConstantSpeedSteps()
        => Run((canvas, api, input, clock, config, wm, overview, pass) =>
        {
            config.SmoothZoom = true;
            config.ZoomAcceleration = false;
            overview.RefreshConfig();

            var before = overview.ViewCamera;

            // Notch 1
            pass.OnWheel!(pass, 300, 300, -120, false, false, false);
            double expectedTarget1 = before.Zoom * 0.90;
            Assert.Equal(expectedTarget1, overview.SmoothZoomTarget, 5);

            // Fast consecutive notch at dt = 25ms
            clock.Advance(25);
            pass.OnWheel!(pass, 300, 300, -120, false, false, false);

            // When acceleration is disabled, target should be exactly expectedTarget1 * 0.90
            double expectedTarget2 = expectedTarget1 * 0.90;
            Assert.Equal(expectedTarget2, overview.SmoothZoomTarget, 5);
        });
}
