using System;
using CanvasDesktop;

// Only in-memory saved geometry; the real app's HWND coordinator is deliberately absent.
internal sealed class PreviewLayoutCommands : IZoneLayoutCommands, IDisposable
{
    private readonly ZoneService _zones;
    private readonly ZoneLayoutHistory _history = new();
    public event Action? Changed;
    public bool CanUndoZoneLayout => _history.Peek(Guid.Empty, false) != null;
    public bool CanRedoZoneLayout => _history.Peek(Guid.Empty, true) != null;
    public PreviewLayoutCommands(ZoneService zones) { _zones = zones; zones.Changed += Notify; }
    private void Notify() => Changed?.Invoke();
    public bool Move(Guid id, double dx, double dy)
        => _zones.TryPlanMove(id, dx, dy, out var plan) && Apply(plan!, _zones.Membership(id));
    public bool Resize(Guid id, WorldRect bounds)
        => _zones.TryPlanResize(id, bounds, out var plan) && Apply(plan!, null);
    private bool Apply(ZoneLayoutPlan plan, Guid[]? membership)
    {
        var before = _zones.Snapshot(plan);
        if (ZoneLayoutPlanner.SamePlacement(before.Bounds, plan.Bounds)) return true;
        _history.Record(Guid.Empty, new(before, plan, membership));
        return _zones.ApplyLayout(plan);
    }
    public bool UndoZoneLayout() => Replay(false);
    public bool RedoZoneLayout() => Replay(true);
    private bool Replay(bool redo)
    {
        var edit = _history.Peek(Guid.Empty, redo);
        if (edit == null) return false;
        if (!_zones.TryReplay(edit, redo, out var plan)) { _history.Clear(Guid.Empty); Notify(); return false; }
        _history.Accept(Guid.Empty, redo);
        return _zones.ApplyLayout(plan!);
    }
    public void Dispose() => _zones.Changed -= Notify;
}
