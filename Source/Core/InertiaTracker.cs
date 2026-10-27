using System;
using System.Collections.Generic;

namespace CanvasDesktop;

/// <summary>UI-owned, elapsed-time pan momentum in screen pixels, independent of presentation rate.</summary>
internal sealed class InertiaTracker
{
    private const double StopThresholdPxPerMs = 0.02;
    private const long VelocityWindowMs = 100;
    private static readonly double DecayPerMs = -Math.Log(0.92) / 16.667;
    private readonly List<(double dx, double dy, long time)> _samples = new();
    private readonly IClock _clock;
    private double _vx, _vy;
    private long _lastTick;
    public bool IsActive { get; private set; }

    public InertiaTracker(IClock? clock = null) => _clock = clock ?? SystemClock.Instance;

    public void Begin(long? timestamp = null)
    {
        Cancel();
        _samples.Add((0, 0, timestamp ?? _clock.TickCount64));
    }

    public void RecordDelta(int dx, int dy, long? timestamp = null)
    {
        long time = timestamp ?? _clock.TickCount64;
        if (_samples.Count > 0)
        {
            var previous = _samples[^1];
            time = Math.Max(time, previous.time);
            if (time == previous.time)
            {
                _samples[^1] = (previous.dx + dx, previous.dy + dy, time);
                return;
            }
        }
        _samples.Add((dx, dy, time));
        if (_samples.Count > 128) _samples.RemoveRange(0, _samples.Count - 128);
    }

    public bool Release(long? timestamp = null)
    {
        IsActive = false;
        _vx = _vy = 0;
        long released = timestamp ?? _clock.TickCount64;
        long start = _samples.Count == 0 ? released : Math.Max(released - VelocityWindowMs, _samples[0].time);
        double dx = 0, dy = 0;
        for (int i = 1; i < _samples.Count; i++)
        {
            var previous = _samples[i - 1]; var sample = _samples[i];
            long overlap = Math.Min(released, sample.time) - Math.Max(start, previous.time);
            if (overlap <= 0 || sample.time <= previous.time) continue;
            double fraction = (double)overlap / (sample.time - previous.time);
            dx += sample.dx * fraction; dy += sample.dy * fraction;
        }
        _samples.Clear();
        long elapsed = released - start;
        if (elapsed <= 0) return false;
        _vx = dx / elapsed; _vy = dy / elapsed;
        IsActive = Math.Max(Math.Abs(_vx), Math.Abs(_vy)) >= StopThresholdPxPerMs;
        // Input can have waited in the UI queue; use event time for velocity,
        // but begin the visible coast now instead of jumping over its first frames.
        _lastTick = _clock.TickCount64;
        return IsActive;
    }

    public void Cancel()
    {
        IsActive = false;
        _vx = _vy = 0;
        _samples.Clear();
    }

    public (double dx, double dy, bool stopped) Tick()
    {
        if (!IsActive) return (0, 0, false);
        long now = _clock.TickCount64;
        double dt = Math.Max(0, now - _lastTick);
        _lastTick = now;
        double remaining = Math.Log(Math.Max(Math.Abs(_vx), Math.Abs(_vy)) / StopThresholdPxPerMs) / DecayPerMs;
        bool stopped = dt >= remaining;
        dt = Math.Min(dt, Math.Max(0, remaining));
        double decay = Math.Exp(-DecayPerMs * dt);
        // Integrate the decay exactly; retain subpixels and the final displacement.
        double distance = (1 - decay) / DecayPerMs;
        double dx = _vx * distance, dy = _vy * distance;
        _vx *= decay; _vy *= decay;
        if (stopped) IsActive = false;
        return (dx, dy, stopped);
    }
}
