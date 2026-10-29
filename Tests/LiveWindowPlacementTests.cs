using CanvasDesktop;

namespace CanvasDesktop.Tests;

public class LiveWindowPlacementTests
{
    [Theory]
    [InlineData(.5)]
    [InlineData(1)]
    [InlineData(2)]
    public void DistinctOverviewCameraRetainsTheRequestedScreenPositionAndCanvasSize(double zoom)
    {
        var source = new CanvasCamera(9000, -6000, 1);
        var world = new WorldRect { X = 9200, Y = -5850, W = 400, H = 300, ZOrder = 7 };
        var view = new ArrivalViewport(new(-8000, 3000, zoom), [new(0, 0, 1920, 1080)], [new(0, 0, 1920, 1080)]);
        var placed = LiveWindowPlacement.Place(world, source, view);
        Assert.Equal((200, 150), view.Camera.ProjectPosition(placed.X, placed.Y));
        Assert.Equal(world.W, placed.W); Assert.Equal(world.H, placed.H); Assert.Equal(world.ZOrder, placed.ZOrder);
        Assert.False(placed.InInventory); Assert.False(placed.PinnedToScreen);
    }

    [Theory]
    [InlineData((int)WindowState.Normal)]
    [InlineData((int)WindowState.Minimized)]
    [InlineData((int)WindowState.Maximized)]
    public void NativePositionOnANegativeOriginMonitorAndWindowStateArePreserved(int state)
    {
        var world = new WorldRect { X = -1800, Y = -900, W = 400, H = 300, State = (WindowState)state };
        var view = new ArrivalViewport(new(0, 0, 1), [new(0, 0, 1920, 1080), new(-1920, -1080, 1920, 1080)]);
        Assert.Equal(world, LiveWindowPlacement.Place(world, view.Camera, view));
    }

    [Fact]
    public void AnOffscreenDialogIsBroughtIntoViewWithoutRequiringFreeSpace()
    {
        var world = new WorldRect { X = -30000, Y = -30000, W = 400, H = 300 };
        var view = new ArrivalViewport(new(5000, 2000, 1), [new(0, 0, 1920, 1080)]);
        var placed = LiveWindowPlacement.Place(world, new(0, 0, 1), view);
        Assert.Equal(5760, placed.X); Assert.Equal(2390, placed.Y);
        Assert.Equal(400, placed.W); Assert.Equal(300, placed.H); Assert.False(placed.InInventory);
    }
}
