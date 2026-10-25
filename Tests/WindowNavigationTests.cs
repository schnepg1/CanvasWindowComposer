using CanvasDesktop;

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
