using System;
using System.Collections.Generic;

namespace CanvasDesktop;

/// <summary>
/// Consumes Canvas state and applies it to real windows.
/// Handles enumeration, positioning, reconciliation.
/// </summary>
internal sealed class WindowManager : IDisposable
{
    private const int ReconcileTolerancePx = 2;
    private const int ReprojectThrottleMs = 200;

    private readonly Canvas _canvas;
    private readonly IWindowApi _win32;
    private readonly IAppConfig _config;
    private readonly IClock _clock;
    private readonly IVirtualDesktops? _vds;
    private readonly ProjectionWorker? _projection;
    private readonly IInputRouter _input;
    private bool _disposed;

    // Retain identities across virtual-desktop resets; never move a recycled HWND
    // belonging to a different process during shutdown.
    private readonly Dictionary<IntPtr, uint> _sessionWindows = new();

    // Track last projected screen positions to detect manual moves
    private readonly Dictionary<IntPtr, (int x, int y, int w, int h)> _lastScreen = new();

    // Windows with clipped (empty) region to prevent them from fighting off-screen
    private readonly HashSet<IntPtr> _clippedWindows = new();
    // Clipping can succeed before a move is failed, cancelled or superseded.
    // Keep retrying edge placement until a native move has been accepted.
    private readonly HashSet<IntPtr> _unconfirmedMoves = new();

    private long _lastReprojectTick;

    public event Action<IntPtr>? WindowRemoving;

    // Our own placeholder forms are explicitly enrolled, never discovered as apps.
    public void RestorePlacement(IntPtr hWnd, WorldRect world)
    {
        DrainProjectionResults();
        TrackForDesktopRestore(hWnd);
        _canvas.RestoreWindow(hWnd, world);
        var plan = WindowPlacementPlanner.Plan(world, _canvas.Camera, _win32.GetScreenWorkingAreas(),
            ClipOffScreen, _clippedWindows.Contains(hWnd), PlacementIntent.RestoreLayout, repositionClipped: true);
        if (!ApplyRegionChange(hWnd, plan.RegionChange)) return;
        if (!plan.ShouldMove) return;
        var rect = plan.TargetRect;
        _lastScreen[hWnd] = (rect.X, rect.Y, rect.W, rect.H);
        var result = _win32.SetWindowPosition(hWnd, rect.X, rect.Y, rect.W, rect.H,
            (uint)(SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE));
        ApplyMoveResult(new(hWnd, rect, false), result);
    }

    // Temporarily suspends greedy draw (SetWindowRgn clipping)
    public bool SuspendGreedyDraw { get; set; }

    /// <summary>
    /// While true, <see cref="OnCameraChanged"/> short-circuits — no transient
    /// reproject is scheduled on the worker. Set during overview Zooming, when
    /// click-through is off and real-window positions don't need to track the
    /// camera for hit-testing. Eliminates the worker-job wait at overview close
    /// (we'd otherwise queue a batch from the close-time SetCamera and then
    /// immediately wait for it inside ReprojectSync). <see cref="OnCommitted"/>
    /// bypasses this gate so the explicit close commit still runs.
    /// </summary>
    public bool SuspendProjection { get; set; }

    private bool _suspendReconcile;
    private bool _reconcilePending;

    /// <summary>
    /// While true, <see cref="Reconcile"/> and <see cref="ReconcileWindow"/>
    /// short-circuit and only flag that a reconcile was requested. Set by the
    /// overview lifecycle so our own reprojections don't feed back through
    /// WindowMoved -> Reconcile and overwrite canvas world coords with
    /// rounding-drifted screen coords. When flipped back to false, if any
    /// reconcile call was suppressed during the on period, a full
    /// <see cref="Reconcile"/> runs to catch up on any drift.
    /// </summary>
    public bool SuspendReconcile
    {
        get { return _suspendReconcile; }
        set
        {
            if (_suspendReconcile == value) return;
            _suspendReconcile = value;
            if (!value && _reconcilePending)
            {
                _reconcilePending = false;
                Reconcile();
            }
        }
    }

    public WindowManager(
        Canvas canvas,
        IWindowApi win32,
        IAppConfig config,
        IInputRouter input,
        IClock? clock = null,
        IVirtualDesktops? vds = null,
        bool useAsyncProjection = false)
    {
        _canvas = canvas;
        _win32 = win32;
        _config = config;
        _clock = clock ?? SystemClock.Instance;
        _vds = vds;
        _input = input;
        _projection = useAsyncProjection ? new ProjectionWorker(win32) : null;

        canvas.Committed += OnCommitted;
        canvas.CameraChanged += OnCameraChanged;
        canvas.CollapseChanged += OnReprojectWindowEvent;
        canvas.MaximizeChanged += OnReprojectWindowEvent;

        input.WindowMinimized += OnWindowMinimizedEvent;
        input.WindowRestored += OnWindowRestoredEvent;
        input.WindowDestroyed += OnWindowDestroyedEvent;
        input.WindowShown += OnWindowShownEvent;
        input.WindowMoved += OnWindowMovedEvent;
        input.WindowFocused += OnWindowFocusedEvent;
        input.AltTabStarted += OnAltTabStarted;
        input.AltTabEnded += OnAltTabEnded;
    }

    /// <summary>Background tick for window discovery + stale removal.</summary>
    public void Tick()
    {
        DiscoverNewWindows();
        RemoveStale();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _canvas.Committed -= OnCommitted;
        _canvas.CameraChanged -= OnCameraChanged;
        _canvas.CollapseChanged -= OnReprojectWindowEvent;
        _canvas.MaximizeChanged -= OnReprojectWindowEvent;
        _input.WindowMinimized -= OnWindowMinimizedEvent;
        _input.WindowRestored -= OnWindowRestoredEvent;
        _input.WindowDestroyed -= OnWindowDestroyedEvent;
        _input.WindowShown -= OnWindowShownEvent;
        _input.WindowMoved -= OnWindowMovedEvent;
        _input.WindowFocused -= OnWindowFocusedEvent;
        _input.AltTabStarted -= OnAltTabStarted;
        _input.AltTabEnded -= OnAltTabEnded;
        _projection?.Dispose();
    }

    public void TrackForDesktopRestore(IntPtr hWnd)
    {
        uint pid = _win32.GetWindowProcessId(hWnd);
        if (pid != 0 && pid != (uint)Environment.ProcessId)
            _sessionWindows[hWnd] = pid;
    }

    /// <summary>
    /// Final shutdown only. Save the workspace and remove cards before calling.
    /// Native recovery must never be reconciled back into the saved canvas layout.
    /// </summary>
    public void RestoreDesktopViewport()
    {
        if (_disposed) return;
        foreach (var hWnd in _canvas.Windows.Keys)
            if (!_sessionWindows.ContainsKey(hWnd)) TrackForDesktopRestore(hWnd);
        _projection?.ClearPending();
        Dispose(); // No queued camera/input events may schedule a later move.
        var screens = _win32.GetScreenWorkingAreas();
        var failures = new List<Exception>();
        foreach (var (hWnd, pid) in _sessionWindows)
        {
            try
            {
                if (!_win32.IsWindowVisible(hWnd) || _win32.GetWindowProcessId(hWnd) != pid) continue;
                if (_clippedWindows.Contains(hWnd)) RequireAccepted(_win32.UnclipWindow(hWnd));
                var (x, y, w, h) = _win32.GetNormalRect(hWnd);
                if (w <= 0 || h <= 0) continue;
                var before = new WindowRect(x, y, w, h);
                var rect = WindowPlacementPlanner.FitDesktop(before, screens);
                if (rect == before) continue;
                int style = _win32.GetWindowStyle(hWnd);
                var state = (style & (int)WINDOW_STYLE.WS_MINIMIZE) != 0 ? WindowState.Minimized
                    : (style & (int)WINDOW_STYLE.WS_MAXIMIZE) != 0 ? WindowState.Maximized : WindowState.Normal;
                if (state == WindowState.Normal)
                    RequireAccepted(_win32.SetWindowPosition(hWnd, rect.X, rect.Y, rect.W, rect.H,
                        (uint)(SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE)));
                else
                    RequireAccepted(_win32.RestoreNativeState(hWnd, rect, state));
            }
            catch (Exception ex) { failures.Add(new InvalidOperationException($"Could not recover window {hWnd}.", ex)); }
        }
        _sessionWindows.Clear();
        _clippedWindows.Clear();
        _canvas.ClearWindows();
        _lastScreen.Clear();
        _unconfirmedMoves.Clear();
        if (failures.Count > 0) throw new AggregateException("Some desktop windows could not be recovered.", failures);
    }

    private void OnCommitted()
    {
        // Sync — pan-end / overview-close commits need windows at their final
        // positions before the next paint, otherwise users see a one-frame
        // jitter as the worker batch lands after the form repaints. Cost is a
        // UI-thread block on cross-process SetWindowPos SendMessage round-trips,
        // tracked in Tracing/pan-hitch-findings.md as a known cost we accept
        // here over the visual artifact.
        ReprojectSync();
    }

    private void OnCameraChanged()
    {
        if (SuspendProjection) return;

        // Overview renders its own camera + thumbnails, so real windows don't
        // need to track every frame — but clicks pass through the overlay
        // (WS_EX_TRANSPARENT) and hit whichever real window is under the
        // cursor, so we keep HWND positions roughly in sync for WindowFromPoint.
        // Throttled; final reproject on overview close comes via OnCommitted.
        long now = _clock.TickCount64;
        if (now - _lastReprojectTick > ReprojectThrottleMs)
        {
            Reproject(isAsync: true, isTransient: true);
            _lastReprojectTick = now;
        }
    }

    private void OnReprojectWindowEvent(IntPtr hWnd)
    {
        ReprojectWindow(hWnd);
    }

    private void OnWindowMinimizedEvent(IntPtr hWnd)
    {
        if (_canvas.HasWindow(hWnd))
            _canvas.CollapseWindow(hWnd);
    }

    private void OnWindowRestoredEvent(IntPtr hWnd)
    {
        if (_canvas.HasWindow(hWnd))
            _canvas.ExpandWindow(hWnd);
        ReprojectWindow(hWnd);
    }

    private void OnWindowDestroyedEvent(IntPtr hWnd)
    {
        RemoveWindow(hWnd);
    }

    private void OnWindowShownEvent(IntPtr hWnd)
    {
        TryRegisterWindow(hWnd);
    }

    private void OnWindowMovedEvent(IntPtr hWnd)
    {
        if (_canvas.HasWindow(hWnd))
            ReconcileWindow(hWnd);
    }

    private void OnWindowFocusedEvent(IntPtr hWnd)
    {
        _canvas.BringToForeground(hWnd);
    }

    private void OnAltTabStarted()
    {
        SuspendGreedyDraw = true;
        UnclipAll();
    }

    private void OnAltTabEnded()
    {
        SuspendGreedyDraw = false;
        ReclipAll();
    }

    /// <summary>
    /// Project all canvas windows to screen. Call after Pan.
    /// </summary>
    public void Reproject(bool isAsync = false, bool isTransient = false)
    {
        var batch = BuildReprojectBatch();

        if (_projection != null)
            _projection.Schedule(batch, isAsync: isAsync, isTransient: isTransient);
        else
            ApplyBatchResult(batch, _win32.BatchMove(batch, isAsync: isAsync, isTransient: isTransient));
    }

    /// <summary>
    /// Like <see cref="Reproject"/>, but bypasses the <see cref="ProjectionWorker"/>
    /// and applies the batch synchronously on the calling thread. Use this when
    /// the caller depends on the windows being at their final positions before
    /// the next visible frame (e.g. just before the overview overlay hides).
    /// Cancels any in-flight worker batch so the sync run doesn't have to wait
    /// for it.
    /// </summary>
    public void ReprojectSync(bool isAsync = false, bool isTransient = false)
    {
        _projection?.ClearPending();
        var batch = BuildReprojectBatch();
        ApplyBatchResult(batch, _win32.BatchMove(batch, isAsync: isAsync, isTransient: isTransient));
    }

    /// <summary>Re-evaluate clipping and edge parking against a fresh monitor layout.</summary>
    public void ReprojectForDisplayChange()
    {
        if (_disposed) return;
        _projection?.ClearPending();
        var batch = BuildReprojectBatch(repositionClipped: true);
        ApplyBatchResult(batch, _win32.BatchMove(batch, isAsync: false, isTransient: false));
    }

    private bool ClipOffScreen => !_config.DisableGreedyDraw && !SuspendGreedyDraw;

    private List<BatchMoveItem> BuildReprojectBatch(bool repositionClipped = false)
    {
        DrainProjectionResults();
        var batch = new List<BatchMoveItem>();
        // One geometry/camera/config snapshot for the entire decision pass. Do not
        // rebuild monitor arrays for every window or consult a moving camera midway.
        var screens = _win32.GetScreenWorkingAreas();
        var camera = _canvas.Camera;
        bool clipOffScreen = ClipOffScreen;

        foreach (var (hWnd, world) in _canvas.Windows)
        {
            var plan = WindowPlacementPlanner.Plan(world, camera, screens, clipOffScreen,
                _clippedWindows.Contains(hWnd), repositionClipped: repositionClipped || _unconfirmedMoves.Contains(hWnd));
            if (!ApplyRegionChange(hWnd, plan.RegionChange)) continue;
            if (!plan.ShouldMove) continue;
            var r = plan.TargetRect;
            batch.Add(new BatchMoveItem(hWnd, r, PosOnly: true));
            _lastScreen[hWnd] = (r.X, r.Y, r.W, r.H);
            _unconfirmedMoves.Add(hWnd);
        }

        return batch;
    }

    /// <summary>
    /// Project a single window (e.g., after restore from minimized).
    /// Returns true if the window was reprojected, false if skipped.
    /// </summary>
    public bool ReprojectWindow(IntPtr hWnd)
    {
        DrainProjectionResults();
        uint ownPid = (uint)Environment.ProcessId;
        if (!_win32.IsManageable(hWnd, ownPid))
            return false;

        if (!_canvas.HasWindow(hWnd))
            RegisterWindow(hWnd);

        if (!_canvas.Windows.TryGetValue(hWnd, out var world))
            return false;

        var plan = WindowPlacementPlanner.Plan(world, _canvas.Camera, _win32.GetScreenWorkingAreas(),
            ClipOffScreen, _clippedWindows.Contains(hWnd), repositionClipped: _unconfirmedMoves.Contains(hWnd));
        if (!ApplyRegionChange(hWnd, plan.RegionChange)) return false;
        if (!plan.ShouldMove) return true;
        var r = plan.TargetRect;

        var result = _win32.SetWindowPosition(hWnd, r.X, r.Y, r.W, r.H,
            (uint)(SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE));
        _lastScreen[hWnd] = (r.X, r.Y, r.W, r.H);
        ApplyMoveResult(new(hWnd, r, false), result);
        return result.Accepted;
    }

    /// <summary>
    /// Detect windows the user manually moved/resized and update the canvas.
    /// </summary>
    public void Reconcile()
    {
        if (_suspendReconcile)
        {
            _reconcilePending = true;
            return;
        }
        foreach (var (hWnd, _) in _canvas.Windows)
            ReconcileWindow(hWnd);
    }

    /// <summary>Update a single window's world position from its actual screen position.</summary>
    public void ReconcileWindow(IntPtr hWnd)
    {
        DrainProjectionResults();
        if (_suspendReconcile)
        {
            _reconcilePending = true;
            return;
        }
        if (!IsWindowActive(hWnd))
            return;

        int style = _win32.GetWindowStyle(hWnd);
        bool isMaximized = (style & (int)WINDOW_STYLE.WS_MAXIMIZE) != 0;

        // Keep canvas's maximize state in sync with Win32
        if (_canvas.HasWindow(hWnd))
        {
            if (isMaximized && !_canvas.IsMaximized(hWnd))
                _canvas.MaximizeWindow(hWnd);
            else if (!isMaximized && _canvas.IsMaximized(hWnd))
                _canvas.UnmaximizeWindow(hWnd);
        }

        // Skip reprojecting maximized windows — their full-screen rect isn't a meaningful canvas position
        if (isMaximized)
            return;

        if (!_lastScreen.TryGetValue(hWnd, out var last))
            return;

        var (ax, ay, aw, ah) = _win32.GetWindowRect(hWnd);

        if (Math.Abs(ax - last.x) <= ReconcileTolerancePx && Math.Abs(ay - last.y) <= ReconcileTolerancePx &&
            Math.Abs(aw - last.w) <= ReconcileTolerancePx && Math.Abs(ah - last.h) <= ReconcileTolerancePx)
            return;

        // Don't reconcile clipped windows — they're hidden and we don't
        // care where the app thinks they are
        if (_clippedWindows.Contains(hWnd))
            return;

        _canvas.SetWindowFromScreen(hWnd, ax, ay, aw, ah);
        _lastScreen[hWnd] = (ax, ay, aw, ah);
    }

    /// <summary>Remove windows from canvas that no longer exist.</summary>
    public void RemoveStale()
    {
        var stale = new List<IntPtr>();
        foreach (var hWnd in _canvas.Windows.Keys)
        {
            if (!_win32.IsWindowVisible(hWnd))
                stale.Add(hWnd);
        }
        foreach (var hWnd in stale)
            RemoveWindow(hWnd);
    }

    /// <summary>Drop a single window from canvas and internal tracking.</summary>
    public void RemoveWindow(IntPtr hWnd)
    {
        if (_canvas.HasWindow(hWnd)) WindowRemoving?.Invoke(hWnd);
        _canvas.RemoveWindow(hWnd);
        _lastScreen.Remove(hWnd);
        _unconfirmedMoves.Remove(hWnd);
        _clippedWindows.Remove(hWnd);
        _sessionWindows.Remove(hWnd);
    }

    /// <summary>Restore regions on all clipped windows (for overview thumbnails).</summary>
    public void UnclipAll()
    {
        foreach (var hWnd in _clippedWindows)
            _win32.UnclipWindow(hWnd);
    }

    /// <summary>Re-clip windows that should be off-screen.</summary>
    public void ReclipAll()
    {
        foreach (var hWnd in _clippedWindows)
            _win32.ClipWindow(hWnd);
    }

    /// <summary>
    /// Recovery action for the tray "Refresh" menu: enumerate every visible
    /// top-level window (not just canvas-tracked ones — third-party tools like
    /// Aero Snap or screen recorders can leave stray clip regions on windows
    /// we never registered) and clear any window region + force a full repaint.
    /// </summary>
    public unsafe void RefreshAllWindows()
    {
        _clippedWindows.Clear();
        _win32.EnumWindows(hWnd =>
        {
            if (_win32.IsWindowVisible(hWnd))
            {
                _win32.UnclipWindow(hWnd);
                PInvoke.RedrawWindow((HWND)hWnd, null, (HRGN)IntPtr.Zero,
                    REDRAW_WINDOW_FLAGS.RDW_INVALIDATE | REDRAW_WINDOW_FLAGS.RDW_ERASE |
                    REDRAW_WINDOW_FLAGS.RDW_FRAME | REDRAW_WINDOW_FLAGS.RDW_ALLCHILDREN);
            }
            return true;
        });
    }

    /// <summary>Register a new window into the canvas from its screen position.</summary>
    public void RegisterWindow(IntPtr hWnd)
    {
        uint ownPid = (uint)Environment.ProcessId;
        if (!_win32.IsManageable(hWnd, ownPid))
            return;

        var (sx, sy, sw, sh) = _win32.GetWindowRect(hWnd);
        TrackForDesktopRestore(hWnd);
        _canvas.SetWindowFromScreen(hWnd, sx, sy, sw, sh);
        _lastScreen[hWnd] = (sx, sy, sw, sh);
    }

    public bool SetWindowPinnedToScreen(IntPtr hWnd, bool pinned)
    {
        uint ownPid = (uint)Environment.ProcessId;
        if (!_win32.IsManageable(hWnd, ownPid, allowMinimized: true))
            return _canvas.IsPinnedToScreen(hWnd);

        if (!_canvas.HasWindow(hWnd))
            RegisterWindow(hWnd);

        if (!_canvas.Windows.TryGetValue(hWnd, out var world))
            return false;

        if (pinned)
            PinWindowToScreen(hWnd, world);
        else
            UnpinWindowFromScreen(hWnd);

        return _canvas.IsPinnedToScreen(hWnd);
    }

    public bool ToggleWindowPinnedToScreen(IntPtr hWnd)
    {
        return SetWindowPinnedToScreen(hWnd, !_canvas.IsPinnedToScreen(hWnd));
    }

    /// <summary>
    /// Register a single HWND if it passes the full "new window" filter chain
    /// (not already tracked, manageable, on current virtual desktop).
    /// Event-driven counterpart to DiscoverNewWindows.
    /// </summary>
    public void TryRegisterWindow(IntPtr hWnd)
    {
        if (_canvas.HasWindow(hWnd)) return;
        if (_vds != null && !_vds.IsOnCurrentDesktop(hWnd)) return;
        RegisterWindow(hWnd);
    }

    /// <summary>Reset: restore all windows to world positions, clear canvas.</summary>
    public void Reset()
    {
        // Drop any in-flight worker batch so it can't stomp on the sync reset below.
        _projection?.ClearPending();

        foreach (var hWnd in _clippedWindows)
            _win32.UnclipWindow(hWnd);
        _clippedWindows.Clear();

        _canvas.ResetCamera();

        var batch = new List<BatchMoveItem>();

        foreach (var (hWnd, world) in _canvas.Windows)
        {
            TrackForDesktopRestore(hWnd);
            if (!IsWindowActive(hWnd))
                continue;
            if (world.State != WindowState.Normal)
                continue;
            if (world.PinnedToScreen)
                continue;

            var rect = new WindowRect((int)world.X, (int)world.Y, (int)world.W, (int)world.H);
            batch.Add(new BatchMoveItem(hWnd, rect, PosOnly: false));
        }

        _win32.BatchMove(batch, isAsync: false, isTransient: false);
        _canvas.ClearWindows();
        _lastScreen.Clear();
        _unconfirmedMoves.Clear();
    }

    // ==================== PRIVATE ====================

    public void DiscoverNewWindows()
    {
        uint ownPid = (uint)Environment.ProcessId;
        var toAdd = new List<IntPtr>();

        _win32.EnumWindows(hWnd =>
        {
            if (_canvas.HasWindow(hWnd)) return true;
            if (!_win32.IsManageable(hWnd, ownPid)) return true;
            if (_vds != null && !_vds.IsOnCurrentDesktop(hWnd)) return true;
            toAdd.Add(hWnd);
            return true;
        });

        foreach (var hWnd in toAdd)
            RegisterWindow(hWnd);
    }

    private bool IsWindowActive(IntPtr hWnd)
    {
        if (!_win32.IsWindowVisible(hWnd))
            return false;
        int style = _win32.GetWindowStyle(hWnd);
        return (style & (int)WINDOW_STYLE.WS_MINIMIZE) == 0;
    }

    private void PinWindowToScreen(IntPtr hWnd, WorldRect world)
    {
        var rect = ResolvePinnedScreenRect(hWnd, world);

        if (_clippedWindows.Contains(hWnd) && !ApplyRegionChange(hWnd, WindowRegionChange.Unclip)) return;

        _canvas.SetPinnedToScreen(hWnd, true);
        _lastScreen[hWnd] = (rect.X, rect.Y, rect.W, rect.H);
        var result = _win32.SetWindowPosition(hWnd, rect.X, rect.Y, rect.W, rect.H,
            (uint)(SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE));
        ApplyMoveResult(new(hWnd, rect, false), result);
    }

    private void UnpinWindowFromScreen(IntPtr hWnd)
    {
        var (x, y, w, h) = _win32.GetWindowRect(hWnd);
        _canvas.SetWindowFromScreen(hWnd, x, y, w, h);
        _canvas.SetPinnedToScreen(hWnd, false);
        _lastScreen[hWnd] = (x, y, w, h);
    }

    private WindowRect ResolvePinnedScreenRect(IntPtr hWnd, WorldRect world)
    {
        var (ax, ay, aw, ah) = _win32.GetWindowRect(hWnd);
        return WindowPlacementPlanner.ResolvePinnedPlacement(world, _canvas.Camera,
            new WindowRect(ax, ay, aw, ah), _clippedWindows.Contains(hWnd), _win32.GetScreenWorkingAreas());
    }

    private void DrainProjectionResults()
    {
        if (_projection == null) return;
        while (_projection.TryTakeCompleted(out var completed))
            ApplyBatchResult(completed.Items, completed.Result);
    }

    private void ApplyBatchResult(List<BatchMoveItem> items, WindowBatchResult result)
    {
        for (int i = 0; i < items.Count; i++) ApplyMoveResult(items[i], result.Items[i]);
    }

    private void ApplyMoveResult(BatchMoveItem item, NativeOperationResult result)
    {
        var rect = item.Rect;
        // A worker completion must not overwrite a newer requested placement.
        if (!_lastScreen.TryGetValue(item.HWnd, out var latest) || latest != (rect.X, rect.Y, rect.W, rect.H)) return;
        if (result.Accepted) _unconfirmedMoves.Remove(item.HWnd);
        else
        {
            _lastScreen.Remove(item.HWnd);
            _unconfirmedMoves.Add(item.HWnd);
        }
    }

    private bool ApplyRegionChange(IntPtr hWnd, WindowRegionChange change)
    {
        if (change == WindowRegionChange.Clip)
        {
            if (!_win32.ClipWindow(hWnd).Accepted) return false;
            _clippedWindows.Add(hWnd);
        }
        else if (change == WindowRegionChange.Unclip)
        {
            if (!_win32.UnclipWindow(hWnd).Accepted) return false;
            _clippedWindows.Remove(hWnd);
        }
        return true;
    }

    private static void RequireAccepted(NativeOperationResult result)
    {
        if (!result.Accepted)
            throw new InvalidOperationException($"{result.Operation} failed for window {result.Window} (native error {result.ErrorCode?.ToString() ?? "unspecified"}).");
    }
}
