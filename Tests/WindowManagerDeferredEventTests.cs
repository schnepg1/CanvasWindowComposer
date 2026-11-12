using CanvasDesktop;
using Windows.Win32.UI.WindowsAndMessaging;

namespace CanvasDesktop.Tests;

public class WindowManagerDeferredEventTests
{
    private static readonly IntPtr Target = (IntPtr)42;
    private static readonly IntPtr Other = (IntPtr)43;

    [Fact]
    public void RestoreAndStateProjectionWaitForOwnerAndUseCurrentCamera()
    {
        var canvas = new Canvas();
        var api = new FakeWindowApi();
        var input = new FakeInputRouter();
        using var wm = CreateManager(canvas, api, input);
        RegisterMinimizedWindow(canvas, api, wm);
        api.AddWindow(Other, 100, 100, 800, 600);
        using var lease = Assert.IsType<ProjectionWorker.PlacementLease>(wm.TryAcquireDesktopPlacementLease([Other]));
        int restored = 0;
        wm.WindowStateChanged += _ => restored++;

        api.Windows[Target].Style = 0;
        input.RaiseWindowRestored(Target);
        input.RaiseWindowRestored(Target);
        canvas.ExpandWindow(Target); // The Canvas state callback must wait too.
        canvas.SetCamera(50, 0);
        Assert.Empty(api.SetPositionCalls);
        Assert.Equal(0, restored);

        AwaitPlacement(lease.Submit([(new BatchMoveItem(Other, new(100, 100, 800, 600), PosOnly: true), 1u)]));
        wm.Tick();
        Assert.Empty(api.SetPositionCalls);
        Assert.Equal(0, restored);
        lease.Dispose();
        wm.Tick();
        wm.Tick();

        Assert.Equal(1, restored);
        Assert.Equal(WindowState.Normal, canvas.GetWindowState(Target));
        Assert.Equal((250, 100, 800, 600), api.GetWindowRect(Target));
        Assert.Equal(Target, Assert.Single(api.SetPositionCalls).hWnd);
    }

    [Fact]
    public void ANewMinimizeTakesPriorityOverAnEarlierRestore()
    {
        var canvas = new Canvas();
        var api = new FakeWindowApi();
        var input = new FakeInputRouter();
        using var wm = CreateManager(canvas, api, input);
        RegisterMinimizedWindow(canvas, api, wm);
        using var lease = Assert.IsType<ProjectionWorker.PlacementLease>(wm.TryAcquireDesktopPlacementLease([Target]));

        api.Windows[Target].Style = 0;
        input.RaiseWindowRestored(Target);
        api.Windows[Target].Style = (int)WINDOW_STYLE.WS_MINIMIZE;
        input.RaiseWindowMinimized(Target);
        lease.Dispose();
        wm.Tick();

        Assert.True(canvas.IsCollapsed(Target));
        Assert.Empty(api.SetPositionCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeferredRestoreDoesNotTouchAReusedHandle(bool observedDestruction)
    {
        var canvas = new Canvas();
        var api = new FakeWindowApi();
        var input = new FakeInputRouter();
        long processStart = 10;
        using var wm = CreateManager(canvas, api, input, pid => new(pid, processStart, "app.exe"));
        RegisterMinimizedWindow(canvas, api, wm);
        using var lease = Assert.IsType<ProjectionWorker.PlacementLease>(wm.TryAcquireDesktopPlacementLease([Target]));
        api.Windows[Target].Style = 0;
        input.RaiseWindowRestored(Target);
        int restores = 0;
        wm.WindowStateChanged += _ => restores++;

        api.Windows.Remove(Target);
        if (observedDestruction) input.RaiseWindowDestroyed(Target);
        else processStart++;
        api.AddWindow(Target, 500, 100, 800, 600);
        input.RaiseWindowShown(Target);
        Assert.True(canvas.HasWindow(Target));
        lease.Dispose();
        wm.Tick();

        Assert.Equal(0, restores);
        Assert.Empty(api.SetPositionCalls);
        Assert.Equal(500, api.GetWindowRect(Target).x);
    }

    [Fact]
    public void LatestCarriedWindowFocusReplaysAfterOwnedCoverTakesFocus()
    {
        var canvas = new Canvas();
        var api = new FakeWindowApi();
        var input = new FakeInputRouter();
        using var wm = CreateManager(canvas, api, input);
        foreach (IntPtr handle in new[] { Target, Other })
        {
            api.AddWindow(handle, 100, 100, 800, 600);
            Assert.True(wm.TryCarryWindow(handle, new WorldRect { X = 100, Y = 100, W = 800, H = 600 }));
        }
        IntPtr cover = (IntPtr)44;
        api.AddWindow(cover, 0, 0, 800, 600, pid: (uint)Environment.ProcessId);
        api.ClipCalls.Clear();
        var inventory = new List<IntPtr>();
        wm.InventoryWindowFocused += handle =>
        {
            using var reentrant = wm.TryAcquireDesktopPlacementLease([handle]);
            Assert.Null(reentrant);
            inventory.Add(handle);
        };

        using var lease = Assert.IsType<ProjectionWorker.PlacementLease>(wm.TryAcquireDesktopPlacementLease([Target]));
        input.RaiseWindowFocused(Target);
        input.RaiseWindowFocused(Other);
        api.Foreground = cover;
        input.RaiseWindowFocused(cover);
        wm.Tick();
        Assert.Empty(api.ClipCalls);
        Assert.Empty(inventory);
        lease.Dispose();
        input.RaiseWindowFocused(cover);
        wm.Tick();
        wm.Tick();

        Assert.Equal(Other, Assert.Single(api.ClipCalls));
        Assert.Equal(Other, Assert.Single(inventory));
    }

    private static WindowManager CreateManager(Canvas canvas, FakeWindowApi api, FakeInputRouter input,
        Func<uint, WorkspaceProcessIdentity?>? inspectProcess = null)
        => new(canvas, api, new FakeAppConfig { DisableGreedyDraw = true }, input,
            useAsyncProjection: true, inspectProcess: inspectProcess ?? (pid => new(pid, 10, "app.exe")))
        { SuspendProjection = true };

    private static void RegisterMinimizedWindow(Canvas canvas, FakeWindowApi api, WindowManager wm)
    {
        api.AddWindow(Target, 100, 100, 800, 600);
        wm.RegisterWindow(Target);
        canvas.SetWindow(Target, 300, 100, 800, 600);
        api.Windows[Target].Style = (int)WINDOW_STYLE.WS_MINIMIZE;
        canvas.CollapseWindow(Target);
    }

    // Lease release must stay on the owner thread.
    private static void AwaitPlacement(Task<WindowBatchResult> placement)
        => placement.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
}
