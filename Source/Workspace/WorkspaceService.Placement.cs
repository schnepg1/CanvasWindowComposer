using System;
using System.Collections.Generic;

namespace CanvasDesktop;

internal sealed partial class WorkspaceService
{
    internal const string PlacementRetryMessage = "Desktop placement is still in progress. Try again when it finishes.";
    private const string PlacementQueuedMessage = "This action will resume when desktop placement finishes.";
    private readonly Queue<PendingItemCommand> _pendingItemCommands = new();
    private long _desktopGeneration;

    private sealed record PendingItemCommand(WorkspaceItem Item, Guid ItemDesktop, WorkspaceLiveWindow? Binding,
        long DesktopGeneration, Action<WorkspaceItem> Execute);

    private readonly struct PlacementWriter(WindowManager manager) : IDisposable
    {
        public void Dispose() => manager.EndPeriodicWorkspacePlacementWork();
    }

    // The manager's writer scope also blocks a reservation acquired reentrantly
    // while a synchronous native window call is pumping the UI thread.
    private PlacementWriter? BeginExplicitPlacement(bool inventory = false)
    {
        if (_wm.TryBeginPeriodicWorkspacePlacementWork())
        {
            if (_status == PlacementRetryMessage) _status = null;
            if (inventory && _inventoryStatus == PlacementRetryMessage) _inventoryStatus = null;
            return new(_wm);
        }
        _status = PlacementRetryMessage;
        if (inventory) _inventoryStatus = _status;
        Changed?.Invoke();
        return null;
    }

    private void RunItemCommand(Guid id, Action<WorkspaceItem> execute)
    {
        if (Find(id) is not { } item) return;
        if (_wm.TryBeginPeriodicWorkspacePlacementWork())
        {
            using var writer = new PlacementWriter(_wm);
            execute(item);
        }
        else if (_wm.HasDesktopPlacementReservation && !_wm.DesktopPlacementShutdownRequested)
        {
            _pendingItemCommands.Enqueue(new(item, item.DesktopId, _bindings.ForItem(id), _desktopGeneration, execute));
            _status = PlacementQueuedMessage;
            Changed?.Invoke();
        }
    }

    private void ReplayPendingItemCommands()
    {
        // Replay under the writer scope, before periodic capture can bind a new
        // window to an item the user originally targeted while it was closed.
        if (_status == PlacementQueuedMessage) _status = null;
        int count = _pendingItemCommands.Count;
        for (int i = 0; i < count && !_disposed && !_switching; i++)
        {
            var pending = _pendingItemCommands.Dequeue();
            if (!CanReplayItemCommand(pending))
            {
                _status = "A pending workspace action was canceled because its saved place or window changed. Try again.";
                continue;
            }
            pending.Execute(pending.Item);
        }
        Changed?.Invoke();
    }

    private bool CanReplayItemCommand(PendingItemCommand pending)
    {
        var item = pending.Item;
        if (pending.Binding is { } live && (!_desktops.IsOnCurrentDesktop(live.Identity.Handle)
            || BoundWindow(item.Id) != live)) return false;
        return _desktopGeneration == pending.DesktopGeneration && _desktops.CurrentDesktopId == _desktop
            && Find(item.Id) == item && item.DesktopId == pending.ItemDesktop
            && _bindings.ForItem(item.Id) == pending.Binding;
    }
}
