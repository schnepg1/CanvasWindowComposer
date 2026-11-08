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

        var mid = flight.Sample(WindowNavigationFlight.Duration * 0.50);
        Assert.Equal(150, mid.X, 1);
        Assert.Equal(125, mid.Y, 1);
        Assert.Equal(1.0, mid.Zoom); // No vertical climb for short distance
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
}
