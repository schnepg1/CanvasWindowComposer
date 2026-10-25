using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace CanvasDesktop;

/// <summary>Visible-only image work, drained between input frames in focal order.</summary>
internal sealed class OverviewCardImageQueue
{
    internal const int ImageMaxDimension = 1024;
    internal static int ImageDimension(double projectedEdge)
        => (int)Math.Clamp(Math.Ceiling(projectedEdge / 256) * 256, 256, ImageMaxDimension);
    private sealed record Request(long Priority, long Order, Action Prepare);
    private readonly Dictionary<Guid, Request> _pending = new();
    private readonly HashSet<Guid> _visible = new();
    private readonly Func<double> _milliseconds;
    private long _order;

    public OverviewCardImageQueue(Func<double>? milliseconds = null)
        => _milliseconds = milliseconds ?? (() => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency);

    public bool HasPending => _pending.Count > 0;
    public void BeginFrame() => _visible.Clear();

    public void RequestImage(Guid id, long priority, Action prepare)
    {
        _visible.Add(id);
        long order = _pending.TryGetValue(id, out var old) ? old.Order : ++_order;
        _pending[id] = new(priority, order, prepare);
    }

    public void EndFrame()
    {
        foreach (var id in _pending.Keys.Where(id => !_visible.Contains(id)).ToArray()) _pending.Remove(id);
    }

    public int Process()
    {
        double started = _milliseconds();
        int count = 0;
        // A single slow image may exceed the time budget; never follow it with
        // more work in the same tick. Wheel/pan callbacks never call this method.
        foreach (var pair in _pending.OrderBy(p => p.Value.Priority).ThenBy(p => p.Value.Order).ToArray())
        {
            if (count >= 2 || (count > 0 && _milliseconds() - started >= 4)) break;
            _pending.Remove(pair.Key);
            pair.Value.Prepare();
            count++;
        }
        return count;
    }

    public void Clear() { _pending.Clear(); _visible.Clear(); }
}
