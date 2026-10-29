using System;
using System.Windows;
using System.Windows.Input;

namespace CanvasDesktop;

/// <summary>Captured-pointer drag out of a drawer, with cancellation and screen-space release coordinates.</summary>
internal sealed class DrawerRowDrag
{
    private readonly FrameworkElement _surface;
    private Guid? _pressed;
    private Point _origin;
    private bool _moved;
    public bool IsHeld => _pressed.HasValue;
    public bool IsDragging => _moved;
    public event Action<bool>? HeldChanged;
    public event Action<Guid, Point>? Dropped;

    public DrawerRowDrag(FrameworkElement surface)
    {
        _surface = surface;
        surface.PreviewMouseMove += (_, e) => Move(e.GetPosition(surface), e.LeftButton == MouseButtonState.Pressed);
        surface.PreviewMouseLeftButtonUp += (_, e) => { if (IsHeld) { Release(e.GetPosition(surface)); e.Handled = true; } };
        surface.LostMouseCapture += (_, _) => Cancel();
        surface.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && IsHeld) { Cancel(); e.Handled = true; } };
    }

    public void Begin(Guid id, Point point)
    {
        Cancel(); _origin = point;
        // WPF can synchronously raise MouseMove while acquiring capture. Arm only after it succeeds.
        if (!_surface.CaptureMouse()) return;
        _pressed = id;
        HeldChanged?.Invoke(true);
    }

    internal void Move(Point point, bool leftDown)
    {
        if (!IsHeld) return;
        if (!leftDown) { Cancel(); return; }
        if (Math.Abs(point.X - _origin.X) >= SystemParameters.MinimumHorizontalDragDistance
            || Math.Abs(point.Y - _origin.Y) >= SystemParameters.MinimumVerticalDragDistance)
        { _moved = true; _surface.Cursor = Cursors.SizeAll; }
    }

    internal void Release(Point point)
    {
        if (_pressed is not { } id) return;
        bool outside = point.X < 0 || point.Y < 0 || point.X >= _surface.ActualWidth || point.Y >= _surface.ActualHeight;
        bool drop = _moved && outside;
        var screen = drop ? _surface.PointToScreen(point) : default;
        Cancel();
        if (drop) Dropped?.Invoke(id, screen);
    }

    public void Cancel()
    {
        if (!IsHeld) return;
        _pressed = null; _moved = false; _surface.Cursor = Cursors.Arrow;
        if (_surface.IsMouseCaptured) _surface.ReleaseMouseCapture();
        HeldChanged?.Invoke(false);
    }
}
