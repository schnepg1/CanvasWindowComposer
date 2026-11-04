using System;
using System.Windows.Forms;

namespace CanvasDesktop;

internal sealed partial class OverviewManager
{
    private readonly SmoothZoomTracker _smoothZoom;
    private Timer? _smoothZoomTimer;

    internal bool SmoothZoomActive => _smoothZoom.IsActive;
    internal double SmoothZoomTarget => _smoothZoom.TargetZoom;

    private bool SmoothZoomAllowed => CurrentMode == OverviewMode.Zooming
        && _appConfig.SmoothZoom
        && !GuidedTutorialActive
        && !PlacingWebResource
        && !_prefersReducedStartupMotion();

    private void StartOrUpdateSmoothZoom(int virtualX, int virtualY, double notches)
    {
        var (wx, wy) = _camera.WorldFromVirtual(virtualX, virtualY);
        _smoothZoom.PushZoom(virtualX, virtualY, wx, wy, _camera.Zoom, notches, _gestureClock.TickCount64);
        if (!_smoothZoom.IsActive) return;

        ObserveGestureMotion();
        _startupIntroLanded = false;

        _smoothZoomTimer ??= new Timer { Interval = 16 };
        _smoothZoomTimer.Tick -= OnSmoothZoomTick;
        _smoothZoomTimer.Tick += OnSmoothZoomTick;
        _smoothZoomTimer.Start();

        AdvanceSmoothZoom();
    }

    private void OnSmoothZoomTick(object? sender, EventArgs e) => AdvanceSmoothZoom();

    internal void AdvanceSmoothZoom(long? timestamp = null)
    {
        if (_disposed || !_smoothZoom.IsActive)
        {
            CancelSmoothZoom();
            return;
        }

        if (!SmoothZoomAllowed)
        {
            var target = _smoothZoom.TargetZoom;
            var focal = _smoothZoom.FocalVirtual;
            var world = _smoothZoom.FocalWorld;
            CancelSmoothZoom();
            _camera.SetZoomAroundWorldPoint(focal.X, focal.Y, world.X, world.Y, target);
            _visualFrames.Request();
            return;
        }

        double before = _camera.Zoom;
        long time = timestamp ?? _gestureClock.TickCount64;
        var (vx, vy, wx, wy, newZoom, stopped) = _smoothZoom.Tick(_camera.Zoom, time);
        if (_camera.SetZoomAroundWorldPoint(vx, vy, wx, wy, newZoom))
        {
            ObserveGestureMotion();
            if (_tour?.ObserveZoom(before, _camera.Zoom) == true) SaveTourProgress();
            _startupIntroLanded = false;
            _visualFrames.Request();
        }

        if (stopped)
        {
            _smoothZoomTimer?.Stop();
        }
    }

    public void CancelSmoothZoom()
    {
        _smoothZoomTimer?.Stop();
        _smoothZoom.Cancel();
    }
}
