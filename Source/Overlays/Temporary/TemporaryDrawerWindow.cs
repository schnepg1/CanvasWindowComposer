using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using FormsTimer = System.Windows.Forms.Timer;

namespace CanvasDesktop;

/// <summary>Screen-edge host. Only the visible drawer has an HWND hit area; the canvas stays clickable beside it.</summary>
internal sealed class TemporaryDrawerWindow : Window, IDisposable
{
    private readonly FormsTimer _timer = new() { Interval = 20 };
    internal TemporaryDrawerState State { get; } = new();
    public TemporaryDrawerPanel Panel { get; } = new();
    private ScreenRect _work;
    private bool _disposing, _reduceMotion;
    private double _progress, _from, _target;
    private long _animationStarted;
    private ScreenRect _bounds;
    public event Action? BoundsChanged;

    public TemporaryDrawerWindow(ScreenRect work)
    {
        _work = work;
        Title = "Canvas temporary windows"; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = Brushes.Transparent; ShowInTaskbar = false;
        ShowActivated = false; Topmost = true; Width = TemporaryDrawerPanel.RailWidth; Height = 570;
        Content = new Grid { ClipToBounds = true, Children = { Panel } };
        Panel.ToggleRequested += () => { if (State.IsOpen) State.Close(); else State.Open(Environment.TickCount64); Animate(); };
        Panel.Drag.HeldChanged += held => { State.SetDragging(held, Environment.TickCount64); Animate(); };
        MouseEnter += (_, _) => State.SetHovered(true, Environment.TickCount64);
        MouseLeave += (_, _) => State.SetHovered(false, Environment.TickCount64);
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && !e.Handled) { ClosePeek(); e.Handled = true; } };
        Closing += (_, e) => { if (!_disposing) { e.Cancel = true; ClosePeek(); } };
        _timer.Tick += (_, _) => Advance(Environment.TickCount64);
    }

    public ScreenRect ScreenBounds => _bounds;

    public void Refresh(ScreenRect work, TemporaryAreaView? area, IReadOnlyList<WorkspaceItemView> items, bool reduceMotion)
    {
        if (_disposing) return;
        _work = work; _reduceMotion = reduceMotion;
        var added = State.Update(area?.Id, items, Environment.TickCount64);
        Panel.UpdateItems(area?.Name ?? "Temporary area", State.Items, added, reduceMotion);
        if (!IsVisible) Show();
        Position(); Animate();
    }

    public void ClosePeek() { Panel.Drag.Cancel(); State.Close(); Animate(); }
    private void Animate() { Advance(Environment.TickCount64); if (State.IsOpen || _progress > 0) _timer.Start(); }

    internal void Advance(long now)
    {
        State.Tick(now);
        Panel.SetExpanded(State.IsOpen);
        double target = State.IsOpen ? 1 : 0;
        if (_target != target) { _from = _progress; _target = target; _animationStarted = now; }
        double t = _reduceMotion ? 1 : Math.Clamp((now - _animationStarted) / 240.0, 0, 1);
        _progress = _from + (_target - _from) * (1 - Math.Pow(1 - t, 3));
        Width = TemporaryDrawerPanel.RailWidth + TemporaryDrawerPanel.PanelWidth * _progress;
        Position();
        if (!State.IsOpen && _progress == 0) _timer.Stop();
    }

    private void Position()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || _disposing) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        // Leave the upper edge for Canvas tools. Clamp even on short/high-DPI displays.
        Panel.Height = Math.Min(570, Math.Max(160, _work.Height / dpi.DpiScaleY - 32));
        Height = State.IsOpen || _progress > 0 ? Panel.Height : Math.Min(160, Panel.Height);
        int width = (int)Math.Ceiling(Width * dpi.DpiScaleX), height = Math.Min(_work.Height, (int)Math.Ceiling(Height * dpi.DpiScaleY));
        int top = Math.Clamp(_work.Top + (int)Math.Round(210 * dpi.DpiScaleY), _work.Top, Math.Max(_work.Top, _work.Bottom - height));
        var bounds = new ScreenRect(_work.Right - width, top, width, height);
        if (_bounds == bounds) return;
        _bounds = bounds;
        PInvoke.SetWindowPos((HWND)handle, (HWND)new IntPtr(-1), bounds.X, bounds.Y, width, height,
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER);
        BoundsChanged?.Invoke();
    }

    public void RaiseWithoutActivation()
    {
        if (!IsVisible || _disposing) return;
        PInvoke.SetWindowPos((HWND)new WindowInteropHelper(this).Handle, (HWND)new IntPtr(-1), 0, 0, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
    }

    public void Dispose()
    {
        if (_disposing) return;
        _disposing = true; Panel.Drag.Cancel(); _timer.Dispose(); Close();
    }
}
