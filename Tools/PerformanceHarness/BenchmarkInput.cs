using CanvasDesktop;

namespace CanvasDesktop.PerformanceHarness;

internal sealed class BenchmarkInput : IInputRouter
{
    public event Action? InputAvailable;
    public event Action? DragStarted;
    public event Action? ButtonDown { add { } remove { } }
    public event Action? SearchHotkey { add { } remove { } }
    public event Action? OverviewHotkey { add { } remove { } }
    public event Action? EscPressed { add { } remove { } }
    public event Action<IntPtr>? WindowMinimized { add { } remove { } }
    public event Action<IntPtr>? WindowDestroyed { add { } remove { } }
    public event Action<IntPtr>? WindowShown { add { } remove { } }
    public event Action<IntPtr>? WindowRestored { add { } remove { } }
    public event Action<IntPtr>? WindowFocused { add { } remove { } }
    public event Action<IntPtr>? WindowMoved { add { } remove { } }
    public event Action? AltTabStarted { add { } remove { } }
    public event Action? AltTabEnded { add { } remove { } }
    public event Action<IntPtr>? WindowSelected { add { } remove { } }
    public void SetTaskbarNavigationEnabled(bool enabled) { }
    public void SetWindowSelectionTarget(Func<IntPtr, bool>? canSelect) { }
    private readonly List<PanSample> _samples = new();
    private int _dx, _dy;
    private bool _released;
    public long? PanStartTimestamp { get; private set; }
    public long? PanReleaseTimestamp { get; private set; }
    public void BeginPan()
    {
        _dx = _dy = 0; _released = false; _samples.Clear();
        PanStartTimestamp = Environment.TickCount64;
        DragStarted?.Invoke();
    }
    public void Pan(int dx, int dy)
    {
        _dx += dx; _dy += dy;
        _samples.Add(new(dx, dy, Environment.TickCount64));
        InputAvailable?.Invoke();
    }
    public void ReleasePan()
    {
        PanReleaseTimestamp = Environment.TickCount64;
        _released = true;
        InputAvailable?.Invoke();
    }
    public IReadOnlyList<PanSample> DrainPanSamples()
    {
        var samples = _samples.ToArray(); _samples.Clear(); return samples;
    }
    public bool TryDrainPanDelta(out int dx, out int dy)
    {
        dx = _dx; dy = _dy; _dx = _dy = 0;
        return dx != 0 || dy != 0;
    }
    public bool TryDrainDragEnded() { bool released = _released; _released = false; return released; }
    public bool TryDrainZoom() => false;
    public bool Enabled { get; set; } = true;
    public void EnableEscHotkey() { }
    public void DisableEscHotkey() { }
    public void SetExtraPanSurfaces(IEnumerable<IntPtr> handles) { }
    public void ClearExtraPanSurfaces() { }
    public void EnableMiddleButtonBlock() { }
    public void DisableMiddleButtonBlock() { }
}
