using System;
using System.Collections.Generic;
using Windows.Win32.UI.WindowsAndMessaging;

namespace CanvasDesktop.Tests;

internal sealed class FakeWindowApi : IWindowApi
{
    public class WindowInfo
    {
        public int X, Y, W, H;
        public int Style;
        public bool Visible = true;
        public uint ProcessId = 1;
        public bool Manageable = true;
        public string Title = "";
    }

    public readonly Dictionary<IntPtr, WindowInfo> Windows = new();
    public readonly HashSet<IntPtr> ClippedWindows = new();
    public readonly List<BatchMoveItem> LastBatch = new();
    public readonly List<(IntPtr hWnd, int x, int y, int w, int h, uint flags)> SetPositionCalls = new();
    public readonly List<(IntPtr hWnd, WindowRect rect, WindowState state)> NativeStateCalls = new();
    public Action<IntPtr>? BeforeSetPosition;
    public List<(int x, int y, int w, int h)> ScreenAreas = new() { (0, 0, 1920, 1080) };
    public int ScreenAreaQueryCount;
    public long ScreenTopologyVersion { get; set; } = -1;
    public readonly List<IntPtr> ClipCalls = new();
    public readonly List<IntPtr> UnclipCalls = new();
    public HashSet<IntPtr> FailedClips = new();
    public HashSet<IntPtr> FailedUnclips = new();
    public HashSet<IntPtr> FailedMoves = new();
    public HashSet<IntPtr> ParkingIneligible = new();
    public HashSet<IntPtr> FailedMinimizations = new();
    public HashSet<IntPtr> FailedParkingRestores = new();
    public readonly List<IntPtr> MinimizeCalls = new();
    public readonly List<(IntPtr handle, WindowRect rect)> ParkingRestoreCalls = new();
    public IntPtr Foreground;
    public int ParkingEligibilityQueries;
    public Action<IntPtr>? AfterParkingMinimize;
    public Action<IntPtr>? AfterParkingRestore;

    public IntPtr GetForegroundWindow() => Foreground;
    public bool CanAutomaticallyMinimize(IntPtr handle)
    {
        ParkingEligibilityQueries++;
        return Windows.TryGetValue(handle, out var w) && w.Manageable && w.Visible
            && !ParkingIneligible.Contains(handle) && Foreground != handle;
    }
    public NativeOperationResult MinimizeForParking(IntPtr handle)
    {
        MinimizeCalls.Add(handle);
        if (FailedMinimizations.Contains(handle)) return new("MinimizeForParking", handle.ToInt64(), NativeOperationStatus.Failed, 5, 0);
        Windows[handle].Style |= (int)WINDOW_STYLE.WS_MINIMIZE;
        AfterParkingMinimize?.Invoke(handle);
        return NativeOperationResult.Success("MinimizeForParking", handle);
    }
    public NativeOperationResult RestoreFromParking(IntPtr handle, WindowRect rect)
    {
        ParkingRestoreCalls.Add((handle, rect));
        if (FailedParkingRestores.Contains(handle)) return new("RestoreFromParking", handle.ToInt64(), NativeOperationStatus.Failed, 5, 0);
        Windows[handle].Style &= ~(int)(WINDOW_STYLE.WS_MINIMIZE | WINDOW_STYLE.WS_MAXIMIZE);
        var result = SetWindowPosition(handle, rect.X, rect.Y, rect.W, rect.H, 0x14);
        AfterParkingRestore?.Invoke(handle);
        return result;
    }

    // Windows returned by EnumWindows, in order
    public List<IntPtr> EnumOrder = new();

    public void AddWindow(IntPtr hWnd, int x, int y, int w, int h,
        uint pid = 1, int style = 0, bool manageable = true, string title = "")
    {
        Windows[hWnd] = new WindowInfo
        {
            X = x, Y = y, W = w, H = h,
            ProcessId = pid, Style = style, Manageable = manageable, Title = title
        };
        if (!EnumOrder.Contains(hWnd))
            EnumOrder.Add(hWnd);
    }

    public bool IsWindowVisible(IntPtr hWnd) =>
        Windows.TryGetValue(hWnd, out var w) && w.Visible;

    public int GetWindowStyle(IntPtr hWnd) =>
        Windows.TryGetValue(hWnd, out var w) ? w.Style : 0;

    public (int x, int y, int w, int h) GetWindowRect(IntPtr hWnd) =>
        Windows.TryGetValue(hWnd, out var w) ? (w.X, w.Y, w.W, w.H) : (0, 0, 0, 0);

    public (int left, int top, int right, int bottom) GetFrameInset(IntPtr hWnd)
    {
        return (0, 0, 0, 0);
    }

    public uint GetWindowProcessId(IntPtr hWnd) =>
        Windows.TryGetValue(hWnd, out var w) ? w.ProcessId : 0;

    public string GetWindowTitle(IntPtr hWnd) =>
        Windows.TryGetValue(hWnd, out var w) ? w.Title : "";

    public Dictionary<uint, (string name, string exe)> Processes = new();
    public (string name, string exe) ProcessFallback = ("", "");

    public (string name, string exe) GetProcessInfo(uint pid)
    {
        return Processes.TryGetValue(pid, out var info) ? info : ProcessFallback;
    }

    public bool IsManageable(IntPtr hWnd, uint ownPid, bool allowMinimized = false, bool allowMaximized = false)
    {
        if (!Windows.TryGetValue(hWnd, out var w)) return false;
        if (w.ProcessId == ownPid) return false;
        return w.Manageable;
    }

    public NativeOperationResult SetWindowPosition(IntPtr hWnd, int x, int y, int w, int h, uint flags)
    {
        BeforeSetPosition?.Invoke(hWnd);
        SetPositionCalls.Add((hWnd, x, y, w, h, flags));
        if (FailedMoves.Contains(hWnd)) return new("SetWindowPos", hWnd.ToInt64(), NativeOperationStatus.Failed, 5, 0);
        if (Windows.TryGetValue(hWnd, out var win))
        {
            win.X = x; win.Y = y; win.W = w; win.H = h;
        }
        return NativeOperationResult.Success("SetWindowPos", hWnd);
    }

    public NativeOperationResult ClipWindow(IntPtr hWnd)
    {
        ClipCalls.Add(hWnd);
        if (FailedClips.Contains(hWnd)) return new("ClipWindow", hWnd.ToInt64(), NativeOperationStatus.Failed, null, 0);
        ClippedWindows.Add(hWnd);
        return NativeOperationResult.Success("ClipWindow", hWnd);
    }

    public NativeOperationResult RestoreNativeState(IntPtr hWnd, WindowRect rect, WindowState state)
    {
        NativeStateCalls.Add((hWnd, rect, state));
        return SetWindowPosition(hWnd, rect.X, rect.Y, rect.W, rect.H, 0x14);
    }

    public NativeOperationResult UnclipWindow(IntPtr hWnd)
    {
        UnclipCalls.Add(hWnd);
        if (FailedUnclips.Contains(hWnd)) return new("UnclipWindow", hWnd.ToInt64(), NativeOperationStatus.Failed, null, 0);
        ClippedWindows.Remove(hWnd);
        return NativeOperationResult.Success("UnclipWindow", hWnd);
    }

    public Action<System.Threading.CancellationToken>? BeforeBatchMove;

    public WindowBatchResult BatchMove(List<BatchMoveItem> items, bool isAsync, bool isTransient, System.Threading.CancellationToken ct = default)
    {
        BeforeBatchMove?.Invoke(ct);
        LastBatch.Clear();
        LastBatch.AddRange(items);
        var results = new List<NativeOperationResult>();
        foreach (var item in items)
        {
            if (ct.IsCancellationRequested) { results.Add(new("BatchMoveItem", item.HWnd.ToInt64(), NativeOperationStatus.Cancelled, null, 0)); continue; }
            if (FailedMoves.Contains(item.HWnd)) { results.Add(new("BatchMoveItem", item.HWnd.ToInt64(), NativeOperationStatus.Failed, 5, 0)); continue; }
            if (Windows.TryGetValue(item.HWnd, out var win))
            {
                var r = item.Rect;
                win.X = r.X; win.Y = r.Y;
                if (!item.PosOnly) { win.W = r.W; win.H = r.H; }
            }
            results.Add(NativeOperationResult.Success("BatchMoveItem", item.HWnd));
        }
        var status = results.Exists(r => r.Status == NativeOperationStatus.Failed) ? NativeOperationStatus.Failed
            : ct.IsCancellationRequested ? NativeOperationStatus.Cancelled : NativeOperationStatus.Succeeded;
        return new(new("BatchMove", 0, status, null, 0, items.Count), results);
    }

    public void EnumWindows(Func<IntPtr, bool> callback)
    {
        foreach (var hWnd in EnumOrder)
        {
            if (!callback(hWnd)) break;
        }
    }

    public IReadOnlyList<(int x, int y, int w, int h)> GetScreenWorkingAreas()
    {
        ScreenAreaQueryCount++;
        return ScreenAreas;
    }
}
