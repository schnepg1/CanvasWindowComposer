using System;
using System.Collections.Generic;

namespace CanvasDesktop;

/// <summary>
/// The slice of <see cref="OverviewManager"/> that the orchestrator drives.
/// Lets tests stand in a fake without spinning up D3D11 + per-monitor forms.
/// </summary>
internal interface IOverviewController
{
    event Action<OverviewMode, OverviewMode>? BeforeModeChanged;
    IReadOnlyList<IntPtr> MonitorHandles { get; }
    OverviewMode CurrentMode { get; }
    void TransitionTo(OverviewMode target, bool syncCameraOnClose = true, bool zoomOutOnOpen = false);
    bool NavigateToWindow(IntPtr window);
    void HandleEscape();
    bool CanvasInputSuspended => false;
    bool GuidedTutorialActive => false;
    void PanGuidedTutorial(int x,int y) { }
    bool CancelInteraction();
    bool StartupIntroInputSuppressed => false;
    bool TrySkipStartupIntro() => false;
    void CancelStartupIntro() { }
    void CancelInertia();
    void SyncCamera();
    void BeginPan(long? timestamp = null) => CancelInertia();
    void RecordPanDelta(int dx, int dy, long? timestamp = null);
    void ReleaseInertia(long? timestamp = null);
}
