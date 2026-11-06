using System;
using System.Linq;
using CanvasDesktop;
using Xunit;

namespace CanvasDesktop.Tests;

public class StartupIntroFlightTests
{
    private static readonly CanvasCamera Saved = new(123.25, -456.75, 0.85);
    private static readonly ScreenRect Screen = new(0, 0, 1920, 1080);
    private static readonly WorldRect[] Nearby =
    [
        new() { X = 1500, Y = 100, W = 800, H = 600 },
        new() { X = 700, Y = 250, W = 500, H = 400 }
    ];

    private static (double X, double Y) Center(StartupIntroFrame frame, ScreenRect? screen = null)
    {
        var s = screen ?? Screen;
        return (frame.X + (s.X + s.Width / 2.0) / frame.Zoom,
            frame.Y + (s.Y + s.Height / 2.0) / frame.Zoom);
    }

    [Fact]
    public void StartAndFinishPreserveExactSavedCameraAndSamplingHasNoState()
    {
        var flight = new StartupIntroFlight(Saved, Screen, Nearby);
        foreach (double time in new[] { -1, 0, flight.DurationSeconds, flight.DurationSeconds + 1, double.PositiveInfinity })
        {
            var frame = flight.Sample(time);
            Assert.Equal(Saved, new CanvasCamera(frame.X, frame.Y, frame.Zoom));
            Assert.Equal(time >= flight.DurationSeconds, frame.Complete);
        }
        Assert.Equal(flight.Sample(flight.DurationSeconds), flight.Sample(double.PositiveInfinity));
        Assert.Equal(flight.Sample(1.7), new StartupIntroFlight(Saved, Screen, Nearby).Sample(1.7));
    }

    [Fact]
    public void TakeoffStartsAtRestAndAcceleratesGraduallyAfterTheReveal()
    {
        var flight = new StartupIntroFlight(Saved, Screen, Nearby);
        var initial = flight.Sample(0.6);
        Assert.Equal(Saved, new CanvasCamera(initial.X, initial.Y, initial.Zoom));
        var early = flight.Sample(0.61);
        Assert.InRange(Math.Abs(early.X - initial.X), 0, 0.01);
        Assert.InRange(Math.Abs(early.Y - initial.Y), 0, 0.01);
        Assert.InRange(Math.Abs(early.Zoom - initial.Zoom), 0, 0.00001);
        // At the old takeoff's completion time we are still gently pulling away.
        Assert.InRange(flight.Sample(1.4).Zoom, 0.65, Saved.Zoom);
        Assert.True(flight.Sample(3).Zoom < flight.Sample(1.4).Zoom);
    }

    [Theory]
    [InlineData(0.05)]
    [InlineData(0.15)]
    [InlineData(0.2)]
    [InlineData(0.85)]
    public void ZoomRemainsWithinLimitsAndNeverZoomsCloserThanTheSavedView(double zoom)
    {
        var flight = new StartupIntroFlight(Saved with { Zoom = zoom }, Screen, Nearby);
        for (double t = 0; t <= flight.DurationSeconds; t += 0.01)
            Assert.InRange(flight.Sample(t).Zoom, OverviewCamera.ZoomMin, zoom + 1e-12);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1920, -1080)]
    [InlineData(-3000, 700)]
    public void EmptyCanvasSpiralStaysNearTheRestoredViewOnAnyMonitor(int originX, int originY)
    {
        var screen = new ScreenRect(originX, originY, 1920, 1080);
        var flight = new StartupIntroFlight(Saved, screen, []);
        Assert.Equal(StartupIntroFlight.MinimumDurationSeconds, flight.DurationSeconds, 10);
        var origin = Center(flight.Sample(0), screen);
        var centers = Enumerable.Range(0, 1201).Select(i => Center(flight.Sample(i / 100.0), screen)).ToArray();
        Assert.All(centers, center =>
        {
            Assert.InRange(Math.Abs(center.X - origin.X), 0, 2400);
            Assert.InRange(Math.Abs(center.Y - origin.Y), 0, 1400);
        });
        Assert.True(centers.Max(p => p.X) - centers.Min(p => p.X) > 2000);
        Assert.True(centers.Max(p => p.Y) - centers.Min(p => p.Y) > 1000);
    }

    [Fact]
    public void SpiralTurnsOutwardThenInward()
    {
        var origin = new StartupIntroRoute.Point(0, 0);
        var route = new StartupIntroRoute(origin, origin, [], null, 1920, 1080, 11.4);
        var previous = origin;
        double previousRadius = 0, totalTurn = 0;
        for (int i = 1; i < 100; i++)
        {
            var point = route.Sample(route.Duration * i / 100, 1, 0);
            var normalized = new StartupIntroRoute.Point(point.X / 1920, point.Y / 1080);
            double radius = Math.Sqrt(normalized.X * normalized.X + normalized.Y * normalized.Y);
            Assert.True(i <= 50 ? radius > previousRadius : radius < previousRadius);
            double cross = previous.X * normalized.Y - previous.Y * normalized.X;
            double dot = previous.X * normalized.X + previous.Y * normalized.Y;
            totalTurn += Math.Atan2(cross, dot);
            previous = normalized;
            previousRadius = radius;
        }
        Assert.InRange(totalTurn, 1.9 * Math.Tau, 2 * Math.Tau);
    }

    [Fact]
    public void NearbyContentStaysInViewDuringCruiseEvenWithDistantWindowsAndRegions()
    {
        WorldRect nearby = new() { X = 5300, Y = 800, W = 500, H = 400 };
        WorldRect farWindow = new() { X = 100000, W = 800, H = 600 };
        WorldRect farRegion = new() { X = -100000, W = 50000, H = 50000 };
        var local = new StartupIntroFlight(Saved, Screen, [nearby]);
        var spreadOut = new StartupIntroFlight(Saved, Screen, [nearby, farWindow], projectRegions: [farRegion]);
        Assert.Equal(local.DurationSeconds, spreadOut.DurationSeconds);
        for (double t = 0; t <= local.DurationSeconds; t += .01)
        {
            var frame = spreadOut.Sample(t);
            Assert.Equal(local.Sample(t), frame);
            if (t < 3.8 || t > 8.8) continue;
            Assert.InRange((nearby.X + nearby.W / 2 - frame.X) * frame.Zoom, 0, Screen.Width);
            Assert.InRange((nearby.Y + nearby.H / 2 - frame.Y) * frame.Zoom, 0, Screen.Height);
        }
    }

    [Fact]
    public void EnumerationOrderAndDuplicateWindowsCannotChangeTheTour()
    {
        WorldRect[] regions = [new() { X = -8000, Y = 4000, W = 2000, H = 1500 },
            new() { X = 14000, Y = -3000, W = 2000, H = 1500 }];
        var a = new StartupIntroFlight(Saved, Screen, Nearby, projectRegions: regions);
        var b = new StartupIntroFlight(Saved, Screen, Nearby.Reverse().Concat(Nearby).ToArray(),
            projectRegions: regions.Reverse().ToArray());
        Assert.Equal(a.DurationSeconds, b.DurationSeconds);
        for (double t = 0; t < a.DurationSeconds; t += .13) Assert.Equal(a.Sample(t), b.Sample(t));
    }

    [Theory]
    [InlineData(-1920, -1080)]
    [InlineData(0, 0)]
    public void AbortingOnAnyMonitorCanLandOnADifferentTutorialTarget(int screenX, int screenY)
    {
        var target = new CanvasCamera(8000, -2500, .95);
        var flight = new StartupIntroFlight(Saved, new(screenX, screenY, 1920, 1080), Nearby, landing: target);
        var landing = flight.CreateLanding(5);
        Assert.Equal(flight.Sample(5), landing.Sample(0));
        var frame = landing.Sample(StartupIntroLanding.DurationSeconds);
        Assert.Equal(target, new CanvasCamera(frame.X, frame.Y, frame.Zoom));
        var almost = landing.Sample(StartupIntroLanding.DurationSeconds - .0001);
        Assert.InRange(Math.Abs(almost.X - target.X) + Math.Abs(almost.Y - target.Y), 0, .001);
    }

    [Fact]
    public void SpiralKeepsCruiseZoomAndBreathingSmallAndSlow()
    {
        var flight = new StartupIntroFlight(Saved, Screen, Nearby);
        const double h = .01;
        int crossings = 0;
        for (double t = 3.8 + h; t < 8.8 - h; t += h)
        {
            var before = flight.Sample(t - h);
            var frame = flight.Sample(t);
            var after = flight.Sample(t + h);
            double logBefore = Math.Log(before.Zoom / .2), logZoom = Math.Log(frame.Zoom / .2);
            double logAfter = Math.Log(after.Zoom / .2);
            Assert.InRange(Math.Abs(logZoom), 0, .00601); // Less than 0.61% in either direction.
            Assert.InRange(Math.Abs((logAfter - logBefore) / (2 * h)), 0, .003);
            Assert.InRange(Math.Abs((logAfter - 2 * logZoom + logBefore) / (h * h)), 0, .0015);
            if (logBefore * logZoom < 0)
            {
                Assert.InRange(t, 7.6, 7.6 + h);
                crossings++;
            }
        }
        Assert.Equal(1, crossings);
    }

    [Fact]
    public void EntireTourHasContinuousVelocityIncludingRollingCurvesAndStageJoins()
    {
        var flight = new StartupIntroFlight(Saved, Screen, Nearby);
        const double h = .0001;
        for (double t = 0; t <= flight.DurationSeconds + h; t += .01)
        {
            var before = flight.Sample(t - h); var at = flight.Sample(t); var after = flight.Sample(t + h);
            Assert.InRange(Math.Abs((after.X - 2 * at.X + before.X) / h), 0, 2);
            Assert.InRange(Math.Abs((after.Y - 2 * at.Y + before.Y) / h), 0, 2);
            Assert.InRange(Math.Abs((after.Zoom - 2 * at.Zoom + before.Zoom) / h), 0, .001);
        }
    }

    [Theory]
    [InlineData(0.2)]
    [InlineData(0.61)]
    [InlineData(1.8)]
    [InlineData(4.5)]
    [InlineData(8)]
    [InlineData(11.9)]
    public void AbortingJoinsTheCurrentMotionAndLandsExactlyWithoutASnap(double time)
    {
        var flight = new StartupIntroFlight(Saved, Screen, Nearby);
        var landing = flight.CreateLanding(time);
        Assert.Equal(flight.Sample(time), landing.Sample(0));
        const double h = .0001;
        var before = flight.Sample(time - h); var at = landing.Sample(0); var after = landing.Sample(h);
        Assert.InRange(Math.Abs((after.X - 2 * at.X + before.X) / h), 0, 2);
        Assert.InRange(Math.Abs((after.Y - 2 * at.Y + before.Y) / h), 0, 2);
        Assert.InRange(Math.Abs((after.Zoom - 2 * at.Zoom + before.Zoom) / h), 0, .001);
        var finish = landing.Sample(StartupIntroLanding.DurationSeconds);
        Assert.True(finish.Complete);
        Assert.Equal(Saved, new CanvasCamera(finish.X, finish.Y, finish.Zoom));
        Assert.Equal(finish, landing.Sample(double.PositiveInfinity));
        var almost = landing.Sample(StartupIntroLanding.DurationSeconds - h);
        Assert.InRange(Math.Abs(almost.X - finish.X) + Math.Abs(almost.Y - finish.Y), 0, .001);
        for (double t = 0; t < StartupIntroLanding.DurationSeconds; t += .01)
        {
            var frame = landing.Sample(t);
            Assert.True(double.IsFinite(frame.X) && double.IsFinite(frame.Y));
            Assert.InRange(frame.Zoom, OverviewCamera.ZoomMin, OverviewCamera.ZoomMax);
        }
    }

    [Fact]
    public void PinnedAndInvalidContentDoesNotChangeTheEmptySweep()
    {
        WorldRect[] ignored = [new() { X = 1500, W = 800, H = 600, PinnedToScreen = true },
            new() { X = 1e20, Y = 1e20, W = 800, H = 600 },
            new() { X = double.NaN, W = 800, H = 600 },
            new() { Y = double.PositiveInfinity, W = 800, H = 600 },
            new() { W = -100, H = 600 }, new() { W = 800, H = double.PositiveInfinity },
            new() { X = double.MaxValue, W = double.MaxValue, H = 1 }];
        var flight = new StartupIntroFlight(Saved, Screen, ignored, projectRegions: ignored);
        var empty = new StartupIntroFlight(Saved, Screen, []);
        Assert.Equal(empty.DurationSeconds, flight.DurationSeconds);
        for (double t = 0; t < empty.DurationSeconds; t += .1) Assert.Equal(empty.Sample(t), flight.Sample(t));
    }

    [Fact]
    public void InvalidTimesAndMonitorDimensionsAreSanitizedToFiniteFrames()
    {
        var flight = new StartupIntroFlight(Saved, new ScreenRect(int.MinValue, int.MaxValue, 0, -1), Nearby);
        Assert.Equal(flight.Sample(0), flight.Sample(double.NaN));
        Assert.Equal(flight.Sample(0), flight.Sample(double.NegativeInfinity));
        foreach (double time in new[] { 0, .7, 1.9, 2.9, 3.5, double.PositiveInfinity })
        {
            var frame = flight.Sample(time);
            Assert.True(double.IsFinite(frame.X) && double.IsFinite(frame.Y));
            Assert.InRange(frame.Zoom, OverviewCamera.ZoomMin, OverviewCamera.ZoomMax);
            Assert.InRange(frame.TitleOpacity, 0, 1);
            Assert.InRange(frame.RevealOpacity, 0, 1);
            Assert.InRange(frame.TitleSpacing, 1, 4);
        }
    }

    [Theory]
    [InlineData(double.NaN, 0, 1)]
    [InlineData(0, double.PositiveInfinity, 1)]
    [InlineData(0, 0, double.NaN)]
    [InlineData(0, 0, 0)]
    [InlineData(0, 0, 1.1)]
    public void InvalidSavedCameraIsRejected(double x, double y, double zoom)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new StartupIntroFlight(new(x, y, zoom), Screen, []));

    [Fact]
    public void RevealAndTitleRetainTheirIndependentSmoothEnvelopes()
    {
        var flight = new StartupIntroFlight(Saved, Screen, []);
        Assert.Equal(0, flight.Sample(0).RevealOpacity);
        Assert.Equal(1, flight.Sample(.6).RevealOpacity);
        Assert.Equal(0, flight.Sample(.6).TitleOpacity);
        Assert.Equal(1, flight.Sample(1.1).TitleOpacity);
        Assert.Equal(1, flight.Sample(3.6).TitleOpacity);
        Assert.InRange(flight.Sample(3.9).TitleOpacity, .49, .51);
        for (double t = 4.2; t <= flight.DurationSeconds; t += .1)
        {
            Assert.Equal(0, flight.Sample(t).TitleOpacity, 12);
            Assert.Equal(1, flight.Sample(t).RevealOpacity);
        }
    }

    [Fact]
    public void ReducedMotionStaysAtTheLandingTargetAndAbortsImmediately()
    {
        var target = new CanvasCamera(3000, 800, .95);
        var flight = new StartupIntroFlight(Saved, Screen, Nearby, reducedMotion: true, landing: target,
            projectRegions: [new() { X = 100000, W = 2000, H = 1000 }]);
        Assert.Equal(StartupIntroFlight.ReducedMotionDurationSeconds, flight.DurationSeconds);
        for (double t = 0; t <= 1; t += .01)
        {
            var frame = flight.Sample(t);
            Assert.Equal(target, new CanvasCamera(frame.X, frame.Y, frame.Zoom));
            Assert.Equal(1, frame.TitleSpacing);
            Assert.Equal(t >= flight.DurationSeconds, frame.Complete);
        }
        Assert.True(flight.CreateLanding(.15).Sample(0).Complete);
    }
}
