using System;
using CanvasDesktop;
using Xunit;
using Windows.Win32.UI.WindowsAndMessaging;

namespace CanvasDesktop.Tests;

public class WindowVisibilityTests
{
    private static readonly IntPtr Handle = (IntPtr)42;
    private static readonly WorkspaceWindowIdentity Identity = new(Handle, 7, 100);
    private static readonly (int x, int y, int w, int h)[] Screens = { (0, 0, 1000, 800) };
    private readonly WindowVisibilityTracker _tracker = new();
    private WindowVisibilitySnapshot Observe(int x, long now = 0, bool minimized = false,
        bool eligible = true, WorkspaceWindowIdentity? identity = null)
        => _tracker.Observe(Handle, identity ?? Identity, new(x, 0, 200, 100), Screens, minimized, eligible, now);

    [Theory]
    [InlineData(999, (int)RuntimeWindowVisibility.Visible)]
    [InlineData(1000, (int)RuntimeWindowVisibility.NearViewport)]
    [InlineData(1511, (int)RuntimeWindowVisibility.NearViewport)]
    [InlineData(1512, (int)RuntimeWindowVisibility.OffScreen)]
    public void ClassifiesLogicalProjection(int x, int expected)
        => Assert.Equal((RuntimeWindowVisibility)expected, Observe(x).State);

    [Fact]
    public void MarginsProvideHysteresis()
    {
        Observe(1512);
        Assert.Equal(RuntimeWindowVisibility.OffScreen, Observe(1300).State);
        Assert.Equal(RuntimeWindowVisibility.NearViewport, Observe(1255).State);
        Assert.Equal(RuntimeWindowVisibility.NearViewport, Observe(1300).State);
    }

    [Fact]
    public void ParkingRequiresContinuousEligibleDwell()
    {
        Assert.False(Observe(2000).CanPark);
        Assert.False(Observe(2000, 1499).CanPark);
        Assert.True(Observe(2000, 1500).CanPark);
        Assert.False(Observe(2000, 1600, eligible: false).CanPark);
        Assert.False(Observe(2000, 3000).CanPark);
        Assert.True(Observe(2000, 4500).CanPark);
        Observe(0, 4600);
        Assert.False(Observe(2000, 6000).CanPark);
    }

    private AutomaticParkingToken BeginParking()
    {
        Observe(2000);
        Observe(2000, 1500);
        var token = _tracker.BeginParking(Handle);
        Assert.NotNull(token);
        Assert.Null(_tracker.BeginParking(Handle));
        return token.Value;
    }

    [Fact]
    public void OwnershipRequiresNativeSuccessAndConfirmedMinimization()
    {
        var token = BeginParking();
        Assert.True(_tracker.CompleteParking(token, NativeOperationResult.Success("Minimize", Handle), true));
        var parked = Observe(2000, 1600, minimized: true);
        Assert.Equal(RuntimeWindowVisibility.AutomaticallyMinimized, parked.State);
        Assert.False(parked.ShouldRestore);
        Assert.False(Observe(1200, 1700, minimized: true).ShouldRestore);
        Assert.False(Observe(1200, 1849, minimized: true).ShouldRestore);
        Assert.True(Observe(1200, 1850, minimized: true).ShouldRestore);
        // Leaving the approach band cancels the restore timer.
        Assert.False(Observe(2000, 1900, minimized: true).ShouldRestore);
        Assert.True(Observe(0, 1901, minimized: true).ShouldRestore);
        Assert.Equal(RuntimeWindowVisibility.Visible, Observe(0, 1902).State);
        Assert.Equal(RuntimeWindowVisibility.UserMinimized, Observe(0, 1903, minimized: true).State);
    }

    [Theory]
    [InlineData((int)NativeOperationStatus.Failed, true)]
    [InlineData((int)NativeOperationStatus.Cancelled, true)]
    [InlineData((int)NativeOperationStatus.Queued, true)]
    [InlineData((int)NativeOperationStatus.Succeeded, false)]
    public void FailedOrUnconfirmedParkingDoesNotOwnUserIntent(int status, bool minimized)
    {
        var token = BeginParking();
        Assert.False(_tracker.CompleteParking(token, new("Minimize", Handle.ToInt64(), (NativeOperationStatus)status, null, 0), minimized));
        Assert.Equal(RuntimeWindowVisibility.UserMinimized, Observe(2000, 1600, minimized: true).State);
        Assert.False(Observe(0, 2000, minimized: true).ShouldRestore);
    }

    [Fact]
    public void RecycledProcessAndObservationLifetimeRejectOldTokens()
    {
        var token = BeginParking();
        Observe(2000, 1600, identity: Identity with { ProcessStartedAtUtcTicks = 200 });
        Assert.False(_tracker.IsCurrent(token));
        Assert.False(_tracker.CompleteParking(token, NativeOperationResult.Success("Minimize", Handle), true));
        _tracker.Remove(Handle);
        Observe(2000, 2000);
        Assert.False(_tracker.IsCurrent(token));
        _tracker.Clear();
        Assert.Null(_tracker.Get(Handle));
    }

    [Fact]
    public void UnavailableIdentityNeverAllowsParking()
    {
        _tracker.Observe(Handle, null, new(2000, 0, 200, 100), Screens, false, true, 0);
        var snapshot = _tracker.Observe(Handle, null, new(2000, 0, 200, 100), Screens, false, true, 5000);
        Assert.False(snapshot.CanPark);
        Assert.Null(_tracker.BeginParking(Handle));
    }

    [Fact]
    public void InvalidTopologyDoesNotAuthorizeParking()
    {
        var screens = new[] { (0, 0, 0, 800) };
        _tracker.Observe(Handle, Identity, new(2000, 0, 200, 100), screens, false, true, 0);
        Assert.False(_tracker.Observe(Handle, Identity, new(2000, 0, 200, 100),
            screens, false, true, 5000).CanPark);
    }

    [Fact]
    public void ManagerRefreshesIdentityOnTickAndExcludesPinnedAndMaximizedWindows()
    {
        var canvas = new Canvas();
        var api = new FakeWindowApi();
        var clock = new FakeClock();
        long started = 100;
        int inspections = 0;
        api.AddWindow(Handle, 4000, 0, 400, 300, pid: 7);
        using var manager = new WindowManager(canvas, api, new FakeAppConfig(), new FakeInputRouter(), clock,
            inspectProcess: pid => { inspections++; return new(pid, started, "test.exe"); });
        manager.RegisterWindow(Handle);
        int initialInspections = inspections;
        manager.SuspendProjection = true;
        canvas.SetCamera(1, 0);
        Assert.Equal(initialInspections, inspections);
        started = 200;
        clock.Now = 2000;
        manager.Tick();
        Assert.Equal(200, manager.GetRuntimeVisibility(Handle)!.Value.Identity!.Value.ProcessStartedAtUtcTicks);
        Assert.False(manager.GetRuntimeVisibility(Handle)!.Value.CanPark);
        clock.Now = 3500;
        manager.Tick();
        Assert.True(manager.GetRuntimeVisibility(Handle)!.Value.CanPark);
        canvas.SetPinnedToScreen(Handle, true);
        manager.Tick();
        Assert.False(manager.GetRuntimeVisibility(Handle)!.Value.CanPark);
        canvas.SetPinnedToScreen(Handle, false);
        api.Windows[Handle].Style = (int)WINDOW_STYLE.WS_MAXIMIZE;
        manager.Tick();
        Assert.False(manager.GetRuntimeVisibility(Handle)!.Value.CanPark);
        api.Windows[Handle].Visible = false;
        manager.Tick();
        Assert.Null(manager.GetRuntimeVisibility(Handle));
    }

    [Fact]
    public void MultipleMonitorsAndLargeCoordinatesUseWideArithmetic()
    {
        var screens = new[] { (-1000, 0, 800, 800), (int.MaxValue - 100, 0, 100, 800) };
        Assert.Equal(RuntimeWindowVisibility.Visible, _tracker.Observe(Handle, Identity,
            new(int.MaxValue - 1, 0, 200, 100), screens, false, true, 0).State);
        Assert.Equal(RuntimeWindowVisibility.Visible, _tracker.Observe(Handle, Identity,
            new(-1000, 0, 200, 100), screens, false, true, 1).State);
    }

    [Fact]
    public void ManagerTracksWhileProjectionSuspendedWithoutChangingSavedLayout()
    {
        var canvas = new Canvas();
        var api = new FakeWindowApi();
        var clock = new FakeClock();
        var input = new FakeInputRouter();
        api.AddWindow(Handle, 4000, 0, 400, 300, pid: 7);
        using var manager = new WindowManager(canvas, api, new FakeAppConfig(), input, clock,
            inspectProcess: pid => new(pid, 100, "test.exe"));
        manager.RegisterWindow(Handle);
        var before = canvas.Windows[Handle];
        clock.Now = 1500;
        manager.Tick();
        Assert.True(manager.GetRuntimeVisibility(Handle)!.Value.CanPark);
        Assert.Empty(api.SetPositionCalls);
        Assert.Empty(api.NativeStateCalls);
        Assert.Equal(before, canvas.Windows[Handle]);
        manager.SuspendProjection = true;
        canvas.SetCamera(4000, 0);
        Assert.Equal(RuntimeWindowVisibility.Visible, manager.GetRuntimeVisibility(Handle)!.Value.State);
        Assert.Equal(before, canvas.Windows[Handle]);
        api.Windows[Handle].Style = (int)WINDOW_STYLE.WS_MINIMIZE;
        input.RaiseWindowMinimized(Handle);
        Assert.Equal(RuntimeWindowVisibility.UserMinimized, manager.GetRuntimeVisibility(Handle)!.Value.State);
        Assert.True(canvas.IsCollapsed(Handle));
        manager.RemoveWindow(Handle);
        Assert.Null(manager.GetRuntimeVisibility(Handle));
    }
}

