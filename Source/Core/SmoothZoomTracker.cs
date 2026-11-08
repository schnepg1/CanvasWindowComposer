using System;

namespace CanvasDesktop;

/// <summary>
/// UI-owned, elapsed-time smooth zoom interpolation, independent of presentation rate.
/// Smoothly animates zoom from current to target level keeping a focal point anchored.
/// Features cadence-based scroll acceleration so fast flicking speeds up rather than
/// capping zoom velocity.
/// </summary>
internal sealed class SmoothZoomTracker
{
    public const double DefaultLambda = 0.032;
    public const double MaxLambda = 0.055;
    public const double StopThreshold = 0.0005;

    public const double BaseCadenceNotchesPerSec = 4.0;
    public const double MaxAccelerationIntervalMs = 250.0;
    public const double MinIntervalMs = 2.0;
    public const double AccelerationSlope = 0.08;
    public const double StreakSlope = 0.15;
    public const int MaxStreak = 6;
    public const double MaxMultiplier = 7.0;

    private readonly IClock _clock;
    private readonly double _baseLambda;
    private double _activeLambda;

    private double _targetZoom = 1.0;
    private double _currentZoom = 1.0;
    private long _lastTick;
    private long _lastNotchTime;
    private int _lastDirection;
    private int _streak;

    private int _focalVirtualX;
    private int _focalVirtualY;
    private double _focalWorldX;
    private double _focalWorldY;

    public bool IsActive { get; private set; }
    public double TargetZoom => _targetZoom;
    public double CurrentMultiplier { get; private set; } = 1.0;
    public bool IsAccelerating => CurrentMultiplier > 1.05;
    public bool AccelerationEnabled { get; set; } = true;
    public double MaxZoom { get; set; } = OverviewCamera.ZoomMax;
    public (int X, int Y) FocalVirtual => (_focalVirtualX, _focalVirtualY);
    public (double X, double Y) FocalWorld => (_focalWorldX, _focalWorldY);

    public SmoothZoomTracker(IClock? clock = null, double lambda = DefaultLambda, bool enableAcceleration = true)
    {
        _clock = clock ?? SystemClock.Instance;
        _baseLambda = lambda > 0 ? lambda : DefaultLambda;
        _activeLambda = _baseLambda;
        AccelerationEnabled = enableAcceleration;
    }

    public void PushZoom(int virtualX, int virtualY, double worldX, double worldY, double currentZoom, double notches, long? timestamp = null)
    {
        if (!double.IsFinite(notches) || notches == 0) return;

        long now = timestamp ?? _clock.TickCount64;
        double dt = Math.Max(0, now - _lastNotchTime);
        _lastNotchTime = now;

        int direction = Math.Sign(notches);

        if (!IsActive)
        {
            _currentZoom = currentZoom;
            _targetZoom = currentZoom;
            _streak = 0;
            _lastDirection = direction;
        }
        else
        {
            // If direction reversed, pivot from the current intermediate zoom level and reset acceleration
            if ((notches > 0 && _targetZoom < currentZoom) || (notches < 0 && _targetZoom > currentZoom) || direction != _lastDirection)
            {
                _targetZoom = currentZoom;
                _streak = 0;
            }
        }

        double multiplier = 1.0;
        if (AccelerationEnabled && dt > 0 && dt < MaxAccelerationIntervalMs && direction == _lastDirection && _lastDirection != 0)
        {
            double notchRate = Math.Abs(notches) * 1000.0 / Math.Max(dt, MinIntervalMs);
            if (notchRate > BaseCadenceNotchesPerSec)
            {
                _streak = Math.Min(MaxStreak, _streak + 1);
                double rateBonus = (notchRate - BaseCadenceNotchesPerSec) * AccelerationSlope;
                double streakBonus = 1.0 + (_streak - 1) * StreakSlope;
                multiplier = Math.Clamp(1.0 + rateBonus * streakBonus, 1.0, MaxMultiplier);
            }
            else
            {
                _streak = 0;
            }
        }
        else
        {
            _streak = 0;
        }

        CurrentMultiplier = multiplier;
        _lastDirection = direction;

        double effectiveNotches = notches * multiplier;
        double step = effectiveNotches * OverviewCamera.ZoomStep * _targetZoom;
        double nextTarget = Math.Clamp(_targetZoom + step, OverviewCamera.ZoomMin, MaxZoom);

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
        _lastTick = now;
        IsActive = Math.Abs(_targetZoom - _currentZoom) >= OverviewCamera.ZoomEpsilon;

        // Dynamically adjust responsiveness so fast scrolling tracks with zero sluggishness
        _activeLambda = Math.Clamp(_baseLambda * (1.0 + 0.25 * (multiplier - 1.0)), _baseLambda, MaxLambda);
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
        double decay = Math.Exp(-_activeLambda * dt);
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
            CurrentMultiplier = 1.0;
            _streak = 0;
            _activeLambda = _baseLambda;
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
        CurrentMultiplier = 1.0;
        _streak = 0;
        _activeLambda = _baseLambda;
    }
}
