using System;
using System.Collections.Generic;

namespace CanvasDesktop;

/// <summary>
/// Consumes Canvas state and applies it to real windows.
/// Handles enumeration, positioning, reconciliation.
/// </summary>
internal sealed partial class WindowManager : IDisposable
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
    private readonly Func<uint, WorkspaceProcessIdentity?> _inspectProcess;
    private readonly WindowVisibilityTracker _visibility = new();
    private CanvasCamera? _overviewViewport;
    private IReadOnlyList<(int x, int y, int w, int h)>? _overviewScreens;
    internal const double OverviewLivePreviewZoom = OverviewZoomDefaults.CardEnter;
    internal const double OverviewCardEnterZoom = OverviewZoomDefaults.CardEnter, OverviewCardExitZoom = OverviewZoomDefaults.CardExit;
    internal const double OverviewSimpleCardEnterZoom = OverviewZoomDefaults.SimpleCardEnter, OverviewSimpleCardExitZoom = OverviewZoomDefaults.SimpleCardExit;
    private bool _overviewUsesInfoCards;
    public bool OverviewUsesInfoCards => _overviewUsesInfoCards;
    private bool _overviewUsesSimpleCards;
    public bool OverviewUsesSimpleCards => _overviewUsesSimpleCards;

    /// <summary>Resource policy follows the viewed camera; native window sizes still follow the main canvas.</summary>
    public void SetOverviewViewport(CanvasCamera? camera,
        IReadOnlyList<(int x, int y, int w, int h)>? screens = null)
    {
        if (_disposed) return;
        if (camera is { } c && (!double.IsFinite(c.X) || !double.IsFinite(c.Y)
            || !double.IsFinite(c.Zoom) || c.Zoom <= 0)) return;

        if (camera is { } next)
        {
            double z = next.Zoom;
            bool enableLive = _config.EnableLivePreviewsTier;
            bool enableDetailed = _config.EnableDetailedCardsTier;
            bool enableSimple = _config.EnableSimpleCardsTier;

            double cardEnter = _config.OverviewCardEnterZoom;
            if (cardEnter <= 0) cardEnter = OverviewCardEnterZoom;
            double cardExit = _config.OverviewCardExitZoom;
            if (cardExit <= cardEnter) cardExit = cardEnter * 1.15;

            double simpleEnter = _config.OverviewSimpleCardEnterZoom;
            if (simpleEnter <= 0) simpleEnter = OverviewSimpleCardEnterZoom;
            double simpleExit = _config.OverviewSimpleCardExitZoom;
            if (simpleExit <= simpleEnter) simpleExit = simpleEnter * 1.30;

            if (!enableLive && !enableDetailed && !enableSimple)
            {
                enableLive = true;
            }

            if (!enableDetailed && !enableSimple)
            {
                _overviewUsesInfoCards = false;
                _overviewUsesSimpleCards = false;
            }
            else if (!enableLive && !enableSimple)
            {
                _overviewUsesInfoCards = true;
                _overviewUsesSimpleCards = false;
            }
            else if (!enableLive && !enableDetailed)
            {
                _overviewUsesInfoCards = true;
                _overviewUsesSimpleCards = true;
            }
            else if (!enableDetailed)
            {
                bool isSimple = _overviewViewport.HasValue
                    ? (_overviewUsesSimpleCards ? z < simpleExit : z < simpleEnter)
                    : (z < simpleEnter);
                _overviewUsesInfoCards = isSimple;
                _overviewUsesSimpleCards = isSimple;
            }
            else if (!enableSimple)
            {
                bool isDetailed = _overviewViewport.HasValue
                    ? (_overviewUsesInfoCards ? z < cardExit : z < cardEnter)
                    : (z < cardEnter);
                _overviewUsesInfoCards = isDetailed;
                _overviewUsesSimpleCards = false;
            }
            else if (!enableLive)
            {
                bool isSimple = _overviewViewport.HasValue
                    ? (_overviewUsesSimpleCards ? z < simpleExit : z < simpleEnter)
                    : (z < simpleEnter);
                _overviewUsesInfoCards = true;
                _overviewUsesSimpleCards = isSimple;
            }
            else
            {
                bool isSimple = _overviewViewport.HasValue
                    ? (_overviewUsesSimpleCards ? z < simpleExit : z < simpleEnter)
                    : (z < simpleEnter);
                bool isInfo = isSimple || (_overviewViewport.HasValue
                    ? (_overviewUsesInfoCards ? z < cardExit : z < cardEnter)
                    : (z < cardEnter));
                _overviewUsesInfoCards = isInfo;
                _overviewUsesSimpleCards = isSimple;
            }
        }
        else
        {
            _overviewUsesInfoCards = false;
            _overviewUsesSimpleCards = false;
        }

        _overviewViewport = camera;
        _overviewScreens = camera.HasValue ? screens : null;
        UpdateRuntimeVisibility(refreshIdentity: false);
        ProcessAutomaticParking(cameraOnly: true);
    }
    public WindowPreviewCache Previews { get; }
    public WindowVisibilitySnapshot? GetRuntimeVisibility(IntPtr hWnd) => _visibility.Get(hWnd);
    public bool UsesCachedPreview(IntPtr hWnd) => _parking.ContainsKey(hWnd) || _visibility.Get(hWnd)?.AutomaticallyParked == true;
    public IntPtr GetCachedPreviewSource(IntPtr hWnd)
        => _visibility.GetToken(hWnd) is { } key ? Previews.GetSource(key) : IntPtr.Zero;

    /// <summary>Request a fresh snapshot; native parking waits for a successful capture.</summary>
    public bool CapturePreviewBeforeParking(IntPtr hWnd)
    {
        if (_disposed || !_canvas.HasWindow(hWnd) || !_win32.IsWindowVisible(hWnd)) return false;
        ObserveRuntimeWindow(hWnd);
        if (_visibility.Get(hWnd)?.CanPark != true || _visibility.GetToken(hWnd) is not { } key) return false;
        var rect = _win32.GetWindowRect(hWnd);
        Previews.InvalidateImage(hWnd);
        return Previews.RequestCapture(key, new System.Drawing.Size(rect.w, rect.h));
    }
    public bool HasCachedPreview(IntPtr hWnd)
        => _visibility.GetToken(hWnd) is { } key && Previews.HasImage(key);
    public void RefreshPreviewState()
    {
        if (_disposed) return;
        UpdateRuntimeVisibility();
        Previews.DrainCompleted();
    }
    private bool _disposed;

    // Retain identities across virtual-desktop resets; never move a recycled HWND
    // belonging to a different process during shutdown.
    private readonly Dictionary<IntPtr, uint> _sessionWindows = new();

    // Last observed or acknowledged placement, retained when a later request fails.
    private readonly Dictionary<IntPtr, (int x, int y, int w, int h)> _lastScreen = new();
    private readonly Dictionary<IntPtr, PendingMove> _pendingMoves = new();
    private long _nextMoveId;
    private readonly record struct PendingMove(BatchMoveItem Item, WindowRect Expected,
        List<WindowRect>? Superseded);

    // Windows with clipped (empty) region to prevent them from fighting off-screen
    private readonly HashSet<IntPtr> _clippedWindows = new();
    private readonly HashSet<IntPtr> _livePreviewWindows = new();
    private readonly HashSet<IntPtr> _previewUnclipped = new();
    private bool _allPreviewsVisible;
    // Clipping can succeed before a move is failed, cancelled or superseded.
    // Keep retrying edge placement until a native move has been accepted.
    private readonly HashSet<IntPtr> _unconfirmedMoves = new();

    private long _lastReprojectTick;
    private long _lastScreenTopologyVersion = -1;

    public event Action<IntPtr>? WindowRemoving;
    public event Action<IntPtr>? WindowStateChanged;

    // Our own placeholder forms are explicitly enrolled, never discovered as apps.
    public void RestorePlacement(IntPtr hWnd, WorldRect world)
    {
        // A queued camera projection may still contain the window's pre-match
        // desktop geometry. Finish/cancel it before applying the saved slot so
        // it cannot land later and be mistaken for a user move.
        _projection?.ClearPending();
        DrainProjectionResults();
        TrackForDesktopRestore(hWnd);
        _canvas.RestoreWindow(hWnd, world);
        if (_parking.TryGetValue(hWnd, out var parked))
        {
            if (world.State == WindowState.Normal && !RestoreAutomaticWindow(hWnd, parked)) return;
            ForgetParking(hWnd);
        }
        ObserveRuntimeWindow(hWnd);
        var plan = WindowPlacementPlanner.Plan(world, _canvas.Camera, _win32.GetScreenWorkingAreas(),
            ClipOffScreen, _clippedWindows.Contains(hWnd), PlacementIntent.RestoreLayout, repositionClipped: true);
        if (!ApplyRegionChange(hWnd, plan.RegionChange)) return;
        if (!plan.ShouldMove) return;
        var rect = plan.TargetRect;
        var move = TrackMove(hWnd, rect, posOnly: false);
        var result = _win32.SetWindowPosition(hWnd, rect.X, rect.Y, rect.W, rect.H,
            (uint)(SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE));
        ApplyMoveResult(move, result);
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
        bool useAsyncProjection = false,
        Func<uint, WorkspaceProcessIdentity?>? inspectProcess = null,
        WindowPreviewCache? previews = null)
    {
        _canvas = canvas;
        _win32 = win32;
        _config = config;
        _clock = clock ?? SystemClock.Instance;
        _vds = vds;
        _input = input;
        _inspectProcess = inspectProcess ?? new WorkspaceIdentity().InspectProcess;
        Previews = previews ?? new WindowPreviewCache();
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
        long topology = _win32.ScreenTopologyVersion;
        if (topology >= 0 && topology != _lastScreenTopologyVersion)
            ReprojectForDisplayChange();
        DiscoverNewWindows();
        RemoveStale();
        UpdateRuntimeVisibility();
        Previews.DrainCompleted();
        ProcessAutomaticParking();
    }

    public void Dispose()
    {
        if (_disposed) return;
        var failures = RecoverAutomaticParking();
        DisposeCore();
        if (failures.Count > 0) throw new AggregateException("Automatic parking recovery failed.", failures);
    }

    private void DisposeCore()
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
        _visibility.Clear();
        Previews.Dispose();
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
        var failures = RecoverAutomaticParking();
        DisposeCore(); // No queued camera/input events may schedule a later move.
        var screens = _win32.GetScreenWorkingAreas();
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
        _pendingMoves.Clear();
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
        UpdateRuntimeVisibility(refreshIdentity: false);
        ProcessAutomaticParking(cameraOnly: true);
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
        if (_parking.ContainsKey(hWnd) && _canvas.Windows.TryGetValue(hWnd, out var world)
            && world.State == WindowState.Minimized)
            ForgetParking(hWnd); // An explicit model collapse is user intent.
        ReprojectWindow(hWnd);
    }

    private void OnWindowMinimizedEvent(IntPtr hWnd)
    {
        if (_parking.TryGetValue(hWnd, out var parked) && IsParkingIdentityCurrent(parked.Token))
        {
            if (parked.Phase != ParkingPhase.Restoring || _applyingParking
                || (_win32.GetWindowStyle(hWnd) & (int)WINDOW_STYLE.WS_MINIMIZE) == 0) return;
            ForgetParking(hWnd); // A new minimize after our restore is user intent.
        }
        ObserveRuntimeWindow(hWnd, minimized: true);
        if (_visibility.Get(hWnd)?.AutomaticallyParked == true) return;
        if (_canvas.HasWindow(hWnd))
            _canvas.CollapseWindow(hWnd);
        WindowStateChanged?.Invoke(hWnd);
    }

    private void OnWindowRestoredEvent(IntPtr hWnd)
    {
        if (_parking.TryGetValue(hWnd, out var parked))
        {
            if (parked.Phase == ParkingPhase.Restoring || parked.Phase == ParkingPhase.Minimizing) return;
            if ((_win32.GetWindowStyle(hWnd) & (int)WINDOW_STYLE.WS_MINIMIZE) != 0) return;
            HandleManualParkingRestore(hWnd);
        }
        ObserveRuntimeWindow(hWnd, minimized: false);
        if (_canvas.HasWindow(hWnd))
            _canvas.ExpandWindow(hWnd);
        ReprojectWindow(hWnd);
        WindowStateChanged?.Invoke(hWnd);
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
        if (_parking.TryGetValue(hWnd, out var parked) && parked.Phase == ParkingPhase.Parked
            && (_win32.GetWindowStyle(hWnd) & (int)WINDOW_STYLE.WS_MINIMIZE) == 0)
            HandleManualParkingRestore(hWnd);
        if (_parkingExcluded.TryGetValue(hWnd, out var excluded) && _visibility.GetToken(hWnd) == excluded)
            CenterManuallyRestoredWindow(hWnd);
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
        long topology = _win32.ScreenTopologyVersion;
        var screens = _win32.GetScreenWorkingAreas();
        var camera = _canvas.Camera;
        repositionClipped |= topology >= 0 && topology != _lastScreenTopologyVersion;
        _lastScreenTopologyVersion = topology;
        bool clipOffScreen = ClipOffScreen;
        UpdateRuntimeVisibility(screens, refreshIdentity: false);
        ProcessAutomaticParking(cameraOnly: true);

        foreach (var (hWnd, world) in _canvas.Windows)
        {
            if (_parking.ContainsKey(hWnd)) continue;
            var plan = WindowPlacementPlanner.Plan(world, camera, screens, clipOffScreen,
                _clippedWindows.Contains(hWnd), repositionClipped: repositionClipped || _unconfirmedMoves.Contains(hWnd));
            if (!ApplyRegionChange(hWnd, plan.RegionChange)) continue;
            if (!plan.ShouldMove) continue;
            var r = plan.TargetRect;
            if (CanSkipMove(hWnd, r, posOnly: true)) continue;
            batch.Add(TrackMove(hWnd, r, posOnly: true));
        }

        return batch;
    }

    /// <summary>
    /// Project a single window (e.g., after restore from minimized).
    /// Returns true if the window was reprojected, false if skipped.
    /// </summary>
    public bool ReprojectWindow(IntPtr hWnd)
    {
        if (_parking.ContainsKey(hWnd)) return false;
        DrainProjectionResults();
        uint ownPid = (uint)Environment.ProcessId;
        if (!_win32.IsManageable(hWnd, ownPid))
            return false;

        if (!_canvas.HasWindow(hWnd))
            RegisterWindow(hWnd);

        if (!_canvas.Windows.TryGetValue(hWnd, out var world))
            return false;

        ObserveRuntimeWindow(hWnd);
        var plan = WindowPlacementPlanner.Plan(world, _canvas.Camera, _win32.GetScreenWorkingAreas(),
            ClipOffScreen, _clippedWindows.Contains(hWnd), repositionClipped: _unconfirmedMoves.Contains(hWnd));
        if (!ApplyRegionChange(hWnd, plan.RegionChange)) return false;
        if (!plan.ShouldMove) return true;
        var r = plan.TargetRect;
        if (CanSkipMove(hWnd, r, posOnly: false)) return true;

        var move = TrackMove(hWnd, r, posOnly: false);
        var result = _win32.SetWindowPosition(hWnd, r.X, r.Y, r.W, r.H,
            (uint)(SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE));
        ApplyMoveResult(move, result);
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
        if (_parking.ContainsKey(hWnd)) return;
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

        if (_pendingMoves.TryGetValue(hWnd, out var pending))
        {
            var expected = pending.Expected;
            // Either the requested move has landed, or the old placement is still
            // visible while the worker is processing it. Neither is a user drag.
            if (Math.Abs(ax - expected.X) <= ReconcileTolerancePx && Math.Abs(ay - expected.Y) <= ReconcileTolerancePx &&
                Math.Abs(aw - expected.W) <= ReconcileTolerancePx && Math.Abs(ah - expected.H) <= ReconcileTolerancePx)
            {
                _lastScreen[hWnd] = (ax, ay, aw, ah);
                _pendingMoves.Remove(hWnd);
                _unconfirmedMoves.Remove(hWnd);
                return;
            }
            // During a pan, earlier queued requests can land before the newest
            // one. They are also our placements, never evidence of a user drag.
            if (pending.Superseded != null)
                foreach (var earlier in pending.Superseded)
                    if (Math.Abs(ax - earlier.X) <= ReconcileTolerancePx && Math.Abs(ay - earlier.Y) <= ReconcileTolerancePx &&
                        Math.Abs(aw - earlier.W) <= ReconcileTolerancePx && Math.Abs(ah - earlier.H) <= ReconcileTolerancePx)
                        return;
        }

        if (Math.Abs(ax - last.x) <= ReconcileTolerancePx && Math.Abs(ay - last.y) <= ReconcileTolerancePx &&
            Math.Abs(aw - last.w) <= ReconcileTolerancePx && Math.Abs(ah - last.h) <= ReconcileTolerancePx)
            return;

        // Don't reconcile clipped windows — they're hidden and we don't
        // care where the app thinks they are
        if (_clippedWindows.Contains(hWnd))
            return;

        _canvas.SetWindowFromScreen(hWnd, ax, ay, aw, ah);
        _lastScreen[hWnd] = (ax, ay, aw, ah);
        _pendingMoves.Remove(hWnd); // Ignore a stale completion after an observed manual change.
    }

    /// <summary>Remove windows from canvas that no longer exist.</summary>
    public void RemoveStale()
    {
        List<IntPtr>? stale = null;
        foreach (var hWnd in _canvas.Windows.Keys)
        {
            if (!_win32.IsWindowVisible(hWnd))
                (stale ??= new()).Add(hWnd);
        }
        if (stale == null) return;
        foreach (var hWnd in stale)
            RemoveWindow(hWnd);
    }

    /// <summary>Drop a single window from canvas and internal tracking.</summary>
    public void RemoveWindow(IntPtr hWnd)
    {
        ForgetParking(hWnd);
        _parkingExcluded.Remove(hWnd);
        _parkingRetry.Remove(hWnd);
        if (_canvas.HasWindow(hWnd)) WindowRemoving?.Invoke(hWnd);
        _canvas.RemoveWindow(hWnd);
        _lastScreen.Remove(hWnd);
        _pendingMoves.Remove(hWnd);
        _unconfirmedMoves.Remove(hWnd);
        _clippedWindows.Remove(hWnd);
        _livePreviewWindows.Remove(hWnd);
        _previewUnclipped.Remove(hWnd);
        _sessionWindows.Remove(hWnd);
        _visibility.Remove(hWnd);
        Previews.Remove(hWnd);
    }

    /// <summary>Restore regions on all clipped windows (for overview thumbnails).</summary>
    public void UnclipAll()
    {
        _allPreviewsVisible = true;
        ReconcilePreviewRegions();
    }

    /// <summary>Re-clip windows that should be off-screen.</summary>
    public void ReclipAll()
    {
        _allPreviewsVisible = false;
        ReconcilePreviewRegions();
    }

    /// <summary>Only visible live overview sources need their compatibility region removed.</summary>
    public void SetLivePreviewWindows(IEnumerable<IntPtr> handles)
    {
        _livePreviewWindows.Clear();
        foreach (var handle in handles)
            if (!UsesCachedPreview(handle)) _livePreviewWindows.Add(handle);
        ReconcilePreviewRegions();
    }

    private void ReconcilePreviewRegions()
    {
        _previewUnclipped.RemoveWhere(handle =>
        {
            if (!_clippedWindows.Contains(handle)) return true;
            if (_allPreviewsVisible || _livePreviewWindows.Contains(handle)) return false;
            return _win32.ClipWindow(handle).Accepted;
        });
        foreach (var handle in _clippedWindows)
            if ((_allPreviewsVisible || _livePreviewWindows.Contains(handle)) && !_previewUnclipped.Contains(handle)
                && _win32.UnclipWindow(handle).Accepted)
                _previewUnclipped.Add(handle);
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
        _previewUnclipped.Clear();
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
        ObserveRuntimeWindow(hWnd);
    }

    public bool SetWindowPinnedToScreen(IntPtr hWnd, bool pinned)
    {
        if (_parking.TryGetValue(hWnd, out var parked))
        {
            if (!RestoreAutomaticWindow(hWnd, parked)) return _canvas.IsPinnedToScreen(hWnd);
            ForgetParking(hWnd);
        }
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
        _overviewViewport = null;
        _overviewUsesInfoCards = false;
        _overviewUsesSimpleCards = false;
        _overviewScreens = null;
        RecoverAutomaticParking(); // Failed debts stay tracked for retry/shutdown.
        _parkingExcluded.Clear();
        _parkingRetry.Clear();
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
            if (_parking.ContainsKey(hWnd) || !IsWindowActive(hWnd))
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
        _pendingMoves.Clear();
        _unconfirmedMoves.Clear();
        _visibility.Clear();
        Previews.Clear();
        _previewUnclipped.Clear();
        _livePreviewWindows.Clear();
    }

    // ==================== PRIVATE ====================

    private void UpdateRuntimeVisibility(IReadOnlyList<(int x, int y, int w, int h)>? screens = null,
        bool refreshIdentity = true)
    {
        if (_disposed) return;
        screens ??= _win32.GetScreenWorkingAreas();
        // Inspect each process once per pass; never cache identity across process reuse.
        Dictionary<uint, WorkspaceProcessIdentity?>? processes = null;
        // Native/COM queries can dispatch window events on this same UI thread.
        // Those events may enroll or remove windows while this pass is running.
        // New windows are observed on the next pass; never enumerate the live map
        // across a native call or republish state for a window removed by one.
        foreach (var handle in new List<IntPtr>(_canvas.Windows.Keys))
        {
            if (_disposed) return;
            if (!_canvas.Windows.TryGetValue(handle, out var world)) continue;
            uint pid = _win32.GetWindowProcessId(handle);
            var snapshot = _visibility.Get(handle);
            WorkspaceWindowIdentity? identity = snapshot?.Identity;
            // Camera frames reuse tick-validated identity; process inspection is too
            // expensive for every animation frame. A PID change invalidates it now.
            if (refreshIdentity || snapshot == null || (identity.HasValue && identity.Value.ProcessId != pid))
            {
                processes ??= new();
                if (!processes.TryGetValue(pid, out var process))
                    processes[pid] = process = pid == 0 ? null : _inspectProcess(pid);
                identity = process is { } p && p.ProcessId == pid
                    ? new(handle, pid, p.StartedAtUtcTicks) : null;
            }
            ObserveRuntimeWindow(handle, world, screens, identity, null);
        }
    }

    private void ObserveRuntimeWindow(IntPtr handle, bool? minimized = null)
    {
        if (!_canvas.Windows.TryGetValue(handle, out var world)) return;
        uint pid = _win32.GetWindowProcessId(handle);
        var process = pid == 0 ? null : _inspectProcess(pid);
        WorkspaceWindowIdentity? identity = process is { } p && p.ProcessId == pid
            ? new(handle, pid, p.StartedAtUtcTicks) : null;
        ObserveRuntimeWindow(handle, world, _win32.GetScreenWorkingAreas(),
            identity, minimized);
    }

    private void ObserveRuntimeWindow(IntPtr handle, WorldRect world,
        IReadOnlyList<(int x, int y, int w, int h)> screens, WorkspaceWindowIdentity? identity, bool? minimized)
    {
        int style = _win32.GetWindowStyle(handle);
        bool eligible = world.State == WindowState.Normal && !world.PinnedToScreen
            && (style & (int)WINDOW_STYLE.WS_MAXIMIZE) == 0
            && _win32.IsManageable(handle, (uint)Environment.ProcessId, allowMinimized: true)
            && (_vds == null || _vds.IsOnCurrentDesktop(handle));
        var projected = world.PinnedToScreen
            ? _win32.GetWindowRect(handle) : default;
        if (_disposed || !_canvas.Windows.TryGetValue(handle, out var current) || !current.Equals(world)) return;
        var policyCamera = _overviewViewport ?? _canvas.Camera;
        var policyRect = world.PinnedToScreen
            ? new WindowRect(projected.x, projected.y, projected.w, projected.h)
            : _overviewViewport.HasValue
                ? new WindowRect((int)((world.X - policyCamera.X) * policyCamera.Zoom),
                    (int)((world.Y - policyCamera.Y) * policyCamera.Zoom),
                    Math.Max(1, (int)Math.Ceiling(world.W * policyCamera.Zoom)),
                    Math.Max(1, (int)Math.Ceiling(world.H * policyCamera.Zoom)))
                : policyCamera.Project(world);
        _visibility.Observe(handle, identity, policyRect, _overviewScreens ?? screens,
            minimized ?? (style & (int)WINDOW_STYLE.WS_MINIMIZE) != 0, eligible, _clock.TickCount64,
            allowLivePreview: !OverviewUsesInfoCards);
        Previews.Observe(handle, _visibility.GetToken(handle));
    }

    public void DiscoverNewWindows()
    {
        uint ownPid = (uint)Environment.ProcessId;
        List<IntPtr>? toAdd = null;

        _win32.EnumWindows(hWnd =>
        {
            if (_canvas.HasWindow(hWnd)) return true;
            if (!_win32.IsManageable(hWnd, ownPid)) return true;
            if (_vds != null && !_vds.IsOnCurrentDesktop(hWnd)) return true;
            (toAdd ??= new()).Add(hWnd);
            return true;
        });

        if (toAdd == null) return;
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
        var move = TrackMove(hWnd, rect, posOnly: false);
        var result = _win32.SetWindowPosition(hWnd, rect.X, rect.Y, rect.W, rect.H,
            (uint)(SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE));
        ApplyMoveResult(move, result);
    }

    private void UnpinWindowFromScreen(IntPtr hWnd)
    {
        var (x, y, w, h) = _win32.GetWindowRect(hWnd);
        _canvas.SetWindowFromScreen(hWnd, x, y, w, h);
        _canvas.SetPinnedToScreen(hWnd, false);
        _lastScreen[hWnd] = (x, y, w, h);
        _pendingMoves.Remove(hWnd);
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

    private BatchMoveItem TrackMove(IntPtr hWnd, WindowRect rect, bool posOnly)
    {
        if (!_lastScreen.ContainsKey(hWnd))
        {
            var actual = _win32.GetWindowRect(hWnd);
            if (actual.w > 0 && actual.h > 0) _lastScreen[hWnd] = actual;
        }
        var expected = posOnly && _lastScreen.TryGetValue(hWnd, out var last)
            ? new WindowRect(rect.X, rect.Y, last.w, last.h) : rect;
        var item = new BatchMoveItem(hWnd, rect, posOnly) { RequestId = ++_nextMoveId };
        List<WindowRect>? superseded = null;
        if (_pendingMoves.TryGetValue(hWnd, out var previous))
        {
            superseded = previous.Superseded;
            if (previous.Expected != expected)
            {
                superseded ??= new(4);
                // Bound memory even if an application stops servicing its queue.
                if (superseded.Count == 64) superseded.RemoveAt(0);
                superseded.Add(previous.Expected);
            }
        }
        _pendingMoves[hWnd] = new(item, expected, superseded);
        _unconfirmedMoves.Add(hWnd);
        return item;
    }

    private bool CanSkipMove(IntPtr handle, WindowRect target, bool posOnly)
        => !_pendingMoves.ContainsKey(handle) && !_unconfirmedMoves.Contains(handle)
            && _lastScreen.TryGetValue(handle, out var last)
            && last.x == target.X && last.y == target.Y
            && (posOnly || (last.w == target.W && last.h == target.H));

    private void ApplyMoveResult(BatchMoveItem item, NativeOperationResult result)
    {
        // Identical rectangles can belong to different requests. Compare revisions
        // so an older worker completion cannot consume a newer pending placement.
        if (!_pendingMoves.TryGetValue(item.HWnd, out var pending) || pending.Item.RequestId != item.RequestId) return;
        // USER32 queue acceptance is not an observed placement. Keep both the
        // pending target and the old baseline until reconciliation sees it land;
        // otherwise the old screen rect becomes a false drag under the new camera.
        if (result.Status == NativeOperationStatus.Queued)
        {
            _unconfirmedMoves.Add(item.HWnd);
            return;
        }
        _pendingMoves.Remove(item.HWnd);
        if (result.Accepted)
        {
            var expected = pending.Expected;
            _lastScreen[item.HWnd] = (expected.X, expected.Y, expected.W, expected.H);
            _unconfirmedMoves.Remove(item.HWnd);
        }
        else
        {
            _unconfirmedMoves.Add(item.HWnd);
        }
    }

    private bool ApplyRegionChange(IntPtr hWnd, WindowRegionChange change)
    {
        if (change == WindowRegionChange.Clip)
        {
            if (_allPreviewsVisible || _livePreviewWindows.Contains(hWnd)) _previewUnclipped.Add(hWnd);
            else if (!_win32.ClipWindow(hWnd).Accepted) return false;
            _clippedWindows.Add(hWnd);
        }
        else if (change == WindowRegionChange.Unclip)
        {
            if (!_win32.UnclipWindow(hWnd).Accepted) return false;
            _clippedWindows.Remove(hWnd);
            _previewUnclipped.Remove(hWnd);
        }
        return true;
    }

    private static void RequireAccepted(NativeOperationResult result)
    {
        if (!result.Accepted)
            throw new InvalidOperationException($"{result.Operation} failed for window {result.Window} (native error {result.ErrorCode?.ToString() ?? "unspecified"}).");
    }
}
