using System;
using System.Collections.Generic;
using System.Threading;

namespace CanvasDesktop;

/// <summary>One entry of a batched <see cref="IWindowApi.BatchMove"/> call.</summary>
/// <param name="PosOnly">If true, the call uses SWP_NOSIZE (move only).</param>
internal readonly record struct BatchMoveItem(IntPtr HWnd, WindowRect Rect, bool PosOnly)
{
    // Manager-local request identity; native backends ignore this metadata.
    public long RequestId { get; init; }
}

/// <summary>
/// Abstracts Win32 window operations so WindowManager can be unit-tested.
/// </summary>
internal interface IWindowApi
{
    // Query
    bool IsWindowVisible(IntPtr hWnd);
    int GetWindowStyle(IntPtr hWnd);
    (int x, int y, int w, int h) GetWindowRect(IntPtr hWnd);
    (int left, int top, int right, int bottom) GetFrameInset(IntPtr hWnd);
    uint GetWindowProcessId(IntPtr hWnd);
    string GetWindowTitle(IntPtr hWnd);

    /// <summary>
    /// Returns (process name, exe filename) for <paramref name="pid"/>, or a
    /// fallback if the process isn't accessible (exited, denied, etc.).
    /// </summary>
    (string name, string exe) GetProcessInfo(uint pid);

    // Filtering
    bool IsManageable(IntPtr hWnd, uint ownPid, bool allowMinimized = false, bool allowMaximized = false);

    (int x, int y, int w, int h) GetNormalRect(IntPtr hWnd) => GetWindowRect(hWnd);
    NativeOperationResult RestoreNativeState(IntPtr hWnd, WindowRect rect, WindowState state)
        => SetWindowPosition(hWnd, rect.X, rect.Y, rect.W, rect.H, 0x14);
    // Fail closed for backends without an explicit automatic-parking implementation.
    bool CanAutomaticallyMinimize(IntPtr hWnd) => false;
    IntPtr GetForegroundWindow() => IntPtr.Zero;
    NativeOperationResult MinimizeForParking(IntPtr hWnd)
        => new("MinimizeForParking", hWnd.ToInt64(), NativeOperationStatus.Failed, null, 0);
    NativeOperationResult RestoreFromParking(IntPtr hWnd, WindowRect rect)
        => RestoreNativeState(hWnd, rect, WindowState.Normal);

    // Mutation
    NativeOperationResult SetWindowPosition(IntPtr hWnd, int x, int y, int w, int h, uint flags);
    NativeOperationResult ClipWindow(IntPtr hWnd);
    NativeOperationResult UnclipWindow(IntPtr hWnd);
    /// <summary>
    /// Apply a batch of window position changes. Cancellation stops queuing new
    /// moves and finalizes the queued prefix. Acknowledgement does not confirm
    /// display, roll back prior moves, or cancel an in-flight native call.
    /// </summary>
    WindowBatchResult BatchMove(List<BatchMoveItem> items, bool isAsync, bool isTransient, CancellationToken ct = default);

    // Enumeration
    void EnumWindows(Func<IntPtr, bool> callback);

    // Screen geometry
    IReadOnlyList<(int x, int y, int w, int h)> GetScreenWorkingAreas();
}
