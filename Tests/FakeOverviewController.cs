using System;
using System.Collections.Generic;

namespace CanvasDesktop.Tests;

internal sealed class FakeOverviewController : IOverviewController
{
    public event Action<OverviewMode, OverviewMode>? BeforeModeChanged;

    public List<IntPtr> Monitors = new();
    public OverviewMode CurrentMode { get; private set; } = OverviewMode.Hidden;

    public bool CanvasInputSuspended { get; set; }
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
    public List<OverviewMode> Transitions = new();
    public List<IntPtr> NavigatedWindows = new();
    public bool NavigateToWindow(IntPtr window) { NavigatedWindows.Add(window); return true; }
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

    public void TransitionTo(OverviewMode target, bool syncCameraOnClose = true)
    {
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

    public void RecordPanDelta(int dx, int dy)
    {
        RecordedDeltas.Add((dx, dy));
    }

    public void ReleaseInertia()
    {
        ReleaseInertiaCalls++;
    }
}
