using CanvasDesktop;

namespace CanvasDesktop.PerformanceHarness;

internal sealed class BenchmarkInput : IInputRouter
{
    public event Action? InputAvailable { add { } remove { } }
    public event Action? DragStarted { add { } remove { } }
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
    public bool TryDrainPanDelta(out int dx, out int dy) { dx = dy = 0; return false; }
    public bool TryDrainDragEnded() => false;
    public bool TryDrainZoom() => false;
    public bool Enabled { get; set; }
    public void EnableEscHotkey() { }
    public void DisableEscHotkey() { }
    public void SetExtraPanSurfaces(IEnumerable<IntPtr> handles) { }
    public void ClearExtraPanSurfaces() { }
    public void EnableMiddleButtonBlock() { }
    public void DisableMiddleButtonBlock() { }
}
