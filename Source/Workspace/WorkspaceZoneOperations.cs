using System;
using System.Collections.Generic;
using System.Linq;

namespace CanvasDesktop;

internal sealed partial class WorkspaceService : IZoneLayoutCommands
{
    private readonly ZoneLayoutHistory _zoneHistory = new();
    public bool CanUndoZoneLayout => _zoneHistory.Peek(_desktop, false) != null;
    public bool CanRedoZoneLayout => _zoneHistory.Peek(_desktop, true) != null;
    /// <summary>
    /// Capture current identities/geometry, validate the entire move, update logical
    /// canvas positions, then persist once. Native projection keeps its existing
    /// acknowledgement/retry policy; minimized/maximized apps retain their state.
    /// </summary>
    public bool MoveZone(Guid id, double dx, double dy)
    {
        if (_disposed || !_started || _switching) return false;
        Capture();
        if (!Zones.TryPlanMove(id, dx, dy, out var plan))
        { _status = "The area could not move: its members or destination are no longer valid."; return false; }

        if (dx == 0 && dy == 0) return true;
        var edit = new ZoneLayoutEdit(Zones.Snapshot(plan!), plan!, Zones.Membership(id));
        return ApplyZoneLayout(plan!, () => _zoneHistory.Record(_desktop, edit));
    }

    public bool ResizeZone(Guid id, WorldRect bounds)
    {
        if (_disposed || !_started || _switching) return false;
        if (!Zones.TryPlanResize(id, bounds, out var plan))
        { _status = "The area could not resize: its boundary is no longer valid."; return false; }
        var before = Zones.Snapshot(plan!);
        if (ZoneLayoutPlanner.SamePlacement(before.Bounds, plan!.Bounds)) return true;
        var edit = new ZoneLayoutEdit(before, plan, null);
        return ApplyZoneLayout(plan, () => _zoneHistory.Record(_desktop, edit));
    }

    public bool UndoZoneLayout() => ReplayZoneLayout(false);
    public bool RedoZoneLayout() => ReplayZoneLayout(true);

    private bool ReplayZoneLayout(bool redo)
    {
        if (_disposed || !_started || _switching) return false;
        var edit = _zoneHistory.Peek(_desktop, redo);
        if (edit == null) return false;
        Capture();
        if (!Zones.TryReplay(edit, redo, out var plan))
        {
            _zoneHistory.Clear(_desktop);
            _status = "Area layout history was cleared because an affected area, member, or position changed.";
            Changed?.Invoke();
            return false;
        }
        return ApplyZoneLayout(plan!, () => _zoneHistory.Accept(_desktop, redo));
    }

    // Move, resize, undo and redo share identity checks, persistence and projection.
    // A save/native acknowledgement failure retains the applied logical edit in history,
    // just as the workspace retains it in memory for retry.
    private bool ApplyZoneLayout(ZoneLayoutPlan plan, Action acceptHistory)
    {
        // Only validated current bindings or our own placeholders may be moved.
        var targets = new List<(IntPtr Handle, WorldRect Bounds, bool Live)>();
        foreach (var placement in plan.Items)
        {
            var live = BoundWindow(placement.Id);
            var handle = live?.Identity.Handle
                ?? (_cards.TryGetValue(placement.Id, out var card) ? card.Handle : IntPtr.Zero);
            if (handle != IntPtr.Zero && _canvas.HasWindow(handle)) targets.Add((handle, placement.Bounds, live != null));
        }
        foreach (var (handle, bounds, _) in targets)
            _canvas.SetWindow(handle, bounds.X, bounds.Y, bounds.W, bounds.H);

        _status = null;
        bool saved = Zones.ApplyLayout(plan);
        acceptHistory();
        bool nativeAccepted = true;
        foreach (var (handle, bounds, live) in targets)
            if (live && bounds.State != WindowState.Normal)
                nativeAccepted &= _api.RestoreNativeState(handle, _canvas.WorldToScreen(bounds), bounds.State).Accepted;
        if (!nativeAccepted) _status = "The area layout was updated, but Windows could not update some applications' restore positions.";
        foreach (var card in _cards)
            if (Document.Items.FirstOrDefault(i => i.Id == card.Key) is { } item) card.Value.UpdateItem(View(item));
        if (targets.Count > 0) _wm.Reproject(isAsync: true);
        Changed?.Invoke();
        return saved && nativeAccepted;
    }
}
