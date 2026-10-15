using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace CanvasDesktop;

/// <summary>
/// Coordinator for the overview: owns mode state, camera, inertia, and one
/// OverviewOverlay per physical monitor (each with its own Form + swap chain).
/// </summary>
internal sealed partial class OverviewManager : IDisposable, IOverviewController
{
    private readonly Canvas _mainCanvas;
    private readonly WindowManager _wm;
    private readonly IWindowApi _win32;
    private readonly IAppConfig _appConfig;
    private readonly IScreens _screens;
    private readonly IInputRouter _input;
    private readonly OverviewState _state = new();
    private readonly OverviewCamera _camera;

    public OverviewMode CurrentMode { get { return _state.CurrentMode; } }
    private OverviewModeConfig _cfg { get { return _state.CurrentConfig; } }

    public event Action<OverviewMode, OverviewMode>? BeforeModeChanged;
    public event Action<OverviewMode, OverviewMode>? AfterModeChanged;

    private const double ExtentsPaddingRatio = 0.1;
    private const double MouseWheelDeltaPerNotch = 120.0;
    private static readonly string[] MyDockFinderProcessHints =
    {
        "mydockfinder",
        "mydock",
        "myfinder"
    };
    private static readonly string[] MyDockFinderExactProcessNames =
    {
        "dock_64",
        "dock"
    };

    private readonly InertiaTracker _inertia = new();
    private readonly object _inertiaQueueLock = new();
    private int _pendingInertiaDx, _pendingInertiaDy;
    private bool _inertiaPanQueued;

    // Per-monitor passes
    private readonly List<OverviewOverlay> _passes = new();

    internal IReadOnlyList<string> RenderingBackends
    {
        get
        {
            var backends = new string[_passes.Count];
            for (int i = 0; i < backends.Length; i++)
                backends[i] = _passes[i].Grid?.RenderingBackend ?? "Unavailable";
            return backends;
        }
    }

    // MMCSS registration for the UI thread while the overview is open —
    // keeps WM_PAINT / mouse-message dispatch on near-realtime scheduling.
    // Released on Hide.
    private IntPtr _mmcssHandle;

    /// <summary>HWNDs of all monitor forms (for IInputRouter.SetExtraPanSurfaces).</summary>
    public IReadOnlyList<IntPtr> MonitorHandles
    {
        get
        {
            var list = new List<IntPtr>(_passes.Count);
            foreach (var p in _passes) list.Add(p.Handle);
            return list;
        }
    }

    // Shared ordered list of canvas windows shown in the overview
    // (arrow navigation, hit testing). Refreshed on Show.
    private readonly OverviewWindowList _windows;

    // All DWM thumbnail state (desktop + taskbars + per-window). Owned here
    // so it can reference _passes; OverviewManager only drives lifecycle.
    private readonly OverviewThumbnails _thumbnails;
    private readonly OverviewInputs _inputs;
    private volatile bool _disposed;

    // Pan/drag state (virtual-screen coords)
    private int _panStartVx, _panStartVy;
    private int _dragIndex = -1;
    private int _dragStartVx, _dragStartVy;
    private void ReconcileVisuals()
    {
        _wm.SetOverviewViewport(new CanvasCamera(_camera.X, _camera.Y, _camera.Zoom),
            _passes.ConvertAll(p => (p.Screen.Bounds.X, p.Screen.Bounds.Y, p.Screen.Bounds.Width, p.Screen.Bounds.Height)));
        if (_placeholderWorkspace?.SetOverviewCardMode(_wm.OverviewUsesInfoCards) == true)
        {
            _placeholderTitles = _placeholderWorkspace.GetPlaceholderTitles();
            RefreshOverviewWindows();
        }
        RefreshCardIdentities();
        _thumbnails.Reconcile();
        RefreshZoneAnnotations();
    }

    private bool _previewRefreshQueued;
    private void OnPreviewChanged()
    {
        if (_disposed || CurrentMode == OverviewMode.Hidden || _previewRefreshQueued || _passes.Count == 0) return;
        var pass = _passes[0];
        if (!pass.IsHandleCreated || pass.IsDisposed) return;
        _previewRefreshQueued = true;
        pass.BeginInvoke((Action)(() =>
        {
            _previewRefreshQueued = false;
            if (!_disposed && CurrentMode != OverviewMode.Hidden)
            {
                _thumbnails.InvalidateCachedSources();
                RefreshOverviewWindows();
                ReconcileVisuals();
            }
        }));
    }

    public OverviewManager(Canvas mainCanvas, WindowManager wm, IWindowApi win32, IInputRouter input, IAppConfig appConfig, IScreens? screens = null,
        Func<bool>? prefersReducedStartupMotion = null)
    {
        _mainCanvas = mainCanvas;
        _wm = wm;
        _win32 = win32;
        _appConfig = appConfig;
        _prefersReducedStartupMotion = prefersReducedStartupMotion ?? (() => !System.Windows.SystemParameters.ClientAreaAnimation);
        _input = input;
        _screens = screens ?? WinFormsScreens.Instance;
        _camera = new OverviewCamera(_screens);
        _zoneTool = new ZoneCanvasTool(() => _zones != null,
            () => _zones?.Items ?? Array.Empty<ZoneView>(), ZoneMoveMembers);
        _zoneTool.Completed += OnZoneToolCompleted;
        _tools.SetTool(_zoneTool);
        _windows = new OverviewWindowList(mainCanvas, win32);
        _thumbnails = new OverviewThumbnails(_passes, _windows, _camera, _state, _win32, _screens,
            _wm.UsesCachedPreview, _wm.GetCachedPreviewSource, prepareLiveSources: _wm.SetLivePreviewWindows,
            useNativeSource: handle => !_placeholderTitles.ContainsKey(handle),
            transitionSource: GetCardTransitionSource);
        _wm.Previews.Changed += OnPreviewChanged;
        _wm.AutomaticParkingChanged += OnPreviewChanged;

        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        _inputs = new OverviewInputs(this, input, mainCanvas);
    }

    public void RefreshConfig()
    {
        if (CurrentMode != OverviewMode.Hidden)
            ApplyConfig();
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        // Monitor topology changed — rebuild passes to match. If overview is
        // open, close it, rebuild, reopen in the previous mode.
        CancelStartupIntro();
        OverviewMode prev = CurrentMode;
        bool wasVisible = prev != OverviewMode.Hidden;

        if (wasVisible)
            TransitionTo(OverviewMode.Hidden, syncCameraOnClose: false);

        foreach (var p in _passes)
        {
            p.Close();
            p.Dispose();
        }
        _passes.Clear();

        // Shell HWNDs may have moved (Progman / WorkerW are recreated on some
        // display changes; secondary taskbars come and go with monitors).
        _thumbnails.InvalidateShellCache();

        // Hidden overview sessions also need to re-evaluate native clipping. A
        // parked window may still be at the edge of a monitor that no longer exists.
        _wm.ReprojectForDisplayChange();

        EnsurePasses();
        WarmupPasses();

        if (wasVisible)
            TransitionTo(prev);
    }

    /// <summary>Pre-initialize D3D11 and grid threads on every monitor.</summary>
    public void Warmup()
    {
        EnsurePasses();
        WarmupPasses();
    }

    /// <summary>Initialize D3D11 per pass and push screen layout to the grid shader.</summary>
    private void WarmupPasses()
    {
        var monitors = new System.Drawing.Rectangle[_passes.Count];
        for (int i = 0; i < _passes.Count; i++)
        {
            var b = _passes[i].Screen.Bounds;
            monitors[i] = new System.Drawing.Rectangle(b.X, b.Y, b.Width, b.Height);
        }

        foreach (var p in _passes)
        {
            p.Warmup(_appConfig.EnableFlipRendering);
            p.Grid?.SetScreenLayout(p.OriginX, p.OriginY, p.Screen.Primary, monitors);
        }
    }

    private void EnsurePasses()
    {
        if (_passes.Count > 0) return;
        foreach (var screen in Screen.AllScreens)
        {
            var pass = new OverviewOverlay(screen);
            pass.SetDrawingLayers(_drawingLayers);
            pass.OnKey = HandleKeyDown;
            pass.OnMouseButtonDown = HandleMouseDown;
            pass.OnMouseMoved = HandleMouseMove;
            pass.OnMouseButtonUp = HandleMouseUp;
            pass.OnWheel = HandleMouseWheel;
            pass.OnMouseDoubleClicked = HandleDoubleClick;
            pass.OnCaptureLost = _ =>
            {
                if (_tools.CapturedSurface != _passes.IndexOf(pass)) return;
                CancelInteraction();
            };
            _passes.Add(pass);
        }
    }

    public void RecordPanDelta(int dx, int dy)
    {
        _inertia.RecordDelta(dx, dy);
    }

    public void ReleaseInertia()
    {
        bool active = _inertia.Release();
        if (_passes.Count > 0 && _passes[0].Grid is { } grid) grid.FrameTicksRequired = active;
        if (!active && CurrentMode != OverviewMode.Hidden)
        {
            TransitionTo(OverviewMode.Hidden);
        }
    }

    public void CancelInertia()
    {
        _inertia.Cancel();
        if (_passes.Count > 0 && _passes[0].Grid is { } grid) grid.FrameTicksRequired = false;
        lock (_inertiaQueueLock)
        {
            _pendingInertiaDx = 0;
            _pendingInertiaDy = 0;
        }
    }

    /// <summary>Called on a grid render thread after Present. Drives inertia.</summary>
    private void OnGridFrameTick()
    {
        if (_disposed) return;
        var (dx, dy, stopped) = _inertia.Tick();

        if (stopped)
        {
            if (_passes.Count > 0 && _passes[0].Grid is { } grid) grid.FrameTicksRequired = false;
            if (_passes.Count > 0 && _passes[0].IsHandleCreated)
                _passes[0].BeginInvoke(() => { if (!_disposed && CurrentMode != OverviewMode.Hidden) TransitionTo(OverviewMode.Hidden); });
            return;
        }

        if ((dx != 0 || dy != 0) && _passes.Count > 0 && _passes[0].IsHandleCreated)
        {
            bool queue;
            lock (_inertiaQueueLock)
            {
                _pendingInertiaDx += dx;
                _pendingInertiaDy += dy;
                queue = !_inertiaPanQueued;
                if (queue) _inertiaPanQueued = true;
            }

            if (queue)
            {
                _passes[0].BeginInvoke(() =>
                {
                    int cdx, cdy;
                    lock (_inertiaQueueLock)
                    {
                        cdx = _pendingInertiaDx;
                        cdy = _pendingInertiaDy;
                        _pendingInertiaDx = 0;
                        _pendingInertiaDy = 0;
                        _inertiaPanQueued = false;
                    }
                    if (_disposed || !_inertia.IsActive || (cdx == 0 && cdy == 0)) return;
                    _mainCanvas.Pan(cdx, cdy);
                });
            }
        }
    }

    /// <summary>Sync overview camera to the main canvas and update visuals on all passes.</summary>
    public void SyncCamera()
    {
        if (IsStartupIntroActive) { CancelStartupIntro(); return; }
        if (CurrentMode == OverviewMode.Hidden) return;
        CancelInteraction();
        _camera.SyncFrom(_mainCanvas);

        foreach (var p in _passes)
            p.Grid?.UpdateCamera(_camera.X, _camera.Y, _camera.Zoom);
        ReconcileVisuals();
    }

    /// <summary>Single entry point for every mode change.</summary>
    public void TransitionTo(OverviewMode target, bool syncCameraOnClose = true)
    {
        if (IsStartupIntroActive && !_openingStartupIntro)
            FinishStartupIntro(refresh: false);
        CancelInertia();

        OverviewMode from = CurrentMode;
        if (!_state.SetMode(target)) return;

        BeforeModeChanged?.Invoke(from, target);

        if (target == OverviewMode.Hidden)
        {
            HideInternal(syncCamera: syncCameraOnClose && from != OverviewMode.Hidden && !_startupIntroLanded);
            _startupIntroLanded = false;
        }
        else if (from == OverviewMode.Hidden)
            ShowInternal();
        else
            ApplyConfig();

        AfterModeChanged?.Invoke(from, target);
    }

    private void ShowInternal()
    {
        _wm.RefreshPreviewState();
        EnsurePasses();
        WarmupPasses();

        if (_mmcssHandle == IntPtr.Zero)
            _mmcssHandle = Mmcss.Begin("Window Manager");

        _camera.SyncFrom(_mainCanvas);

        _wm.SuspendReconcile = true;

        _thumbnails.Show();

        ApplyConfig();

        foreach (var p in _passes)
        {
            if (_startupIntroFrame is { } intro) p.SetIntroOpacity(intro.RevealOpacity);
            p.Show();
        }
        if (_passes.Count > 0) _passes[0].Activate();
        RefreshPlaceholderTitles();
        if (ShouldShowScreenFixedWindowsDuringPan)
            RaiseMyDockFinderWindowsAboveOverview();

        // Attach frame tick to the first pass's grid (drives inertia)
        if (_passes.Count > 0 && _passes[0].Grid != null)
            _passes[0].Grid!.OnFrameTick = OnGridFrameTick;

        foreach (var p in _passes)
            p.Grid?.Start(_camera.X, _camera.Y, _camera.Zoom);
    }

    private void HideInternal(bool syncCamera)
    {
        _previewRefreshQueued = false;
        ClearCardTransitions();
        CancelInteraction();
        _annotationCards = Array.Empty<PlaceholderAnnotation>();
        _annotationToolbar?.Hide();
        foreach (var p in _passes) p.HideZones();
        foreach (var p in _passes)
        {
            if (p.Grid != null) p.Grid.OnFrameTick = null;
            p.Grid?.Stop();
        }

        if (syncCamera)
        {
            var (vx, vy) = _camera.ViewportCamera;
            _mainCanvas.SetCamera(vx, vy);
        }

        _wm.SetLivePreviewWindows(Array.Empty<IntPtr>());
        _wm.SetOverviewViewport(null);
        _placeholderWorkspace?.SetOverviewCardMode(false);
        _mainCanvas.Commit();
        _wm.SuspendReconcile = false;
        _wm.SuspendProjection = false;

        _thumbnails.Hide();
        _wm.Previews.ReleaseSurfaces();
        foreach (var p in _passes)
            p.Hide();
        _windows.Clear();

        if (_mmcssHandle != IntPtr.Zero)
        {
            Mmcss.Revert(_mmcssHandle);
            _mmcssHandle = IntPtr.Zero;
        }
    }

    private void ApplyConfig()
    {
        RefreshOverviewWindows();

        // WS_EX_LAYERED is added in Panning and removed in Zooming. Panning
        // is where background-throttle freezes were seen after losing
        // foreground (click lands on an underlying window on drag release);
        // layered compositing seems to keep DWM driving the overlay. Zooming
        // holds foreground itself so it doesn't need the layered cost.
        if (CurrentMode != OverviewMode.Zooming)
        {
            CancelInteraction();
            _annotationToolbar?.Hide();
            foreach (var p in _passes) p.HideZones();
        }
        bool wantLayered = CurrentMode == OverviewMode.Panning || IsStartupIntroActive;
        bool wantNoActivate = (CurrentMode == OverviewMode.Panning);
        foreach (var p in _passes)
        {
            if (p.Grid != null) p.Grid.DrawGrid = _cfg.GridVisible;
            p.SetModeStyle(wantLayered, wantNoActivate);
            p.SetClickThrough(!_cfg.InputEnabled);
        }
        ReconcileVisuals();
        if (ShouldShowScreenFixedWindowsDuringPan && AnyPassVisible())
            RaiseMyDockFinderWindowsAboveOverview();
        else if (CurrentMode == OverviewMode.Zooming && AnyPassVisible())
            RaiseOverviewPassesAboveTopmostWindows();

        // During Zooming, real-window positions don't need to track the camera
        // (click-through is off — clicks land on the overview form, not real
        // windows). Suppressing projection eliminates the worker-job wait at
        // close-time ReprojectSync.
        _wm.SuspendProjection = (CurrentMode == OverviewMode.Zooming);
    }

    private bool AnyPassVisible()
    {
        foreach (var p in _passes)
        {
            if (p.Visible) return true;
        }
        return false;
    }

    private bool ShouldShowScreenFixedWindowsDuringPan
    {
        get { return CurrentMode == OverviewMode.Panning && _appConfig.ShowScreenFixedWindowsDuringPan; }
    }

    private void RaiseOverviewPassesAboveTopmostWindows()
    {
        HWND topMost = (HWND)new IntPtr(-1);
        var flags =
            SET_WINDOW_POS_FLAGS.SWP_NOMOVE |
            SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE |
            SET_WINDOW_POS_FLAGS.SWP_NOSENDCHANGING;

        foreach (var p in _passes)
        {
            if (p.IsHandleCreated)
                PInvoke.SetWindowPos((HWND)p.Handle, topMost, 0, 0, 0, 0, flags);
        }
    }

    private void RaiseMyDockFinderWindowsAboveOverview()
    {
        var windows = new List<IntPtr>();
        uint ownPid = (uint)Environment.ProcessId;

        _win32.EnumWindows(hWnd =>
        {
            if (IsMyDockFinderWindow(hWnd, ownPid))
                windows.Add(hWnd);
            return true;
        });

        HWND topMost = (HWND)new IntPtr(-1);
        var flags =
            SET_WINDOW_POS_FLAGS.SWP_NOMOVE |
            SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE |
            SET_WINDOW_POS_FLAGS.SWP_NOSENDCHANGING;

        // EnumWindows is topmost-first; raising bottom-to-top preserves the
        // dock/finder order while still putting the group above the overlay.
        for (int i = windows.Count - 1; i >= 0; i--)
        {
            PInvoke.SetWindowPos((HWND)windows[i], topMost, 0, 0, 0, 0, flags);
        }
    }

    private bool IsMyDockFinderWindow(IntPtr hWnd, uint ownPid)
    {
        if (!_win32.IsWindowVisible(hWnd))
            return false;

        uint pid = _win32.GetWindowProcessId(hWnd);
        if (pid == 0 || pid == ownPid)
            return false;

        if (!IsMyDockFinderProcess(pid))
            return false;

        int style = _win32.GetWindowStyle(hWnd);
        if ((style & (int)WINDOW_STYLE.WS_MINIMIZE) != 0)
            return false;

        var (_, _, w, h) = _win32.GetWindowRect(hWnd);
        return w > 0 && h > 0;
    }

    private bool IsMyDockFinderProcess(uint pid)
    {
        var (name, exe) = _win32.GetProcessInfo(pid);
        return IsMyDockFinderProcessName(name) || IsMyDockFinderProcessName(exe);
    }

    private static bool IsMyDockFinderProcessName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        string normalized = value.Trim();
        if (normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[..^4];

        foreach (string name in MyDockFinderExactProcessNames)
        {
            if (string.Equals(normalized, name, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        foreach (string name in MyDockFinderProcessHints)
        {
            if (value.Contains(name, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Raise the window at the given _windows index in both the system
    /// z-order and the overview thumbnail draw order; move its entry to index 0.
    /// </summary>
    private void BringWindowToFront(int index)
    {
        if (index <= 0 || index >= _windows.Count) return;

        IntPtr hWnd = _windows.Windows[index].HWnd;
        _windows.MoveToFront(index);

        // System z-order — HWND_TOP (0), no move/size/activate
        PInvoke.SetWindowPos((HWND)hWnd, HWND.Null, 0, 0, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);

        _thumbnails.BringToFront(hWnd);
        ReconcileVisuals();
    }

    // ==================== INPUT ====================

    private void HandleKeyDown(OverviewOverlay _, KeyEventArgs e)
    {
        if (TrySkipStartupIntro()) { e.SuppressKeyPress = true; e.Handled = true; return; }
        if (!_cfg.InputEnabled) return;

        if (e.KeyCode == Keys.Escape)
        {
            HandleEscape();
            e.Handled = true;
            return;
        }

        if (HandleAnnotationKey(e)) return;
        if (HandleZoneKey(e)) return;
        if (_tools.Active) { e.Handled = true; return; }
        if (_windows.Count == 0) return;

        if (e.KeyCode == Keys.Right || e.KeyCode == Keys.Down)
        {
            _windows.SelectNext();
            NavigateToSelected();
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Up)
        {
            _windows.SelectPrev();
            NavigateToSelected();
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Enter && _windows.SelectedIndex >= 0)
        {
            var entry = _windows.Windows[_windows.SelectedIndex];
            GoToWindow(entry.HWnd, entry.World);
            e.Handled = true;
        }
    }

    private void NavigateToSelected()
    {
        if (_windows.SelectedIndex < 0 || _windows.SelectedIndex >= _windows.Count) return;
        var world = _windows.Windows[_windows.SelectedIndex].World;

        // Center overview camera on selected window (do NOT change zoom)
        _startupIntroLanded = false;
        _camera.CenterOnWorld(world.X, world.Y, world.W, world.H);

        foreach (var p in _passes)
            p.Grid?.UpdateCamera(_camera.X, _camera.Y, _camera.Zoom);
        ReconcileVisuals();
    }

    private void HandleMouseDown(OverviewOverlay pass, MouseEventArgs e)
    {
        if (TrySkipStartupIntro()) return;
        if (!_cfg.InputEnabled)
        {
            TransitionTo(OverviewMode.Hidden);
            return;
        }

        if (_annotationEditor != null) return;
        int vx = e.X + pass.OriginX;
        int vy = e.Y + pass.OriginY;

        var pointer = Pointer(pass, e);
        if (_tools.TryBeginTool(pointer)) { ApplyPointerCapture(); RefreshToolPreview(); return; }
        if (e.Button == MouseButtons.Left)
        {
            var (wx, wy) = _camera.WorldFromVirtual(vx, vy);
            int hit = HitOverviewWindow(pass, e.X, e.Y, wx, wy);
            if (hit >= 0)
            {
                BringWindowToFront(hit);
                _tools.TryBeginNavigation(CanvasInteractionKind.Window, pointer);
                ApplyPointerCapture();
                _dragIndex = 0;
                _dragStartVx = vx;
                _dragStartVy = vy;
                return;
            }

            _tools.TryBeginNavigation(CanvasInteractionKind.Pan, pointer);
            ApplyPointerCapture();
            _panStartVx = vx;
            _panStartVy = vy;
        }
        else if (e.Button == MouseButtons.Middle)
        {
            _tools.TryBeginNavigation(CanvasInteractionKind.Pan, pointer);
            ApplyPointerCapture();
            _panStartVx = vx;
            _panStartVy = vy;
        }
    }

    private void HandleMouseMove(OverviewOverlay pass, MouseEventArgs e)
    {
        if (!_cfg.InputEnabled || _annotationEditor != null) return;

        int vx = e.X + pass.OriginX;
        int vy = e.Y + pass.OriginY;

        var pointer = Pointer(pass, e);
        if (_tools.MoveTool(pointer)) { RefreshToolPreview(); return; }
        if (_tools.Active && !_tools.OwnsSurface(pointer.SurfaceId)) return;
        var hoverWorld = _camera.WorldFromVirtual(vx, vy);
        int hoverIndex = HitOverviewWindow(pass, e.X, e.Y, hoverWorld.x, hoverWorld.y);
        var hovered = hoverIndex >= 0 && _placeholderTitles.ContainsKey(_windows.Windows[hoverIndex].HWnd)
            ? _windows.Windows[hoverIndex].HWnd : IntPtr.Zero;
        if (_hoveredPlaceholder != hovered)
        {
            _hoveredPlaceholder = hovered;
            RefreshZoneAnnotations();
        }
        pass.Cursor = _tools.CursorAt(pointer) switch
        {
            CanvasCursor.Crosshair => Cursors.Cross,
            CanvasCursor.ResizeNorthWestSouthEast => Cursors.SizeNWSE,
            CanvasCursor.ResizeNorthEastSouthWest => Cursors.SizeNESW,
            _ => Cursors.Default
        };
        if (_tools.Kind == CanvasInteractionKind.Window && _dragIndex >= 0 && _dragIndex < _windows.Count)
        {
            double dx = (vx - _dragStartVx) / _camera.Zoom;
            double dy = (vy - _dragStartVy) / _camera.Zoom;
            _dragStartVx = vx;
            _dragStartVy = vy;

            _windows.TranslateAt(_dragIndex, dx, dy);
            var entry = _windows.Windows[_dragIndex];
            _thumbnails.UpdateWorldRect(entry.HWnd, entry.World);

            _mainCanvas.SetWindow(entry.HWnd, entry.World.X, entry.World.Y, entry.World.W, entry.World.H);
            ReconcileVisuals();
        }
        else if (_tools.Kind == CanvasInteractionKind.Pan)
        {
            int dx = vx - _panStartVx;
            int dy = vy - _panStartVy;
            _panStartVx = vx;
            _panStartVy = vy;

            double worldDx = dx / _camera.Zoom;
            double worldDy = dy / _camera.Zoom;
            if (dx != 0 || dy != 0) _startupIntroLanded = false;
            _camera.PanByVirtual(dx, dy);

            foreach (var p in _passes)
            {
                p.Grid?.AccumulatePan(worldDx, worldDy);
                p.Grid?.UpdateCamera(_camera.X, _camera.Y, _camera.Zoom);
            }
            ReconcileVisuals();
        }
    }

    private void HandleMouseUp(OverviewOverlay pass, MouseEventArgs e)
    {
        if (TrySkipStartupIntro()) return;
        var pointer = Pointer(pass, e);
        if (_tools.Kind == CanvasInteractionKind.Tool)
        {
            try { _tools.CompleteTool(pointer); }
            finally { ApplyPointerCapture(); RefreshToolPreview(); }
            return;
        }
        var ended = _tools.EndNavigation(pointer);
        ApplyPointerCapture();
        if (ended == CanvasInteractionKind.Window)
        {
            _wm.Reproject(true);
            _dragIndex = -1;
        }
    }

    private void HandleMouseWheel(OverviewOverlay pass, MouseEventArgs e)
    {
        if (TrySkipStartupIntro()) return;
        if (!_cfg.InputEnabled || _tools.Active || _annotationEditor != null) return;

        double notches = e.Delta / MouseWheelDeltaPerNotch;
        int vx = e.X + pass.OriginX;
        int vy = e.Y + pass.OriginY;

        if (!_camera.ZoomToCursor(vx, vy, notches)) return;
        _startupIntroLanded = false;

        foreach (var p in _passes)
            p.Grid?.UpdateCamera(_camera.X, _camera.Y, _camera.Zoom);

        ReconcileVisuals();
    }

    private void HandleDoubleClick(OverviewOverlay pass, MouseEventArgs e)
    {
        if (TrySkipStartupIntro()) return;
        if (!_cfg.InputEnabled) return;
        if (_annotationTool is { Mode: not AnnotationMode.Navigate } annotationTool)
        {
            if (e.Button == MouseButtons.Left && annotationTool.Mode == AnnotationMode.Select
                && annotationTool.Hit(Pointer(pass,e)) is { Kind: AnnotationKind.Text } note)
            {
                CancelInteraction(); annotationTool.Select(note.Id); QueueAnnotationTextEdit(note);
            }
            return;
        }
        if (e.Button != MouseButtons.Left || _tools.ToolHasPendingInteraction || (Control.ModifierKeys & Keys.Shift) != 0) return;

        int vx = e.X + pass.OriginX;
        int vy = e.Y + pass.OriginY;
        var (wx, wy) = _camera.WorldFromVirtual(vx, vy);

        int hit = HitOverviewWindow(pass, e.X, e.Y, wx, wy);
        if (hit >= 0)
        {
            var entry = _windows.Windows[hit];
            GoToWindow(entry.HWnd, entry.World);
        }
    }

    // ==================== HELPERS ====================

    private void GoToWindow(IntPtr hWnd, WorldRect world)
    {
        if (_placeholderWorkspace?.LiveCardItem(hWnd) is { } itemId)
        {
            TransitionTo(OverviewMode.Hidden, syncCameraOnClose: false);
            _placeholderWorkspace.Open(itemId);
            return;
        }
        if (_mainCanvas.IsCollapsed(hWnd))
            PInvoke.ShowWindow((HWND)hWnd, SHOW_WINDOW_CMD.SW_RESTORE);

        var vs = _screens.VirtualScreen;
        _mainCanvas.CenterOn(world.X, world.Y, world.W, world.H, vs.Width, vs.Height);
        PInvoke.SetForegroundWindow((HWND)hWnd);
        TransitionTo(OverviewMode.Hidden, syncCameraOnClose: false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _wm.Previews.Changed -= OnPreviewChanged;
        _wm.AutomaticParkingChanged -= OnPreviewChanged;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        if (_zones != null) _zones.Changed -= RefreshZoneAnnotations;
        if (_placeholderWorkspace != null) _placeholderWorkspace.Changed -= RefreshPlaceholderTitles;
        var failures = new List<Exception>();
        void Attempt(Action action) { try { action(); } catch (Exception ex) { failures.Add(ex); } }
        Attempt(DisposeAnnotations);
        Attempt(() => FinishStartupIntro(refresh: false));
        Attempt(() =>
        {
            if (CurrentMode != OverviewMode.Hidden)
                TransitionTo(OverviewMode.Hidden, syncCameraOnClose: false);
        });
        Attempt(() => { CancelInteraction(); });
        _annotationCards = Array.Empty<PlaceholderAnnotation>();
        _zoneTool.Completed -= OnZoneToolCompleted;
        Attempt(ClearCardTransitions);
        Attempt(() => _cardAnimationTimer?.Dispose());
        Attempt(_inputs.Dispose);
        Attempt(CancelInertia);
        // HideInternal may have failed after the state changed to Hidden.
        // Retry native ownership cleanup independently of that transition.
        Attempt(_thumbnails.Hide);
        Attempt(_wm.Previews.ReleaseSurfaces);
        Attempt(() =>
        {
            if (_mmcssHandle != IntPtr.Zero)
            {
                Mmcss.Revert(_mmcssHandle);
                _mmcssHandle = IntPtr.Zero;
            }
        });
        foreach (var p in _passes)
        {
            if (p.Grid != null) p.Grid.OnFrameTick = null;
            Attempt(p.Dispose);
        }
        _passes.Clear();
        if (failures.Count > 0) throw new AggregateException("Overview resource cleanup failed.", failures);
    }

}
