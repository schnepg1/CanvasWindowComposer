using System;
using System.Windows;
using FormsTimer = System.Windows.Forms.Timer;

namespace CanvasDesktop;

internal interface IDrawerDragFeedbackView : IDisposable
{
    void ShowPointer(Point point, string title, bool outside, bool canDrop);
    void ShowLanding(WindowRect? landing);
    void Hide();
}

/// <summary>The pointer follows every move; expensive placement queries run at most ten times per second.</summary>
internal sealed class DrawerDragFeedback : IDisposable
{
    private readonly DrawerRowDrag _drag;
    private readonly Func<Guid, Point, WindowRect?> _preview;
    private readonly Func<Guid, string> _title;
    private readonly IDrawerDragFeedbackView _view;
    private readonly FormsTimer _timer = new() { Interval = 100 };
    private DrawerDragPosition? _position;
    private WindowRect? _landing;
    private bool _disposed;

    public DrawerDragFeedback(DrawerRowDrag drag, Func<Guid, Point, WindowRect?> preview,
        Func<Guid, string> title, IDrawerDragFeedbackView view)
    {
        _drag = drag; _preview = preview; _title = title; _view = view;
        drag.Moved += Move; drag.HeldChanged += OnHeldChanged;
        _timer.Tick += (_, _) => RefreshPreview();
    }

    internal void Move(DrawerDragPosition position)
    {
        if (_disposed) return;
        _position = position;
        if (!position.Outside)
        {
            _timer.Stop(); _landing = null; _view.ShowLanding(null);
        }
        else if (!_timer.Enabled)
        {
            RefreshPreview(); _timer.Start();
        }
        _view.ShowPointer(position.Screen, _title(position.Id), position.Outside, _landing.HasValue);
    }

    internal void RefreshPreview()
    {
        if (_disposed || _position is not { Outside: true } position) return;
        _landing = _preview(position.Id, position.Screen);
        _view.ShowLanding(_landing);
        _view.ShowPointer(position.Screen, _title(position.Id), true, _landing.HasValue);
    }

    private void OnHeldChanged(bool held) { if (!held) Clear(); }

    internal void Clear()
    {
        _timer.Stop(); _position = null; _landing = null; _view.Hide();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _drag.Moved -= Move; _drag.HeldChanged -= OnHeldChanged;
        Clear(); _timer.Dispose(); _view.Dispose();
    }
}
