using System;
using System.Drawing;

namespace CanvasDesktop;

internal sealed class Win32WindowInteractionApi : IWindowInteractionApi
{
    public bool IsAvailable(IntPtr window) => window != IntPtr.Zero && PInvoke.IsWindow((HWND)window)
        && PInvoke.IsWindowVisible((HWND)window) && PInvoke.IsWindowEnabled((HWND)window)
        && (PInvoke.GetWindowLong((HWND)window, WINDOW_LONG_PTR_INDEX.GWL_STYLE) & (int)WINDOW_STYLE.WS_MINIMIZE) == 0;

    public bool Activate(IntPtr window)
    {
        PInvoke.SetForegroundWindow((HWND)window);
        return PInvoke.GetForegroundWindow() == (HWND)window;
    }

    public bool IsChildOf(IntPtr window, IntPtr child) => child != IntPtr.Zero
        && (child == window || PInvoke.IsChild((HWND)window, (HWND)child));

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
}
