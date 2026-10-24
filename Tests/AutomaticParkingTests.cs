using System;
using System.Drawing;
using System.Threading;
using CanvasDesktop;
using Windows.Win32.UI.WindowsAndMessaging;
using Xunit;

namespace CanvasDesktop.Tests;

public class AutomaticParkingTests
{
    private sealed class FakeCapture : IWindowPreviewCapture
    {
        public bool Fail;
        public Bitmap? Capture(IntPtr handle, Size source, Size target)
            => Fail ? null : new Bitmap(target.Width, target.Height);
    }
    private sealed class Harness : IDisposable
    {
        public readonly Canvas Canvas = new();
        public readonly FakeWindowApi Api = new();
        public readonly FakeInputRouter Input = new();
        public readonly FakeClock Clock = new();
        public readonly FakeAppConfig Config = new() { EnableAutomaticMinimization = true, DisableGreedyDraw = true };
        public readonly FakeVirtualDesktops Desktops = new();
        public readonly FakeCapture Capture = new();
        public readonly WindowManager Manager;
        public long Started = 100;
        public int ProcessInspections;
        public readonly IntPtr Handle = (IntPtr)42;
        public Harness()
        {
            var cache = new WindowPreviewCache(Capture);
            Manager = new(Canvas, Api, Config, Input, Clock, Desktops,
                inspectProcess: pid => { ProcessInspections++; return new(pid, Started, "app.exe"); }, previews: cache);
            Api.AddWindow(Handle, 5000, 100, 400, 300, pid: 7);
            Manager.RegisterWindow(Handle);
        }
        public void Tick(long time) { Clock.Now = time; Manager.Tick(); }
        public void FinishCapture()
        {
            Assert.True(SpinWait.SpinUntil(() =>
            { Manager.Previews.DrainCompleted(); return !Manager.Previews.CapturePending; }, 5000));
        }
        public void Park()
        {
            Tick(1500); FinishCapture(); Tick(1501);
            Assert.True(Manager.UsesCachedPreview(Handle));
            Assert.True(IsMinimized);
        }
        public bool IsMinimized => (Api.Windows[Handle].Style & (int)WINDOW_STYLE.WS_MINIMIZE) != 0;
        public void Dispose()
        {
            Api.FailedParkingRestores.Clear(); Api.FailedMoves.Clear();
            Manager.Dispose();
        }
    }

    [Fact]
    public void DistantParkedWindowsDoNotInspectProcessesOrEnumerateOwnedWindowsDuringCameraBursts()
    {
        using var h = new Harness(); h.Park();
        h.Manager.SuspendProjection = true;
        int inspections = h.ProcessInspections;
        int eligibility = h.Api.ParkingEligibilityQueries;
        for (int frame = 0; frame < 120; frame++)
        {
            h.Canvas.SetCamera(-frame, 0);
            h.Manager.SetOverviewViewport(new CanvasCamera(-frame, 0, .8));
        }
        Assert.Equal(inspections, h.ProcessInspections);
        Assert.Equal(eligibility, h.Api.ParkingEligibilityQueries);
        Assert.Empty(h.Api.ParkingRestoreCalls);
        h.Api.ParkingIneligible.Add(h.Handle);
        h.Tick(2000);
        Assert.True(h.ProcessInspections > inspections);
        Assert.Single(h.Api.ParkingRestoreCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CameraApproachValidatesIdentityBeforeRestoringRecycledParkedWindow(bool reusePid)
    {
        using var h = new Harness(); h.Park();
        h.Manager.SuspendProjection = true;
        if (reusePid) h.Started++;
        else h.Api.Windows[h.Handle].ProcessId++;
        h.Api.SetPositionCalls.Clear();
        h.Manager.SetOverviewViewport(new CanvasCamera(5000, 0, 1));
        Assert.Empty(h.Api.ParkingRestoreCalls);
        Assert.Empty(h.Api.SetPositionCalls);
        Assert.False(h.Manager.UsesCachedPreview(h.Handle));
    }

    [Fact]
    public void IntroSuspensionPreventsCameraFlightFromParkingOrRestoringApps()
    {
        using var h = new Harness();
        h.Manager.SuspendAutomaticParking = true;
        h.Manager.SetOverviewViewport(new CanvasCamera(0, 0, .4));
        h.Tick(5000);
        Assert.Empty(h.Api.MinimizeCalls);
        Assert.False(h.Manager.Previews.CapturePending);
        h.Manager.SuspendAutomaticParking = false;
        h.Tick(5001); h.FinishCapture(); h.Tick(5002);
        Assert.True(h.IsMinimized);
        h.Manager.SuspendAutomaticParking = true;
        h.Manager.SetOverviewViewport(new CanvasCamera(5000, 0, 1));
        h.Tick(5500);
        Assert.True(h.IsMinimized);
        Assert.Empty(h.Api.ParkingRestoreCalls);
        h.Manager.SuspendAutomaticParking = false;
        h.Manager.SetOverviewViewport(new CanvasCamera(5000, 0, 1));
        Assert.False(h.IsMinimized);
    }

    [Fact]
    public void ZoomPolicyUsesHysteresisAndResetsBetweenOverviewSessions()
    {
        using var h = new Harness();
        h.Manager.SetOverviewViewport(new CanvasCamera(0, 0, 1));
        foreach (double zoom in new[] { .70, .65, .61, .60 })
        {
            h.Manager.SetOverviewViewport(new CanvasCamera(0, 0, zoom));
            Assert.False(h.Manager.OverviewUsesInfoCards);
        }
        h.Manager.SetOverviewViewport(new CanvasCamera(0, 0, .59));
        foreach (double zoom in new[] { .60, .65, .68, .69 })
        {
            h.Manager.SetOverviewViewport(new CanvasCamera(0, 0, zoom));
            Assert.True(h.Manager.OverviewUsesInfoCards);
        }
        h.Manager.SetOverviewViewport(new CanvasCamera(0, 0, .70));
        Assert.False(h.Manager.OverviewUsesInfoCards);
        h.Manager.SetOverviewViewport(null);
        h.Manager.SetOverviewViewport(new CanvasCamera(0, 0, .59));
        Assert.True(h.Manager.OverviewUsesInfoCards); // Initial session uses default enter zoom.
        h.Manager.SetOverviewViewport(new CanvasCamera(0, 0, double.NaN));
        Assert.True(h.Manager.OverviewUsesInfoCards);
        h.Manager.Reset();
        Assert.False(h.Manager.OverviewUsesInfoCards);
    }

    [Fact]
    public void OverviewUsesSimpleCardsTracksDualThresholdHysteresis()
    {
        using var h = new Harness();
        h.Manager.SetOverviewViewport(new CanvasCamera(0, 0, 1));
        Assert.False(h.Manager.OverviewUsesInfoCards);
        Assert.False(h.Manager.OverviewUsesSimpleCards);

        h.Manager.SetOverviewViewport(new CanvasCamera(0, 0, 0.50));
        Assert.True(h.Manager.OverviewUsesInfoCards);
        Assert.False(h.Manager.OverviewUsesSimpleCards);

        h.Manager.SetOverviewViewport(new CanvasCamera(0, 0, 0.18));
        Assert.True(h.Manager.OverviewUsesInfoCards);
        Assert.True(h.Manager.OverviewUsesSimpleCards);

        h.Manager.SetOverviewViewport(new CanvasCamera(0, 0, 0.24));
        Assert.True(h.Manager.OverviewUsesSimpleCards);

        h.Manager.SetOverviewViewport(new CanvasCamera(0, 0, 0.28));
        Assert.True(h.Manager.OverviewUsesInfoCards);
        Assert.False(h.Manager.OverviewUsesSimpleCards);

        h.Manager.SetOverviewViewport(new CanvasCamera(0, 0, 0.72));
        Assert.False(h.Manager.OverviewUsesInfoCards);
        Assert.False(h.Manager.OverviewUsesSimpleCards);
    }

    [Fact]
    public void OverviewApproachRestoresDespiteDistantMainCameraAndSuspendedProjection()
    {
        using var h = new Harness(); h.Park();
        h.Manager.SuspendProjection = true;
        h.Api.Foreground = (IntPtr)999;
        h.Clock.Now = 1600;
        h.Manager.SetOverviewViewport(new CanvasCamera(5000, 0, 1));
        Assert.False(h.IsMinimized);
        Assert.Equal(0, h.Canvas.CamX);
        Assert.Equal((IntPtr)999, h.Api.Foreground);
        Assert.Equal(h.Canvas.Camera.Project(h.Canvas.Windows[h.Handle]), h.Api.ParkingRestoreCalls.Single().rect);
    }

    [Fact]
    public void OverviewDepartureParksDespiteMainCameraBeingNear()
    {
        using var h = new Harness();
        h.Canvas.SetCamera(5000, 0);
        h.Manager.SuspendProjection = true;
        h.Manager.SetOverviewViewport(new CanvasCamera(0, 0, 1));
        h.Tick(1500); h.FinishCapture(); h.Tick(1501);
        Assert.True(h.IsMinimized);
        Assert.Equal(5000, h.Canvas.CamX);
    }

    [Fact]
    public void ZoomedOutVisibleWindowParksAndDoesNotWakeUntilZoomedIn()
    {
        using var h = new Harness();
        h.Manager.SuspendProjection = true;
        h.Manager.SetOverviewViewport(new CanvasCamera(0, 0, .1)); // 500 screen pixels: visibly in the map.
        h.Tick(1500); h.FinishCapture(); h.Tick(1501);
        Assert.True(h.IsMinimized);
        h.Tick(5000);
        Assert.Empty(h.Api.ParkingRestoreCalls);
        h.Manager.SetOverviewViewport(new CanvasCamera(5000, 0, .75));
        Assert.False(h.IsMinimized);
        Assert.Equal(400, h.Api.Windows[h.Handle].W); // Native app is never shrunk to overview zoom.
        Assert.Equal(300, h.Api.Windows[h.Handle].H);
    }

    [Fact]
    public void LeavingZoomedOutOverviewReturnsPolicyToMainCamera()
    {
        using var h = new Harness(); h.Canvas.SetCamera(5000, 0);
        h.Manager.SuspendProjection = true;
        h.Manager.SetOverviewViewport(new CanvasCamera(0, 0, .1));
        h.Tick(1500); h.FinishCapture(); h.Tick(1501);
        Assert.True(h.IsMinimized);
        h.Manager.SetOverviewViewport(null);
        Assert.False(h.IsMinimized);
        Assert.False(h.Manager.OverviewUsesInfoCards);
    }

    [Fact]
    public void OverviewProximityAndZoomNeverRestoreUserMinimizedWindow()
    {
        using var h = new Harness();
        h.Api.Windows[h.Handle].Style = (int)WINDOW_STYLE.WS_MINIMIZE;
        h.Input.RaiseWindowMinimized(h.Handle);
        h.Manager.SetOverviewViewport(new CanvasCamera(5000, 0, 1));
        h.Tick(2000);
        h.Manager.SetOverviewViewport(new CanvasCamera(0, 0, .1)); h.Tick(5000);
        h.Manager.SetOverviewViewport(null);
        Assert.True(h.IsMinimized);
        Assert.Empty(h.Api.ParkingRestoreCalls);
        Assert.Empty(h.Api.MinimizeCalls);
    }

    [Fact]
    public void OverviewApproachUsesEachMonitorIncludingNegativeOrigins()
    {
        using var h = new Harness(); h.Park(); h.Manager.SuspendProjection = true;
        h.Manager.SetOverviewViewport(new CanvasCamera(6500, 0, 1), [(-1920, -200, 1920, 1080)]);
        Assert.False(h.IsMinimized); // Projects at -1500, on the left monitor.
    }

    [Fact]
    public void DisabledByDefaultDoesNotCaptureOrMinimize()
    {
        using var h = new Harness(); h.Config.EnableAutomaticMinimization = false;
        h.Tick(1500); h.Tick(5000);
        Assert.False(h.Manager.Previews.CapturePending);
        Assert.Empty(h.Api.MinimizeCalls);
    }

    [Fact]
    public void CapturesBeforeParkingAndRetainsSavedWorldAndUserIntent()
    {
        using var h = new Harness(); var world = h.Canvas.Windows[h.Handle];
        h.Api.AfterParkingMinimize = handle => h.Input.RaiseWindowMinimized(handle);
        h.Tick(1499); Assert.Empty(h.Api.MinimizeCalls);
        h.Tick(1500); Assert.Empty(h.Api.MinimizeCalls);
        h.FinishCapture(); h.Tick(1501);
        Assert.Single(h.Api.MinimizeCalls);
        Assert.Equal(world, h.Canvas.Windows[h.Handle]);
        Assert.Equal(RuntimeWindowVisibility.AutomaticallyMinimized, h.Manager.GetRuntimeVisibility(h.Handle)!.Value.State);
        h.Manager.ReprojectSync(); h.Manager.ReconcileWindow(h.Handle);
        Assert.Empty(h.Api.SetPositionCalls);
        Assert.Equal(world, h.Canvas.Windows[h.Handle]);
    }

    [Fact]
    public void NearViewportWaitsThenRestoresWithoutChangingFocusAndKeepsTransitionImage()
    {
        using var h = new Harness(); h.Park();
        h.Api.Foreground = (IntPtr)999;
        h.Api.AfterParkingRestore = handle => h.Input.RaiseWindowRestored(handle);
        h.Clock.Now = 1600; h.Canvas.SetCamera(3000, 0);
        Assert.Empty(h.Api.ParkingRestoreCalls);
        h.Tick(1749); Assert.Empty(h.Api.ParkingRestoreCalls);
        h.Tick(1750);
        Assert.Single(h.Api.ParkingRestoreCalls);
        Assert.False(h.IsMinimized);
        Assert.True(h.Manager.UsesCachedPreview(h.Handle));
        Assert.Equal((IntPtr)999, h.Api.Foreground);
        Assert.All(h.Api.SetPositionCalls, call => Assert.NotEqual(0u, call.flags & 0x10u));
        h.Tick(1900); Assert.False(h.Manager.UsesCachedPreview(h.Handle));
        Assert.Equal(WindowState.Normal, h.Canvas.Windows[h.Handle].State);
    }

    [Fact]
    public void VisibleViewportCommitRestoresImmediately()
    {
        using var h = new Harness(); h.Park();
        h.Clock.Now = 1600; h.Canvas.SetCamera(5000, 0); h.Canvas.Commit();
        Assert.False(h.IsMinimized);
        Assert.Equal(0, h.Api.Windows[h.Handle].X);
    }

    [Theory]
    [InlineData("pinned")]
    [InlineData("maximized")]
    [InlineData("user-minimized")]
    [InlineData("dialog")]
    [InlineData("foreground")]
    [InlineData("other-desktop")]
    public void IneligibleWindowsNeverPark(string reason)
    {
        using var h = new Harness();
        switch (reason)
        {
            case "pinned": h.Canvas.SetPinnedToScreen(h.Handle, true); break;
            case "maximized": h.Api.Windows[h.Handle].Style = (int)WINDOW_STYLE.WS_MAXIMIZE; break;
            case "user-minimized": h.Api.Windows[h.Handle].Style = (int)WINDOW_STYLE.WS_MINIMIZE; h.Input.RaiseWindowMinimized(h.Handle); break;
            case "dialog": h.Api.ParkingIneligible.Add(h.Handle); break;
            case "foreground": h.Api.Foreground = h.Handle; break;
            case "other-desktop": h.Desktops.DefaultIsOnCurrent = false; break;
        }
        h.Tick(1500); h.Tick(5000);
        Assert.Empty(h.Api.MinimizeCalls);
    }

    [Fact]
    public void FailedCaptureAndFailedMinimizeBackOff()
    {
        using var h = new Harness(); h.Capture.Fail = true;
        h.Tick(1500); h.FinishCapture(); h.Tick(1501);
        Assert.Empty(h.Api.MinimizeCalls);
        h.Capture.Fail = false; h.Tick(5000);
        Assert.False(h.Manager.Previews.CapturePending);
        h.Api.FailedMinimizations.Add(h.Handle);
        h.Tick(11501); h.FinishCapture(); h.Tick(11502);
        Assert.Single(h.Api.MinimizeCalls);
        Assert.False(h.Manager.UsesCachedPreview(h.Handle));
        Assert.Equal(WindowState.Normal, h.Canvas.Windows[h.Handle].State);
        h.Tick(15000); Assert.Single(h.Api.MinimizeCalls);
    }

    [Fact]
    public void IdentityReplacementBetweenCaptureAndParkingDoesNotMinimizeReplacement()
    {
        using var h = new Harness(); h.Tick(1500); h.FinishCapture();
        h.Started = 200; h.Tick(1501);
        Assert.Empty(h.Api.MinimizeCalls);
        Assert.False(h.Manager.HasCachedPreview(h.Handle));
    }

    [Fact]
    public void RecycledHandleIsNeverRestoredDuringRecovery()
    {
        using var h = new Harness(); h.Park();
        h.Started = 200;
        h.Api.SetPositionCalls.Clear();
        h.Manager.RestoreDesktopViewport();
        Assert.Empty(h.Api.ParkingRestoreCalls);
        Assert.Empty(h.Api.SetPositionCalls);
    }

    [Fact]
    public void FailedRestoreKeepsDebtAndCachedPreviewUntilSuccessfulRetry()
    {
        using var h = new Harness(); h.Park(); h.Api.FailedParkingRestores.Add(h.Handle);
        h.Clock.Now = 1600; h.Canvas.SetCamera(5000, 0);
        Assert.True(h.IsMinimized); Assert.True(h.Manager.UsesCachedPreview(h.Handle));
        h.Tick(2000); Assert.Single(h.Api.ParkingRestoreCalls);
        h.Api.FailedParkingRestores.Clear(); h.Tick(11600);
        Assert.False(h.IsMinimized); h.Tick(11750);
        Assert.False(h.Manager.UsesCachedPreview(h.Handle));
    }

    [Fact]
    public void DisableAndDialogAppearanceRecoverExistingParking()
    {
        using var h = new Harness(); h.Park();
        h.Config.EnableAutomaticMinimization = false;
        h.Tick(1600); Assert.False(h.IsMinimized);
        h.Tick(1750); h.Config.EnableAutomaticMinimization = true;
        h.Tick(12000); h.FinishCapture(); h.Tick(12001);
        Assert.True(h.IsMinimized);
        h.Api.ParkingIneligible.Add(h.Handle);
        h.Tick(12500); Assert.False(h.IsMinimized);
    }

    [Fact]
    public void ManualTaskbarRestoreRecentersAndExcludesSelfRestoringWindowForLifetime()
    {
        using var h = new Harness(); h.Park();
        h.Api.Windows[h.Handle].Style = 0; h.Api.Foreground = h.Handle;
        h.Input.RaiseWindowRestored(h.Handle);
        Assert.False(h.Manager.UsesCachedPreview(h.Handle));
        Assert.Empty(h.Api.ParkingRestoreCalls);
        Assert.True(h.Canvas.CamX > 0);
        h.Api.Foreground = IntPtr.Zero; h.Canvas.SetCamera(0, 0);
        h.Tick(15000); h.Tick(20000);
        Assert.Single(h.Api.MinimizeCalls);
    }

    [Fact]
    public void UserMinimizationAfterAutomaticRestoreIsNeverRecovered()
    {
        using var h = new Harness(); h.Park(); h.Canvas.SetCamera(5000, 0);
        h.Api.Windows[h.Handle].Style = (int)WINDOW_STYLE.WS_MINIMIZE;
        h.Input.RaiseWindowMinimized(h.Handle);
        Assert.Equal(WindowState.Minimized, h.Canvas.Windows[h.Handle].State);
        int restores = h.Api.ParkingRestoreCalls.Count;
        h.Manager.Dispose();
        Assert.Equal(restores, h.Api.ParkingRestoreCalls.Count);
        Assert.True(h.IsMinimized);
    }

    [Theory]
    [InlineData("reset")]
    [InlineData("dispose")]
    [InlineData("exit")]
    public void RecoveryRestoresOnlyCanvasOwnedMinimization(string action)
    {
        using var h = new Harness(); h.Park();
        var user = (IntPtr)43; h.Api.AddWindow(user, 300, 200, 400, 300, style: (int)WINDOW_STYLE.WS_MINIMIZE);
        h.Canvas.SetWindow(user, 300, 200, 400, 300); h.Canvas.CollapseWindow(user);
        switch (action)
        {
            case "reset": h.Manager.Reset(); break;
            case "dispose": h.Manager.Dispose(); break;
            case "exit": h.Manager.RestoreDesktopViewport(); break;
        }
        Assert.False(h.IsMinimized);
        Assert.NotEqual(0, h.Api.Windows[user].Style & (int)WINDOW_STYLE.WS_MINIMIZE);
        Assert.Single(h.Api.ParkingRestoreCalls);
        Assert.Equal(h.Handle, h.Api.ParkingRestoreCalls[0].handle);
    }

    [Fact]
    public void PinningParkedWindowRestoresAndPinsIt()
    {
        using var h = new Harness(); h.Park();
        Assert.True(h.Manager.SetWindowPinnedToScreen(h.Handle, true));
        Assert.False(h.IsMinimized); Assert.False(h.Manager.UsesCachedPreview(h.Handle));
    }

    [Fact]
    public void DisablingWhileCaptureCompletesCancelsParking()
    {
        using var h = new Harness(); h.Tick(1500); h.FinishCapture();
        h.Config.EnableAutomaticMinimization = false; h.Tick(1501);
        Assert.Empty(h.Api.MinimizeCalls);
    }

    [Fact]
    public void ThrowingMinimizeRetainsRecoverableOwnership()
    {
        using var h = new Harness();
        h.Api.AfterParkingMinimize = _ => throw new InvalidOperationException("Injected failure");
        h.Tick(1500); h.FinishCapture();
        Assert.Throws<InvalidOperationException>(() => h.Tick(1501));
        Assert.True(h.IsMinimized);
        h.Tick(1502);
        Assert.False(h.IsMinimized);
        Assert.Equal(WindowState.Normal, h.Canvas.Windows[h.Handle].State);
    }

    [Fact]
    public void FailedResetRecoveryRetainsDebtForLaterTick()
    {
        using var h = new Harness(); h.Park();
        h.Api.FailedParkingRestores.Add(h.Handle);
        h.Manager.Reset(); Assert.True(h.IsMinimized);
        h.Api.FailedParkingRestores.Clear(); h.Tick(11501);
        Assert.False(h.IsMinimized);
    }

    [Fact]
    public void MultipleWindowsFromSameProcessHaveIndependentParkingOwnership()
    {
        using var h = new Harness();
        var second = (IntPtr)43;
        h.Api.AddWindow(second, 6000, 100, 400, 300, pid: 7);
        h.Manager.RegisterWindow(second);
        h.Tick(1500); h.FinishCapture(); h.Tick(1501);
        h.FinishCapture(); h.Tick(1502);
        Assert.Equal(2, h.Api.MinimizeCalls.Count);
        h.Api.Windows[h.Handle].Style = 0;
        h.Input.RaiseWindowRestored(h.Handle);
        h.Manager.Reset();
        Assert.Single(h.Api.ParkingRestoreCalls);
        Assert.Equal(second, h.Api.ParkingRestoreCalls[0].handle);
    }

    [Fact]
    public void ManualRestoreCentersWithinMonitorWithNegativeOrigin()
    {
        using var h = new Harness(); h.Park();
        h.Api.ScreenAreas = new() { (-1920, -200, 1920, 1080) };
        h.Api.Windows[h.Handle].Style = 0; h.Api.Foreground = h.Handle;
        h.Input.RaiseWindowRestored(h.Handle);
        var projected = h.Canvas.Camera.Project(h.Canvas.Windows[h.Handle]);
        Assert.Equal(-960, projected.X + projected.W / 2);
        Assert.Equal(340, projected.Y + projected.H / 2);
    }

    [Fact]
    public void OverviewTierThresholdsAndHysteresisFollowDefaults()
    {
        using var h = new Harness();
        // Zoom 0.80: Live tier
        h.Manager.SetOverviewViewport(new CanvasCamera(0, 0, 0.80));
        Assert.False(h.Manager.OverviewUsesInfoCards);
        Assert.False(h.Manager.OverviewUsesSimpleCards);

        // Zoom 0.55: Detailed Cards tier (enters at < 0.60)
        h.Manager.SetOverviewViewport(new CanvasCamera(0, 0, 0.55));
        Assert.True(h.Manager.OverviewUsesInfoCards);
        Assert.False(h.Manager.OverviewUsesSimpleCards);

        // Zoom 0.18: Simple Cards tier (enters at < 0.20)
        h.Manager.SetOverviewViewport(new CanvasCamera(0, 0, 0.18));
        Assert.True(h.Manager.OverviewUsesInfoCards);
        Assert.True(h.Manager.OverviewUsesSimpleCards);

        // Zoom in to 0.24: Hysteresis keeps Simple Cards active (< exit 0.26)
        h.Manager.SetOverviewViewport(new CanvasCamera(0, 0, 0.24));
        Assert.True(h.Manager.OverviewUsesInfoCards);
        Assert.True(h.Manager.OverviewUsesSimpleCards);

        // Zoom in to 0.28: Transitions to Detailed Cards (> exit 0.26)
        h.Manager.SetOverviewViewport(new CanvasCamera(0, 0, 0.28));
        Assert.True(h.Manager.OverviewUsesInfoCards);
        Assert.False(h.Manager.OverviewUsesSimpleCards);

        // Zoom in to 0.65: Hysteresis keeps Detailed Cards active (< exit 0.70)
        h.Manager.SetOverviewViewport(new CanvasCamera(0, 0, 0.65));
        Assert.True(h.Manager.OverviewUsesInfoCards);
        Assert.False(h.Manager.OverviewUsesSimpleCards);

        // Zoom in to 0.72: Restores Live Previews (> exit 0.70)
        h.Manager.SetOverviewViewport(new CanvasCamera(0, 0, 0.72));
        Assert.False(h.Manager.OverviewUsesInfoCards);
        Assert.False(h.Manager.OverviewUsesSimpleCards);
    }

    [Fact]
    public void DisablingDetailedCardsTierTransitionsDirectlyFromLiveToSimple()
    {
        using var h = new Harness();
        h.Config.EnableDetailedCardsTier = false;

        // Zoom 0.55 (would normally be detailed cards): remains Live
        h.Manager.SetOverviewViewport(new CanvasCamera(0, 0, 0.55));
        Assert.False(h.Manager.OverviewUsesInfoCards);
        Assert.False(h.Manager.OverviewUsesSimpleCards);

        // Zoom 0.18: switches directly to Simple Cards
        h.Manager.SetOverviewViewport(new CanvasCamera(0, 0, 0.18));
        Assert.True(h.Manager.OverviewUsesInfoCards);
        Assert.True(h.Manager.OverviewUsesSimpleCards);
    }

    [Fact]
    public void DisablingSimpleCardsTierKeepsDetailedCardsAtExtremeZoomOut()
    {
        using var h = new Harness();
        h.Config.EnableSimpleCardsTier = false;

        // Zoom 0.55: Detailed Cards
        h.Manager.SetOverviewViewport(new CanvasCamera(0, 0, 0.55));
        Assert.True(h.Manager.OverviewUsesInfoCards);
        Assert.False(h.Manager.OverviewUsesSimpleCards);

        // Zoom 0.05: stays in Detailed Cards, never enters Simple Cards
        h.Manager.SetOverviewViewport(new CanvasCamera(0, 0, 0.05));
        Assert.True(h.Manager.OverviewUsesInfoCards);
        Assert.False(h.Manager.OverviewUsesSimpleCards);
    }

    [Fact]
    public void DisablingLivePreviewsTierKeepsCardsEvenWhenZoomedIn()
    {
        using var h = new Harness();
        h.Config.EnableLivePreviewsTier = false;

        // Zoom 1.0 (zoomed in): stays in Detailed Cards, never uses Live Previews
        h.Manager.SetOverviewViewport(new CanvasCamera(0, 0, 1.0));
        Assert.True(h.Manager.OverviewUsesInfoCards);
        Assert.False(h.Manager.OverviewUsesSimpleCards);

        // Zoom 0.15: Simple Cards
        h.Manager.SetOverviewViewport(new CanvasCamera(0, 0, 0.15));
        Assert.True(h.Manager.OverviewUsesInfoCards);
        Assert.True(h.Manager.OverviewUsesSimpleCards);
    }

    [Fact]
    public void CustomTierThresholdsAreRespected()
    {
        using var h = new Harness();
        h.Config.OverviewCardEnterZoom = 0.80;
        h.Config.OverviewCardExitZoom = 0.90;
        h.Config.OverviewSimpleCardEnterZoom = 0.40;
        h.Config.OverviewSimpleCardExitZoom = 0.45;

        // Zoom 0.75: enters Detailed (< 0.80)
        h.Manager.SetOverviewViewport(new CanvasCamera(0, 0, 0.75));
        Assert.True(h.Manager.OverviewUsesInfoCards);
        Assert.False(h.Manager.OverviewUsesSimpleCards);

        // Zoom 0.35: enters Simple (< 0.40)
        h.Manager.SetOverviewViewport(new CanvasCamera(0, 0, 0.35));
        Assert.True(h.Manager.OverviewUsesInfoCards);
        Assert.True(h.Manager.OverviewUsesSimpleCards);
    }
}

