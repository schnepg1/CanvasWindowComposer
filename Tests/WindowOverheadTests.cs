using CanvasDesktop;

namespace CanvasDesktop.Tests;

public class WindowOverheadTests
{
    [Fact]
    public void RepeatedProjectionSkipsAcceptedMovesButStillAppliesResize()
    {
        var api = new FakeWindowApi(); var canvas = new Canvas();
        using var manager = new WindowManager(canvas, api, new FakeAppConfig { DisableGreedyDraw = true }, new FakeInputRouter());
        api.AddWindow((IntPtr)1, 100, 100, 400, 300);
        manager.RegisterWindow((IntPtr)1);
        canvas.SetWindow((IntPtr)1, 200, 100, 500, 300);
        manager.ReprojectSync();
        Assert.Single(api.LastBatch);
        manager.ReprojectSync(); Assert.Empty(api.LastBatch);
        // Batch projection is position-only; the individual path must still resize.
        Assert.True(manager.ReprojectWindow((IntPtr)1));
        Assert.Single(api.SetPositionCalls);
        Assert.Equal((200, 100, 500, 300), api.GetWindowRect((IntPtr)1));
        Assert.True(manager.ReprojectWindow((IntPtr)1));
        Assert.Single(api.SetPositionCalls);
    }

    [Fact]
    public void FailedMoveIsRetriedEvenWhenTargetMatchesPreviousPlacement()
    {
        var api = new FakeWindowApi(); var canvas = new Canvas();
        using var manager = new WindowManager(canvas, api, new FakeAppConfig { DisableGreedyDraw = true }, new FakeInputRouter());
        api.AddWindow((IntPtr)1, 100, 100, 400, 300); manager.RegisterWindow((IntPtr)1);
        api.FailedMoves.Add((IntPtr)1);
        manager.RestorePlacement((IntPtr)1, new() { X = 100, Y = 100, W = 400, H = 300 });
        api.FailedMoves.Clear(); manager.ReprojectSync();
        Assert.Single(api.LastBatch);
        manager.ReprojectSync(); Assert.Empty(api.LastBatch);
    }

    [Fact]
    public void WorkAreaNotificationRepositionsPreviouslyClippedWindowsOnTick()
    {
        var api = new FakeWindowApi { ScreenTopologyVersion = 0 }; var canvas = new Canvas();
        using var manager = new WindowManager(canvas, api, new FakeAppConfig(), new FakeInputRouter());
        api.AddWindow((IntPtr)1, 0, 0, 400, 300); canvas.SetWindow((IntPtr)1, 5000, 100, 400, 300);
        manager.ReprojectSync(); Assert.Equal(1919, api.Windows[(IntPtr)1].X);
        api.ScreenAreas = new() { (-1280, 0, 1280, 720) }; api.ScreenTopologyVersion++;
        manager.Tick();
        Assert.Equal(-1, api.Windows[(IntPtr)1].X);
        Assert.Equal(5000, canvas.Windows[(IntPtr)1].X);
    }

    [Fact]
    public void SelectivePreviewsUnclipOnlyRequestedSourcesAndRetainOtherClips()
    {
        var api = new FakeWindowApi(); var canvas = new Canvas();
        using var manager = new WindowManager(canvas, api, new FakeAppConfig(), new FakeInputRouter());
        for (int i = 1; i <= 3; i++)
        { api.AddWindow((IntPtr)i, 0, 0, 400, 300); canvas.SetWindow((IntPtr)i, 5000 + i * 500, 100, 400, 300); }
        manager.ReprojectSync();
        manager.SetLivePreviewWindows([(IntPtr)1]);
        Assert.Equal(new[] { (IntPtr)1 }, api.UnclipCalls);
        Assert.DoesNotContain((IntPtr)1, api.ClippedWindows);
        Assert.Contains((IntPtr)2, api.ClippedWindows);
        manager.SetLivePreviewWindows([(IntPtr)1]); Assert.Single(api.UnclipCalls);
        manager.SetLivePreviewWindows([(IntPtr)2]);
        Assert.Contains((IntPtr)1, api.ClippedWindows);
        Assert.DoesNotContain((IntPtr)2, api.ClippedWindows);
        manager.SetLivePreviewWindows([]);
        Assert.Equal(3, api.ClippedWindows.Count);
    }

    [Fact]
    public void AltTabAndOverviewShareRegionOwnershipWithoutClosingEachOthersPreviews()
    {
        var api = new FakeWindowApi(); var canvas = new Canvas(); var input = new FakeInputRouter();
        using var manager = new WindowManager(canvas, api, new FakeAppConfig(), input);
        for (int i = 1; i <= 2; i++)
        { api.AddWindow((IntPtr)i, 0, 0, 400, 300); canvas.SetWindow((IntPtr)i, 5000 + i * 500, 100, 400, 300); }
        manager.ReprojectSync(); manager.SetLivePreviewWindows([(IntPtr)1]);
        input.RaiseAltTabStarted(); Assert.Empty(api.ClippedWindows);
        input.RaiseAltTabEnded();
        Assert.DoesNotContain((IntPtr)1, api.ClippedWindows);
        Assert.Contains((IntPtr)2, api.ClippedWindows);
        manager.SetLivePreviewWindows([]); Assert.Equal(2, api.ClippedWindows.Count);
    }

    [Fact]
    public void FailedPreviewRegionChangesRemainRetryable()
    {
        var api = new FakeWindowApi(); var canvas = new Canvas();
        using var manager = new WindowManager(canvas, api, new FakeAppConfig(), new FakeInputRouter());
        api.AddWindow((IntPtr)1, 0, 0, 400, 300); canvas.SetWindow((IntPtr)1, 5000, 100, 400, 300);
        manager.ReprojectSync(); api.FailedUnclips.Add((IntPtr)1);
        manager.SetLivePreviewWindows([(IntPtr)1]); Assert.Contains((IntPtr)1, api.ClippedWindows);
        api.FailedUnclips.Clear(); manager.SetLivePreviewWindows([(IntPtr)1]); Assert.Empty(api.ClippedWindows);
        api.FailedClips.Add((IntPtr)1); manager.SetLivePreviewWindows([]); Assert.Empty(api.ClippedWindows);
        api.FailedClips.Clear(); manager.SetLivePreviewWindows([]); Assert.Contains((IntPtr)1, api.ClippedWindows);
    }
}
