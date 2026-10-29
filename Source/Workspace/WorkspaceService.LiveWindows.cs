using System;
using System.Linq;

namespace CanvasDesktop;

// Runtime identity validation and native placement handoff.
internal sealed partial class WorkspaceService
{
    private bool StillCurrent(WorkspaceLiveWindow live)
        => _bindings.At(live.Identity.Handle) == live && _windowInspector.Inspect(live.Identity.Handle)?.Identity == live.Identity;

    private WorkspaceLiveWindow? ValidBinding(IntPtr handle)
    {
        var live = _bindings.At(handle);
        if (live?.ItemId == null) return null;
        bool inactive = Document.Items.Any(i => i.Id == live.ItemId && i.DesktopId != _desktop);
        if (StillCurrent(live) && (inactive || _api.IsWindowVisible(handle))) return live;
        _bindings.Unbind(live);
        return null;
    }

    private WorkspaceLiveWindow? BoundWindow(Guid itemId)
        => _bindings.ForItem(itemId) is { } live ? ValidBinding(live.Identity.Handle) : null;

    private void EnrollNativeGeometry(IntPtr handle)
    {
        var (x, y, w, h) = _api.GetNormalRect(handle);
        _wm.TrackForDesktopRestore(handle);
        var (wx, wy) = _canvas.ScreenToWorld(x, y);
        var (ww, wh) = _canvas.ScreenToWorldSize(w, h);
        int style = _api.GetWindowStyle(handle);
        _canvas.RestoreWindow(handle, new WorldRect { X = wx, Y = wy, W = ww, H = wh,
            State = (style & (int)WINDOW_STYLE.WS_MINIMIZE) != 0 ? WindowState.Minimized
                : (style & (int)WINDOW_STYLE.WS_MAXIMIZE) != 0 ? WindowState.Maximized : WindowState.Normal });
    }

    private void RestoreBinding(WorkspaceLiveWindow live, WorkspaceItem item)
    {
        if (!StillCurrent(live)) { _bindings.Unbind(live); return; }
        var handle = live.Identity.Handle;
        var placed = item.Bounds;
        if (placed.InInventory)
        {
            if (!_wm.TryCarryWindow(handle, placed)) { _bindings.Unbind(live); return; }
            RemoveCard(item.Id); _launching.Remove(item.Id); _launchBaselines.Remove(item.Id);
            _launchMessages.Remove(item.Id); _ambiguous.Remove(item.Id);
            return;
        }
        if ((_api.GetWindowStyle(handle) & (int)WINDOW_STYLE.WS_MINIMIZE) != 0 && !_wm.UsesCachedPreview(handle))
        {
            // Matching a saved slot must not wake an app the user minimized
            // before Canvas started. Keep the saved geometry, adopt native intent.
            placed.State = WindowState.Minimized;
            item.Bounds = placed;
        }
        var restored = _api.RestoreNativeState(handle, _canvas.WorldToScreen(placed), placed.State);
        if (!restored.Accepted || !StillCurrent(live)) { _bindings.Unbind(live); return; }
        if (placed.State == WindowState.Normal) _wm.RestorePlacement(handle, placed);
        else _canvas.RestoreWindow(handle, placed);
        // Keep the progress card until the matched window has actually accepted
        // its saved placement; failed/native-stale handoffs can retry next tick.
        RemoveCard(item.Id);
        _launching.Remove(item.Id);
        _launchBaselines.Remove(item.Id);
        _launchMessages.Remove(item.Id);
        _ambiguous.Remove(item.Id);
    }

    private void OnRemoving(IntPtr handle)
    {
        InvalidateOverviewSnapshot();
        if (_disposed || _switching) return;
        var live = _bindings.Remove(handle);
        if (live?.ItemId is { } id)
        {
            var item = Document.Items.FirstOrDefault(i => i.Id == id);
            if (item?.Placement == WorkspacePlacement.Temporary)
            {
                Document.Items.Remove(item);
                foreach (var zone in Document.Zones.Where(z => z.IsTemporaryArea)) zone.ItemIds.Remove(id);
                _temporaryAreaChanged = true;
                _nextCapture = 0;
                return;
            }
            // A destroyed window may no longer be queryable. Cached canvas geometry
            // is safe only if the handle has not already been reused by another process.
            var current = _windowInspector.Inspect(handle);
            if (item != null && (current?.Identity == live.Identity || _api.GetWindowProcessId(handle) == 0)
                && _canvas.Windows.TryGetValue(handle, out var world) && WorkspaceGeometry.ValidBounds(world))
                item.Bounds = world;
            Save();
        }
        // Defer form creation until Tick, outside WM's enumeration/removal loop.
    }

    private void OnWindowStateChanged(IntPtr handle)
    {
        InvalidateOverviewSnapshot();
        _nextCapture = 0;
    }
    private void OnAutomaticParkingChanged() => _nextCapture = 0;

    public bool IsOpen(WorkspaceItem item) => BoundWindow(item.Id) != null;


}
