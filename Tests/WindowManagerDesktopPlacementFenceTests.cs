using CanvasDesktop;

namespace CanvasDesktop.Tests;

public class WindowManagerDesktopPlacementFenceTests
{
    private static readonly IntPtr Target = (IntPtr)42;
    private static readonly IntPtr Other = (IntPtr)43;
    private static readonly BatchMoveItem Move = new(Target, new(200, 100, 800, 600), PosOnly: true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreviewRegionsWaitForOwnerRelease(bool previewsInitiallyVisible)
    {
        var api = new FakeWindowApi();
        using var wm = CreateClippedWindows(api);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var lease = Assert.IsType<ProjectionWorker.PlacementLease>(wm.TryAcquireDesktopPlacementLease([Target]));
        if (previewsInitiallyVisible) wm.UnclipAll(); // Pre-submission UI preparation still applies immediately.
        Assert.Equal(previewsInitiallyVisible ? 0 : 2, api.ClippedWindows.Count);
        api.ClipCalls.Clear();
        api.UnclipCalls.Clear();
        api.BeforeBatchMove = _ => { entered.Set(); release.Wait(TimeSpan.FromSeconds(5)); };
        var placement = lease.Submit([(Move, 1u)]);

        try
        {
            Assert.True(entered.Wait(5000));
            if (previewsInitiallyVisible) wm.ReclipAll(); else wm.UnclipAll();
            Assert.Empty(api.ClipCalls);
            Assert.Empty(api.UnclipCalls);
        }
        finally
        {
            release.Set();
            AwaitPlacement(placement);
        }

        // A native result has arrived. Even the other HWND could share its owner queue.
        if (previewsInitiallyVisible) wm.ReclipAll(); else wm.UnclipAll();
        Assert.Empty(api.ClipCalls);
        Assert.Empty(api.UnclipCalls);
        lease.Dispose();

        var writes = previewsInitiallyVisible ? api.ClipCalls : api.UnclipCalls;
        Assert.Equal(2, writes.Count);
        Assert.Contains(Target, writes);
        Assert.Contains(Other, writes);
        Assert.Equal(previewsInitiallyVisible ? 2 : 0, api.ClippedWindows.Count);
        lease.Dispose();
        Assert.Equal(2, writes.Count);
    }

    [Fact]
    public void DeferredPreviewWorkUsesCurrentPolicyAndWindowMembership()
    {
        var api = new FakeWindowApi();
        var input = new FakeInputRouter();
        using var wm = CreateClippedWindows(api, input);
        using var lease = Assert.IsType<ProjectionWorker.PlacementLease>(wm.TryAcquireDesktopPlacementLease([Target]));
        AwaitPlacement(lease.Submit([(Move, 1u)]));

        wm.SetLivePreviewWindows([Target, Other]);
        api.Windows.Remove(Target);
        input.RaiseWindowDestroyed(Target);
        wm.SetLivePreviewWindows([Target]); // The other window is no longer needed by the latest preview state.
        lease.Dispose();

        Assert.Empty(api.UnclipCalls);
        Assert.Contains(Other, api.ClippedWindows);
    }

    [Fact]
    public void ShutdownWaitsForAnUnsubmittedLeaseToRelease()
    {
        var api = new FakeWindowApi();
        using var wm = CreateClippedWindows(api);
        using var lease = Assert.IsType<ProjectionWorker.PlacementLease>(wm.TryAcquireDesktopPlacementLease([Target]));
        int shutdowns = 0;
        int batches = 0;
        api.BeforeBatchMove = _ => batches++;
        wm.ReprojectForDisplayChange();

        Assert.True(wm.TryDeferShutdownForDesktopPlacement(() => shutdowns++));
        Assert.True(wm.DesktopPlacementShutdownRequested);
        Assert.Equal(0, shutdowns);

        lease.Dispose();
        Assert.Equal(1, shutdowns);
        Assert.False(wm.TryDeferShutdownForDesktopPlacement(() => shutdowns++));
        Assert.Null(wm.TryAcquireDesktopPlacementLease([Other]));
        wm.Tick();
        Assert.Equal(0, batches);
        Assert.Equal(1, shutdowns);
    }

    [Fact]
    public void DeferredShutdownRunsOnceAfterReleaseAndSkipsPreviewReplay()
    {
        var api = new FakeWindowApi();
        using var wm = CreateClippedWindows(api);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var lease = Assert.IsType<ProjectionWorker.PlacementLease>(wm.TryAcquireDesktopPlacementLease([Target]));
        api.BeforeBatchMove = _ => { entered.Set(); release.Wait(TimeSpan.FromSeconds(5)); };
        var placement = lease.Submit([(Move, 1u)]);
        int shutdowns = 0;

        try
        {
            Assert.True(entered.Wait(5000));
            wm.UnclipAll();
            Assert.True(wm.TryDeferShutdownForDesktopPlacement(() =>
            {
                Assert.False(wm.TryDeferShutdownForDesktopPlacement(() => shutdowns += 10));
                shutdowns++;
            }));
            Assert.True(wm.TryDeferShutdownForDesktopPlacement(() => shutdowns += 100));
            Assert.Throws<InvalidOperationException>(lease.Dispose);
            Assert.Equal(0, shutdowns);
            Assert.Empty(api.UnclipCalls);
        }
        finally
        {
            release.Set();
            AwaitPlacement(placement);
        }

        Assert.Equal(0, shutdowns);
        lease.Dispose();
        lease.Dispose();
        wm.UnclipAll();
        Assert.Equal(1, shutdowns);
        Assert.Empty(api.UnclipCalls);
        Assert.Null(wm.TryAcquireDesktopPlacementLease([Other]));
    }

    [Fact]
    public void DeferredDisplayProjectionUsesLatestCameraAndMonitorLayoutOnce()
    {
        var canvas = new Canvas();
        var api = new FakeWindowApi { ScreenTopologyVersion = 1 };
        var clock = new FakeClock(1000);
        using var wm = new WindowManager(canvas, api, new FakeAppConfig(), new FakeInputRouter(), clock,
            useAsyncProjection: true);
        api.AddWindow(Target, 100, 100, 800, 600);
        api.AddWindow(Other, 100, 100, 800, 600);
        canvas.SetWindow(Target, 5000, 100, 800, 600);
        canvas.SetWindow(Other, 700, 100, 800, 600);
        wm.ReprojectForDisplayChange();
        Assert.Equal(1919, api.GetWindowRect(Target).x);

        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var lease = Assert.IsType<ProjectionWorker.PlacementLease>(wm.TryAcquireDesktopPlacementLease([Other]));
        int batches = 0;
        api.BeforeBatchMove = _ =>
        {
            if (Interlocked.Increment(ref batches) != 1) return;
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(5));
        };

        canvas.SetCamera(100, 0);
        api.ScreenTopologyVersion = 2;
        api.ScreenAreas = [(0, 0, 1280, 720)];
        wm.ReprojectForDisplayChange();
        wm.Tick();
        Assert.Equal(0, batches);
        var placement = lease.Submit([(new BatchMoveItem(Other, new(700, 100, 800, 600), PosOnly: true), 1u)]);
        try
        {
            Assert.True(entered.Wait(5000));
            canvas.SetCamera(200, 0);
            api.ScreenTopologyVersion = 3;
            api.ScreenAreas = [(0, 0, 1000, 720)];
            wm.ReprojectForDisplayChange();
            wm.Tick();
            Assert.Equal(1, batches);
        }
        finally
        {
            release.Set();
            AwaitPlacement(placement);
        }

        wm.Tick();
        Assert.Equal(1, batches);
        lease.Dispose();
        Assert.Equal(1, batches);
        wm.Tick();
        Assert.Equal(999, api.GetWindowRect(Target).x);
        Assert.Equal(500, api.GetWindowRect(Other).x);
        clock.Advance(1000);
        wm.Tick();
        Assert.Equal(2, batches);
    }

    [Fact]
    public void DeferredCameraProjectionWaitsForTheProjectionPolicyToResume()
    {
        var canvas = new Canvas();
        var api = new FakeWindowApi();
        var clock = new FakeClock(1000);
        using var wm = new WindowManager(canvas, api, new FakeAppConfig(), new FakeInputRouter(), clock,
            useAsyncProjection: true);
        api.AddWindow(Target, 700, 100, 800, 600);
        wm.RegisterWindow(Target);
        using var finished = new ManualResetEventSlim();
        using var lease = Assert.IsType<ProjectionWorker.PlacementLease>(wm.TryAcquireDesktopPlacementLease([Target]));
        api.AfterBatchMove = () => finished.Set();

        canvas.SetCamera(100, 0);
        canvas.SetCamera(200, 0);
        wm.SuspendProjection = true;
        lease.Dispose();
        wm.Tick();
        Assert.False(finished.IsSet);
        Assert.Equal(700, api.GetWindowRect(Target).x);

        wm.SuspendProjection = false;
        wm.Tick();
        Assert.True(finished.Wait(5000));
        Assert.Equal(500, api.GetWindowRect(Target).x);
    }

    [Fact]
    public void DisplayProjectionCannotReenterAndReservePlacementDuringNativeWrite()
    {
        var api = new FakeWindowApi();
        using var wm = CreateClippedWindows(api);
        api.BeforeBatchMove = _ =>
        {
            using var lease = wm.TryAcquireDesktopPlacementLease([Target]);
            Assert.Null(lease);
        };

        wm.ReprojectForDisplayChange();
        using var next = Assert.IsType<ProjectionWorker.PlacementLease>(wm.TryAcquireDesktopPlacementLease([Target]));
    }

    private static WindowManager CreateClippedWindows(FakeWindowApi api, FakeInputRouter? input = null)
    {
        var canvas = new Canvas();
        foreach (IntPtr handle in new[] { Target, Other })
        {
            api.AddWindow(handle, 100, 100, 800, 600);
            canvas.SetWindow(handle, 5000, 100, 800, 600);
        }

        var wm = new WindowManager(canvas, api, new FakeAppConfig(), input ?? new FakeInputRouter(), useAsyncProjection: true);
        wm.ReprojectSync();
        return wm;
    }

    // Keep assertions and release on the thread that owns the lease.
    private static void AwaitPlacement(Task<WindowBatchResult> placement)
        => placement.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
}
