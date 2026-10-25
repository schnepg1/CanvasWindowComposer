using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace CanvasDesktop;

/// <summary>
/// Owned, non-activating annotation surface above DWM thumbnails. No input is
/// intercepted: the native transparent style forwards it to the overview HWND.
/// Coordinates arrive in physical pixels and are converted to WPF DIPs per monitor.
/// </summary>
internal sealed class OverviewAnnotationWindow : Window
{
    private OverviewAnnotationSurface _drawing = new();
    private readonly IntPtr _handle;
    private readonly PerformanceDiagnostics _diagnostics;
    private static readonly Action RenderBarrier = static () => { };
    private System.Drawing.Rectangle _bounds;
    private System.Drawing.Rectangle _workingArea;
    private bool _showTaskbar;
    private readonly OverviewTaskbarRegion _taskbarRegion = new();

    public void SetTaskbarVisible(System.Drawing.Rectangle workingArea, bool visible)
    {
        _workingArea = workingArea;
        _showTaskbar = visible;
        if (!_bounds.IsEmpty) _taskbarRegion.Apply(_handle, _bounds, workingArea, visible);
    }
    private IReadOnlyList<IOverviewAnnotationLayer> _layers = Array.Empty<IOverviewAnnotationLayer>();
    public void SetDrawingLayers(IReadOnlyList<IOverviewAnnotationLayer> layers)
    {
        _layers = layers;
        _drawing.SetDrawingLayers(layers);
    }
    public IntPtr HitPlaceholderLabel(int x, int y) => _drawing.HitPlaceholderLabel(x, y);
    public void Clear()
    {
        // Drop both input snapshots and WPF's retained drawing when hidden.
        Content = null;
        _drawing = new OverviewAnnotationSurface();
        _drawing.SetDrawingLayers(_layers);
        Content = _drawing;
    }

    public OverviewAnnotationWindow(IntPtr owner, PerformanceDiagnostics? diagnostics = null)
    {
        _diagnostics = diagnostics ?? PerformanceDiagnostics.Shared;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        IsHitTestVisible = false;
        Topmost = true;
        Content = _drawing;
        var interop = new WindowInteropHelper(this) { Owner = owner };
        var hwnd = (HWND)interop.EnsureHandle();
        _handle = (IntPtr)hwnd;
        int style = PInvoke.GetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        PInvoke.SetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, style | 0x20 | 0x80 | 0x08000000);
    }

    public void Update(OverviewAnnotationSnapshot frame)
    {
        StageUpdate(frame);
        PrepareFrame();
        FlushFrame();
    }

    // A multi-monitor frame stages every window before servicing WPF rendering.
    public void StageUpdate(OverviewAnnotationSnapshot frame)
    {
        var screen = new System.Drawing.Rectangle(frame.Screen.X, frame.Screen.Y, frame.Screen.Width, frame.Screen.Height);
        bool opening = !IsVisible;
        _taskbarRegion.Apply(_handle, screen, _workingArea, _showTaskbar);
        if (opening) Show();
        if (opening || _bounds != screen)
        {
            _bounds = screen;
            PInvoke.SetWindowPos((HWND)_handle, (HWND)new IntPtr(-1),
                screen.X, screen.Y, screen.Width, screen.Height,
                SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER);
        }
        _drawing.Update(frame);
    }

    public void PrepareFrame() => _drawing.UpdateLayout();

    public void FlushFrame()
    {
        // The host runs a WinForms message loop. During a wheel/mouse-message burst,
        // WPF's deferred render can otherwise trail the synchronous DWM thumbnails.
        // Complete layout and service render-priority work for this camera frame;
        // do not drain lower-priority input or use Application.DoEvents().
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        Dispatcher.Invoke(DispatcherPriority.Render, RenderBarrier);
        _diagnostics.Complete("OverviewAnnotationFlush", _handle, started, true);
    }

}
