using System;
using Xunit;
using CanvasDesktop;

namespace CanvasDesktop.Tests;

public class InertiaTrackerTests
{
    [Fact]
    public void Release_NoSamples_ReturnsFalse()
    {
        var clock = new FakeClock();
        var tracker = new InertiaTracker(clock);
        Assert.False(tracker.Release());
        Assert.False(tracker.IsActive);
    }

    [Fact]
    public void Release_SingleSample_ReturnsFalse()
    {
        var clock = new FakeClock();
        var tracker = new InertiaTracker(clock);
        tracker.RecordDelta(10, 0);
        Assert.False(tracker.Release());
    }

    [Fact]
    public void Release_FastSamples_StartsInertia()
    {
        var clock = new FakeClock();
        var tracker = new InertiaTracker(clock);

        // 10px every 16ms -> ~0.625 px/ms, well above 0.02 stop threshold
        for (int i = 0; i < 5; i++)
        {
            tracker.RecordDelta(10, 5);
            clock.Advance(16);
        }

        Assert.True(tracker.Release());
        Assert.True(tracker.IsActive);
    }

    [Fact]
    public void Release_StaleSamplesOutsideWindow_ReturnsFalse()
    {
        var clock = new FakeClock();
        var tracker = new InertiaTracker(clock);

        tracker.RecordDelta(10, 10);
        tracker.RecordDelta(10, 10);
        // Velocity window is 100ms; jumping forward 500ms drops both samples.
        clock.Advance(500);

        Assert.False(tracker.Release());
        Assert.False(tracker.IsActive);
    }

    [Fact]
    public void Release_SlowSamples_BelowThreshold_ReturnsFalse()
    {
        var clock = new FakeClock();
        var tracker = new InertiaTracker(clock);

        // 1px every 100ms -> 0.01 px/ms, below 0.02 stop threshold
        tracker.RecordDelta(1, 0);
        clock.Advance(100);
        tracker.RecordDelta(1, 0);
        clock.Advance(100);

        Assert.False(tracker.Release());
    }

    [Fact]
    public void Tick_BeforeRelease_ReturnsZeroAndInactive()
    {
        var clock = new FakeClock();
        var tracker = new InertiaTracker(clock);
        var (dx, dy, stopped) = tracker.Tick();
        Assert.Equal(0, dx);
        Assert.Equal(0, dy);
        Assert.False(stopped);
    }

    [Fact]
    public void Tick_DecaysVelocityOverTime()
    {
        var clock = new FakeClock();
        var tracker = new InertiaTracker(clock);

        // Build up strong velocity: 30px/16ms ~= 1.875 px/ms
        for (int i = 0; i < 5; i++)
        {
            tracker.RecordDelta(30, 0);
            clock.Advance(16);
        }
        Assert.True(tracker.Release());

        clock.Advance(16);
        var first = tracker.Tick();

        clock.Advance(16);
        var second = tracker.Tick();

        // Decay is multiplicative — second tick must move less than the first.
        Assert.True(Math.Abs(second.dx) < Math.Abs(first.dx),
            $"expected decay: first={first.dx} second={second.dx}");
    }

    [Fact]
    public void Tick_EventuallyStops()
    {
        var clock = new FakeClock();
        var tracker = new InertiaTracker(clock);

        for (int i = 0; i < 5; i++)
        {
            tracker.RecordDelta(20, 0);
            clock.Advance(16);
        }
        Assert.True(tracker.Release());

        bool stopped = false;
        for (int i = 0; i < 1000 && !stopped; i++)
        {
            clock.Advance(16);
            stopped = tracker.Tick().stopped;
        }

        Assert.True(stopped, "inertia never decayed below stop threshold");
        Assert.False(tracker.IsActive);
    }

    [Fact]
    public void Cancel_StopsInertiaImmediately()
    {
        var clock = new FakeClock();
        var tracker = new InertiaTracker(clock);

        for (int i = 0; i < 5; i++)
        {
            tracker.RecordDelta(20, 0);
            clock.Advance(16);
        }
        Assert.True(tracker.Release());
        Assert.True(tracker.IsActive);

        tracker.Cancel();

        Assert.False(tracker.IsActive);
        var (dx, dy, _) = tracker.Tick();
        Assert.Equal(0, dx);
        Assert.Equal(0, dy);
    }

    [Fact]
    public void RecordDelta_BeyondSampleWindow_TrimsOldest()
    {
        var clock = new FakeClock();
        var tracker = new InertiaTracker(clock);

        // Push 30 samples — old ones beyond SampleWindow=15 get dropped, but the
        // logic still releases on the recent ones.
        for (int i = 0; i < 30; i++)
        {
            tracker.RecordDelta(15, 0);
            clock.Advance(8);
        }

        Assert.True(tracker.Release());
    }
    [Theory]
    [InlineData(7)]
    [InlineData(16)]
    [InlineData(100)]
    [InlineData(2000)]
    public void FrameRateAndStallsDoNotChangeFinalDistance(int interval)
    {
        double Coast(int step)
        {
            var clock = new FakeClock(); var tracker = new InertiaTracker(clock);
            tracker.Begin(0); tracker.RecordDelta(40, 0, 20); tracker.RecordDelta(40, 0, 40);
            clock.Now = 40; Assert.True(tracker.Release(40));
            double distance = 0;
            while (tracker.IsActive) { clock.Advance(step); distance += tracker.Tick().dx; }
            return distance;
        }
        Assert.Equal(Coast(1), Coast(interval), 8);
    }

    [Fact]
    public void DelayedInputUsesEventTimeAndStationaryReleaseDoesNotCoast()
    {
        var clock = new FakeClock(500); var tracker = new InertiaTracker(clock);
        tracker.Begin(0); tracker.RecordDelta(30, 0, 16); tracker.RecordDelta(30, 0, 32);
        Assert.True(tracker.Release(40)); // UI processed all events much later.
        clock.Advance(16); Assert.True(tracker.Tick().dx > 0);
        tracker.Begin(600); tracker.RecordDelta(30, 0, 616);
        Assert.False(tracker.Release(800)); // Intentional pause before release.
    }

    [Fact]
    public void FinalTickReturnsItsFractionalDisplacementAndNewGestureClearsVelocity()
    {
        var clock = new FakeClock(); var tracker = new InertiaTracker(clock);
        tracker.Begin(); tracker.RecordDelta(1, 0, 20); clock.Now = 20;
        Assert.True(tracker.Release());
        clock.Advance(1000); var final = tracker.Tick();
        Assert.True(final.stopped); Assert.True(final.dx > 0);
        Assert.NotEqual(Math.Round(final.dx), final.dx);
        Assert.False(tracker.IsActive); Assert.Equal(0, tracker.Tick().dx);
        tracker.Begin(); Assert.False(tracker.Release());
    }

    [Fact]
    public void DuplicateTimestampsCannotProduceInfiniteVelocityOrPhantomTime()
    {
        var clock = new FakeClock(10); var tracker = new InertiaTracker(clock);
        tracker.Begin(10); tracker.RecordDelta(20, 10, 10); tracker.RecordDelta(20, 10, 10);
        Assert.False(tracker.Release(10));
        tracker.Begin(0); tracker.RecordDelta(20, 10, 10); tracker.RecordDelta(20, 10, 10);
        Assert.True(tracker.Release(10));
        Assert.Equal(0, tracker.Tick().dx); // No elapsed time means no displacement.
        clock.Advance(1); Assert.True(double.IsFinite(tracker.Tick().dx));
    }

    [Fact]
    public void LongConstantSpeedGestureHasSameCoastAtRawAndPointerCadences()
    {
        double FirstStep(int cadence)
        {
            var clock = new FakeClock(200); var tracker = new InertiaTracker(clock);
            tracker.Begin(0);
            for (int time = cadence; time <= 200; time += cadence) tracker.RecordDelta(2 * cadence, 0, time);
            Assert.True(tracker.Release(200)); clock.Advance(16); return tracker.Tick().dx;
        }
        Assert.Equal(FirstStep(1), FirstStep(20), 8);
    }

}
