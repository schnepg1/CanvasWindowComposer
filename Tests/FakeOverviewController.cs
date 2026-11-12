using System;
using System.Collections.Generic;

namespace CanvasDesktop.Tests;

internal sealed class FakeOverviewController : IOverviewController
{
    public event Action<OverviewMode, OverviewMode>? BeforeModeChanged;

    public List<IntPtr> Monitors = new();
    public OverviewMode CurrentMode { get; private set; } = OverviewMode.Hidden;

    public bool CanvasInputSuspended { get; set; }
    public bool? HotkeySuspendedOverride { get; set; }
    public bool OverviewHotkeySuspended => HotkeySuspendedOverride ?? CanvasInputSuspended;
    public bool GuidedTutorialActive { get; set; }
    public List<(int x,int y)> TutorialPan = new();
    public void PanGuidedTutorial(int x,int y) => TutorialPan.Add((x,y));
    public bool StartupIntroActive;
    public bool ConsumeIntroGesture;
    public bool StartupIntroInputSuppressed => ConsumeIntroGesture;
    public int CancelStartupIntroCalls;
    public bool TrySkipStartupIntro()
    {
        if (!StartupIntroActive && !ConsumeIntroGesture) return false;
        StartupIntroActive = false;
        return true;
    }
    public void CancelStartupIntro() { CancelStartupIntroCalls++; StartupIntroActive = false; }

    public int CancelInertiaCalls;
    public int SyncCameraCalls;
    public int ReleaseInertiaCalls;
    public List<(int dx, int dy)> RecordedDeltas = new();
    public List<long?> RecordedTimes = new();
    public long? ReleasedAt;
    public List<OverviewMode> Transitions = new();
    public bool WindowNavigationActive { get; set; }
    public bool DesktopHandoffPending { get; set; }
    public int DesktopHandoffRecoveryRequests { get; private set; }
    public bool TryRecoverDesktopHandoff()
    {
        if (!DesktopHandoffPending) return false;
        DesktopHandoffRecoveryRequests++;
        return true;
    }
    public List<IntPtr> NavigatedWindows = new();
    public bool NavigateToWindowResult { get; set; } = true;
    public Action<IntPtr>? OnNavigateToWindow { get; set; }
    public bool NavigateToWindow(IntPtr window)
    {
        NavigatedWindows.Add(window);
        OnNavigateToWindow?.Invoke(window);
        return NavigateToWindowResult;
    }
    public List<CanvasCamera> NavigatedCameras = new();
    public bool NavigateToCamera(CanvasCamera target, bool landInDesktopView = false) { NavigatedCameras.Add(target); return true; }
    public List<int> RecalledBookmarks = new();
    public bool RecallBookmark(int slot) { RecalledBookmarks.Add(slot); return true; }
    public bool PendingInteraction;
    public int EscapeCalls, CancelInteractionCalls;

    public bool CancelInteraction()
    {
        CancelInteractionCalls++;
        bool pending = PendingInteraction;
        PendingInteraction = false;
        return pending;
    }

    public void HandleEscape()
    {
        EscapeCalls++;
        if (CurrentMode == OverviewMode.Zooming && !CancelInteraction()) TransitionTo(OverviewMode.Hidden);
    }

    public IReadOnlyList<IntPtr> MonitorHandles { get { return Monitors; } }

    public bool LastZoomOutOnOpen;
    public void TransitionTo(OverviewMode target, bool syncCameraOnClose = true, bool zoomOutOnOpen = false)
    {
        LastZoomOutOnOpen = zoomOutOnOpen;
        Transitions.Add(target);
        if (target == CurrentMode) return;
        var from = CurrentMode;
        CurrentMode = target;
        BeforeModeChanged?.Invoke(from, target);
    }

    public void CancelInertia()
    {
        CancelInertiaCalls++;
    }

    public void SyncCamera()
    {
        SyncCameraCalls++;
    }

    public void RecordPanDelta(int dx, int dy, long? timestamp = null)
    {
        RecordedDeltas.Add((dx, dy));
        RecordedTimes.Add(timestamp);
    }

    public void ReleaseInertia(long? timestamp = null)
    {
        ReleaseInertiaCalls++;
        ReleasedAt = timestamp;
    }
}
