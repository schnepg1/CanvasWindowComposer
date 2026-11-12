using System;
using System.Collections.Generic;

namespace CanvasDesktop;

internal sealed partial class WindowManager
{
    [Flags]
    private enum WindowPlacementEvent { Reproject = 1, Restored = 2, Focused = 4 }

    private readonly record struct DeferredWindowEvents(AutomaticParkingToken Token, WindowPlacementEvent Events);
    private readonly record struct DeferredWindowFocus(AutomaticParkingToken Token, bool WasCarried);
    private readonly Dictionary<IntPtr, DeferredWindowEvents> _deferredWindowEvents = new();
    private DeferredWindowFocus? _deferredWindowFocus;

    private void DispatchWindowPlacementEvent(IntPtr handle, WindowPlacementEvent kind)
    {
        if (_disposed || DesktopPlacementShutdownRequested) return;
        if (HasDesktopPlacementReservation)
        {
            DeferWindowPlacementEvent(handle, kind);
            return;
        }

        if (kind == WindowPlacementEvent.Focused && _deferredWindowFocus != null && IsForeignFocus(handle))
            _deferredWindowFocus = null;
        if (kind == WindowPlacementEvent.Restored) _deferredWindowEvents.Remove(handle);
        ApplyWindowPlacementEvent(handle, kind);
    }

    private void DeferWindowPlacementEvent(IntPtr handle, WindowPlacementEvent kind)
    {
        if (kind == WindowPlacementEvent.Focused)
        {
            bool carried = IsCarried(handle);
            if (!carried) _canvas.BringToForeground(handle);
            if (carried || _parking.ContainsKey(handle) || _parkingExcluded.ContainsKey(handle))
                _deferredWindowFocus = GetWindowEventToken(handle) is { } focus ? new(focus, carried) : null;
            else if (IsForeignFocus(handle)) _deferredWindowFocus = null;
            return;
        }

        // Restore can be the first event for a previously minimized, unenrolled HWND.
        if (kind == WindowPlacementEvent.Restored) TryRegisterWindow(handle);
        if (GetWindowEventToken(handle) is not { } token) return;
        if (_deferredWindowEvents.TryGetValue(handle, out var previous) && previous.Token == token)
            kind |= previous.Events;
        _deferredWindowEvents[handle] = new(token, kind);
    }

    private bool IsForeignFocus(IntPtr handle)
    {
        uint pid = _win32.GetWindowProcessId(handle);
        return pid != 0 && pid != Environment.ProcessId;
    }

    private AutomaticParkingToken? GetWindowEventToken(IntPtr handle)
    {
        if (!_canvas.HasWindow(handle)) return null;
        ObserveRuntimeWindow(handle);
        return _visibility.GetToken(handle);
    }

    private bool CanReplayWindowEvent(AutomaticParkingToken token)
    {
        IntPtr handle = token.Identity.Handle;
        return _visibility.IsCurrent(token) && _win32.IsWindowVisible(handle)
            && (_vds == null || _vds.IsOnCurrentDesktop(handle)) && IsParkingIdentityCurrent(token)
            && _canvas.HasWindow(handle) && _visibility.IsCurrent(token);
    }

    private void ReplayDeferredWindowEvents()
    {
        if (_implicitPlacementWriterDepth != 0 || DefersPeriodicPlacementWork
            || (_deferredWindowEvents.Count == 0 && _deferredWindowFocus == null)) return;
        // Identity checks can pump messages. Keep another placement from reserving the lane mid-replay.
        _implicitPlacementWriterDepth++;
        try
        {
            var events = new List<KeyValuePair<IntPtr, DeferredWindowEvents>>(_deferredWindowEvents);
            foreach (var (handle, deferred) in events)
            {
                bool current = CanReplayWindowEvent(deferred.Token);
                if (!_deferredWindowEvents.Remove(handle) || !current) continue;
                bool restored = (deferred.Events & WindowPlacementEvent.Restored) != 0
                    && (_win32.GetWindowStyle(handle) & (int)WINDOW_STYLE.WS_MINIMIZE) == 0;
                if (restored) ApplyWindowRestored(handle);
                else if ((deferred.Events & WindowPlacementEvent.Reproject) != 0) ApplyWindowStateProjection(handle);
            }

            if (_deferredWindowFocus is not { } request) return;
            IntPtr target = request.Token.Identity.Handle;
            bool focusCurrent = CanReplayWindowEvent(request.Token);
            if (_deferredWindowFocus != request) return;
            _deferredWindowFocus = null;
            if (focusCurrent && (request.WasCarried && IsCarried(target) || _win32.GetForegroundWindow() == target))
                ApplyWindowFocused(target, replay: true);
        }
        finally { _implicitPlacementWriterDepth--; }
    }

    private void ApplyWindowPlacementEvent(IntPtr handle, WindowPlacementEvent kind)
    {
        _implicitPlacementWriterDepth++;
        try
        {
            switch (kind)
            {
                case WindowPlacementEvent.Reproject: ApplyWindowStateProjection(handle); break;
                case WindowPlacementEvent.Restored: ApplyWindowRestored(handle); break;
                case WindowPlacementEvent.Focused: ApplyWindowFocused(handle, replay: false); break;
            }
        }
        finally { _implicitPlacementWriterDepth--; }
    }

    private void ClearDeferredWindowEvents()
    {
        _deferredWindowEvents.Clear();
        _deferredWindowFocus = null;
    }
}
