using CanvasDesktop;
using System;
using Xunit;

namespace CanvasDesktop.Tests;

public class WindowNavigationTests
{
    [Fact]
    public void FlightPreservesZoomAndHasExactEndpoints()
    {
        var from = new CanvasCamera(-4000, 300, .18);
        var to = new CanvasCamera(7000, -800, .18);
        var flight = new WindowNavigationFlight(from, to);
        Assert.Equal(from, flight.Sample(0));
        Assert.Equal(from, flight.Sample(double.NaN));
        Assert.Equal(to, flight.Sample(WindowNavigationFlight.Duration));
        var middle = flight.Sample(WindowNavigationFlight.Duration / 2);
        Assert.Equal((from.X + to.X) / 2, middle.X, 1);
        Assert.Equal((from.Y + to.Y) / 2, middle.Y, 1);
        Assert.Equal(from.Zoom, middle.Zoom);
    }

    [Fact]
    public void HelicopterFlightTravelsInStrictlyStraight2DLineAcrossCanvas()
    {
        // Window A at (1000, 1000) and Window B at (6000, 3000) in world coordinates
        var vp = new ScreenRect(0, 0, 1920, 1080);
        var from = WindowNavigationFlight.Target(new WorldRect { X = 1000, Y = 1000, W = 800, H = 600 }, 1.0, vp);
        var to = WindowNavigationFlight.Target(new WorldRect { X = 6000, Y = 3000, W = 800, H = 600 }, 1.0, vp);
        var flight = new WindowNavigationFlight(from, to, vp);

        // Verify that throughout the entire flight, the 2D world focal center follows an exact straight line
        foreach (double u in new[] { 0.05, 0.10, 0.20, 0.35, 0.50, 0.65, 0.80, 0.90, 0.95 })
        {
            var focal = flight.SampleFocalPoint(WindowNavigationFlight.Duration * u);
            double ratioX = (focal.X - flight.FromFocalPoint.X) / (flight.ToFocalPoint.X - flight.FromFocalPoint.X);
            double ratioY = (focal.Y - flight.FromFocalPoint.Y) / (flight.ToFocalPoint.Y - flight.FromFocalPoint.Y);

            // Straight line invariant: ratioX == ratioY at all times (no 2D arcing or canvas deflection)
            Assert.Equal(ratioX, ratioY, 5);
        }
    }

    [Fact]
    public void HelicopterFlightFollowsContinuousSmooth3DCurve()
    {
        var from = new CanvasCamera(0, 0, 1.0);
        var to = new CanvasCamera(5000, 2000, 1.0);
        var flight = new WindowNavigationFlight(from, to);

        // At takeoff (u = 0.08): 2D horizontal progress starts very slowly without flat freeze,
        // while altitude (zoom-out) climbs strongly first (vertical takeoff emphasis).
        var takeoff = flight.Sample(WindowNavigationFlight.Duration * 0.08);
        var takeoffFocal = flight.SampleFocalPoint(WindowNavigationFlight.Duration * 0.08);
        double horizFraction = (takeoffFocal.X - flight.FromFocalPoint.X) / (flight.ToFocalPoint.X - flight.FromFocalPoint.X);
        double vertFraction = (1.0 - takeoff.Zoom) / (1.0 - flight.CruisingZoom);

        Assert.True(horizFraction > 0, "Horizontal motion should be continuous and start without flat freeze");
        Assert.True(horizFraction < 0.01, "Horizontal motion should start gradually during takeoff");
        Assert.True(takeoff.Zoom < 1.0, "Camera should lift up (zoom out) during takeoff");
        Assert.True(vertFraction > horizFraction * 5.0, "Takeoff trajectory should climb strongly in altitude first");

        // At midpoint (u = 0.50): halfway horizontally, reaches peak cruising altitude (crests smoothly)
        var mid = flight.Sample(WindowNavigationFlight.Duration * 0.50);
        var midFocal = flight.SampleFocalPoint(WindowNavigationFlight.Duration * 0.50);
        Assert.Equal((flight.FromFocalPoint.X + flight.ToFocalPoint.X) / 2.0, midFocal.X, 3);
        Assert.Equal((flight.FromFocalPoint.Y + flight.ToFocalPoint.Y) / 2.0, midFocal.Y, 3);
        Assert.True(mid.Zoom < takeoff.Zoom, "Midpoint zoom should be at cruising altitude");

        // Near landing (u = 0.95): horizontal movement is nearly arrived (>99%), while vertical descent is settling
        var landing = flight.Sample(WindowNavigationFlight.Duration * 0.95);
        var landingFocal = flight.SampleFocalPoint(WindowNavigationFlight.Duration * 0.95);
        double landingHoriz = (landingFocal.X - flight.FromFocalPoint.X) / (flight.ToFocalPoint.X - flight.FromFocalPoint.X);
        Assert.True(landingHoriz > 0.99, "Horizontal position should be nearly arrived at landing zone");
        Assert.True(landing.Zoom > mid.Zoom, "Vertical descent should already be declining towards destination");
        Assert.True(landing.Zoom < to.Zoom, "Camera should be settling vertically onto landing pad");

        // Touchdown (u = 1.0): horizontal stops and vertical decline stops simultaneously at destination
        var touchdown = flight.Sample(WindowNavigationFlight.Duration);
        Assert.Equal(to, touchdown);
    }

    [Fact]
    public void ShortDistanceSwitchDoesNotClimbToCruisingAltitude()
    {
        var from = new CanvasCamera(100, 100, 1.0);
        var to = new CanvasCamera(200, 150, 1.0); // distance ~ 111px (< 300px)
        var flight = new WindowNavigationFlight(from, to);

        var mid = flight.Sample(flight.FlightDuration * 0.50);
        Assert.Equal(150, mid.X, 1);
        Assert.Equal(125, mid.Y, 1);
        Assert.Equal(1.0, mid.Zoom); // No vertical climb for short distance
        Assert.InRange(flight.FlightDuration, 0.25, 0.5);
        Assert.Equal(to, flight.Sample(flight.FlightDuration));
    }

    [Theory]
    [InlineData(0, 0.25, 1)]
    [InlineData(300, 0.486, 1)]
    [InlineData(500, 0.643, 0.911)]
    [InlineData(3500, 3, 0.286)]
    [InlineData(6000, 3, 0.286)]
    public void DefaultDurationAndClimbScaleWithRouteLength(double distance, double seconds, double zoom)
    {
        var flight = new WindowNavigationFlight(new(0, 0, 1), new(distance, 0, 1));
        Assert.Equal(seconds, flight.FlightDuration, 3);
        Assert.Equal(zoom, flight.CruisingZoom, 3);
        Assert.Equal(flight.CruisingZoom, flight.Sample(flight.FlightDuration / 2).Zoom);
    }

    [Fact]
    public void DurationAndClimbDistancesCanBeAdjustedIndependently()
    {
        var from = new CanvasCamera(0, 0, 1);
        var to = new CanvasCamera(1000, 0, 1);
        var flight = new WindowNavigationFlight(from, to, fullDurationDistance: 2000, fullClimbDistance: 1700);
        var faster = new WindowNavigationFlight(from, to, fullDurationDistance: 4000, fullClimbDistance: 1700);
        var shallower = new WindowNavigationFlight(from, to, fullDurationDistance: 2000, fullClimbDistance: 3100);

        Assert.Equal(1.625, flight.FlightDuration);
        Assert.Equal(1 / Math.Sqrt(3.5), flight.CruisingZoom, 6);
        Assert.Equal(flight.CruisingZoom, faster.CruisingZoom);
        Assert.True(faster.FlightDuration < flight.FlightDuration);
        Assert.Equal(flight.FlightDuration, shallower.FlightDuration);
        Assert.True(shallower.CruisingZoom > flight.CruisingZoom);
    }

    [Fact]
    public void ZoomChangeRemainsContinuousAcrossTheNoClimbDistance()
    {
        var viewport = new ScreenRect(0, 0, 200, 100);
        var from = new CanvasCamera(0, 0, 0.8);
        var near = new WindowNavigationFlight(from, new(324, 12.5, 1), viewport);
        var farther = new WindowNavigationFlight(from, new(326, 12.5, 1), viewport);
        Assert.Equal(299, near.Distance);
        Assert.Equal(301, farther.Distance);
        Assert.Equal(near.Sample(near.FlightDuration / 2).Zoom,
            farther.Sample(farther.FlightDuration / 2).Zoom, 3);
    }

    [Fact]
    public void StrongClimbReachesTheZoomFloorAtTheConfiguredDistance()
    {
        var from = new CanvasCamera(0, 0, 1);
        var halfway = new WindowNavigationFlight(from, new(1650, 0, 1), cruisingScale: 2.5, profile: "Cinematic");
        var full = new WindowNavigationFlight(from, new(3000, 0, 1), cruisingScale: 2.5, profile: "Cinematic");

        Assert.Equal(0.072, full.CruisingZoom, 6);
        Assert.Equal(Math.Sqrt(full.CruisingZoom), halfway.CruisingZoom, 6);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1920, -200)]
    public void TargetCentersInTheWorkingAreaIncludingMonitorOffset(int x, int y)
    {
        var world = new WorldRect { X = 5000, Y = -3000, W = 800, H = 600 };
        var target = WindowNavigationFlight.Target(world, .2, new(x, y, 1920, 1040));
        var center = target.ProjectPosition(world.X + world.W / 2, world.Y + world.H / 2);
        Assert.Equal((x + 960, y + 520), center);
    }

    [Fact]
    public void DecoupledPanningSpeedIsMuchSlowerAtBeginningAndEnd()
    {
        var from = new CanvasCamera(0, 0, 1.0);
        var to = new CanvasCamera(6000, 3000, 1.0);
        var flight = new WindowNavigationFlight(from, to);

        // At u = 0.20: horizontal movement is very slow (< 2.5%), while altitude has climbed strongly (> 40%)
        var sampleEarly = flight.Sample(WindowNavigationFlight.Duration * 0.20);
        var focalEarly = flight.SampleFocalPoint(WindowNavigationFlight.Duration * 0.20);
        double horizEarly = (focalEarly.X - flight.FromFocalPoint.X) / (flight.ToFocalPoint.X - flight.FromFocalPoint.X);
        double vertEarly = (1.0 - sampleEarly.Zoom) / (1.0 - flight.CruisingZoom);
        Assert.True(horizEarly < 0.025, "Horizontal movement should be less than 2.5% at u = 0.20");
        Assert.True(vertEarly > 0.40, "Altitude should have climbed more than 40% at u = 0.20");

        // At u = 0.85: horizontal movement has essentially arrived (> 99%), while altitude is gently settling
        var sampleLate = flight.Sample(WindowNavigationFlight.Duration * 0.85);
        var focalLate = flight.SampleFocalPoint(WindowNavigationFlight.Duration * 0.85);
        double horizLate = (focalLate.X - flight.FromFocalPoint.X) / (flight.ToFocalPoint.X - flight.FromFocalPoint.X);
        Assert.True(horizLate > 0.99, "Horizontal position should be >99% arrived by u = 0.85");
        Assert.True(sampleLate.Zoom < 1.0, "Altitude should still be gently settling towards 1.0 at u = 0.85");
    }

    [Fact]
    public void CruisingAltitudeProvidesSubstantialOverviewForCanvasJumps()
    {
        var from = new CanvasCamera(0, 0, 1.0);
        var to = new CanvasCamera(1500, 1000, 1.0); // distance ~ 1800px
        var flight = new WindowNavigationFlight(from, to);

        // Cruising zoom should provide a substantial overview (< 0.50x)
        Assert.True(flight.CruisingZoom < 0.50, $"Cruising zoom ({flight.CruisingZoom:F3}) should provide substantial zoom-out overview");
        Assert.InRange(flight.FlightDuration, 0.75, WindowNavigationFlight.Duration);
    }
}
