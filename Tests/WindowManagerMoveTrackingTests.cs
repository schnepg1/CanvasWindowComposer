using CanvasDesktop;

namespace CanvasDesktop.Tests;

public class WindowManagerMoveTrackingTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void FailedProjection_PreservesBaselineAndTracksManualDrag(bool single, bool registerFirst)
    {
        var api = new FakeWindowApi(); var canvas = new Canvas(); var input = new FakeInputRouter();
        using var wm = new WindowManager(canvas, api, new FakeAppConfig { DisableGreedyDraw = true }, input);
        IntPtr handle = (IntPtr)1;
        api.AddWindow(handle, 100, 100, 800, 600);
        if (registerFirst) wm.RegisterWindow(handle);
        canvas.SetWindow(handle, 600, 200, 800, 600);
        api.FailedMoves.Add(handle);
        if (single) Assert.False(wm.ReprojectWindow(handle)); else wm.ReprojectSync();

        // The failed request must not turn the unchanged native placement into a user drag.
        input.RaiseWindowMoved(handle);
        Assert.Equal(600, canvas.Windows[handle].X);

        api.Windows[handle].X = 400; api.Windows[handle].Y = 250;
        api.Windows[handle].W = 900; api.Windows[handle].H = 650;
        input.RaiseWindowMoved(handle);
        Assert.Equal((400d, 250d, 900d, 650d), Bounds(canvas.Windows[handle]));

        api.FailedMoves.Clear();
        wm.ReprojectSync();
        Assert.Equal((400, 250, 900, 650), api.GetWindowRect(handle));
        Assert.Equal((400d, 250d, 900d, 650d), Bounds(canvas.Windows[handle]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedExplicitPlacement_StillTracksManualDrag(bool pin)
    {
        var api = new FakeWindowApi(); var canvas = new Canvas(); var input = new FakeInputRouter();
        using var wm = new WindowManager(canvas, api, new FakeAppConfig { DisableGreedyDraw = true }, input);
        IntPtr handle = (IntPtr)1;
        api.AddWindow(handle, 100, 100, 800, 600);
        api.FailedMoves.Add(handle);
        if (pin)
        {
            wm.RegisterWindow(handle);
            wm.SetWindowPinnedToScreen(handle, true);
        }
        else wm.RestorePlacement(handle, new() { X = 600, Y = 200, W = 800, H = 600 });

        api.Windows[handle].X = 400;
        input.RaiseWindowMoved(handle);
        Assert.Equal(400, canvas.Windows[handle].X);
        Assert.Equal(pin, canvas.IsPinnedToScreen(handle));
    }

    [Fact]
    public void PendingWorker_DoesNotReconcileOldPlacementAsManualDrag()
    {
        var api = new FakeWindowApi(); var canvas = new Canvas(); var input = new FakeInputRouter();
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        using var wm = new WindowManager(canvas, api, new FakeAppConfig { DisableGreedyDraw = true }, input, useAsyncProjection: true);
        api.AddWindow((IntPtr)1, 100, 100, 800, 600);
        wm.RegisterWindow((IntPtr)1);
        canvas.SetWindow((IntPtr)1, 600, 200, 800, 600);
        api.BeforeBatchMove = _ => { entered.Set(); resume.Wait(TimeSpan.FromSeconds(5)); };
        wm.Reproject();
        try
        {
            Assert.True(entered.Wait(5000));
            input.RaiseWindowMoved((IntPtr)1);
            Assert.Equal(600, canvas.Windows[(IntPtr)1].X);
        }
        finally { resume.Set(); }
        api.BeforeBatchMove = null;
        wm.ReprojectSync();
        Assert.Equal(600, canvas.Windows[(IntPtr)1].X);
    }

    private static (double, double, double, double) Bounds(WorldRect world)
        => (world.X, world.Y, world.W, world.H);
}
