using System;
using Xunit;
using CanvasDesktop;

namespace CanvasDesktop.Tests;

public class SmoothZoomTrackerTests
{
    [Fact]
    public void PushZoom_InitialState_IsNotActive()
    {
        var clock = new FakeClock();
        var tracker = new SmoothZoomTracker(clock);
        Assert.False(tracker.IsActive);
    }

    [Fact]
    public void PushZoom_AtMaxZoom_ZoomInDoesNotActivate()
    {
        var clock = new FakeClock();
        var tracker = new SmoothZoomTracker(clock);
        tracker.PushZoom(100, 100, 500, 500, OverviewCamera.ZoomMax, 1);
        Assert.False(tracker.IsActive);
        Assert.Equal(OverviewCamera.ZoomMax, tracker.TargetZoom);
    }

    [Fact]
    public void PushZoom_AtMinZoom_ZoomOutDoesNotActivate()
    {
        var clock = new FakeClock();
        var tracker = new SmoothZoomTracker(clock);
        tracker.PushZoom(100, 100, 500, 500, OverviewCamera.ZoomMin, -1);
        Assert.False(tracker.IsActive);
        Assert.Equal(OverviewCamera.ZoomMin, tracker.TargetZoom);
    }

    [Fact]
    public void PushZoom_SingleNotch_SetsTargetAndActivates()
    {
        var clock = new FakeClock();
        var tracker = new SmoothZoomTracker(clock);
        tracker.PushZoom(150, 200, 300, 400, 0.5, 1);
        Assert.True(tracker.IsActive);
        Assert.Equal(0.55, tracker.TargetZoom, 8);
        Assert.Equal(1.0, tracker.CurrentMultiplier);
        Assert.Equal((150, 200), tracker.FocalVirtual);
        Assert.Equal((300, 400), tracker.FocalWorld);
    }

    [Fact]
    public void Tick_AdvancesZoomTowardTargetAndPreservesFocalPoint()
    {
        var clock = new FakeClock(1000);
        var tracker = new SmoothZoomTracker(clock);
        tracker.PushZoom(100, 200, 400, 500, 1.0, -1);

        clock.Advance(16);
        var (vx, vy, wx, wy, zoom1, stopped1) = tracker.Tick(1.0);
        Assert.False(stopped1);
        Assert.Equal(100, vx);
        Assert.Equal(200, vy);
        Assert.Equal(400, wx);
        Assert.Equal(500, wy);
        Assert.True(zoom1 < 1.0, "zoom should have decreased toward target 0.9");
        Assert.True(zoom1 > 0.9, "zoom should not have overshot target 0.9 on first frame");

        clock.Advance(16);
        var (_, _, _, _, zoom2, stopped2) = tracker.Tick(zoom1);
        Assert.False(stopped2);
        Assert.True(zoom2 < zoom1, "zoom should continue decreasing");
        Assert.True(zoom2 > 0.9);
    }

    [Fact]
    public void Tick_SettlesAtTargetAndStops()
    {
        var clock = new FakeClock(1000);
        var tracker = new SmoothZoomTracker(clock);
        tracker.PushZoom(0, 0, 0, 0, 1.0, -1);

        double currentZoom = 1.0;
        bool stopped = false;
        for (int i = 0; i < 30; i++)
        {
            clock.Advance(16);
            var result = tracker.Tick(currentZoom);
            currentZoom = result.zoom;
            stopped = result.stopped;
            if (stopped) break;
        }

        Assert.True(stopped, "tracker should settle and stop");
        Assert.False(tracker.IsActive);
        Assert.Equal(0.9, currentZoom, 6);
    }

    [Fact]
    public void PushZoom_SlowScrolls_RemainUnaccelerated()
    {
        var clock = new FakeClock(1000);
        var tracker = new SmoothZoomTracker(clock);

        // Notch 1 at t=1000
        tracker.PushZoom(0, 0, 0, 0, 1.0, -1);
        Assert.Equal(0.90, tracker.TargetZoom, 6);
        Assert.Equal(1.0, tracker.CurrentMultiplier);

        // Advance 300ms (slow cadence, > 250ms interval)
        clock.Advance(300);
        var (_, _, _, _, zoom1, _) = tracker.Tick(1.0);

        // Notch 2 at t=1300
        tracker.PushZoom(0, 0, 0, 0, zoom1, -1);
        Assert.Equal(1.0, tracker.CurrentMultiplier);
        Assert.Equal(0.81, tracker.TargetZoom, 6);
    }

    [Fact]
    public void PushZoom_FastScrolls_AccelerateZoomSpeedLikeMouseAcceleration()
    {
        var clock = new FakeClock(1000);
        var tracker = new SmoothZoomTracker(clock);

        // Notch 1 at t=1000: base 1.0x
        tracker.PushZoom(0, 0, 0, 0, 1.0, -1);
        Assert.Equal(0.90, tracker.TargetZoom, 6);
        Assert.Equal(1.0, tracker.CurrentMultiplier);

        // Notch 2 arrives 25ms later (fast flick, cadence = 40 clicks/sec)
        clock.Advance(25);
        tracker.PushZoom(0, 0, 0, 0, 0.98, -1);

        // Multiplier should now be accelerated (> 2.0x)
        Assert.True(tracker.CurrentMultiplier > 2.0, $"Expected multiplier > 2.0, was {tracker.CurrentMultiplier}");
        // Target zoom should have dropped significantly faster than standard unaccelerated 0.81
        Assert.True(tracker.TargetZoom < 0.80, $"Expected target < 0.80 due to acceleration, was {tracker.TargetZoom}");

        // Notch 3 arrives 25ms later: streak builds further acceleration
        clock.Advance(25);
        double prevMultiplier = tracker.CurrentMultiplier;
        tracker.PushZoom(0, 0, 0, 0, 0.85, -1);
        Assert.True(tracker.CurrentMultiplier > prevMultiplier, "Consecutive fast notches should increase multiplier");
    }

    [Fact]
    public void PushZoom_ReversingDirection_ResetsAccelerationAndPivots()
    {
        var clock = new FakeClock(1000);
        var tracker = new SmoothZoomTracker(clock);

        // Fast flick zoom out: builds acceleration
        tracker.PushZoom(0, 0, 0, 0, 1.0, -1);
        clock.Advance(25);
        tracker.PushZoom(0, 0, 0, 0, 0.95, -1);
        Assert.True(tracker.CurrentMultiplier > 1.0);

        clock.Advance(25);
        var (_, _, _, _, currentZoom, _) = tracker.Tick(0.95);

        // Reverse direction: zoom in (+1 notch)
        tracker.PushZoom(0, 0, 0, 0, currentZoom, 1);

        // Multiplier should reset to 1.0 on direction reversal
        Assert.Equal(1.0, tracker.CurrentMultiplier);
        // Target should pivot from currentZoom without lurching
        Assert.True(tracker.TargetZoom > currentZoom);
        Assert.Equal(currentZoom * 1.1, tracker.TargetZoom, 5);
    }

    [Fact]
    public void PushZoom_AccelerationCappedAtMaxMultiplier()
    {
        var clock = new FakeClock(1000);
        var tracker = new SmoothZoomTracker(clock);

        // Extreme rapid scrolling (every 12ms)
        for (int i = 0; i < 20; i++)
        {
            clock.Advance(12);
            tracker.PushZoom(0, 0, 0, 0, 1.0, -1);
        }

        Assert.Equal(SmoothZoomTracker.MaxMultiplier, tracker.CurrentMultiplier);
    }

    [Fact]
    public void Cancel_ImmediatelyDeactivatesTrackerAndResetsMultiplier()
    {
        var clock = new FakeClock();
        var tracker = new SmoothZoomTracker(clock);
        tracker.PushZoom(100, 100, 200, 200, 1.0, -1);
        Assert.True(tracker.IsActive);

        tracker.Cancel();
        Assert.False(tracker.IsActive);
        Assert.Equal(1.0, tracker.CurrentMultiplier);
    }

    [Fact]
    public void PushZoom_UpdatesFocalPointWhenCursorMoves()
    {
        var clock = new FakeClock(1000);
        var tracker = new SmoothZoomTracker(clock);

        tracker.PushZoom(100, 100, 10, 20, 1.0, -1);
        Assert.Equal((100, 100), tracker.FocalVirtual);
        Assert.Equal((10, 20), tracker.FocalWorld);

        clock.Advance(20);
        tracker.PushZoom(500, 300, 50, 60, 0.98, -1);
        Assert.Equal((500, 300), tracker.FocalVirtual);
        Assert.Equal((50, 60), tracker.FocalWorld);
    }
}
