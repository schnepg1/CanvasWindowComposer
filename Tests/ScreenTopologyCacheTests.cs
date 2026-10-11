using CanvasDesktop;

namespace CanvasDesktop.Tests;

public class ScreenTopologyCacheTests
{
    [Fact]
    public void GeometryIsImmutableAndReusedUntilInvalidated()
    {
        var source = new FakeScreens(); var cache = new CachedScreens(source);
        var first = cache.AllWorkingAreas;
        source.WorkingAreas[0] = new(-1280, 0, 1280, 720);
        source.PrimaryWa = source.WorkingAreas[0];
        Assert.Same(first, cache.AllWorkingAreas);
        Assert.Equal(new ScreenRect(0, 0, 1920, 1040), first[0]);
        long version = cache.TopologyVersion;
        cache.Invalidate();
        Assert.True(cache.TopologyVersion > version);
        Assert.NotSame(first, cache.AllWorkingAreas);
        Assert.Equal(source.WorkingAreas[0], cache.AllWorkingAreas[0]);
        Assert.Equal(source.PrimaryWa, cache.PrimaryWorkingArea);
    }

    [Fact]
    public void NativeAdapterReusesConvertedWorkingAreasAndRefreshesOnNotification()
    {
        var source = new FakeScreens(); var screens = new CachedScreens(source);
        var api = new Win32WindowApi(screens);
        var first = api.GetScreenWorkingAreas();
        Assert.Same(first, api.GetScreenWorkingAreas());
        source.WorkingAreas = new() { new(-1600, 200, 1600, 900), new(0, 0, 1920, 1080) };
        screens.Invalidate();
        var second = api.GetScreenWorkingAreas();
        Assert.NotSame(first, second);
        Assert.Equal((-1600, 200, 1600, 900), second[0]);
        Assert.Equal(2, second.Count);
        Assert.Single(first);
    }

    [Fact]
    public void UnversionedProvidersRemainFresh()
    {
        var screens = new FakeScreens(); var api = new Win32WindowApi(screens);
        _ = api.GetScreenWorkingAreas(); screens.WorkingAreas[0] = new(0, 0, 800, 600);
        Assert.Equal((0, 0, 800, 600), api.GetScreenWorkingAreas()[0]);
    }
}
