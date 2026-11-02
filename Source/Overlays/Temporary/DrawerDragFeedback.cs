using System;
using System.Windows;

namespace CanvasDesktop;

/// <summary>Adapts drawer capture and item identity to the shared canvas placement preview.</summary>
internal sealed class DrawerDragFeedback : IDisposable
{
    private readonly DrawerRowDrag _drag;
    private readonly Func<Guid, string> _title;
    private readonly CanvasPlacementFeedback _feedback;
    private Guid _id;

    public DrawerDragFeedback(DrawerRowDrag drag, Func<Guid, Point, WindowRect?> preview,
        Func<Guid, string> title, ICanvasPlacementFeedbackView view)
    {
        _drag = drag; _title = title;
        _feedback = new(point => preview(_id, point), view);
        drag.Moved += Move; drag.HeldChanged += OnHeldChanged;
    }

    internal void Move(DrawerDragPosition position)
    {
        _id = position.Id;
        _feedback.Move(position.Screen, _title(position.Id), position.Outside);
    }

    internal void RefreshPreview() => _feedback.RefreshPreview();
    internal void Clear() => _feedback.Clear();
    private void OnHeldChanged(bool held) { if (!held) Clear(); }

    public void Dispose()
    {
        _drag.Moved -= Move; _drag.HeldChanged -= OnHeldChanged;
        _feedback.Dispose();
    }
}
