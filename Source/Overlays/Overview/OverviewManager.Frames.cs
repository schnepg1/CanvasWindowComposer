using System;
using System.Windows.Forms;

namespace CanvasDesktop;

internal sealed partial class OverviewManager
{
    private readonly OverviewFrameScheduler _visualFrames;
    private Timer? _visualFrameTimer;
    private Action? _scheduledVisualFrame;
    private bool _visualFramePosted;
    private double _visualPanX, _visualPanY;

    private void ScheduleVisualFrame(Action frame)
    {
        _scheduledVisualFrame = frame;
        if (_visualFramePosted) return;

        if (_passes.Count > 0 && _passes[0].IsHandleCreated && !_passes[0].IsDisposed)
        {
            _visualFramePosted = true;
            try
            {
                _passes[0].BeginInvoke((Action)(() =>
                {
                    _visualFramePosted = false;
                    var scheduled = _scheduledVisualFrame;
                    _scheduledVisualFrame = null;
                    if (!_disposed && CurrentMode != OverviewMode.Hidden) scheduled?.Invoke();
                }));
                return;
            }
            catch (InvalidOperationException)
            {
                _visualFramePosted = false;
            }
        }

        if (_visualFrameTimer == null)
        {
            // One UI clock for all monitor passes; do not reconcile once per input
            // message. This is a nominal frame interval, not a vsync guarantee.
            _visualFrameTimer = new Timer { Interval = 16 };
            _visualFrameTimer.Tick += (_, _) =>
            {
                _visualFrameTimer?.Stop();
                var scheduled = _scheduledVisualFrame;
                _scheduledVisualFrame = null;
                if (!_disposed && CurrentMode != OverviewMode.Hidden) scheduled?.Invoke();
            };
        }
        _visualFrameTimer.Start();
    }

    internal event Action? ViewCameraChanged;

    internal void FocusAfterMinimapNavigation()
    {
        if (!_disposed && CurrentMode == OverviewMode.Zooming && !CanvasInputSuspended)
            FocusOverview();
    }

    internal void CenterViewOnMinimap(double worldX, double worldY)
    {
        if (CurrentMode != OverviewMode.Zooming || !double.IsFinite(worldX) || !double.IsFinite(worldY)) return;
        CancelInertia();
        StopWindowNavigation();
        StopTutorialTravel();
        var primary = _screens.PrimaryBounds;
        double centerX = _camera.X + (primary.X + primary.Width / 2.0) / _camera.Zoom;
        double centerY = _camera.Y + (primary.Y + primary.Height / 2.0) / _camera.Zoom;
        PanOverview((centerX - worldX) * _camera.Zoom, (centerY - worldY) * _camera.Zoom);
    }

    private void UpdateGridCamera(CanvasCamera camera, double dx, double dy)
    {
        foreach (var pass in _passes)
            pass.Grid?.UpdateCamera(camera.X, camera.Y, camera.Zoom, dx, dy);
        ViewCameraChanged?.Invoke();
    }

    private void CancelVisualFrame()
    {
        _visualFrames.Cancel();
        _visualFrameTimer?.Stop();
        _visualFramePosted = false;
        _scheduledVisualFrame = null;
        _visualPanX = _visualPanY = 0;
    }
}
