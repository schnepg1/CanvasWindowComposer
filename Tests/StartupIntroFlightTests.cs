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
    public void EmptyCanvasesGetAWideRollingMinimumSweepRelativeToTheirMonitor(int originX, int originY)
    {
        var screen = new ScreenRect(originX, originY, 1920, 1080);
        var flight = new StartupIntroFlight(Saved, screen, []);
        Assert.Equal(StartupIntroFlight.MinimumDurationSeconds, flight.DurationSeconds, 10);
        var origin = Center(flight.Sample(0), screen);
        var centers = Enumerable.Range(0, 1201).Select(i => Center(flight.Sample(i / 100.0), screen)).ToArray();
        Assert.True(centers.Min(p => p.X) < origin.X - 3000);
        Assert.True(centers.Max(p => p.X) > origin.X + 3000);
        Assert.True(centers.Max(p => p.Y) - centers.Min(p => p.Y) > 1000);
        int reversals = 0, previousDirection = 0;
        for (int i = 1; i < centers.Length; i++)
        {
            double dy = centers[i].Y - centers[i - 1].Y;
            int direction = Math.Abs(dy) < 0.001 ? 0 : Math.Sign(dy);
            if (direction == 0) continue;
            if (previousDirection != 0 && direction != previousDirection) reversals++;
            previousDirection = direction;
        }
        Assert.InRange(reversals, 2, 3); // Broad arcs rather than rapid bobbing on each route leg.
    }

    [Fact]
    public void TourVisitsEveryProjectRegionAndUnzonedClusterIncludingDistantContent()
    {
        WorldRect[] regions = [new() { X = -15000, Y = -4000, W = 2000, H = 1400 },
            new() { X = 14000, Y = 3000, W = 2200, H = 1600 }];
        WorldRect[] windows = [new() { X = -14000, Y = -3600, W = 800, H = 600 },
            new() { X = -5000, Y = 2000, W = 800, H = 600 },
            new() { X = 4000, Y = -3000, W = 800, H = 600 },
            new() { X = 25000, Y = 0, W = 800, H = 600 }];
        var flight = new StartupIntroFlight(Saved, Screen, windows, projectRegions: regions);
        var targets = regions.Concat(windows.Skip(1)).ToArray();
        foreach (var target in targets)
        {
            double closest = double.MaxValue, crossing = 0;
            for (double t = 0; t < flight.DurationSeconds; t += 0.01)
            {
                var p = Center(flight.Sample(t));
                double distance = Math.Sqrt(Math.Pow(p.X - target.X - target.W / 2, 2)
                    + Math.Pow(p.Y - target.Y - target.H / 2, 2));
                if (distance < closest) { closest = distance; crossing = t; }
            }
            Assert.True(closest < Math.Min(target.W, target.H) / 4,
                $"Missed the central part of the region at {target.X}, {target.Y}: {closest}");
            var a = Center(flight.Sample(crossing - .001));
            var b = Center(flight.Sample(crossing + .001));
            Assert.True(Math.Abs(b.X - a.X) + Math.Abs(b.Y - a.Y) > .01, "Stopped at a region crossing.");
        }
        Assert.True(flight.DurationSeconds > StartupIntroFlight.MinimumDurationSeconds);
    }

    [Fact]
    public void DurationGrowsWithBothTheNumberOfAreasAndTravelDistance()
    {
        var empty = new StartupIntroFlight(Saved, Screen, []);
        var local = new StartupIntroFlight(Saved, Screen, [], projectRegions: Nearby);
        var several = new StartupIntroFlight(Saved, Screen, [], projectRegions:
            Enumerable.Range(0, 8).Select(i => new WorldRect { X = i * 3000, W = 1000, H = 800 }).ToArray());
        var distant = new StartupIntroFlight(Saved, Screen, [], projectRegions:
            [new() { X = 100000, W = 1000, H = 800 }]);
        Assert.True(local.DurationSeconds >= empty.DurationSeconds);
        Assert.True(several.DurationSeconds > local.DurationSeconds);
        Assert.True(distant.DurationSeconds > local.DurationSeconds);
    }

    [Fact]
    public void WindowClustersAndProjectRegionsAreCombinedWithoutDuplicateVisits()
    {
        WorldRect zone = new() { X = 0, Y = 0, W = 3000, H = 2000 };
        WorldRect[] windows = [new() { X = 100, Y = 100, W = 800, H = 600 },
            new() { X = 6000, Y = 100, W = 800, H = 600 },
            new() { X = 7200, Y = 100, W = 800, H = 600 },
            new() { X = 8400, Y = 100, W = 800, H = 600 },
            new() { X = 18000, Y = -5000, W = 800, H = 600 }];
        var destinations = StartupIntroRoute.Destinations(windows, [zone], 1920, 1080);
        Assert.Equal(3, destinations.Count);
        Assert.Contains(zone, destinations);
        Assert.Contains(destinations, b => b.X == 6000 && b.W == 3200);
        var regionOnly = new StartupIntroFlight(Saved, Screen, [], projectRegions: [zone]);
        var withMembers = new StartupIntroFlight(Saved, Screen, [windows[0]], projectRegions: [zone]);
        Assert.Equal(regionOnly.DurationSeconds, withMembers.DurationSeconds);
        Assert.Equal(regionOnly.Sample(5), withMembers.Sample(5));
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

    [Fact]
    public void RowsAreTouredSidewaysBeforeBankingIntoTheNextRow()
    {
        WorldRect[] regions = [new() { X = 0, Y = 0, W = 800, H = 600 },
            new() { X = 8000, Y = 0, W = 800, H = 600 },
            new() { X = 0, Y = 8000, W = 800, H = 600 },
            new() { X = 8000, Y = 8000, W = 800, H = 600 }];
        var flight = new StartupIntroFlight(new(0, 0, 1), Screen, [], projectRegions: regions);
        var crossings = regions.Select(region =>
        {
            double distance = double.MaxValue, crossing = 0;
            for (double t = 0; t < flight.DurationSeconds; t += .01)
            {
                var center = Center(flight.Sample(t));
                double d = Math.Pow(center.X - region.X - region.W / 2, 2) + Math.Pow(center.Y - region.Y - region.H / 2, 2);
                if (d < distance) { distance = d; crossing = t; }
            }
            return crossing;
        }).ToArray();
        Assert.True(crossings[0] < crossings[1] && crossings[1] < crossings[3] && crossings[3] < crossings[2]);
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
    public void DenseTourKeepsCruiseZoomAndVerticalSwellSmallAndSlow()
    {
        var regions = Enumerable.Range(0, 12).Select(i =>
            new WorldRect { X = i * 2000, W = 800, H = 600 }).ToArray();
        var flight = new StartupIntroFlight(Saved, Screen, [], projectRegions: regions);
        const double h = .01;
        double previousCrossing = double.NaN;
        int crossings = 0;
        // Sample the middle of the long row, clear of takeoff and the return to the saved view.
        for (double t = 8; t < 22; t += h)
        {
            var before = flight.Sample(t - h);
            var frame = flight.Sample(t);
            var after = flight.Sample(t + h);
            double logBefore = Math.Log(before.Zoom / .2), logZoom = Math.Log(frame.Zoom / .2);
            double logAfter = Math.Log(after.Zoom / .2);
            Assert.InRange(Math.Abs(logZoom), 0, .00601); // Less than 0.61% in either direction.
            Assert.InRange(Math.Abs((logAfter - logBefore) / (2 * h)), 0, .003);
            Assert.InRange(Math.Abs((logAfter - 2 * logZoom + logBefore) / (h * h)), 0, .0015);
            Assert.InRange(Math.Abs(Center(frame).Y - 300), 0, 90);
            if (logBefore * logZoom < 0)
            {
                if (!double.IsNaN(previousCrossing)) Assert.InRange(t - previousCrossing, 6.9, 7.1);
                previousCrossing = t;
                crossings++;
            }
        }
        Assert.True(crossings >= 2, "The long tour should retain a gentle, slow swell.");
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
        Assert.Equal(0, flight.Sample(2.4).TitleOpacity, 12);
        for (double t = 2.4; t <= flight.DurationSeconds; t += .1)
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
