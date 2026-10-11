using System;
using System.Collections.Generic;

namespace CanvasDesktop;

internal interface IZoneLayoutCommands
{
    bool CanUndoZoneLayout { get; }
    bool CanRedoZoneLayout { get; }
    event Action? Changed;
    bool UndoZoneLayout();
    bool RedoZoneLayout();
}

internal sealed record ZoneLayoutEdit(ZoneLayoutPlan Before, ZoneLayoutPlan After, IReadOnlyList<Guid>? Membership);

/// <summary>Bounded session history, scoped by desktop. Contains saved IDs and geometry, never HWNDs.</summary>
internal sealed class ZoneLayoutHistory
{
    internal const int Capacity = 50;
    private sealed class Timeline
    {
        public readonly List<ZoneLayoutEdit> Undo = new();
        public readonly Stack<ZoneLayoutEdit> Redo = new();
    }
    private readonly Dictionary<Guid, Timeline> _desktops = new();
    private Timeline For(Guid desktop)
    {
        if (!_desktops.TryGetValue(desktop, out var timeline)) _desktops.Add(desktop, timeline = new());
        return timeline;
    }
    public ZoneLayoutEdit? Peek(Guid desktop, bool redo)
    {
        if (!_desktops.TryGetValue(desktop, out var t)) return null;
        if (redo) return t.Redo.TryPeek(out var edit) ? edit : null;
        return t.Undo.Count > 0 ? t.Undo[^1] : null;
    }
    public void Record(Guid desktop, ZoneLayoutEdit edit)
    {
        var t = For(desktop);
        t.Redo.Clear(); t.Undo.Add(edit);
        if (t.Undo.Count > Capacity) t.Undo.RemoveAt(0);
    }
    public void Accept(Guid desktop, bool redo)
    {
        var t = For(desktop);
        if (redo) t.Undo.Add(t.Redo.Pop());
        else { var edit = t.Undo[^1]; t.Undo.RemoveAt(t.Undo.Count - 1); t.Redo.Push(edit); }
    }
    public void Clear(Guid desktop) => _desktops.Remove(desktop);
}
