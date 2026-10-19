using System;

namespace CanvasDesktop;

/// <summary>
/// UI-thread camera invalidations share one pending frame. Flush/cancel invalidate
/// old callbacks, and a change during drawing is retained for the next frame.
/// The host supplies the clock so lifecycle behavior is testable without a HWND.
/// </summary>
internal sealed class OverviewFrameScheduler(Action<Action> schedule, Action draw)
{
    private bool _pending, _queued, _drawing;
    private long _generation;

    public void Request()
    {
        _pending = true;
        Queue();
    }

    private void Queue()
    {
        if (_queued || _drawing || !_pending) return;
        _queued = true;
        long generation = ++_generation;
        schedule(() =>
        {
            if (generation != _generation) return;
            _queued = false;
            Flush();
        });
    }

    public void Flush()
    {
        if (_drawing || !_pending) return;
        Cancel();
        _drawing = true;
        try { draw(); }
        finally { _drawing = false; Queue(); }
    }

    public void Cancel()
    {
        _pending = _queued = false;
        ++_generation;
    }
}
