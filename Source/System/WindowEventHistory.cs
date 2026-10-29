using System;
using System.Collections.Generic;

namespace CanvasDesktop;

internal sealed record DiagnosticWindowEvent(long Sequence, DateTime CapturedUtc, uint NativeTimestamp,
    uint Event, long Window, string ClassName);

/// <summary>Metadata only; frequent move events and accessibility child events stay out of this ring.</summary>
internal sealed class WindowEventHistory
{
    private readonly Queue<DiagnosticWindowEvent> _events = new();
    private readonly int _capacity;
    private long _sequence;
    public WindowEventHistory(int capacity = 512)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public void Record(uint type, long window, string className, uint timestamp)
    {
        if (_events.Count == _capacity) _events.Dequeue();
        _events.Enqueue(new(++_sequence, DateTime.UtcNow, timestamp, type, window, className));
    }

    public object Snapshot() => new { TotalEvents = _sequence, DiscardedEvents = _sequence - _events.Count, RecentEvents = _events.ToArray() };
}
