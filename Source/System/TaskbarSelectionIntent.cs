using System;

namespace CanvasDesktop;

// Correlate an actual taskbar click with the shell's subsequent window event.
// Both timestamps use the native 32-bit millisecond clock, including wraparound.
internal sealed class TaskbarSelectionIntent
{
    internal const uint LifetimeMs = 1500;
    private readonly object _gate = new();
    private bool _enabled, _pending;
    public bool NavigateMinimize { get; set; }
    private uint _clickedAt;
    private IntPtr _foreground;

    public bool Enabled
    {
        get { lock (_gate) return _enabled; }
        set { lock (_gate) { _enabled = value; _pending = false; } }
    }

    public void Click(uint timestamp, IntPtr foreground)
    {
        lock (_gate)
        {
            if (!_enabled) return;
            _clickedAt = timestamp; _foreground = foreground; _pending = true;
        }
    }

    public void Cancel() { lock (_gate) _pending = false; }

    public bool TrySelect(IntPtr window, uint timestamp, bool minimizing = false)
    {
        lock (_gate)
        {
            if (!_enabled || !_pending || window == IntPtr.Zero
                || unchecked(timestamp - _clickedAt) > LifetimeMs) return false;
            if (minimizing)
            {
                if (window != _foreground) return false;
                // Desktop minimization is not a choice of whichever app happens
                // to receive fallback focus afterward. Consume that click now.
                if (!NavigateMinimize) { _pending = false; return false; }
            }
            _pending = false;
            return true;
        }
    }

    internal static bool IsTaskbarClass(string name)
        => name is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "TaskListThumbnailWnd";
}
