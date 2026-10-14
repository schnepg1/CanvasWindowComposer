using System;
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

    [Fact]
    public void StartAndFinishPreserveExactSavedCameraAndStayFinished()
    {
        var flight = new StartupIntroFlight(Saved, Screen, Nearby);
        foreach (double time in new[] { -1, 0, 3.5, 4, double.PositiveInfinity })
        {
            var frame = flight.Sample(time);
            Assert.Equal(Saved.X, frame.X);
            Assert.Equal(Saved.Y, frame.Y);
            Assert.Equal(Saved.Zoom, frame.Zoom);
            Assert.Equal(time >= 3.5, frame.Complete);
        }
        Assert.Equal(flight.Sample(3.5), flight.Sample(999));
        // Sampling has no accumulated state, even after a completed sample.
        Assert.Equal(flight.Sample(1.7), new StartupIntroFlight(Saved, Screen, Nearby).Sample(1.7));
    }

    [Fact]
    public void PullbackAndSettleAreMonotonicAndNeverOvershoot()
    {
        var flight = new StartupIntroFlight(Saved, Screen, Nearby);
        double previous = Saved.Zoom;
        for (int i = 0; i <= 120; i++)
        {
            double zoom = flight.Sample(i / 100.0).Zoom;
            Assert.InRange(zoom, 0.4 - 1e-12, previous + 1e-12);
            previous = zoom;
        }
        Assert.Equal(0.4, previous, 12);
        for (int i = 260; i <= 350; i++)
        {
            double zoom = flight.Sample(i / 100.0).Zoom;
            Assert.InRange(zoom, previous - 1e-12, Saved.Zoom + 1e-12);
            previous = zoom;
        }
        Assert.Equal(Saved.Zoom, previous);
    }

    [Theory]
    [InlineData(0.05)]
    [InlineData(0.2)]
    [InlineData(0.4)]
    public void AlreadyWideSavedViewNeverZoomsInForThePullback(double zoom)
    {
        var flight = new StartupIntroFlight(Saved with { Zoom = zoom }, Screen, Nearby);
        for (int i = 0; i <= 350; i++)
            Assert.Equal(zoom, flight.Sample(i / 100.0).Zoom);
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(1.2)]
    [InlineData(1.9)]
    [InlineData(2.6)]
    [InlineData(3.5)]
    public void StageJoinsHaveContinuousPositionAndVelocity(double time)
    {
        var flight = new StartupIntroFlight(Saved, Screen, Nearby);
        const double h = 0.0001;
        var before = flight.Sample(time - h);
        var at = flight.Sample(time);
        var after = flight.Sample(time + h);
        Assert.InRange(Math.Abs(after.X - before.X), 0, 0.05);
        Assert.InRange(Math.Abs(after.Y - before.Y), 0, 0.05);
        Assert.InRange(Math.Abs(after.Zoom - before.Zoom), 0, 0.00001);
        // Left and right velocities agree at every timeline join. This also
        // catches the common sawtooth or triangle-wave orbit midpoint reversal.
        Assert.InRange(Math.Abs((after.X - 2 * at.X + before.X) / h), 0, 0.02);
        Assert.InRange(Math.Abs((after.Y - 2 * at.Y + before.Y) / h), 0, 0.02);
        Assert.InRange(Math.Abs((after.Zoom - 2 * at.Zoom + before.Zoom) / h), 0, 0.0001);
    }

    [Fact]
    public void SurveyArcHasNoMidpointStopOrReversal()
    {
        var flight = new StartupIntroFlight(Saved, Screen, Nearby);
        var a = flight.Sample(2.1 - 0.001);
        var b = flight.Sample(2.1);
        var c = flight.Sample(2.1 + 0.001);
        double beforeX = b.X - a.X, beforeY = b.Y - a.Y;
        double afterX = c.X - b.X, afterY = c.Y - b.Y;
        Assert.True(beforeX * afterX + beforeY * afterY > 0);
        Assert.True(Math.Abs(afterX) + Math.Abs(afterY) > 0.00001);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1920, -1080)]
    [InlineData(-3000, 700)]
    public void EmptyLayoutKeepsTheSameWorldPointAtMonitorCenter(int originX, int originY)
    {
        var screen = new ScreenRect(originX, originY, 1920, 1080);
        var flight = new StartupIntroFlight(Saved, screen, []);
        double sx = originX + 960, sy = originY + 540;
        double worldX = Saved.X + sx / Saved.Zoom;
        double worldY = Saved.Y + sy / Saved.Zoom;
        for (int i = 0; i <= 350; i++)
        {
            var frame = flight.Sample(i / 100.0);
            Assert.Equal(worldX, frame.X + sx / frame.Zoom, 8);
            Assert.Equal(worldY, frame.Y + sy / frame.Zoom, 8);
        }
    }

    [Fact]
    public void NearbyWindowsProduceACurvedScreenBoundedExcursion()
    {
        var flight = new StartupIntroFlight(Saved, Screen, Nearby);
        var baseline = new StartupIntroFlight(Saved, Screen, []);
        for (int i = 0; i <= 350; i++)
        {
            var frame = flight.Sample(i / 100.0);
            var centered = baseline.Sample(i / 100.0);
            Assert.InRange(Math.Abs(frame.X - centered.X), 0, Screen.Width / Saved.Zoom * 0.3);
            Assert.InRange(Math.Abs(frame.Y - centered.Y), 0, Screen.Height / Saved.Zoom * 0.25);
        }
        var a = flight.Sample(1.9);
        var a0 = baseline.Sample(1.9);
        var b = flight.Sample(2.5);
        var b0 = baseline.Sample(2.5);
        // A nonzero cross product distinguishes a curved survey from a straight
        // outward-and-back translation while making no pixel-timing assumptions.
        double cross = (a.X - a0.X) * (b.Y - b0.Y) - (a.Y - a0.Y) * (b.X - b0.X);
        Assert.True(Math.Abs(cross) > 1);
    }

    [Fact]
    public void PinnedDistantAndInvalidWindowsDoNotChangeTheFlight()
    {
        WorldRect[] ignored =
        [
            new() { X = 1500, Y = 100, W = 800, H = 600, PinnedToScreen = true },
            new() { X = 1e20, Y = 1e20, W = 800, H = 600 },
            new() { X = double.NaN, W = 800, H = 600 },
            new() { Y = double.PositiveInfinity, W = 800, H = 600 },
            new() { W = -100, H = 600 },
            new() { W = 800, H = double.PositiveInfinity },
            new() { X = double.MaxValue, W = double.MaxValue, H = 1 }
        ];
        var flight = new StartupIntroFlight(Saved, Screen, ignored);
        var empty = new StartupIntroFlight(Saved, Screen, []);
        for (int i = 0; i <= 350; i++)
            Assert.Equal(empty.Sample(i / 100.0), flight.Sample(i / 100.0));
    }

    [Fact]
    public void OnlyTheNearestNeighborhoodAffectsSurvey()
    {
        WorldRect nearest = new() { X = 1400, Y = 250, W = 200, H = 200 };
        WorldRect second = nearest with { X = 1500 };
        WorldRect third = nearest with { X = 1600 };
        WorldRect fourth = nearest with { X = 2500 };
        var nearestFlight = new StartupIntroFlight(Saved, Screen, [nearest, second, third]);
        var withExtra = new StartupIntroFlight(Saved, Screen, [fourth, third, nearest, second]);
        Assert.Equal(nearestFlight.Sample(2.2), withExtra.Sample(2.2));
    }

    [Fact]
    public void InvalidTimesAndMonitorDimensionsAreSanitizedToFiniteFrames()
    {
        var flight = new StartupIntroFlight(Saved, new ScreenRect(int.MinValue, int.MaxValue, 0, -1), Nearby);
        Assert.Equal(flight.Sample(0), flight.Sample(double.NaN));
        Assert.Equal(flight.Sample(0), flight.Sample(double.NegativeInfinity));
        foreach (double time in new[] { 0, 0.7, 1.9, 2.9, 3.5, double.PositiveInfinity })
        {
            var frame = flight.Sample(time);
            Assert.True(double.IsFinite(frame.X));
            Assert.True(double.IsFinite(frame.Y));
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
    public void InvalidSavedCameraIsRejectedRatherThanChangingItsDestination(double x, double y, double zoom)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new StartupIntroFlight(new CanvasCamera(x, y, zoom), Screen, []));
    }

    [Fact]
    public void RevealAndTitleHaveIndependentSmoothEnvelopes()
    {
        var flight = new StartupIntroFlight(Saved, Screen, []);
        Assert.Equal(0, flight.Sample(0).RevealOpacity);
        Assert.Equal(1, flight.Sample(0.5).RevealOpacity);
        Assert.Equal(0, flight.Sample(0.5).TitleOpacity);
        Assert.Equal(1, flight.Sample(1.2).TitleOpacity);
        Assert.Equal(0, flight.Sample(2.1).TitleOpacity);
        double previousSpacing = 1;
        for (int i = 0; i <= 350; i++)
        {
            var frame = flight.Sample(i / 100.0);
            Assert.InRange(frame.TitleSpacing, previousSpacing - 1e-12, 4 + 1e-12);
            previousSpacing = frame.TitleSpacing;
            if (i >= 50) Assert.Equal(1, frame.RevealOpacity);
            if (i >= 210) Assert.Equal(0, frame.TitleOpacity);
        }
    }

    [Fact]
    public void ReducedMotionNeverChangesCameraAndCompletesQuickly()
    {
        var flight = new StartupIntroFlight(Saved, Screen, Nearby, reducedMotion: true);
        for (int i = 0; i <= 350; i++)
        {
            var frame = flight.Sample(i / 100.0);
            Assert.Equal(1, frame.TitleSpacing);
            Assert.Equal(Saved.X, frame.X);
            Assert.Equal(Saved.Y, frame.Y);
            Assert.Equal(Saved.Zoom, frame.Zoom);
            Assert.Equal(i >= 45, frame.Complete);
        }
        Assert.True(flight.Sample(0.15).TitleOpacity > 0);
        Assert.Equal(0, flight.Sample(0.45).TitleOpacity);
        Assert.Equal(flight.Sample(0.45), flight.Sample(99));
    }
}
