using System;
using System.Windows.Forms;

namespace CanvasDesktop;

internal sealed partial class OverviewManager
{
    private readonly InertiaTracker _inertia;
    private Timer? _momentumTimer;
    private OverviewOverlay? _desktopStopSurface;
    private MouseButtons _desktopStopButton;
    private bool? _parkingBeforeDesktopPan;
    internal bool MomentumActive => _inertia.IsActive;
    private bool MomentumAllowed => CurrentMode switch
    {
        OverviewMode.Panning => _appConfig.DesktopPanMomentum,
        OverviewMode.Zooming => _appConfig.CanvasPanMomentum && !GuidedTutorialActive,
        _ => false
    };

    public void BeginPan(long? timestamp = null)
    {
        CancelInertia();
        _inertia.Begin(timestamp);
    }

    public void RecordPanDelta(int dx, int dy, long? timestamp = null)
    {
        if (MomentumAllowed) _inertia.RecordDelta(dx, dy, timestamp);
    }

    public void ReleaseInertia(long? timestamp = null)
    {
        if (_desktopStopSurface != null) return;
        if (!MomentumAllowed || !_inertia.Release(timestamp))
        {
            FinishMomentum();
            return;
        }
        _momentumTimer ??= new Timer { Interval = 16 };
        _momentumTimer.Tick -= OnMomentumTick;
        _momentumTimer.Tick += OnMomentumTick;
        _momentumTimer.Start();
    }

    private void OnMomentumTick(object? sender, EventArgs e) => AdvanceMomentum();

    internal void AdvanceMomentum()
    {
        if (_disposed || !_inertia.IsActive) return;
        if (!MomentumAllowed) { FinishMomentum(); return; }
        var (dx, dy, stopped) = _inertia.Tick();
        // Apply the last displacement even when this tick reaches the stop threshold.
        if (dx != 0 || dy != 0)
        {
            if (CurrentMode == OverviewMode.Panning) _mainCanvas.Pan(dx, dy);
            else PanOverview(dx, dy);
            _visualFrames.Flush();
        }
        if (stopped) FinishMomentum();
    }

    public void CancelInertia()
    {
        _momentumTimer?.Stop();
        _inertia.Cancel();
    }

    private void FinishMomentum()
    {
        CancelInertia();
        if (!_disposed && CurrentMode == OverviewMode.Panning && _desktopStopSurface == null)
            TransitionTo(OverviewMode.Hidden);
    }

    private void UpdateDesktopPanResources()
    {
        if (CurrentMode == OverviewMode.Panning)
        {
            _parkingBeforeDesktopPan ??= _wm.SuspendAutomaticParking;
            _wm.SuspendAutomaticParking = true;
        }
        else RestoreDesktopPanResources();
    }

    private void RestoreDesktopPanResources()
    {
        if (_parkingBeforeDesktopPan is not { } previous) return;
        _parkingBeforeDesktopPan = null;
        _wm.SuspendAutomaticParking = previous;
    }

    private void StopDesktopPanWithClick(OverviewOverlay pass, MouseEventArgs e)
    {
        if (e.Button is MouseButtons.None or MouseButtons.Middle || _desktopStopSurface != null) return;
        CancelInertia();
        _visualFrames.Flush();
        _desktopStopSurface = pass;
        _desktopStopButton = e.Button;
        // Consume the complete stopping click. Holding capture through release
        // also prevents a right-button-up from opening an unrelated context menu.
        pass.Capture = true;
    }

    private bool CompleteDesktopStoppingClick(OverviewOverlay pass, MouseEventArgs e)
    {
        if (_desktopStopSurface != pass || e.Button != _desktopStopButton) return false;
        ClearDesktopStoppingClick();
        TransitionTo(OverviewMode.Hidden);
        return true;
    }

    private void ClearDesktopStoppingClick()
    {
        var pass = _desktopStopSurface;
        _desktopStopSurface = null;
        _desktopStopButton = MouseButtons.None;
        if (pass is { IsDisposed: false }) pass.Capture = false;
    }
}
