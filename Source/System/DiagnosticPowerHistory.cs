using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace CanvasDesktop;

// Record only; a power transition never restarts Explorer or changes window/input state.
internal sealed class DiagnosticPowerHistory : IDisposable
{
    private readonly object _gate = new();
    private readonly Queue<Transition> _events = new();
    private readonly Func<DateTime> _utcNow;
    private readonly bool _subscribed;
    private readonly DateTime _startedUtc;
    private long _sequence;
    private bool _disposed;
    private sealed record Transition(long Sequence, DateTime CapturedUtc, string Mode);

    public DiagnosticPowerHistory(bool subscribe = true, Func<DateTime>? utcNow = null)
    {
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _startedUtc = _utcNow();
        if (subscribe) { SystemEvents.PowerModeChanged += OnPowerModeChanged; _subscribed = true; }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs args) => Record(args.Mode);

    internal void Record(PowerModes mode)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_events.Count == 32) _events.Dequeue();
            _events.Enqueue(new(++_sequence, _utcNow(), mode.ToString()));
        }
    }

    public object Snapshot()
    {
        lock (_gate) return new { StartedUtc = _startedUtc, NotificationsEnabled = _subscribed && !_disposed,
            TotalEvents = _sequence, DiscardedEvents = _sequence - _events.Count, RecentEvents = _events.ToArray() };
    }

    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; }
        if (_subscribed) SystemEvents.PowerModeChanged -= OnPowerModeChanged;
    }
}
