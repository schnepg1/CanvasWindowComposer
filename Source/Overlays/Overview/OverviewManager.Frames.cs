using System;
using System.Windows.Forms;

namespace CanvasDesktop;

internal sealed partial class OverviewManager
{
    private readonly OverviewFrameScheduler _visualFrames;
    private Timer? _visualFrameTimer;
    private Action? _scheduledVisualFrame;
    private double _visualPanX, _visualPanY;

    private void ScheduleVisualFrame(Action frame)
    {
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
        _scheduledVisualFrame = frame;
        _visualFrameTimer.Start();
    }

    private void UpdateGridCamera()
    {
        double dx = _visualPanX, dy = _visualPanY;
        _visualPanX = _visualPanY = 0;
        foreach (var pass in _passes)
            pass.Grid?.UpdateCamera(_camera.X, _camera.Y, _camera.Zoom, dx, dy);
    }

    private void CancelVisualFrame()
    {
        _visualFrames.Cancel();
        _visualFrameTimer?.Stop();
        _scheduledVisualFrame = null;
        _visualPanX = _visualPanY = 0;
    }
}
