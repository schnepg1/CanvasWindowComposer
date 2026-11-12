using System;
using Xunit;
using CanvasDesktop;

namespace CanvasDesktop.Tests;

public class DesktopStateCacheTests
{
    private sealed class Harness : IDisposable
    {
        public Canvas Canvas = new();
        public FakeWindowApi Api = new();
        public FakeAppConfig Config = new();
        public FakeClock Clock = new();
        public FakeInputRouter Input = new();
        public FakeOverviewController Overview = new();
        public FakeVirtualDesktops Vds = new();
        public WindowManager Wm = null!;
        public DesktopStateCache Cache = null!;

        public Harness(Guid? initialDesktop = null, bool useAsyncProjection = false)
        {
            Vds.CurrentDesktopId = initialDesktop ?? Guid.Empty;
            Wm = new WindowManager(Canvas, Api, Config, Input, Clock, useAsyncProjection: useAsyncProjection);
            Cache = new DesktopStateCache(Canvas, Wm, Overview, Vds);
        }

        public void Dispose()
        {
            Cache.Dispose();
            Wm.Dispose();
        }
    }

    private static readonly Guid GuidA = new("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid GuidB = new("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid GuidC = new("cccccccc-0000-0000-0000-000000000003");
    private static readonly IntPtr Target = (IntPtr)42;

    [Fact]
    public void SwitchWaitsForPlacementAndOverviewThenLoadsOnlyTheLatestDesktop()
    {
        using var h = new Harness(GuidA, useAsyncProjection: true);
        h.Api.AddWindow(Target, 100, 100, 800, 600);
        h.Canvas.SetWindow(Target, 100, 100, 800, 600);
        h.Overview.DesktopHandoffPending = true;
        var saved = new List<Guid>();
        var loaded = new List<Guid>();
        h.Cache.BeforeStateSaved += () => saved.Add(h.Cache.CurrentDesktopId);
        h.Cache.AfterStateLoaded += () => loaded.Add(h.Cache.CurrentDesktopId);

        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        h.Api.BeforeBatchMove = _ => { entered.Set(); release.Wait(TimeSpan.FromSeconds(10)); };
        using var lease = Assert.IsType<ProjectionWorker.PlacementLease>(h.Wm.TryAcquireDesktopPlacementLease([Target]));
        var placement = lease.Submit([(new BatchMoveItem(Target, new(200, 100, 800, 600), PosOnly: true), 1u)]);

        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            h.Vds.SwitchTo(GuidB);
            h.Vds.SwitchTo(GuidC);
            h.Cache.ApplyPendingSwitch();

            Assert.True(h.Cache.HasPendingSwitch);
            Assert.Equal(GuidA, h.Cache.CurrentDesktopId);
            Assert.Equal(1, h.Overview.DesktopHandoffRecoveryRequests);
            Assert.Equal(0, h.Overview.CancelInteractionCalls);
            Assert.Empty(saved);
            Assert.Empty(loaded);
            Assert.True(h.Canvas.HasWindow(Target));
        }
        finally
        {
            release.Set();
            AwaitPlacement(placement);
        }

        h.Cache.ApplyPendingSwitch(); // Native acknowledgement does not release the reservation.
        Assert.Empty(loaded);
        lease.Dispose();
        h.Cache.ApplyPendingSwitch(); // Overview still owns the stopping click.
        Assert.Empty(loaded);

        h.Overview.DesktopHandoffPending = false;
        h.Cache.ApplyPendingSwitch();
        h.Cache.ApplyPendingSwitch();
        Assert.False(h.Cache.HasPendingSwitch);
        Assert.Equal([GuidA], saved);
        Assert.Equal([GuidC], loaded);
        Assert.Empty(h.Canvas.Windows);
        Assert.Equal(1, h.Overview.CancelInteractionCalls);
        Assert.Equal(1, h.Overview.SyncCameraCalls);
    }

    [Fact]
    public void SwitchBackBeforeReleaseKeepsTheLoadedDesktop()
    {
        using var h = new Harness(GuidA, useAsyncProjection: true);
        h.Canvas.Pan(120, 30);
        var camera = h.Canvas.Camera;
        int loaded = 0;
        h.Cache.AfterStateLoaded += () => loaded++;
        using var lease = Assert.IsType<ProjectionWorker.PlacementLease>(h.Wm.TryAcquireDesktopPlacementLease([Target]));

        h.Vds.SwitchTo(GuidB);
        h.Vds.SwitchTo(GuidA);
        lease.Dispose();
        h.Cache.ApplyPendingSwitch();

        Assert.False(h.Cache.HasPendingSwitch);
        Assert.Equal(camera, h.Canvas.Camera);
        Assert.Equal(0, loaded);
    }

    [Fact]
    public void ShutdownTakesPriorityOverPendingSwitch()
    {
        using var h = new Harness(GuidA, useAsyncProjection: true);
        int loaded = 0;
        h.Cache.AfterStateLoaded += () => loaded++;
        using var lease = Assert.IsType<ProjectionWorker.PlacementLease>(h.Wm.TryAcquireDesktopPlacementLease([Target]));

        h.Vds.SwitchTo(GuidB);
        Assert.True(h.Wm.TryDeferShutdownForDesktopPlacement(() => { }));
        lease.Dispose();
        h.Cache.ApplyPendingSwitch();

        Assert.Equal(GuidA, h.Cache.CurrentDesktopId);
        Assert.Equal(0, loaded);
        Assert.Equal(0, h.Overview.CancelInteractionCalls);
    }

    // Keep the test and lease disposal on the owner thread.
    private static void AwaitPlacement(Task<WindowBatchResult> placement)
        => placement.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();

    [Fact]
    public void DisposedCacheDoesNotResetCanvasOnLaterDesktopEvents()
    {
        var h = new Harness(initialDesktop: GuidA);
        h.Canvas.SetWindow((IntPtr)1, 5000, 5000, 800, 600);
        h.Api.AddWindow((IntPtr)1, 100, 100, 800, 600);
        h.Cache.Dispose();
        h.Cache.Dispose();
        h.Vds.SwitchTo(GuidB);
        Assert.True(h.Canvas.HasWindow((IntPtr)1));
        Assert.Empty(h.Api.LastBatch);
    }

    [Fact]
    public void Switch_FromEmptyInitial_DoesNotResetWmOrSaveState()
    {
        var h = new Harness(initialDesktop: Guid.Empty);
        h.Canvas.SetWindow((IntPtr)1, 100, 100, 800, 600);
        h.Api.AddWindow((IntPtr)1, 100, 100, 800, 600);

        h.Vds.SwitchTo(GuidA);

        // Initial _lastDesktopId was Empty, so Reset shouldn't have run; the
        // window stays in canvas.
        Assert.True(h.Canvas.HasWindow((IntPtr)1));
    }

    [Fact]
    public void Switch_CancelsOverviewInertia()
    {
        var h = new Harness(initialDesktop: GuidA);
        h.Overview.PendingInteraction = true;

        h.Vds.SwitchTo(GuidB);

        Assert.Equal(1, h.Overview.CancelInertiaCalls);
        Assert.Equal(1, h.Overview.CancelInteractionCalls);
        Assert.False(h.Overview.PendingInteraction);
    }

    [Fact]
    public void Switch_FiresAfterStateLoaded()
    {
        var h = new Harness(initialDesktop: GuidA);
        int fired = 0;
        h.Cache.AfterStateLoaded += () => fired++;

        h.Vds.SwitchTo(GuidB);

        Assert.Equal(1, fired);
    }

    [Fact]
    public void SwitchAndReturn_RestoresPanAndZoom()
    {
        var h = new Harness(initialDesktop: GuidA);
        // Pan canvas while on desktop A
        h.Canvas.Pan(50, 30); // CamX=-50, CamY=-30
        double camAx = h.Canvas.CamX;
        double camAy = h.Canvas.CamY;

        // Switch to B then back to A — A's camera should be restored.
        h.Vds.SwitchTo(GuidB);
        h.Vds.SwitchTo(GuidA);

        Assert.Equal(camAx, h.Canvas.CamX);
        Assert.Equal(camAy, h.Canvas.CamY);
    }

    [Fact]
    public void SwitchToNewDesktop_ResetsCanvasCamera()
    {
        var h = new Harness(initialDesktop: GuidA);
        h.Canvas.Pan(50, 30);

        // First-time visit to GuidB — no saved state, so camera resets via WM.Reset.
        h.Vds.SwitchTo(GuidB);

        Assert.Equal(0, h.Canvas.CamX);
        Assert.Equal(0, h.Canvas.CamY);
        Assert.Equal(1.0, h.Canvas.Zoom);
    }

    [Fact]
    public void SwitchAndReturn_KeepsIndependentStatesPerDesktop()
    {
        var h = new Harness(initialDesktop: GuidA);
        h.Canvas.Pan(100, 0);  // A: CamX = -100

        h.Vds.SwitchTo(GuidB);
        h.Canvas.Pan(0, 200); // B: CamY = -200 (CamX 0 from reset)

        h.Vds.SwitchTo(GuidA);
        Assert.Equal(-100, h.Canvas.CamX);
        Assert.Equal(0, h.Canvas.CamY);

        h.Vds.SwitchTo(GuidB);
        Assert.Equal(0, h.Canvas.CamX);
        Assert.Equal(-200, h.Canvas.CamY);
    }

    [Fact]
    public void Switch_ResetsWindowManagerState()
    {
        var h = new Harness(initialDesktop: GuidA);
        h.Canvas.SetWindow((IntPtr)1, 100, 100, 400, 300);
        h.Api.AddWindow((IntPtr)1, 100, 100, 400, 300);

        h.Vds.SwitchTo(GuidB);

        // WM.Reset clears the canvas window map (each desktop's windows are
        // re-discovered after the switch).
        Assert.Empty(h.Canvas.Windows);
    }
}
