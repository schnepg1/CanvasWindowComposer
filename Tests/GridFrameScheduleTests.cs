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

    [Theory]
    [InlineData(0.05f)]
    [InlineData(0.25f)]
    [InlineData(0.69f)]
    public void LowQualityIdlesAtEveryZoomButStillPresentsChangesAndInertia(float zoom)
    {
        var frames = new GridFrameSchedule();
        Assert.True(frames.ShouldRender(true, zoom, false, lowQuality: true));
        frames.Presented(frames.Revision);
        Assert.False(frames.ShouldRender(true, zoom, false, lowQuality: true));
        Assert.True(frames.ShouldRender(true, zoom, true, lowQuality: true));
        Assert.True(frames.ShouldRender(true, zoom, false, lowQuality: false));
        frames.Invalidate(); // Toggling quality or changing the camera must repaint once.
        Assert.True(frames.ShouldRender(true, zoom, false, lowQuality: true));
        frames.Presented(frames.Revision);
        Assert.False(frames.ShouldRender(true, zoom, false, lowQuality: true));
    }
}
