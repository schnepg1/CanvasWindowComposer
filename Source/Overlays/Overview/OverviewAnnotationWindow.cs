using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace CanvasDesktop;

/// <summary>
/// Owned, non-activating annotation surface above DWM thumbnails. No input is
/// intercepted: the native transparent style forwards it to the overview HWND.
/// Coordinates arrive in physical pixels and are converted to WPF DIPs per monitor.
/// </summary>
internal sealed class OverviewAnnotationWindow : Window
{
    private OverviewAnnotationSurface _drawing = new();
    private System.Drawing.Rectangle _bounds;
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

    public OverviewAnnotationWindow(IntPtr owner)
    {
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
        int style = PInvoke.GetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        PInvoke.SetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, style | 0x20 | 0x80 | 0x08000000);
    }

    public void Update(OverviewAnnotationSnapshot frame)
    {
        _drawing.Update(frame);
        var screen = new System.Drawing.Rectangle(frame.Screen.X, frame.Screen.Y, frame.Screen.Width, frame.Screen.Height);
        bool opening = !IsVisible;
        if (opening) Show();
        if (opening || _bounds != screen)
        {
            _bounds = screen;
            PInvoke.SetWindowPos((HWND)new WindowInteropHelper(this).Handle, (HWND)new IntPtr(-1),
                screen.X, screen.Y, screen.Width, screen.Height,
                SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER);
        }
    }

}
