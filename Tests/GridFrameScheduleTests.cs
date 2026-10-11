using CanvasDesktop;

namespace CanvasDesktop.Tests;

public class GridFrameScheduleTests
{
    [Theory]
    [InlineData(false, 0.1f)]
    [InlineData(true, 0.7f)]
    [InlineData(true, 1f)]
    public void StaticContentPresentsOnceThenIdles(bool drawGrid, float zoom)
    {
        var frames = new GridFrameSchedule();
        Assert.True(frames.ShouldRender(drawGrid, zoom, false));
        frames.Presented(frames.Revision);
        Assert.False(frames.ShouldRender(drawGrid, zoom, false));
        frames.Invalidate(); Assert.True(frames.ShouldRender(drawGrid, zoom, false));
    }

    [Fact]
    public void VisibleNebulaAndInertiaContinueUntilAnimationStops()
    {
        var frames = new GridFrameSchedule(); frames.Presented(frames.Revision);
        Assert.True(frames.ShouldRender(true, 0.69f, false));
        Assert.True(frames.ShouldRender(false, 1f, true));
        Assert.False(frames.ShouldRender(false, 1f, false));
    }

    [Fact]
    public void InvalidationDuringPresentIsNotLost()
    {
        var frames = new GridFrameSchedule(); long rendering = frames.Revision;
        frames.Invalidate(); frames.Presented(rendering);
        Assert.True(frames.ShouldRender(true, 1f, false));
        frames.Presented(frames.Revision);
        Assert.False(frames.ShouldRender(true, 1f, false));
    }
}
