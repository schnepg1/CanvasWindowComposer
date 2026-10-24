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

    [Fact]
    public void QueuedNativeMoveDoesNotSaveTheOldScreenPositionUnderTheNewCamera()
    {
        using var rig = new WorkspaceHarness(config: new FakeAppConfig { DisableGreedyDraw = true });
        IntPtr handle = (IntPtr)42;
        rig.Api.AddWindow(handle, 100, 100, 800, 600, title: "ChatGPT");
        rig.Workspace.Start();
        var item = Assert.Single(rig.Workspace.Document.Items);
        var saved = item.Bounds;
        rig.Api.QueueAsyncMoves = true;
        rig.Canvas.SetCamera(0, -500);
        rig.Wm.ReprojectSync(isAsync: true);
        // USER32 accepted the async request, but the app has not processed it yet.
        rig.Input.RaiseWindowMoved(handle);
        rig.Workspace.SaveNow();
        Assert.Equal(saved, item.Bounds);

        rig.Canvas.SetCamera(0, -600);
        rig.Wm.ReprojectSync(isAsync: true);
        rig.Api.Windows[handle].Y = 600; // An older queued move arrives first.
        rig.Input.RaiseWindowMoved(handle);
        rig.Workspace.SaveNow();
        Assert.Equal(saved, item.Bounds);

        rig.Api.Windows[handle].Y = 700; // The latest queued move finally arrives.
        rig.Input.RaiseWindowMoved(handle);
        rig.Workspace.SaveNow();
        Assert.Equal(saved, item.Bounds);
        // Once acknowledged, subsequent real movement must still be captured.
        rig.Api.Windows[handle].Y = 750;
        rig.Input.RaiseWindowMoved(handle);
        rig.Workspace.SaveNow();
        Assert.Equal(saved.Y + 50, item.Bounds.Y);
    }

    private static (double, double, double, double) Bounds(WorldRect world)
        => (world.X, world.Y, world.W, world.H);
}
