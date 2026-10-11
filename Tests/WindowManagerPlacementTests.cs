using CanvasDesktop;

namespace CanvasDesktop.Tests;

public class WindowManagerPlacementTests
{
    [Fact]
    public void Batch_QueriesMonitorGeometryOnceForManyWindows()
    {
        var canvas = new Canvas();
        var api = new FakeWindowApi();
        using var wm = new WindowManager(canvas, api, new FakeAppConfig(), new FakeInputRouter());
        for (int i = 1; i <= 100; i++)
        {
            api.AddWindow((IntPtr)i, 0, 0, 800, 600);
            canvas.SetWindow((IntPtr)i, i * 1000, 100, 800, 600);
        }
        wm.ReprojectSync();
        Assert.Equal(1, api.ScreenAreaQueryCount);
        Assert.Equal(100, api.LastBatch.Count);
        wm.ReprojectSync();
        Assert.Equal(2, api.ScreenAreaQueryCount);
        Assert.Single(api.LastBatch); // Only the visible window is repeatedly moved.
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void IndividualAndBatchProjection_UseTheSameOffScreenPolicy(bool disabled, bool suspended)
    {
        var canvas = new Canvas();
        var api = new FakeWindowApi();
        using var wm = new WindowManager(canvas, api, new FakeAppConfig { DisableGreedyDraw = disabled }, new FakeInputRouter());
        wm.SuspendGreedyDraw = suspended;
        api.AddWindow((IntPtr)1, 0, 0, 800, 600);
        api.AddWindow((IntPtr)2, 0, 0, 800, 600);
        canvas.SetWindow((IntPtr)1, 5000, 5000, 800, 600);
        canvas.SetWindow((IntPtr)2, 5000, 5000, 800, 600);

        Assert.True(wm.ReprojectWindow((IntPtr)1));
        var individual = Assert.Single(api.SetPositionCalls);
        wm.ReprojectSync();
        var batched = Assert.Single(api.LastBatch, i => i.HWnd == (IntPtr)2);
        Assert.Equal(new WindowRect(individual.x, individual.y, individual.w, individual.h), batched.Rect);
        Assert.Equal(!disabled && !suspended, api.ClippedWindows.Contains((IntPtr)1));
        Assert.Equal(api.ClippedWindows.Contains((IntPtr)1), api.ClippedWindows.Contains((IntPtr)2));
    }

    [Fact]
    public void SavedPlacement_OffScreen_ClipsWithoutChangingSavedCoordinates()
    {
        var canvas = new Canvas();
        var api = new FakeWindowApi();
        using var wm = new WindowManager(canvas, api, new FakeAppConfig(), new FakeInputRouter());
        api.AddWindow((IntPtr)1, 0, 0, 800, 600);
        var world = new WorldRect { X = 5000, Y = 5000, W = 800, H = 600 };
        wm.RestorePlacement((IntPtr)1, world);
        Assert.Contains((IntPtr)1, api.ClippedWindows);
        Assert.Equal((1919, 1079, 800, 600), api.GetWindowRect((IntPtr)1));
        Assert.Equal(world, canvas.Windows[(IntPtr)1]);

        world.X = 100; world.Y = 200;
        wm.RestorePlacement((IntPtr)1, world);
        Assert.Empty(api.ClippedWindows);
        Assert.Equal((100, 200, 800, 600), api.GetWindowRect((IntPtr)1));
    }

    [Fact]
    public void MinimizeEvent_DoesNotMoveNativeWindowOrLoseWorldBounds()
    {
        var canvas = new Canvas();
        var api = new FakeWindowApi();
        var input = new FakeInputRouter();
        using var wm = new WindowManager(canvas, api, new FakeAppConfig(), input);
        api.AddWindow((IntPtr)1, 100, 100, 800, 600);
        canvas.SetWindow((IntPtr)1, 100, 100, 800, 600);
        input.RaiseWindowMinimized((IntPtr)1);
        Assert.True(canvas.IsCollapsed((IntPtr)1));
        Assert.Empty(api.SetPositionCalls);
        Assert.Equal(100, canvas.Windows[(IntPtr)1].X);
    }

    [Fact]
    public void RestoreEvent_OffScreen_UsesClippingPolicy()
    {
        var canvas = new Canvas();
        var api = new FakeWindowApi();
        var input = new FakeInputRouter();
        using var wm = new WindowManager(canvas, api, new FakeAppConfig(), input);
        api.AddWindow((IntPtr)1, 0, 0, 800, 600);
        canvas.SetWindow((IntPtr)1, 5000, 5000, 800, 600);
        input.RaiseWindowMinimized((IntPtr)1);
        input.RaiseWindowRestored((IntPtr)1);
        Assert.False(canvas.IsCollapsed((IntPtr)1));
        Assert.Contains((IntPtr)1, api.ClippedWindows);
        Assert.Single(api.SetPositionCalls);
        Assert.Equal((1919, 1079, 800, 600), api.GetWindowRect((IntPtr)1));
    }

    [Fact]
    public void DisplayChange_ReparksClippedWindowsAndKeepsWorldLayout()
    {
        var canvas = new Canvas();
        var api = new FakeWindowApi();
        using var wm = new WindowManager(canvas, api, new FakeAppConfig(), new FakeInputRouter());
        api.AddWindow((IntPtr)1, 0, 0, 800, 600);
        canvas.SetWindow((IntPtr)1, 5000, 100, 800, 600);
        wm.ReprojectSync();
        Assert.Equal(1919, api.GetWindowRect((IntPtr)1).x);

        api.ScreenAreas = [(-1280, 0, 1280, 1024)];
        wm.ReprojectForDisplayChange();
        Assert.Equal(-1, api.GetWindowRect((IntPtr)1).x);
        Assert.Contains((IntPtr)1, api.ClippedWindows);
        Assert.Equal(5000, canvas.Windows[(IntPtr)1].X);
        Assert.Equal(2, api.ScreenAreaQueryCount);
    }

    [Fact]
    public void DisplayChange_AddedMonitorUnclipsNewlyVisibleWindow()
    {
        var canvas = new Canvas();
        var api = new FakeWindowApi();
        using var wm = new WindowManager(canvas, api, new FakeAppConfig(), new FakeInputRouter());
        api.AddWindow((IntPtr)1, 0, 0, 800, 600);
        canvas.SetWindow((IntPtr)1, 2500, 100, 800, 600);
        wm.ReprojectSync();
        Assert.Contains((IntPtr)1, api.ClippedWindows);

        api.ScreenAreas.Add((1920, 0, 1920, 1080));
        wm.ReprojectForDisplayChange();
        Assert.Empty(api.ClippedWindows);
        Assert.Equal((2500, 100, 800, 600), api.GetWindowRect((IntPtr)1));
    }
}
