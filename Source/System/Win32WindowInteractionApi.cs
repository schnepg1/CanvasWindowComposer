using System;
using System.Drawing;
using System.Threading;

namespace CanvasDesktop;

internal sealed class Win32WindowInteractionApi : IWindowInteractionApi
{
    private volatile WindowInteractionInputGuard? _input;
    public bool PrepareInput(IntPtr window) => (_input ??= new()).Protect(window);
    public bool InputHealthy => _input?.IsHealthy == true;
    public void ReleaseInput() { _input?.Dispose(); _input = null; }

    public bool IsAvailable(IntPtr window) => window != IntPtr.Zero && PInvoke.IsWindow((HWND)window)
        && PInvoke.IsWindowVisible((HWND)window) && PInvoke.IsWindowEnabled((HWND)window)
        && (PInvoke.GetWindowLong((HWND)window, WINDOW_LONG_PTR_INDEX.GWL_STYLE) & (int)WINDOW_STYLE.WS_MINIMIZE) == 0;

    public bool Activate(IntPtr window)
    {
        if (window == IntPtr.Zero || !PInvoke.IsWindow((HWND)window)) return false;
        if (PInvoke.GetForegroundWindow() == (HWND)window) return true;
        PInvoke.SetWindowPos((HWND)window, (HWND)(-1), 0, 0, 0, 0, SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE);
        PInvoke.SetWindowPos((HWND)window, (HWND)(-2), 0, 0, 0, 0, SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE);
        PInvoke.SetForegroundWindow((HWND)window);
        PInvoke.BringWindowToTop((HWND)window);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (PInvoke.GetForegroundWindow() != (HWND)window && clock.ElapsedMilliseconds < 100)
            Thread.Sleep(10);
        if (PInvoke.GetForegroundWindow() == (HWND)window) return true;
        var foreground = PInvoke.GetForegroundWindow();
        uint foregroundThread = PInvoke.GetWindowThreadProcessId(foreground, out _);
        uint currentThread = PInvoke.GetCurrentThreadId();
        bool attached = false;
        if (foregroundThread != 0 && foregroundThread != currentThread)
            attached = PInvoke.AttachThreadInput(currentThread, foregroundThread, true);
        try
        {
            PInvoke.BringWindowToTop((HWND)window);
            PInvoke.SetForegroundWindow((HWND)window);
            clock.Restart();
            while (PInvoke.GetForegroundWindow() != (HWND)window && clock.ElapsedMilliseconds < 250)
                Thread.Sleep(10);
            return PInvoke.GetForegroundWindow() == (HWND)window;
        }
        finally
        {
            if (attached) PInvoke.AttachThreadInput(currentThread, foregroundThread, false);
        }
    }

    public bool IsChildOf(IntPtr window, IntPtr child) => child != IntPtr.Zero
        && (child == window || PInvoke.IsChild((HWND)window, (HWND)child));

    public bool IsOwnedBy(IntPtr window, IntPtr owner)
    {
        if (window == IntPtr.Zero || owner == IntPtr.Zero || window == owner) return false;
        var next = (HWND)window;
        for (int depth = 0; depth < 32; depth++)
        {
            next = PInvoke.GetWindow(next, GET_WINDOW_CMD.GW_OWNER);
            if (next == HWND.Null) return false;
            if (next == (HWND)owner) return true;
        }
        return false;
    }

    public bool TryToClient(IntPtr window, Point screen, out Point client)
    {
        var point = new System.Drawing.Point(screen.X, screen.Y);
        bool result = PInvoke.ScreenToClient((HWND)window, ref point);
        client = point;
        return result;
    }

    public bool TryResolveClient(IntPtr window, Point screen, out IntPtr child, out Point client)
    {
        child = window;
        client = default;
        if (!TryToClient(window, screen, out var local) || !PInvoke.GetClientRect((HWND)window, out var rect)
            || local.X < rect.left || local.X >= rect.right || local.Y < rect.top || local.Y >= rect.bottom) return false;
        // ChildWindowFromPointEx uses client coordinates and ignores occluding
        // top-level windows, unlike WindowFromPoint (which would find our overlay).
        for (int depth = 0; depth < 32; depth++)
        {
            var next = PInvoke.ChildWindowFromPointEx((HWND)child, local,
                CWP_FLAGS.CWP_SKIPINVISIBLE | CWP_FLAGS.CWP_SKIPDISABLED | CWP_FLAGS.CWP_SKIPTRANSPARENT);
            if (next == HWND.Null || next == (HWND)child) break;
            child = next;
            if (!TryToClient(child, screen, out local)) return false;
        }
        client = local;
        return true;
    }

    public bool Post(IntPtr window, uint message, uint flags, Point point)
    {
        // LPARAM coordinates are signed 16-bit values. Reject unrepresentable
        // positions instead of wrapping a distant canvas window onto another control.
        if (point.X is < short.MinValue or > short.MaxValue || point.Y is < short.MinValue or > short.MaxValue) return false;
        int packed = (ushort)point.X | ((ushort)point.Y << 16);
        return PInvoke.PostMessage((HWND)window, message, (WPARAM)flags, (LPARAM)packed);
    }

    public bool? ReleaseStandardButton(IntPtr window, uint flags, Point client, bool invoke)
    {
        Span<char> buffer = stackalloc char[256];
        int length = PInvoke.GetClassName((HWND)window, buffer);
        var name = buffer[..Math.Max(0, length)];
        if (!name.Equals("Button", StringComparison.OrdinalIgnoreCase)
            && !name.StartsWith("WindowsForms10.BUTTON.", StringComparison.OrdinalIgnoreCase)) return null;
        if (!PInvoke.GetClientRect((HWND)window, out var rect)) return false;
        bool inside = client.X >= rect.left && client.X < rect.right && client.Y >= rect.top && client.Y < rect.bottom;
        // Release native capture outside the button first, so the original up
        // cannot also click when the source happens to be physically visible.
        // Then use its documented action, independent of covering HWNDs. Never
        // invoke on cancellation or on a drag released outside the original child.
        if (!Post(window, 0x202, flags, new(short.MinValue, short.MinValue))) return false;
        return !invoke || !inside || !PInvoke.IsWindowEnabled((HWND)window)
            || PInvoke.PostMessage((HWND)window, 0xF5, default, default); // BM_CLICK
    }
}
