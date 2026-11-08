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
        Assert.Equal((from.X + to.X) / 2, middle.X);
        Assert.Equal((from.Y + to.Y) / 2, middle.Y);
        Assert.Equal(from.Zoom, middle.Zoom);
    }

    [Fact]
    public void HelicopterFlightDecouplesVerticalTakeoffAndLanding()
    {
        var from = new CanvasCamera(0, 0, 1.0);
        var to = new CanvasCamera(5000, 2000, 1.0);
        var flight = new WindowNavigationFlight(from, to);

        // At takeoff (u = 0.08 / duration): horizontal position has not moved, but zoom is already lifting off
        var takeoff = flight.Sample(WindowNavigationFlight.Duration * 0.08);
        Assert.Equal(from.X, takeoff.X);
        Assert.Equal(from.Y, takeoff.Y);
        Assert.True(takeoff.Zoom < 1.0, "Camera should lift up (zoom out) during takeoff");

        // At midpoint: halfway horizontally, at peak cruising altitude (zoomed out)
        var mid = flight.Sample(WindowNavigationFlight.Duration * 0.50);
        Assert.Equal(2500, mid.X);
        Assert.Equal(1000, mid.Y);
        Assert.True(mid.Zoom < takeoff.Zoom, "Midpoint zoom should be at cruising altitude");

        // Near landing (u = 0.95 / duration): horizontal position has already arrived at target, but vertical descent is finishing
        var landing = flight.Sample(WindowNavigationFlight.Duration * 0.95);
        Assert.Equal(to.X, landing.X);
        Assert.Equal(to.Y, landing.Y);
        Assert.True(landing.Zoom < to.Zoom, "Camera should still be settling vertically onto landing pad");
    }

    [Fact]
    public void ShortDistanceSwitchDoesNotClimbToCruisingAltitude()
    {
        var from = new CanvasCamera(100, 100, 1.0);
        var to = new CanvasCamera(200, 150, 1.0); // distance ~ 111px (< 300px)
        var flight = new WindowNavigationFlight(from, to);

        var mid = flight.Sample(WindowNavigationFlight.Duration * 0.50);
        Assert.Equal(150, mid.X);
        Assert.Equal(125, mid.Y);
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
