using System;
using System.Windows;
using FormsTimer = System.Windows.Forms.Timer;

namespace CanvasDesktop;

internal interface ICanvasPlacementFeedbackView : IDisposable
{
    void ShowPointer(Point point, string title, bool outside, bool canDrop);
    void ShowLanding(WindowRect? landing);
    void Hide();
}

/// <summary>The pointer follows every move; expensive placement queries run at most ten times per second.</summary>
internal sealed class CanvasPlacementFeedback : IDisposable
{
    private readonly Func<Point, WindowRect?> _preview;
    private string _title = "";
    private readonly ICanvasPlacementFeedbackView _view;
    private readonly FormsTimer _timer = new() { Interval = 100 };
    private Point? _position;
    private bool _eligible;
    private WindowRect? _landing;
    private bool _disposed;

    public CanvasPlacementFeedback(Func<Point, WindowRect?> preview, ICanvasPlacementFeedbackView view)
    {
        _preview = preview; _view = view;
        _timer.Tick += (_, _) => RefreshPreview();
    }

    internal void Move(Point screen, string title, bool eligible = true)
    {
        if (_disposed) return;
        _position = screen; _title = title; _eligible = eligible;
        if (!eligible)
        {
            _timer.Stop(); _landing = null; _view.ShowLanding(null);
        }
        else if (!_timer.Enabled)
        {
            RefreshPreview(); _timer.Start();
        }
        _view.ShowPointer(screen, title, eligible, _landing.HasValue);
    }

    internal void RefreshPreview()
    {
        if (_disposed || !_eligible || _position is not { } position) return;
        _landing = _preview(position);
        _view.ShowLanding(_landing);
        _view.ShowPointer(position, _title, true, _landing.HasValue);
    }

    internal void Clear()
    {
        _timer.Stop(); _position = null; _landing = null; _view.Hide();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Clear(); _timer.Dispose(); _view.Dispose();
    }
}
