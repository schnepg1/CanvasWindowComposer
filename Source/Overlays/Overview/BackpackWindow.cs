using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using FormsTimer = System.Windows.Forms.Timer;

namespace CanvasDesktop;

/// <summary>A tool window in physical screen space: it travels with the camera, never with canvas geometry.</summary>
internal sealed class BackpackWindow : CanvasPanelWindow, IDisposable
{
    public BackpackPanel Panel { get; } = new();
    private ScreenRect _work;
    private readonly FormsTimer _timer = new() { Interval = 25 };
    private long _arrivalUntil, _pulseStarted;
    private bool _canvasVisible, _disposing;

    public BackpackWindow(ScreenRect work)
    {
        _work = work;
        Title = "Canvas Backpack";
        Content = Panel; Panel.Margin = new Thickness(3);
        Closing += (_, e) =>
        {
            if (_disposing) return;
            e.Cancel = true;
            CloseRequested?.Invoke();
        };
        _timer.Tick += (_, _) =>
        {
            double elapsed = (Environment.TickCount64 - _pulseStarted) / 650.0;
            double scale = elapsed is >= 0 and <= 1 ? 1 + .015 * Math.Sin(elapsed * Math.PI) : 1;
            Panel.RenderTransformOrigin = new Point(.5, .5); Panel.RenderTransform = new ScaleTransform(scale, scale);
            ApplyVisibility();
            if (elapsed > 1 && Environment.TickCount64 > _arrivalUntil) _timer.Stop();
        };
    }

    public event Action? CloseRequested;

    public ScreenRect ScreenBounds
    {
        get
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle != IntPtr.Zero && PInvoke.GetWindowRect((HWND)handle, out var rect))
                return new(rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top);
            return new(_work.Left + (_work.Width - (int)Width) / 2, _work.Top, (int)Width, (int)Height);
        }
    }

    public bool ContainsScreenPoint(double x, double y)
    {
        if (!IsVisible) return false;
        var b = ScreenBounds; return x >= b.Left && x < b.Right && y >= b.Top && y < b.Bottom;
    }

    public void Present(bool open, bool canvasVisible, ScreenRect work, bool activate = false)
    {
        _work = work; _canvasVisible = canvasVisible;
        Panel.SetOpen(open); Width = open ? 450 : 264;
        Height = open ? Math.Min(650, Math.Max(340, work.Height / Math.Max(1, VisualTreeHelper.GetDpi(this).DpiScaleY) - 150)) : 78;
        Panel.SetAvailableHeight(Height - 6);
        ApplyVisibility(); Position();
        if (open && activate) { Activate(); Panel.Focus(); }
    }

    public void NotifyArrival()
    {
        _arrivalUntil = Environment.TickCount64 + 8000; _pulseStarted = Environment.TickCount64;
        _timer.Start(); ApplyVisibility(); Position();
    }

    private void ApplyVisibility()
    {
        if (_disposing) return;
        bool visible = Panel.IsOpen || _canvasVisible || Environment.TickCount64 < _arrivalUntil;
        if (visible && !IsVisible) Show();
        else if (!visible && IsVisible) Hide();
    }

    public void RaiseWithoutActivation()
    {
        if (!IsVisible) return;
        var handle = new WindowInteropHelper(this).Handle;
        PInvoke.SetWindowPos((HWND)handle, (HWND)new IntPtr(-1), 0, 0, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
    }

    private void Position()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        int width = (int)Math.Ceiling(Width * dpi.DpiScaleX), height = (int)Math.Ceiling(Height * dpi.DpiScaleY);
        int left = _work.Left + (_work.Width - width) / 2;
        PInvoke.SetWindowPos((HWND)handle, (HWND)new IntPtr(-1), left, _work.Top, width, height,
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER);
    }

    public void Dispose() { if (_disposing) return; _disposing = true; Panel.Drag.Cancel(); _timer.Dispose(); Close(); }
}
