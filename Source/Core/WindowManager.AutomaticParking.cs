using System;
using System.Collections.Generic;

namespace CanvasDesktop;

internal sealed partial class WindowManager
{
    private const long ParkingRetryMs = 10_000;
    private const long CachedRestoreTransitionMs = 150;
    private enum ParkingPhase { Minimizing, Parked, Restoring }
    private sealed class ParkingRecord
    {
        public required AutomaticParkingToken Token;
        public required WindowRect OriginalRect;
        public ParkingPhase Phase;
        public bool Recovery;
        public long RetryAt, RestoredAt;
        public WindowRect RestoreTarget;
    }
    private readonly Dictionary<IntPtr, ParkingRecord> _parking = new();
    private readonly Dictionary<IntPtr, AutomaticParkingToken> _captureRequests = new();
    private readonly Dictionary<IntPtr, long> _parkingRetry = new();
    private readonly Dictionary<IntPtr, AutomaticParkingToken> _parkingExcluded = new();
    private bool _applyingParking;
    public event Action? AutomaticParkingChanged;

    private bool IsParkingIdentityCurrent(AutomaticParkingToken key)
    {
        uint pid = _win32.GetWindowProcessId(key.Identity.Handle);
        if (pid == 0 || pid != key.Identity.ProcessId) return false;
        var process = _inspectProcess(pid);
        return process is { } p && p.ProcessId == pid && p.StartedAtUtcTicks == key.Identity.ProcessStartedAtUtcTicks
            && _win32.GetWindowProcessId(key.Identity.Handle) == pid;
    }

    private bool IsParkingCandidate(IntPtr handle)
        => _canvas.Windows.TryGetValue(handle, out var world) && world.State == WindowState.Normal
            && !world.PinnedToScreen && (_vds == null || _vds.IsOnCurrentDesktop(handle))
            && _win32.CanAutomaticallyMinimize(handle)
            && (!_parkingExcluded.TryGetValue(handle, out var excluded) || _visibility.GetToken(handle) != excluded);

    private void ProcessAutomaticParking(bool allowNewParking = true)
    {
        if (_disposed) return;
        if (_parking.Count == 0 && !_config.EnableAutomaticMinimization) return;
        long now = _clock.TickCount64;
        foreach (var (handle, record) in new List<KeyValuePair<IntPtr, ParkingRecord>>(_parking))
        {
            if (!IsParkingIdentityCurrent(record.Token)) { ForgetStaleParking(handle); continue; }
            bool minimized = (_win32.GetWindowStyle(handle) & (int)WINDOW_STYLE.WS_MINIMIZE) != 0;
            if (record.Phase == ParkingPhase.Minimizing) continue; // Reentrant event during ShowWindow.
            if (record.Phase == ParkingPhase.Parked && !minimized)
            { HandleManualParkingRestore(handle); continue; }
            if (record.Phase == ParkingPhase.Restoring && !minimized && !record.Recovery)
            {
                // Keep the image during the first restored frame; this is a time
                // allowance, not a claim that the application has finished painting.
                if (_canvas.Windows.TryGetValue(handle, out var restoringWorld)
                    && _canvas.Camera.Project(restoringWorld) != record.RestoreTarget)
                { RestoreAutomaticWindow(handle, record); continue; }
                if (now - record.RestoredAt >= CachedRestoreTransitionMs)
                {
                    ForgetParking(handle); _parkingRetry[handle] = now + ParkingRetryMs;
                    ReprojectWindow(handle);
                }
                continue;
            }
            bool shouldRestore = record.Recovery || !_config.EnableAutomaticMinimization
                || !IsParkingCandidate(handle) || _visibility.Get(handle)?.ShouldRestore == true;
            if (shouldRestore && now >= record.RetryAt) RestoreAutomaticWindow(handle, record);
        }

        if (!allowNewParking || !_config.EnableAutomaticMinimization || SuspendProjection) return;
        foreach (var handle in new List<IntPtr>(_canvas.Windows.Keys))
        {
            if (_parking.ContainsKey(handle)) continue;
            if (_visibility.GetToken(handle) is not { } key || !IsParkingCandidate(handle)
                || _visibility.Get(handle)?.CanPark != true)
            { _captureRequests.Remove(handle); continue; }
            if (_parkingRetry.TryGetValue(handle, out var retry) && now < retry) continue;

            if (_captureRequests.TryGetValue(handle, out var captured) && captured == key)
            {
                if (Previews.CapturePending) continue;
                _captureRequests.Remove(handle);
                if (!Previews.HasImage(key)) { _parkingRetry[handle] = now + ParkingRetryMs; continue; }
                StartAutomaticParking(handle, key);
            }
            else if (!Previews.CapturePending)
            {
                if (CapturePreviewBeforeParking(handle) && _visibility.GetToken(handle) is { } requested)
                    _captureRequests[handle] = requested;
            }
        }
    }

    private void StartAutomaticParking(IntPtr handle, AutomaticParkingToken expected)
    {
        _projection?.ClearPending();
        DrainProjectionResults();
        ObserveRuntimeWindow(handle);
        if (_visibility.GetToken(handle) != expected || !IsParkingIdentityCurrent(expected)
            || !IsParkingCandidate(handle) || !Previews.HasImage(expected)
            || (_win32.GetWindowStyle(handle) & (int)WINDOW_STYLE.WS_MINIMIZE) != 0
            || _visibility.BeginParking(handle) is not { } token) return;
        var native = _win32.GetWindowRect(handle);
        var record = new ParkingRecord { Token = token, OriginalRect = new(native.x, native.y, native.w, native.h),
            Phase = ParkingPhase.Minimizing };
        _parking[handle] = record; // Suppress reentrant native events before calling USER32.
        NativeOperationResult result;
        _applyingParking = true;
        try { result = _win32.MinimizeForParking(handle); }
        finally
        {
            _applyingParking = false;
            // A throwing adapter may already have minimized the application.
            // Leave a recoverable debt rather than a permanently pending action.
            record.Phase = ParkingPhase.Parked;
            record.Recovery = true;
        }
        if (!_parking.TryGetValue(handle, out var latest) || latest != record || !IsParkingIdentityCurrent(token))
        { ForgetStaleParking(handle); return; }
        bool minimized = (_win32.GetWindowStyle(handle) & (int)WINDOW_STYLE.WS_MINIMIZE) != 0;
        bool owned = _visibility.CompleteParking(token, result, minimized);
        if (!owned && !minimized)
        { ForgetParking(handle); _parkingRetry[handle] = _clock.TickCount64 + ParkingRetryMs; return; }
        // Even a partially failed operation may have minimized the window. Retain
        // the debt and recover it; never persist it as user intent.
        record.Phase = ParkingPhase.Parked;
        record.Recovery = !owned;
        _pendingMoves.Remove(handle);
        _lastScreen.Remove(handle);
        if (_clippedWindows.Contains(handle)) ApplyRegionChange(handle, WindowRegionChange.Unclip);
        AutomaticParkingChanged?.Invoke();
    }

    private bool RestoreAutomaticWindow(IntPtr handle, ParkingRecord record, bool desktopRecovery = false)
    {
        if (!IsParkingIdentityCurrent(record.Token)) { ForgetStaleParking(handle); return true; }
        var screens = _win32.GetScreenWorkingAreas();
        WindowRect target = desktopRecovery || !_canvas.Windows.TryGetValue(handle, out var world)
            ? WindowPlacementPlanner.FitDesktop(record.OriginalRect, screens)
            : _canvas.Camera.Project(world);
        record.Phase = ParkingPhase.Restoring;
        record.RestoreTarget = target;
        record.RestoredAt = _clock.TickCount64;
        record.Recovery = true;
        record.RetryAt = _clock.TickCount64 + ParkingRetryMs;
        if (_clippedWindows.Contains(handle) && !ApplyRegionChange(handle, WindowRegionChange.Unclip))
        { record.Recovery = true; record.RetryAt = _clock.TickCount64 + ParkingRetryMs; return false; }
        if (!IsParkingIdentityCurrent(record.Token)) { ForgetStaleParking(handle); return true; }
        NativeOperationResult result;
        _applyingParking = true;
        try { result = _win32.RestoreFromParking(handle, target); }
        finally { _applyingParking = false; }
        if (!IsParkingIdentityCurrent(record.Token)) { ForgetStaleParking(handle); return true; }
        bool normal = (_win32.GetWindowStyle(handle) & (int)(WINDOW_STYLE.WS_MINIMIZE | WINDOW_STYLE.WS_MAXIMIZE)) == 0;
        if (!result.Accepted || !normal)
        { record.Recovery = true; record.RetryAt = _clock.TickCount64 + ParkingRetryMs; return false; }
        // Reconcile the requested geometry explicitly, retaining cached pixels if
        // the app accepted restoration but did not accept the final native move.
        var move = TrackMove(handle, target, posOnly: false);
        var moved = _win32.SetWindowPosition(handle, target.X, target.Y, target.W, target.H,
            (uint)(SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE));
        ApplyMoveResult(move, moved);
        var actual = _win32.GetWindowRect(handle);
        bool positioned = Math.Abs((long)actual.x - target.X) <= ReconcileTolerancePx
            && Math.Abs((long)actual.y - target.Y) <= ReconcileTolerancePx
            && Math.Abs((long)actual.w - target.W) <= ReconcileTolerancePx
            && Math.Abs((long)actual.h - target.H) <= ReconcileTolerancePx;
        if (!moved.Accepted || !positioned)
        { record.Recovery = true; record.RetryAt = _clock.TickCount64 + ParkingRetryMs; return false; }
        record.Recovery = false;
        _visibility.RelinquishParking(handle);
        AutomaticParkingChanged?.Invoke();
        if (desktopRecovery) ForgetParking(handle);
        return true;
    }

    private void HandleManualParkingRestore(IntPtr handle)
    {
        if (!_parking.TryGetValue(handle, out var record) || !IsParkingIdentityCurrent(record.Token))
        { ForgetParking(handle); return; }
        _parkingExcluded[handle] = record.Token; // Self-restoring apps stay excluded for this lifetime.
        ForgetParking(handle);
        if (_win32.GetForegroundWindow() == handle) CenterManuallyRestoredWindow(handle);
    }

    private void CenterManuallyRestoredWindow(IntPtr handle)
    {
        if (!_canvas.Windows.TryGetValue(handle, out var world) || world.PinnedToScreen) return;
        var screens = _win32.GetScreenWorkingAreas();
        if (WindowPlacementPlanner.IntersectsAnyScreen(_canvas.Camera.Project(world), screens) || screens.Count == 0) return;
        var screen = screens[0];
        _canvas.SetCamera(world.X + world.W / 2 - (screen.x + screen.w / 2.0) / _canvas.Zoom,
            world.Y + world.H / 2 - (screen.y + screen.h / 2.0) / _canvas.Zoom);
        _canvas.Commit();
    }

    private void ForgetStaleParking(IntPtr handle)
    {
        // Final desktop fitting also must not touch the replacement window.
        _sessionWindows.Remove(handle);
        _clippedWindows.Remove(handle);
        ForgetParking(handle);
    }

    private void ForgetParking(IntPtr handle)
    {
        bool changed = _parking.Remove(handle);
        _captureRequests.Remove(handle);
        _visibility.RelinquishParking(handle);
        if (changed) AutomaticParkingChanged?.Invoke();
    }

    private List<Exception> RecoverAutomaticParking()
    {
        var failures = new List<Exception>();
        _projection?.ClearPending();
        foreach (var (handle, record) in new List<KeyValuePair<IntPtr, ParkingRecord>>(_parking))
        {
            record.Recovery = true;
            try
            {
                if (!RestoreAutomaticWindow(handle, record, desktopRecovery: true))
                    failures.Add(new InvalidOperationException($"Could not recover automatically parked window {handle}."));
            }
            catch (Exception ex) { failures.Add(ex); }
        }
        _captureRequests.Clear();
        return failures;
    }
}
