using System;

namespace CanvasDesktop;

/// <summary>
/// Owns Win32 event hooks and exposes typed events.
/// Decouples TrayApp from raw WinEvent dispatching.
/// </summary>
internal sealed class Win32EventRouter : IDisposable
{
    public event Action<IntPtr>? WindowMinimized;   // minimize start
    public event Action<IntPtr>? WindowDestroyed;  // object destroy
    public event Action<IntPtr>? WindowShown;      // object show (top-level only)
    public event Action<IntPtr>? WindowRestored;   // minimize end
    public event Action<IntPtr>? WindowFocused;    // foreground
    public event Action<IntPtr>? WindowMoved;      // location change (top-level only)
    public event Action? AltTabStarted;
    public event Action? AltTabEnded;
    public event Action<IntPtr>? WindowSelected;
    private readonly TaskbarSelectionIntent? _taskbar;
    private readonly AltTabSelectionIntent? _altTab;
    private readonly Func<IntPtr, bool>? _canSelectWindow;

    private readonly UnhookWinEventSafeHandle _hookMinimize;
    private readonly UnhookWinEventSafeHandle _hookForeground;
    private readonly UnhookWinEventSafeHandle _hookSwitch;
    private readonly UnhookWinEventSafeHandle _hookObjectLifetime; // DESTROY + SHOW
    private readonly UnhookWinEventSafeHandle _hookLocationChange;
    private readonly WINEVENTPROC _winEventProc;

    public Win32EventRouter(TaskbarSelectionIntent? taskbar = null, AltTabSelectionIntent? altTab = null,
        Func<IntPtr, bool>? canSelectWindow = null)
    {
        _taskbar = taskbar;
        _altTab = altTab;
        _canSelectWindow = canSelectWindow;
        _winEventProc = OnWinEvent;

        uint flags = PInvoke.WINEVENT_OUTOFCONTEXT | PInvoke.WINEVENT_SKIPOWNPROCESS;

        _hookMinimize = PInvoke.SetWinEventHook(
            PInvoke.EVENT_SYSTEM_MINIMIZESTART,
            PInvoke.EVENT_SYSTEM_MINIMIZEEND,
            null, _winEventProc, 0, 0, flags);

        _hookForeground = PInvoke.SetWinEventHook(
            PInvoke.EVENT_SYSTEM_FOREGROUND,
            PInvoke.EVENT_SYSTEM_FOREGROUND,
            null, _winEventProc, 0, 0, PInvoke.WINEVENT_OUTOFCONTEXT);

        _hookSwitch = PInvoke.SetWinEventHook(
            PInvoke.EVENT_SYSTEM_SWITCHSTART,
            PInvoke.EVENT_SYSTEM_SWITCHEND,
            null, _winEventProc, 0, 0, PInvoke.WINEVENT_OUTOFCONTEXT);

        _hookObjectLifetime = PInvoke.SetWinEventHook(
            PInvoke.EVENT_OBJECT_DESTROY, // 0x8001
            PInvoke.EVENT_OBJECT_SHOW,    // 0x8002 — adjacent, one hook covers both
            null, _winEventProc, 0, 0, flags);

        _hookLocationChange = PInvoke.SetWinEventHook(
            PInvoke.EVENT_OBJECT_LOCATIONCHANGE,
            PInvoke.EVENT_OBJECT_LOCATIONCHANGE,
            null, _winEventProc, 0, 0, flags);
    }

    private void OnWinEvent(HWINEVENTHOOK hWinEventHook, uint eventType, HWND hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        switch (eventType)
        {
            case PInvoke.EVENT_SYSTEM_MINIMIZESTART:
                WindowMinimized?.Invoke(hwnd);
                if (_canSelectWindow?.Invoke(hwnd) != false
                    && _taskbar?.TrySelect(hwnd, dwmsEventTime, minimizing: true) == true)
                    WindowSelected?.Invoke(hwnd);
                break;

            case PInvoke.EVENT_OBJECT_DESTROY:
                // Fires for any child object; only forward top-level window destruction.
                if (idObject == (int)OBJECT_IDENTIFIER.OBJID_WINDOW)
                    WindowDestroyed?.Invoke(hwnd);
                break;

            case PInvoke.EVENT_OBJECT_SHOW:
                // Same — filter out tooltips, menus, child controls becoming visible.
                if (idObject == (int)OBJECT_IDENTIFIER.OBJID_WINDOW)
                    WindowShown?.Invoke(hwnd);
                break;

            case PInvoke.EVENT_SYSTEM_SWITCHSTART:
                AltTabStarted?.Invoke();
                break;

            case PInvoke.EVENT_SYSTEM_SWITCHEND:
                AltTabEnded?.Invoke();
                // SWITCHEND carries the chosen HWND, even for a quick switch
                // or an unmatched SWITCHSTART. Do not guess from transient focus.
                if (_canSelectWindow?.Invoke(hwnd) != false)
                {
                    _altTab?.CompleteNative();
                    WindowSelected?.Invoke(hwnd);
                }
                break;

            case PInvoke.EVENT_SYSTEM_MINIMIZEEND:
                WindowRestored?.Invoke(hwnd);
                break;

            case PInvoke.EVENT_SYSTEM_FOREGROUND:
                WindowFocused?.Invoke(hwnd);
                if (_canSelectWindow?.Invoke(hwnd) == true
                    && _altTab?.TryCompleteReleased(out bool selected) == true)
                {
                    AltTabEnded?.Invoke();
                    if (selected) WindowSelected?.Invoke(hwnd);
                }
                if (_taskbar?.Enabled == true && _canSelectWindow?.Invoke(hwnd) != false
                    && !ShellSelectionInputHook.IsTaskbarWindow(hwnd)
                    && _taskbar?.TrySelect(hwnd, dwmsEventTime) == true)
                    WindowSelected?.Invoke(hwnd);
                break;

            case PInvoke.EVENT_OBJECT_LOCATIONCHANGE:
                if (idObject == (int)OBJECT_IDENTIFIER.OBJID_WINDOW)
                    WindowMoved?.Invoke(hwnd);
                break;
        }
    }

    public void BeginKeyboardSwitch() => AltTabStarted?.Invoke();
    public void CompleteKeyboardSwitch(IntPtr window, bool selected)
    {
        AltTabEnded?.Invoke();
        if (selected) WindowSelected?.Invoke(window);
    }

    public void Dispose()
    {
        _hookMinimize.Dispose();
        _hookForeground.Dispose();
        _hookSwitch.Dispose();
        _hookObjectLifetime.Dispose();
        _hookLocationChange.Dispose();
    }
}
