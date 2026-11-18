using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Interop;

namespace CanvasDesktop;

internal readonly record struct TopmostWindowCandidate(
    IntPtr Handle,
    string ClassName,
    bool IsVisible,
    bool IsCloaked,
    bool HasNonEmptyBounds,
    IntPtr Owner);

/// <summary>
/// Centralizes Z-order placement for Canvas topmost surfaces and floating panels so they remain
/// in the <c>WS_EX_TOPMOST</c> band strictly below the Windows Taskbar / shell tray popups
/// (when <c>ShowTaskbarInCanvas</c> is enabled) and strictly below Canvas's own active priority
/// menus in all modes. Never mutates Explorer's own Z-order or steals foreground focus.
/// </summary>
internal static class CanvasZOrder
{
    public const int WM_WINDOWPOSCHANGING = 0x0046;

    public static readonly IntPtr HwndTopmost = new(-1);
    public static readonly IntPtr HwndNoTopmost = new(-2);
    public static readonly IntPtr HwndTop = IntPtr.Zero;
    public static readonly IntPtr HwndBottom = new(1);

    private const int MaxTopmostWalk = 512;
    private const int MaxZOrderWalk = 4096;

    private static readonly ConcurrentDictionary<IntPtr, byte> PriorityPopups = new();
    private static readonly ConditionalWeakTable<Window, object> HookedWpfWindows = new();
    private static readonly ConditionalWeakTable<ToolStripDropDown, object> TrackedDropDowns = new();

    [ThreadStatic]
    private static IntPtr _positioningHwnd;

    internal static int ActivePriorityPopupCount
    {
        get
        {
            PruneDeadPriorityPopups();
            return PriorityPopups.Count;
        }
    }

    public static bool IsTaskbarClass(ReadOnlySpan<char> className)
        => className.SequenceEqual("Shell_TrayWnd")
        || className.SequenceEqual("Shell_SecondaryTrayWnd");

    public static bool IsShellTrayPopupClass(ReadOnlySpan<char> className)
        => className.SequenceEqual("TopLevelWindowForOverflowXamlIsland")
        || className.SequenceEqual("NotifyIconOverflowWindow")
        || className.SequenceEqual("TaskListThumbnailWnd")
        || className.SequenceEqual("Xaml_WindowedPopupClass")
        || className.SequenceEqual("XamlExplorerHostIslandWindow")
        || className.SequenceEqual("#32768");

    internal static bool IsCeilingCandidate(
        IntPtr handle,
        ReadOnlySpan<char> className,
        bool isVisible,
        bool isCloaked,
        bool hasNonEmptyBounds,
        IntPtr owner,
        bool clampBelowTaskbar,
        bool isPriorityPopup,
        IntPtr excludeWindow)
    {
        if (handle == IntPtr.Zero)
            return false;
        if (excludeWindow != IntPtr.Zero && (handle == excludeWindow || owner == excludeWindow))
            return false;
        if (!isVisible)
            return false;

        if (isPriorityPopup)
            return !isCloaked;

        if (!clampBelowTaskbar)
            return false;

        if (IsTaskbarClass(className))
            return true;

        return IsShellTrayPopupClass(className) && !isCloaked && hasNonEmptyBounds;
    }

    internal static IntPtr SelectCeilingAnchor(
        IReadOnlyList<TopmostWindowCandidate> topmostBand,
        bool clampBelowTaskbar,
        IReadOnlySet<IntPtr> priorityPopups,
        IntPtr excludeWindow = default)
    {
        IntPtr lowestAnchor = IntPtr.Zero;
        for (int i = 0; i < topmostBand.Count; i++)
        {
            var c = topmostBand[i];
            bool isPriority = priorityPopups.Count > 0 && priorityPopups.Contains(c.Handle);
            if (IsCeilingCandidate(
                    c.Handle,
                    c.ClassName.AsSpan(),
                    c.IsVisible,
                    c.IsCloaked,
                    c.HasNonEmptyBounds,
                    c.Owner,
                    clampBelowTaskbar,
                    isPriority,
                    excludeWindow))
            {
                lowestAnchor = c.Handle;
            }
        }

        return lowestAnchor != IntPtr.Zero ? lowestAnchor : HwndTopmost;
    }

    internal static bool ShouldRewriteWindowPosInsertAfter(
        IntPtr currentInsertAfter,
        IntPtr anchor,
        bool windowIsTopmost,
        Func<IntPtr, IntPtr, bool> isAbove)
    {
        if (!windowIsTopmost || anchor == IntPtr.Zero || anchor == HwndTopmost)
            return false;
        if (currentInsertAfter == HwndNoTopmost || currentInsertAfter == HwndBottom)
            return false;
        if (currentInsertAfter == anchor)
            return false;
        if (currentInsertAfter == HwndTop || currentInsertAfter == HwndTopmost)
            return true;

        return isAbove(currentInsertAfter, anchor);
    }

    public static IntPtr ResolveCeilingInsertAfter(bool clampBelowTaskbar, IntPtr excludeWindow = default)
    {
        if (!PriorityPopups.IsEmpty)
            PruneDeadPriorityPopups();

        bool hasPriority = !PriorityPopups.IsEmpty;
        if (!clampBelowTaskbar && !hasPriority)
            return HwndTopmost;

        IntPtr lowestAnchor = IntPtr.Zero;
        HWND current = PInvoke.GetWindow(PInvoke.GetDesktopWindow(), GET_WINDOW_CMD.GW_CHILD);
        Span<char> classBuffer = stackalloc char[128];

        for (int steps = 0; !current.IsNull && steps < MaxTopmostWalk; steps++)
        {
            int exStyle = PInvoke.GetWindowLong(current, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
            if ((exStyle & (int)WINDOW_EX_STYLE.WS_EX_TOPMOST) == 0)
                break;

            IntPtr handle = current;
            if (PInvoke.IsWindowVisible(current))
            {
                IntPtr owner = PInvoke.GetWindow(current, GET_WINDOW_CMD.GW_OWNER);
                bool excluded = excludeWindow != IntPtr.Zero && (handle == excludeWindow || owner == excludeWindow);
                if (!excluded)
                {
                    bool isPriority = hasPriority && PriorityPopups.ContainsKey(handle);
                    if (isPriority)
                    {
                        if (!IsCloaked(current))
                            lowestAnchor = handle;
                    }
                    else if (clampBelowTaskbar)
                    {
                        int length = PInvoke.GetClassName(current, classBuffer);
                        ReadOnlySpan<char> className = length > 0 ? classBuffer[..length] : ReadOnlySpan<char>.Empty;
                        if (IsTaskbarClass(className))
                        {
                            lowestAnchor = handle;
                        }
                        else if (IsShellTrayPopupClass(className) && !IsCloaked(current) && HasNonEmptyBounds(current))
                        {
                            lowestAnchor = handle;
                        }
                    }
                }
            }

            current = PInvoke.GetWindow(current, GET_WINDOW_CMD.GW_HWNDNEXT);
        }

        return lowestAnchor != IntPtr.Zero ? lowestAnchor : HwndTopmost;
    }

    public static bool IsWindowAbove(IntPtr upper, IntPtr lower)
    {
        if (upper == IntPtr.Zero || lower == IntPtr.Zero || upper == lower)
            return false;

        HWND current = PInvoke.GetWindow((HWND)upper, GET_WINDOW_CMD.GW_HWNDNEXT);
        for (int steps = 0; !current.IsNull && steps < MaxZOrderWalk; steps++)
        {
            if ((IntPtr)current == lower)
                return true;
            current = PInvoke.GetWindow(current, GET_WINDOW_CMD.GW_HWNDNEXT);
        }

        return false;
    }

    public static void PlaceAtCeiling(
        IntPtr hwnd,
        bool clampBelowTaskbar,
        SET_WINDOW_POS_FLAGS flags = SET_WINDOW_POS_FLAGS.SWP_NOMOVE
            | SET_WINDOW_POS_FLAGS.SWP_NOSIZE
            | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE,
        int x = 0,
        int y = 0,
        int width = 0,
        int height = 0)
    {
        if (hwnd == IntPtr.Zero || !PInvoke.IsWindow((HWND)hwnd))
            return;

        IntPtr anchor = ResolveCeilingInsertAfter(clampBelowTaskbar, hwnd);
        IntPtr previous = _positioningHwnd;
        _positioningHwnd = hwnd;
        try
        {
            if (anchor != HwndTopmost && !IsTopmostWindow(hwnd))
            {
                // Win32 invariant: inserting a non-topmost HWND after a topmost HWND leaves it
                // in the non-topmost band. Promote into the topmost band first, then clamp.
                PInvoke.SetWindowPos((HWND)hwnd, (HWND)HwndTopmost, 0, 0, 0, 0,
                    SET_WINDOW_POS_FLAGS.SWP_NOMOVE
                    | SET_WINDOW_POS_FLAGS.SWP_NOSIZE
                    | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE
                    | SET_WINDOW_POS_FLAGS.SWP_NOSENDCHANGING);
            }

            PInvoke.SetWindowPos((HWND)hwnd, (HWND)anchor, x, y, width, height, flags);
        }
        finally
        {
            _positioningHwnd = previous;
        }
    }

    public static unsafe void ClampWindowPosChanging(IntPtr hwnd, IntPtr lParam, bool clampBelowTaskbar)
    {
        if (hwnd == IntPtr.Zero || lParam == IntPtr.Zero || _positioningHwnd == hwnd)
            return;

        var wp = (WindowPos*)lParam;
        const uint skipFlags = (uint)(SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_HIDEWINDOW);
        if ((wp->flags & skipFlags) != 0)
            return;

        if (!IsTopmostWindow(hwnd))
            return;

        IntPtr anchor = ResolveCeilingInsertAfter(clampBelowTaskbar, hwnd);
        if (!ShouldRewriteWindowPosInsertAfter(wp->hwndInsertAfter, anchor, windowIsTopmost: true, IsWindowAbove))
            return;

        wp->hwndInsertAfter = anchor;
    }

    public static void AttachWpfClampHook(Window window, Func<bool> shouldClampBelowTaskbar)
    {
        if (window == null || shouldClampBelowTaskbar == null)
            return;
        if (!HookedWpfWindows.TryAdd(window, new object()))
            return;

        HwndSource? attachedSource = null;
        IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_WINDOWPOSCHANGING)
                ClampWindowPosChanging(hwnd, lParam, shouldClampBelowTaskbar());
            return IntPtr.Zero;
        }

        void TryAttach()
        {
            if (attachedSource != null) return;
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;
            var source = HwndSource.FromHwnd(handle);
            if (source == null) return;
            source.AddHook(Hook);
            attachedSource = source;
        }

        void OnSourceInitialized(object? sender, EventArgs args) => TryAttach();

        window.SourceInitialized += OnSourceInitialized;
        window.Closed += (_, _) =>
        {
            window.SourceInitialized -= OnSourceInitialized;
            if (attachedSource != null)
            {
                attachedSource.RemoveHook(Hook);
                attachedSource = null;
            }
        };

        TryAttach();
    }

    public static void RegisterPriorityMenu(ContextMenuStrip menu)
    {
        if (menu == null) return;
        AttachDropDownTracking(menu);
    }

    public static void RaisePriorityPopup(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !PInvoke.IsWindow((HWND)hwnd))
            return;

        PInvoke.SetWindowPos((HWND)hwnd, (HWND)HwndTopmost, 0, 0, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOMOVE
            | SET_WINDOW_POS_FLAGS.SWP_NOSIZE
            | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
    }

    internal static unsafe bool IsCloaked(HWND window)
    {
        int cloaked = 0;
        HRESULT hr = PInvoke.DwmGetWindowAttribute(window, DWMWINDOWATTRIBUTE.DWMWA_CLOAKED, &cloaked, sizeof(int));
        return hr.Succeeded && cloaked != 0;
    }

    internal static bool HasNonEmptyBounds(HWND window)
        => PInvoke.GetWindowRect(window, out var rect)
        && rect.right > rect.left
        && rect.bottom > rect.top;

    internal static bool IsTopmostWindow(IntPtr hwnd)
        => hwnd != IntPtr.Zero
        && (PInvoke.GetWindowLong((HWND)hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE)
            & (int)WINDOW_EX_STYLE.WS_EX_TOPMOST) != 0;

    private static void AttachDropDownTracking(ToolStripDropDown dropDown)
    {
        if (!TrackedDropDowns.TryAdd(dropDown, new object()))
            return;

        IntPtr trackedHandle = IntPtr.Zero;

        void RemoveTracked()
        {
            if (trackedHandle != IntPtr.Zero)
            {
                PriorityPopups.TryRemove(trackedHandle, out _);
                trackedHandle = IntPtr.Zero;
            }
        }

        dropDown.Opened += (_, _) =>
        {
            if (dropDown.IsDisposed || !dropDown.IsHandleCreated) return;
            IntPtr handle = dropDown.Handle;
            if (handle == IntPtr.Zero) return;
            if (trackedHandle != IntPtr.Zero && trackedHandle != handle)
                PriorityPopups.TryRemove(trackedHandle, out _);
            trackedHandle = handle;
            PriorityPopups[handle] = 0;
            RaisePriorityPopup(handle);
        };

        dropDown.Closed += (_, _) => RemoveTracked();
        dropDown.HandleDestroyed += (_, _) => RemoveTracked();
        dropDown.Disposed += (_, _) => RemoveTracked();

        foreach (ToolStripItem item in dropDown.Items)
        {
            if (item is ToolStripDropDownItem subItem)
                AttachSubItemTracking(subItem);
        }
    }

    private static void AttachSubItemTracking(ToolStripDropDownItem item)
    {
        if (item.HasDropDownItems)
            AttachDropDownTracking(item.DropDown);

        item.DropDownOpening += (_, _) =>
        {
            if (item.HasDropDownItems)
                AttachDropDownTracking(item.DropDown);
        };
    }

    private static void PruneDeadPriorityPopups()
    {
        foreach (var pair in PriorityPopups)
        {
            if (!PInvoke.IsWindow((HWND)pair.Key))
                PriorityPopups.TryRemove(pair.Key, out _);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPos
    {
        public IntPtr hwnd;
        public IntPtr hwndInsertAfter;
        public int x;
        public int y;
        public int cx;
        public int cy;
        public uint flags;
    }
}
