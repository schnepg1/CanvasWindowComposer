using System;

namespace CanvasDesktop;

/// <summary>
/// UI-owned, elapsed-time smooth zoom interpolation, independent of presentation rate.
/// Smoothly animates zoom from current to target level keeping a focal point anchored.
/// </summary>
internal sealed class SmoothZoomTracker
{
    public const double DefaultLambda = 0.022;
    public const double StopThreshold = 0.0005;

    private readonly IClock _clock;
    private readonly double _lambda;

    private double _targetZoom = 1.0;
    private double _currentZoom = 1.0;
    private long _lastTick;

    private int _focalVirtualX;
    private int _focalVirtualY;
    private double _focalWorldX;
    private double _focalWorldY;

    public bool IsActive { get; private set; }
    public double TargetZoom => _targetZoom;
    public (int X, int Y) FocalVirtual => (_focalVirtualX, _focalVirtualY);
    public (double X, double Y) FocalWorld => (_focalWorldX, _focalWorldY);

    public SmoothZoomTracker(IClock? clock = null, double lambda = DefaultLambda)
    {
        _clock = clock ?? SystemClock.Instance;
        _lambda = lambda > 0 ? lambda : DefaultLambda;
    }

    public void PushZoom(int virtualX, int virtualY, double worldX, double worldY, double currentZoom, double notches, long? timestamp = null)
    {
        if (!double.IsFinite(notches) || notches == 0) return;

        if (!IsActive)
        {
            _currentZoom = currentZoom;
            _targetZoom = currentZoom;
        }
        else
        {
            // If direction reversed, pivot from the current intermediate zoom level
            if ((notches > 0 && _targetZoom < currentZoom) || (notches < 0 && _targetZoom > currentZoom))
            {
                _targetZoom = currentZoom;
            }
        }

        double step = notches * OverviewCamera.ZoomStep * _targetZoom;
        double nextTarget = Math.Clamp(_targetZoom + step, OverviewCamera.ZoomMin, OverviewCamera.ZoomMax);

        if (Math.Abs(nextTarget - currentZoom) < OverviewCamera.ZoomEpsilon)
        {
            if (!IsActive) return;
        }

        _targetZoom = nextTarget;
        _currentZoom = currentZoom;
        _focalVirtualX = virtualX;
        _focalVirtualY = virtualY;
        _focalWorldX = worldX;
        _focalWorldY = worldY;
        _lastTick = timestamp ?? _clock.TickCount64;
        IsActive = Math.Abs(_targetZoom - _currentZoom) >= StopThreshold;
    }

    public (int virtualX, int virtualY, double worldX, double worldY, double zoom, bool stopped) Tick(double currentZoom, long? timestamp = null)
    {
        if (!IsActive)
            return (_focalVirtualX, _focalVirtualY, _focalWorldX, _focalWorldY, currentZoom, true);

        long now = timestamp ?? _clock.TickCount64;
        double dt = Math.Max(0, now - _lastTick);
        _lastTick = now;

        if (dt == 0)
            return (_focalVirtualX, _focalVirtualY, _focalWorldX, _focalWorldY, currentZoom, false);

        _currentZoom = currentZoom;
        double decay = Math.Exp(-_lambda * dt);
        double factor = 1.0 - decay;
        double nextZoom = _currentZoom + (_targetZoom - _currentZoom) * factor;

        bool stopped = Math.Abs(_targetZoom - nextZoom) < StopThreshold
            || (_targetZoom > _currentZoom && nextZoom >= _targetZoom)
            || (_targetZoom < _currentZoom && nextZoom <= _targetZoom);

        if (stopped)
        {
            nextZoom = _targetZoom;
            _currentZoom = nextZoom;
            IsActive = false;
        }
        else
        {
            _currentZoom = nextZoom;
        }

        return (_focalVirtualX, _focalVirtualY, _focalWorldX, _focalWorldY, nextZoom, stopped);
    }

    public void Cancel()
    {
        IsActive = false;
    }
}
