using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace CanvasDesktop;

/// <summary>
/// Production implementation of IWindowApi wrapping Win32 APIs.
/// </summary>
internal sealed class Win32WindowApi : IWindowApi
{
    private readonly IScreens _screens;
    private readonly PerformanceDiagnostics _diagnostics;
    private readonly WindowBatchMover _moves;
    private readonly object _screenGate = new();
    private long _screenVersion = -1;
    private IReadOnlyList<(int x, int y, int w, int h)>? _workingAreas;
    public long ScreenTopologyVersion => _screens.TopologyVersion;

    public Win32WindowApi(IScreens? screens = null, PerformanceDiagnostics? diagnostics = null)
    {
        _screens = screens ?? WinFormsScreens.Instance;
        _diagnostics = diagnostics ?? PerformanceDiagnostics.Shared;
        _moves = new(new WindowPositionNative(), _diagnostics);
    }

    private static readonly HashSet<string> ExcludedClasses = new()
    {
        "Progman",
        "WorkerW",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "TaskListThumbnailWnd",
        "NotifyIconOverflowWindow",
        "Windows.UI.Core.CoreWindow"
    };

    public bool IsWindowVisible(IntPtr hWnd)
    {
        return PInvoke.IsWindowVisible((HWND)hWnd);
    }

    public unsafe bool HasVisibleClientArea(IntPtr hWnd)
    {
        var window = (HWND)hWnd;
        if (!PInvoke.IsWindowVisible(window) || !PInvoke.GetClientRect(window, out var client)
            || client.right <= client.left || client.bottom <= client.top) return false;
        int cloaked = 0;
        var result = PInvoke.DwmGetWindowAttribute(window, DWMWINDOWATTRIBUTE.DWMWA_CLOAKED,
            &cloaked, sizeof(int));
        return result.Succeeded && cloaked == 0;
    }

    public int GetWindowStyle(IntPtr hWnd)
    {
        return PInvoke.GetWindowLong((HWND)hWnd, WINDOW_LONG_PTR_INDEX.GWL_STYLE);
    }

    public (int x, int y, int w, int h) GetWindowRect(IntPtr hWnd)
    {
        PInvoke.GetWindowRect((HWND)hWnd, out RECT rect);
        return (rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top);
    }

    public unsafe (int left, int top, int right, int bottom) GetFrameInset(IntPtr hWnd)
    {
        PInvoke.GetWindowRect((HWND)hWnd, out RECT full);
        RECT visual;
        HRESULT hr = PInvoke.DwmGetWindowAttribute((HWND)hWnd,
            DWMWINDOWATTRIBUTE.DWMWA_EXTENDED_FRAME_BOUNDS,
            &visual,
            (uint)sizeof(RECT));

        if (hr.Failed)
            return (0, 0, 0, 0);

        return (
            Math.Max(0, visual.left - full.left),
            Math.Max(0, visual.top - full.top),
            Math.Max(0, full.right - visual.right),
            Math.Max(0, full.bottom - visual.bottom)
        );
    }

    public unsafe uint GetWindowProcessId(IntPtr hWnd)
    {
        uint pid;
        _ = PInvoke.GetWindowThreadProcessId((HWND)hWnd, &pid);
        return pid;
    }

    public unsafe string GetWindowTitle(IntPtr hWnd)
    {
        HWND h = (HWND)hWnd;
        int len = PInvoke.GetWindowTextLength(h);
        if (len <= 0) return "";
        Span<char> buffer = len < 512 ? stackalloc char[len + 1] : new char[len + 1];
        int written;
        fixed (char* p = buffer)
        {
            written = PInvoke.GetWindowText(h, new PWSTR(p), buffer.Length);
        }
        return new string(buffer[..written]);
    }

    public (string name, string exe) GetProcessInfo(uint pid)
    {
        try
        {
            using var proc = Process.GetProcessById((int)pid);
            string name = proc.ProcessName;
            string exe = Path.GetFileName(proc.MainModule?.FileName ?? name);
            return (name, exe);
        }
        catch
        {
            return ($"PID {pid}", "");
        }
    }

    public unsafe bool IsManageable(IntPtr hWnd, uint ownPid, bool allowMinimized = false, bool allowMaximized = false)
    {
        HWND h = (HWND)hWnd;
        if (!PInvoke.IsWindowVisible(h))
            return false;

        uint pid;
        _ = PInvoke.GetWindowThreadProcessId(h, &pid);
        if (pid == ownPid)
            return false;

        int style = PInvoke.GetWindowLong(h, WINDOW_LONG_PTR_INDEX.GWL_STYLE);
        int exStyle = PInvoke.GetWindowLong(h, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);

        if (!allowMaximized && (style & (int)WINDOW_STYLE.WS_MAXIMIZE) != 0)
            return false;
        if (!allowMinimized && (style & (int)WINDOW_STYLE.WS_MINIMIZE) != 0)
            return false;

        if ((exStyle & (int)WINDOW_EX_STYLE.WS_EX_TOOLWINDOW) != 0 &&
            (exStyle & (int)WINDOW_EX_STYLE.WS_EX_APPWINDOW) == 0)
            return false;

        if (PInvoke.GetParent(h) != HWND.Null)
            return false;

        int cloaked;
        HRESULT cloakedHr = PInvoke.DwmGetWindowAttribute(h, DWMWINDOWATTRIBUTE.DWMWA_CLOAKED,
            &cloaked, sizeof(int));
        if (cloakedHr.Succeeded && cloaked != 0)
            return false;

        Span<char> classBuf = stackalloc char[256];
        int classLen;
        fixed (char* p = classBuf)
        {
            classLen = PInvoke.GetClassName(h, new PWSTR((char*)p), classBuf.Length);
        }
        if (classLen == 0)
            return false;
        string className = new string(classBuf[..classLen]);
        if (ExcludedClasses.Contains(className))
            return false;

        return true;
    }

    public NativeOperationResult SetWindowPosition(IntPtr hWnd, int x, int y, int w, int h, uint flags)
        => _moves.Move(new(hWnd, new(x, y, w, h), false), flags);

    public IntPtr GetForegroundWindow() => PInvoke.GetForegroundWindow();

    public NativeOperationResult RestoreForNavigation(IntPtr handle)
    {
        long started = Stopwatch.GetTimestamp();
        // SW_RESTORE honors Windows' restore-to-maximized flag. Its return value
        // describes previous visibility, so confirm the resulting state instead.
        PInvoke.ShowWindow((HWND)handle, SHOW_WINDOW_CMD.SW_RESTORE);
        bool restored = PInvoke.IsWindow((HWND)handle)
            && (GetWindowStyle(handle) & (int)WINDOW_STYLE.WS_MINIMIZE) == 0;
        return _diagnostics.Complete("RestoreForNavigation", handle, started, restored, 0);
    }

    public unsafe bool CanAutomaticallyMinimize(IntPtr hWnd)
    {
        HWND hwnd = (HWND)hWnd;
        if (!IsManageable(hWnd, (uint)Environment.ProcessId, allowMinimized: true)
            || !PInvoke.IsWindowEnabled(hwnd) || PInvoke.GetForegroundWindow() == hwnd
            || PInvoke.GetWindow(hwnd, GET_WINDOW_CMD.GW_OWNER) != HWND.Null) return false;
        Span<char> className = stackalloc char[64];
        int classLength;
        fixed (char* text = className) classLength = PInvoke.GetClassName(hwnd, new PWSTR(text), className.Length);
        if (className[..classLength].SequenceEqual("#32770")) return false; // Standard top-level dialogs.
        bool hasOwnedWindow = false;
        EnumWindows(candidate =>
        {
            if (PInvoke.IsWindowVisible((HWND)candidate)
                && PInvoke.GetWindow((HWND)candidate, GET_WINDOW_CMD.GW_OWNER) == hwnd)
                hasOwnedWindow = true;
            return !hasOwnedWindow;
        });
        return !hasOwnedWindow;
    }

    public NativeOperationResult MinimizeForParking(IntPtr hWnd)
    {
        long started = Stopwatch.GetTimestamp();
        if (!PInvoke.IsWindow((HWND)hWnd))
            return _diagnostics.Complete("MinimizeForParking", hWnd, started, false);
        // ShowWindow returns previous visibility, not success. Verify the native style.
        PInvoke.ShowWindow((HWND)hWnd, SHOW_WINDOW_CMD.SW_SHOWMINNOACTIVE);
        bool minimized = PInvoke.IsWindow((HWND)hWnd)
            && (GetWindowStyle(hWnd) & (int)WINDOW_STYLE.WS_MINIMIZE) != 0;
        return _diagnostics.Complete("MinimizeForParking", hWnd, started, minimized);
    }

    public NativeOperationResult RestoreFromParking(IntPtr hWnd, WindowRect rect)
        => RestoreNativeStateCore(hWnd, rect, WindowState.Normal, clearRestoreToMaximized: true);

    public (int x, int y, int w, int h) GetNormalRect(IntPtr hWnd)
    {
        var placement = new WINDOWPLACEMENT { length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (!PInvoke.GetWindowPlacement((HWND)hWnd, ref placement)) return GetWindowRect(hWnd);
        var screen = System.Windows.Forms.Screen.FromHandle(hWnd);
        var r = placement.rcNormalPosition;
        return (r.left + screen.WorkingArea.X - screen.Bounds.X,
            r.top + screen.WorkingArea.Y - screen.Bounds.Y, r.right - r.left, r.bottom - r.top);
    }

    public NativeOperationResult RestoreNativeState(IntPtr hWnd, WindowRect rect, WindowState state)
        => RestoreNativeStateCore(hWnd, rect, state);

    private NativeOperationResult RestoreNativeStateCore(IntPtr hWnd, WindowRect rect, WindowState state,
        bool clearRestoreToMaximized = false)
    {
        var screen = System.Windows.Forms.Screen.FromRectangle(new System.Drawing.Rectangle(rect.X, rect.Y, rect.W, rect.H));
        int dx = screen.WorkingArea.X - screen.Bounds.X, dy = screen.WorkingArea.Y - screen.Bounds.Y;
        var placement = new WINDOWPLACEMENT
        { length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<WINDOWPLACEMENT>() };
        // Preserve native placement flags, notably restore-to-maximized for a
        // minimized window. Repairing its normal bounds must not lose that state.
        long started = Stopwatch.GetTimestamp();
        if (!PInvoke.GetWindowPlacement((HWND)hWnd, ref placement))
        {
            int error = Marshal.GetLastPInvokeError();
            return _diagnostics.Complete("RestoreNativeState.GetWindowPlacement", hWnd, started, false, error);
        }
        if (clearRestoreToMaximized) placement.flags &= ~(WINDOWPLACEMENT_FLAGS)2;
        placement.showCmd = state == WindowState.Maximized ? SHOW_WINDOW_CMD.SW_SHOWMAXIMIZED
            : state == WindowState.Minimized ? SHOW_WINDOW_CMD.SW_SHOWMINNOACTIVE : SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE;
        placement.rcNormalPosition = new RECT { left = rect.X - dx, top = rect.Y - dy,
            right = rect.X + rect.W - dx, bottom = rect.Y + rect.H - dy };
        bool ok = PInvoke.SetWindowPlacement((HWND)hWnd, placement);
        int lastError = ok ? 0 : Marshal.GetLastPInvokeError();
        return _diagnostics.Complete("RestoreNativeState", hWnd, started, ok, lastError);
    }

    public NativeOperationResult ClipWindow(IntPtr hWnd)
    {
        long started = Stopwatch.GetTimestamp();
        HRGN region = PInvoke.CreateRectRgn(0, 0, 0, 0);
        if (region == default(HRGN)) return _diagnostics.Complete("ClipWindow.CreateRectRgn", hWnd, started, false);
        bool ok = PInvoke.SetWindowRgn((HWND)hWnd, region, true) != 0;
        // USER32 owns the region only on success. Free our allocation on failure.
        if (!ok) PInvoke.DeleteObject(region);
        // SetWindowRgn does not document an extended error code; never report stale LastError.
        return _diagnostics.Complete("ClipWindow", hWnd, started, ok);
    }

    public NativeOperationResult UnclipWindow(IntPtr hWnd)
    {
        long started = Stopwatch.GetTimestamp();
        bool ok = PInvoke.SetWindowRgn((HWND)hWnd, (HRGN)IntPtr.Zero, true) != 0;
        return _diagnostics.Complete("UnclipWindow", hWnd, started, ok);
    }

    public WindowBatchResult BatchMove(List<BatchMoveItem> items, bool isAsync, bool isTransient,
        System.Threading.CancellationToken ct = default)
        => _moves.Execute(items, isAsync, isTransient, ct);

    public unsafe void EnumWindows(Func<IntPtr, bool> callback)
    {
        WNDENUMPROC proc = (HWND hWnd, LPARAM _) => callback(hWnd);
        PInvoke.EnumWindows(proc, 0);
        GC.KeepAlive(proc);
    }

    public IReadOnlyList<(int x, int y, int w, int h)> GetScreenWorkingAreas()
    {
        lock (_screenGate)
        {
            long version = _screens.TopologyVersion;
            if (version >= 0 && version == _screenVersion && _workingAreas != null) return _workingAreas;
            var src = _screens.AllWorkingAreas;
            var areas = new (int, int, int, int)[src.Count];
            for (int i = 0; i < src.Count; i++)
            {
                var s = src[i];
                areas[i] = (s.X, s.Y, s.Width, s.Height);
            }
            _screenVersion = version;
            return _workingAreas = Array.AsReadOnly(areas);
        }
    }
}
