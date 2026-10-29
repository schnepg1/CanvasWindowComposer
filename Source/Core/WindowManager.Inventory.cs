using System;

namespace CanvasDesktop;

internal sealed partial class WindowManager
{
    public event Action<IntPtr>? InventoryWindowFocused;

    /// <summary>Hide the source without minimizing it or releasing its runtime identity.</summary>
    public bool TryCarryWindow(IntPtr handle, WorldRect carried)
    {
        _projection?.ClearPending(); DrainProjectionResults();
        if (_parking.TryGetValue(handle, out var parked))
        {
            if (!RestoreAutomaticWindow(handle, parked)) return false;
            ForgetParking(handle);
        }
        if (!_win32.ClipWindow(handle).Accepted) return false;
        TrackForDesktopRestore(handle);
        carried.InInventory = true; carried.PinnedToScreen = false;
        _canvas.RestoreWindow(handle, carried);
        _clippedWindows.Add(handle); _previewUnclipped.Remove(handle); _livePreviewWindows.Remove(handle);
        _pendingMoves.Remove(handle); _unconfirmedMoves.Remove(handle);
        ObserveRuntimeWindow(handle);
        return true;
    }

    private bool IsCarried(IntPtr handle) => _canvas.Windows.TryGetValue(handle, out var world) && world.InInventory;

    public bool NavigateInventoryWindow(IntPtr handle)
    {
        if (!IsCarried(handle)) return false;
        InventoryWindowFocused?.Invoke(handle); return true;
    }
}
