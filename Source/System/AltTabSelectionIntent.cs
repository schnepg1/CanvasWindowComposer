namespace CanvasDesktop;

// Records only the Alt+Tab gesture, never general keyboard input. Modern shell
// versions can omit EVENT_SYSTEM_SWITCHEND entirely for quick switching.
internal sealed class AltTabSelectionIntent
{
    private readonly object _gate = new();
    private long _generation;
    private bool _active, _released, _cancelled;

    public bool Begin()
    {
        lock (_gate)
        {
            bool first = !_active || _released;
            if (first) { _generation++; _cancelled = false; }
            _active = true; _released = false;
            return first;
        }
    }

    public void Cancel() { lock (_gate) _cancelled = true; }
    public long? Release()
    {
        lock (_gate)
        {
            if (!_active || _released) return null;
            _released = true;
            return _generation;
        }
    }

    public bool TryComplete(long generation, out bool selected)
    {
        lock (_gate)
        {
            selected = false;
            if (generation != _generation || !_active || !_released) return false;
            _active = false;
            selected = !_cancelled;
            return true;
        }
    }

    public void CompleteNative() { lock (_gate) _active = false; }

    public bool TryCompleteReleased(out bool selected)
    {
        lock (_gate)
        {
            selected = false;
            if (!_active || !_released) return false;
            _active = false;
            selected = !_cancelled;
            return true;
        }
    }
}
