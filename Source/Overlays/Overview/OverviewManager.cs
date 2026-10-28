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

    internal CanvasCamera ViewCamera => new(_camera.X, _camera.Y, _camera.Zoom);

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
    private readonly OverviewTaskbarPreviews _taskbarPreviews;
    private readonly OverviewInputs _inputs;
    private volatile bool _disposed;

    // Pan/drag state (virtual-screen coords)
    private int _panStartVx, _panStartVy;
    private int _dragIndex = -1;
    private int _dragStartVx, _dragStartVy;
    private bool _reconcilingVisuals;
    private CanvasCamera? _sceneCamera;
    private CanvasCamera SceneCamera => _sceneCamera ?? ViewCamera;
    private void ReconcileVisuals()
    {
        // Native/WPF calls may reenter the UI thread. Complete this immutable
        // camera frame before servicing a newer invalidation.
        if (_reconcilingVisuals) { _visualFrames.Request(); return; }
        _reconcilingVisuals = true;
        try { ReconcileVisualFrame(); }
        finally { _sceneCamera = null; _reconcilingVisuals = false; }
    }

    private void ReconcileVisualFrame()
    {
        _visualFrames.Cancel();
        UpdateGesturePreviews();
        RefreshTutorialScale();
        var camera = ViewCamera;
        _sceneCamera = camera;
        double panX = _visualPanX, panY = _visualPanY;
        _visualPanX = _visualPanY = 0;
        // A stationary tutorial landing is a help view, not a request to park the user's apps.
        var proximityCamera = _tutorialReading && (_startupIntroLanded || GuidedTutorialActive)
            ? _mainCanvas.Camera : camera;
        _wm.SetOverviewViewport(proximityCamera,
            _passes.ConvertAll(p => (p.Screen.Bounds.X, p.Screen.Bounds.Y, p.Screen.Bounds.Width, p.Screen.Bounds.Height)));
        if (_placeholderWorkspace?.SetOverviewCardMode(_wm.OverviewUsesInfoCards) == true)
        {
            _placeholderTitles = _placeholderWorkspace.GetPlaceholderTitles();
            RefreshOverviewWindows();
        }
        RefreshCardIdentities();
        _thumbnails.Reconcile(camera);
        if (_windowInteraction.Active && (!_thumbnails.HasLiveSource(_windowInteraction.Window)
            || _wm.UsesCachedPreview(_windowInteraction.Window))) EndWindowInteraction(focus: true);
        RefreshZoneAnnotations();
        // Publish the GPU background camera after DWM properties and the staged
        // annotation scene. This avoids waking it with a partially submitted view.
        if (!_disposed && CurrentMode != OverviewMode.Hidden) UpdateGridCamera(camera, panX, panY);
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
        Func<bool>? prefersReducedStartupMotion = null, IClock? gestureClock = null, IDwmThumbnailApi? dwm = null,
        IWindowInteractionApi? interactionApi = null)
    {
        _taskbarPreviews = new(previews =>
        {
            foreach (var pass in _passes) pass.SetTaskbarPreviews(previews);
        });
        _mainCanvas = mainCanvas;
        _wm = wm;
        _win32 = win32;
        _appConfig = appConfig;
        _gestureClock = gestureClock ?? SystemClock.Instance;
        _inertia = new InertiaTracker(_gestureClock);
        _prefersReducedStartupMotion = prefersReducedStartupMotion ?? (() => !System.Windows.SystemParameters.ClientAreaAnimation);
        _input = input;
        _windowInteraction = new(interactionApi ?? new Win32WindowInteractionApi());
        input.WindowDestroyed += OnInteractionWindowLost;
        input.WindowMinimized += OnInteractionWindowLost;
        input.WindowFocused += OnInteractionForegroundChanged;
        _screens = screens ?? WinFormsScreens.Instance;
        _camera = new OverviewCamera(_screens);
        _visualFrames = new OverviewFrameScheduler(ScheduleVisualFrame, ReconcileVisuals);
        _zoneTool = new ZoneCanvasTool(() => _zones != null,
            () => _zones?.Items ?? Array.Empty<ZoneView>(), ZoneMoveMembers);
        _zoneTool.Completed += OnZoneToolCompleted;
        _tools.SetTool(_zoneTool);
        _windows = new OverviewWindowList(mainCanvas, win32);
        _thumbnails = new OverviewThumbnails(_passes, _windows, _camera, _state, _win32, _screens,
            UseCachedOverviewSource, _wm.GetCachedPreviewSource, dwm: dwm, prepareLiveSources: _wm.SetLivePreviewWindows,
            useNativeSource: handle => !_placeholderTitles.ContainsKey(handle),
            transitionSource: GetCardTransitionSource);
        _wm.Previews.Changed += OnPreviewChanged;
        _wm.AutomaticParkingChanged += OnPreviewChanged;

        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        _inputs = new OverviewInputs(this, input, mainCanvas);
        InitializeWindowInteraction();
    }

    public void RefreshConfig()
    {
        if (_inertia.IsActive && !MomentumAllowed) FinishMomentum();
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
            if (p.Grid != null) p.Grid.LowQuality = _appConfig.LowQualityMode;
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
            pass.DpiChanged += (_, _) =>
            {
                if (!_disposed && CurrentMode != OverviewMode.Hidden) ReconcileVisuals();
            };
            pass.OnSettingsRequested = (owner, zoomOnly) => ShowSettings(zoomOnly, owner);
            pass.Deactivate += (_, _) => CloseActiveMenus();
            pass.OnKey = HandleKeyDown;
            pass.OnKeyReleased = HandleKeyUp;
            pass.OnMouseButtonDown = HandleMouseDown;
            pass.OnMouseMoved = HandleMouseMove;
            pass.OnMouseButtonUp = HandleMouseUp;
            pass.OnWheel = HandleMouseWheel;
            pass.OnMouseDoubleClicked = HandleDoubleClick;
            pass.OnCaptureLost = _ =>
            {
                if (_desktopStopSurface == pass)
                {
                    ClearDesktopStoppingClick();
                    FinishMomentum();
                    return;
                }
                if (_tools.Kind == CanvasInteractionKind.Pan) CancelInertia();
                if (_interactionCapture == pass) { EndWindowInteraction(focus: true); return; }
                if (_tools.CapturedSurface != _passes.IndexOf(pass)) return;
                CancelInteraction();
            };
            _passes.Add(pass);
        }
    }

    /// <summary>Sync overview camera to the main canvas and update visuals on all passes.</summary>
    public void SyncCamera()
    {
        if (IsStartupIntroActive) { CancelStartupIntro(); return; }
        if (CurrentMode == OverviewMode.Hidden) return;
        CancelInteractionCore();
        _camera.SyncFrom(_mainCanvas);

        _visualFrames.Request();
    }

    /// <summary>Single entry point for every mode change.</summary>
    public void TransitionTo(OverviewMode target, bool syncCameraOnClose = true, bool zoomOutOnOpen = false)
    {
        if (IsStartupIntroActive && !_openingStartupIntro)
            FinishStartupIntro(refresh: false);
        CancelInertia();

        OverviewMode from = CurrentMode;
        if (target != OverviewMode.Zooming && _tour?.Active==true)
        {
            if (!_tutorialSurfaceTransition) PauseGuidedTutorial();syncCameraOnClose=false;
        }
        if (target != OverviewMode.Zooming) { _tutorialOfferVisible=false;_tourPanel?.Hide(); }
        StopWindowNavigation();
        if (!_state.SetMode(target)) return;

        BeforeModeChanged?.Invoke(from, target);

        if (target == OverviewMode.Hidden)
        {
            HideInternal(syncCamera: syncCameraOnClose && from != OverviewMode.Hidden && !_startupIntroLanded);
            _startupIntroLanded = false;
        }
        else if (from == OverviewMode.Hidden)
            ShowInternal(zoomOutOnOpen);
        else
        {
            if (zoomOutOnOpen) ApplyShortcutZoomOut();
            ApplyConfig();
        }

        AfterModeChanged?.Invoke(from, target);
        TutorialSurfaceChanged(from,target);
    }

    private void ApplyShortcutZoomOut()
    {
        if (!_appConfig.EnableShortcutZoomOut) return;
        _camera.ZoomOutForShortcut(_appConfig.ShortcutZoomOutAmount);
    }

    private void ShowInternal(bool zoomOutOnOpen = false)
    {
        _wm.RefreshPreviewState();
        EnsurePasses();
        WarmupPasses();

        if (_mmcssHandle == IntPtr.Zero)
            _mmcssHandle = Mmcss.Begin("Window Manager");

        _camera.SyncFrom(_mainCanvas);

        _wm.SuspendReconcile = true;

        if (zoomOutOnOpen) ApplyShortcutZoomOut();
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

        OfferTutorialTour();

        foreach (var p in _passes)
            p.Grid?.Start(_camera.X, _camera.Y, _camera.Zoom);
    }

    private void HideInternal(bool syncCamera)
    {
        _taskbarPreviews.SetEnabled(false);
        ClearDesktopStoppingClick();
        RestoreDesktopPanResources();
        CloseSettingsPopup(canvasOnly: true);
        CancelVisualFrame();
        CloseActiveMenus();
        ResetGesturePreviews();
        _tourPanel?.Hide();
        _tutorialReading = false;
        _previewRefreshQueued = false;
        ClearCardTransitions();
        CancelInteraction();
        _annotationCards = Array.Empty<PlaceholderAnnotation>();
        _annotationToolbar?.CloseDropDowns();
        HideAnnotationUi();
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
        _taskbarPreviews.SetEnabled(_appConfig.ShowTaskbarInCanvas);
        UpdateDesktopPanResources();
        _wm.SuspendProjection = true;
        RefreshOverviewWindows();

        // WS_EX_LAYERED is added in Panning and removed in Zooming. Panning
        // is where background-throttle freezes were seen after losing
        // foreground (click lands on an underlying window on drag release);
        // layered compositing seems to keep DWM driving the overlay. Zooming
        // holds foreground itself so it doesn't need the layered cost.
        if (CurrentMode != OverviewMode.Zooming)
        {
            CloseSettingsPopup(canvasOnly: true);
            CancelInteraction();
            _annotationToolbar?.CloseDropDowns();
            HideAnnotationUi();
            foreach (var p in _passes) p.HideZones();
        }
        bool wantLayered = CurrentMode == OverviewMode.Panning || IsStartupIntroActive;
        bool wantNoActivate = CurrentMode == OverviewMode.Panning || _windowInteraction.Active;
        foreach (var p in _passes)
        {
            if (p.Grid != null)
            {
                p.Grid.DrawGrid = _cfg.GridVisible;
                p.Grid.LowQuality = _appConfig.LowQualityMode;
            }
            p.SetTaskbarVisible(_appConfig.ShowTaskbarInCanvas);
            p.SetModeStyle(wantLayered, wantNoActivate);
            // The desktop camera now moves previews only. Own the stopping
            // click so it cannot target a native window at its old position.
            p.SetClickThrough(false);
        }
        ReconcileVisuals();
        if (ShouldShowScreenFixedWindowsDuringPan && AnyPassVisible())
            RaiseMyDockFinderWindowsAboveOverview();
        else if (CurrentMode == OverviewMode.Zooming && AnyPassVisible() && !_composerWaitingForBrowser)
            RaiseOverviewPassesAboveTopmostWindows();

        // Both visible modes own pointer input and render their own camera.
        // Native windows are committed once, under the overlay, when it closes.
        _wm.SuspendProjection = true;
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

    private void HandleKeyDown(OverviewOverlay pass, KeyEventArgs e)
    {
        CancelInertia();
        if (HandleCameraBookmarkKey(pass, e)) return;
        if (TrySkipStartupIntro() || StopTutorialTravel()) { RefreshTourUi();e.SuppressKeyPress = true; e.Handled = true; return; }
        if (!_cfg.InputEnabled) return;
        if (HandleTutorialControlKey(e)) return;

        if (e.KeyCode == Keys.Space && _annotationEditor == null && _composerWindow == null)
        {
            foreach (var p in _passes) p.Cursor = Cursors.Hand;
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }

        if (e.KeyCode == Keys.Escape)
        {
            HandleEscape();
            e.Handled = true;
            return;
        }

        if (_annotationEditor != null || _shapeEditor != null || _composerWindow != null) return;
        if (HandleCanvasHistoryKey(e)) return;
        if (e.KeyCode is Keys.Oemplus or Keys.Add or Keys.OemMinus or Keys.Subtract)
        {
            if (!_tools.Active)
            {
                var center = pass.Screen.Bounds;
                double notches = e.KeyCode is Keys.Oemplus or Keys.Add ? 1 : -1;
                ZoomOverview(center.Left + center.Width / 2, center.Top + center.Height / 2, notches);
            }
            e.SuppressKeyPress = true;
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

    private void HandleKeyUp(OverviewOverlay pass, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space && _annotationEditor == null && _composerWindow == null)
        {
            var pointer = Pointer(pass, new MouseEventArgs(MouseButtons.None, 0, pass.PointToClient(Cursor.Position).X, pass.PointToClient(Cursor.Position).Y, 0));
            pass.Cursor = _tools.CursorAt(pointer) switch
            {
                CanvasCursor.Crosshair => Cursors.Cross,
                CanvasCursor.ResizeNorthWestSouthEast => Cursors.SizeNWSE,
                CanvasCursor.ResizeNorthEastSouthWest => Cursors.SizeNESW,
                CanvasCursor.ResizeNorthSouth => Cursors.SizeNS,
                CanvasCursor.ResizeEastWest => Cursors.SizeWE,
                CanvasCursor.Move => Cursors.SizeAll,
                _ => Cursors.Default
            };
            e.Handled = true;
        }
    }

    public bool CloseActiveMenus()
    {
        bool hadMenu = false;
        if (_activeCardMenu != null && _activeCardMenu.Visible)
        {
            _activeCardMenu.Close();
            _activeCardMenu = null;
            hadMenu = true;
        }
        if (_windowContextMenu != null && _windowContextMenu.Visible)
        {
            _windowContextMenu.Close();
            _windowContextMenu = null;
            hadMenu = true;
        }
        if (_interactionMenu != null && _interactionMenu.Visible)
        {
            _interactionMenu.Close();
            _interactionMenu = null;
            hadMenu = true;
        }
        if (_annotationToolbar != null && _annotationToolbar.HasOpenDropDown)
        {
            _annotationToolbar.CloseDropDowns();
            hadMenu = true;
        }
        return hadMenu;
    }

    private void HandleMouseDown(OverviewOverlay pass, MouseEventArgs e)
    {
        if (CurrentMode == OverviewMode.Panning)
        {
            StopDesktopPanWithClick(pass, e);
            return;
        }
        CancelInertia();
        _visualFrames.Flush();
        if (CloseActiveMenus()) return;
        StopWindowNavigation();
        if (StopTutorialTravel()) RefreshTourUi();
        if (TrySkipStartupIntro()) return;
        if (!_cfg.InputEnabled)
        {
            TransitionTo(OverviewMode.Hidden);
            return;
        }

        if (_annotationEditor != null || _shapeEditor != null || (_composerWindow != null && e.Button == MouseButtons.Left)) return;
        if (RouteCanvasWindowPointer(pass, e, down: true)) return;
        int vx = e.X + pass.OriginX;
        int vy = e.Y + pass.OriginY;

        var pointer = Pointer(pass, e);
        if (e.Button == MouseButtons.Right)
        {
            var (rightWx, rightWy) = _camera.WorldFromVirtual(vx, vy);
            int rightHit = HitOverviewWindow(pass, e.X, e.Y, rightWx, rightWy);
            if (rightHit >= 0 && Control.FromHandle(_windows.Windows[rightHit].HWnd) is PlaceholderForm card)
            {
                PInvoke.SetForegroundWindow((HWND)pass.Handle); _activeCardMenu = card.ContextMenuStrip; card.ShowContextMenu(pass, new Point(e.X, e.Y));
                return;
            }
            else if (rightHit >= 0 && _placeholderWorkspace != null
                && _overviewItemIds.TryGetValue(_windows.Windows[rightHit].HWnd, out var id))
            {
                _windowContextMenu?.Dispose();
                _windowContextMenu = new WorkspaceContextMenu(_placeholderWorkspace, id);
                AddWindowInteractionCommand(_windowContextMenu, pass, _windows.Windows[rightHit].HWnd);
                PInvoke.SetForegroundWindow((HWND)pass.Handle);
                _windowContextMenu.Show(pass, new Point(e.X, e.Y));
                return;
            }
            else if (rightHit >= 0)
            {
                ShowWindowInteractionMenu(pass, _windows.Windows[rightHit].HWnd, new(e.X, e.Y));
                return;
            }

            _tools.TryBeginNavigation(CanvasInteractionKind.Pan, pointer);
            ApplyPointerCapture();
            _panStartVx = vx;
            _panStartVy = vy;
            BeginPan();
            return;
        }

        bool isSpaceDown = (PInvoke.GetAsyncKeyState((int)VIRTUAL_KEY.VK_SPACE) & 0x8000) != 0;
        if (e.Button == MouseButtons.Middle || (e.Button == MouseButtons.Left && isSpaceDown))
        {
            _tools.TryBeginNavigation(CanvasInteractionKind.Pan, pointer);
            ApplyPointerCapture();
            _panStartVx = vx;
            _panStartVy = vy;
            BeginPan();
            return;
        }
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
            BeginPan();
        }
        else if (e.Button == MouseButtons.Middle)
        {
            _tools.TryBeginNavigation(CanvasInteractionKind.Pan, pointer);
            ApplyPointerCapture();
            _panStartVx = vx;
            _panStartVy = vy;
            BeginPan();
        }
    }

    private void HandleMouseMove(OverviewOverlay pass, MouseEventArgs e)
    {
        if (!_cfg.InputEnabled || _annotationEditor != null || _shapeEditor != null) return;
        if (RouteCanvasWindowPointer(pass, e)) return;

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
            _visualFrames.Request();
        }
        bool isSpaceDown = (PInvoke.GetAsyncKeyState((int)VIRTUAL_KEY.VK_SPACE) & 0x8000) != 0;
        pass.Cursor = _tools.Kind == CanvasInteractionKind.Pan || (isSpaceDown && _annotationEditor == null)
            ? Cursors.Hand
            : _tools.CursorAt(pointer) switch
            {
                CanvasCursor.Crosshair => Cursors.Cross,
                CanvasCursor.ResizeNorthWestSouthEast => Cursors.SizeNWSE,
                CanvasCursor.ResizeNorthEastSouthWest => Cursors.SizeNESW,
                CanvasCursor.ResizeNorthSouth => Cursors.SizeNS,
                CanvasCursor.ResizeEastWest => Cursors.SizeWE,
                CanvasCursor.Move => Cursors.SizeAll,
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
            if (dx != 0 || dy != 0) ObserveGestureMotion();
            if(dx!=0 || dy!=0)ObserveTourWindow();
            _visualFrames.Request();
        }
        else if (_tools.Kind == CanvasInteractionKind.Pan)
        {
            int dx = vx - _panStartVx;
            int dy = vy - _panStartVy;
            _panStartVx = vx;
            _panStartVy = vy;

            PanOverview(dx, dy);
            RecordPanDelta(dx, dy);
        }
    }

    private void HandleMouseUp(OverviewOverlay pass, MouseEventArgs e)
    {
        if (CompleteDesktopStoppingClick(pass, e)) return;
        _visualFrames.Flush();
        if (e.Button == MouseButtons.Right && ((_windowContextMenu != null && _windowContextMenu.Visible)
            || (_interactionMenu != null && _interactionMenu.Visible)
            || (_activeCardMenu != null && _activeCardMenu.Visible)))
        {
            return;
        }
        if (CloseActiveMenus()) return;
        if (TrySkipStartupIntro()) return;
        if (RouteCanvasWindowPointer(pass, e, up: true)) return;
        var pointer = Pointer(pass, e);
        if (_tools.Kind == CanvasInteractionKind.Tool)
        {
            try { _tools.CompleteTool(pointer); }
            finally { ApplyPointerCapture(); RefreshToolPreview(); }
            return;
        }
        var ended = _tools.EndNavigation(pointer);
        ApplyPointerCapture();
        if (ended is CanvasInteractionKind.Window or CanvasInteractionKind.Pan) _visualFrames.Request();
        if (ended == CanvasInteractionKind.Pan && !GuidedTutorialActive) ReleaseInertia();
        if (ended == CanvasInteractionKind.Window)
        {
            if (_dragIndex >= 0 && _dragIndex < _windows.Count)
                _placeholderWorkspace?.CommitWindowPlacement(_windows.Windows[_dragIndex].HWnd);
            _wm.Reproject(true);
            _dragIndex = -1;
        }
    }

    private void HandleMouseWheel(OverviewOverlay pass, int x, int y, int delta, bool horizontal, bool control, bool shift)
    {
        if (TrySkipStartupIntro()) return;
        if (!_cfg.InputEnabled || _tools.Active || _annotationEditor != null || _shapeEditor != null) return;
        CancelInertia();
        if (_composerWindow is { WaitingForBrowser: false }) return;
        bool canvasNavigation = control || ((_windowInteraction.Active ? InteractionKeys : Control.ModifierKeys) & Keys.Alt) != 0;
        if (RouteWindowWheel(pass, x, y, delta, horizontal, canvasNavigation)) return;
        if (_windowInteraction.Active && _windowInteraction.Captured) return;
        var (panX, panY, zoomNotches) = OverviewNavigationInput.TranslateWheel(delta, horizontal, control, shift);
        if (zoomNotches != 0) ZoomOverview(x + pass.OriginX, y + pass.OriginY, zoomNotches);
        else PanOverview(panX, panY);
    }

    private void PanOverview(double virtualDx, double virtualDy)
    {
        if (virtualDx == 0 && virtualDy == 0) return;
        StopWindowNavigation();
        StopTutorialTravel();ObserveTourPan(virtualDx,virtualDy);
        _startupIntroLanded = false;
        double worldDx = virtualDx / _camera.Zoom;
        double worldDy = virtualDy / _camera.Zoom;
        _camera.PanByVirtual(virtualDx, virtualDy);
        ObserveGestureMotion();
        _visualPanX += worldDx;
        _visualPanY += worldDy;
        _visualFrames.Request();
    }

    private void ZoomOverview(int virtualX, int virtualY, double notches)
    {
        StopTutorialTravel();
        StopWindowNavigation();
        double before=_camera.Zoom;
        _lastZoomFocalWorld = new CanvasPoint(virtualX / before + _camera.X, virtualY / before + _camera.Y);
        if (!_camera.ZoomToCursor(virtualX, virtualY, notches)) { RefreshTourUi();return; }
        ObserveGestureMotion();
        if(_tour?.ObserveZoom(before,_camera.Zoom)==true)SaveTourProgress();
        _startupIntroLanded = false;
        _visualFrames.Request();
    }

    private void HandleDoubleClick(OverviewOverlay pass, MouseEventArgs e)
    {
        _visualFrames.Flush();
        if (CloseActiveMenus()) return;
        if (TrySkipStartupIntro()) return;
        if (!_cfg.InputEnabled) return;
        if (_windowInteraction.Active) return;
        if (_composerWindow is { WaitingForBrowser: true })
        {
            if (e.Button != MouseButtons.Left || ((_dispatchInteractionKeys ?? Control.ModifierKeys) & (Keys.Shift | Keys.Control)) != 0) return;
            int composerVx = e.X + pass.OriginX;
            int composerVy = e.Y + pass.OriginY;
            var (composerWx, composerWy) = _camera.WorldFromVirtual(composerVx, composerVy);
            int composerHit = HitOverviewWindow(pass, e.X, e.Y, composerWx, composerWy);
            if (composerHit >= 0)
            {
                var entry = _windows.Windows[composerHit];
                BringWindowForward(entry.HWnd, entry.World, center: false);
            }
            return;
        }
        if (_composerWindow != null || ((_dispatchInteractionKeys ?? Control.ModifierKeys) & Keys.Control) != 0) return;
        if (_annotationTool is { Mode: not AnnotationMode.Navigate } annotationTool)
        {
            if (e.Button == MouseButtons.Left && annotationTool.Mode == AnnotationMode.Select
                && annotationTool.Hit(Pointer(pass,e)) is { } note && note.Kind != AnnotationKind.Arrow)
            {
                CancelInteraction(); annotationTool.Select(note.Id);
                if (note.Kind == AnnotationKind.Text)
                    QueueAnnotationTextEdit(note);
                else
                    QueueAnnotationShapeEdit(note);
                return;
            }
            if (annotationTool.Mode!=AnnotationMode.Select) return;
        }
        if (e.Button != MouseButtons.Left || (_tools.ToolHasPendingInteraction && _selectionTool?.HasPendingInteraction != true) || ((_dispatchInteractionKeys ?? Control.ModifierKeys) & (Keys.Shift|Keys.Control)) != 0) return;

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
        ObserveTourWindow();
        if (_placeholderWorkspace?.CardItem(hWnd) is { } itemId)
        {
            TransitionTo(OverviewMode.Hidden, syncCameraOnClose: false);
            _placeholderWorkspace.Open(itemId);
            return;
        }
        BringWindowForward(hWnd, world, center: true);
        TransitionTo(OverviewMode.Hidden, syncCameraOnClose: false);
    }

    private void BringWindowForward(IntPtr hWnd, WorldRect world, bool center)
    {
        if (_mainCanvas.IsCollapsed(hWnd))
            PInvoke.ShowWindow((HWND)hWnd, SHOW_WINDOW_CMD.SW_RESTORE);

        if (center)
        {
            var vs = _screens.VirtualScreen;
            _mainCanvas.CenterOn(world.X, world.Y, world.W, world.H, vs.Width, vs.Height);
        }
        PInvoke.SetForegroundWindow((HWND)hWnd);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _input.WindowDestroyed -= OnInteractionWindowLost;
        _input.WindowMinimized -= OnInteractionWindowLost;
        _input.WindowFocused -= OnInteractionForegroundChanged;
        _interactionMenu?.Dispose();
        _wm.Previews.Changed -= OnPreviewChanged;
        _wm.AutomaticParkingChanged -= OnPreviewChanged;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        if (_zones != null) _zones.Changed -= RefreshZoneAnnotations;
        if (_placeholderWorkspace != null)
        {
            _placeholderWorkspace.Changed -= RefreshPlaceholderTitles;
            _placeholderWorkspace.ZoneLayoutRecorded -= OnZoneLayoutRecorded;
            _placeholderWorkspace.TemporaryWindowsArrived -= OnTemporaryWindowsArrived;
            _placeholderWorkspace.AdditionalPlacementObstacles = null;
        }
        var failures = new List<Exception>();
        void Attempt(Action action) { try { action(); } catch (Exception ex) { failures.Add(ex); } }
        Attempt(_taskbarPreviews.Dispose);
        Attempt(() => _temporaryNotice?.Dispose());
        Attempt(CancelVisualFrame);
        Attempt(() => _windowFlightTimer?.Dispose());
        Attempt(() => CloseSettingsPopup());
        Attempt(ResetGesturePreviews);
        Attempt(() => _gestureSettleTimer?.Dispose());
        Attempt(() => _visualFrameTimer?.Dispose());
        Attempt(DisposeGuidedTutorial);
        Attempt(DisposeAnnotations);
        Attempt(() => FinishStartupIntro(refresh: false));
        Attempt(() =>
        {
            if (CurrentMode != OverviewMode.Hidden)
                TransitionTo(OverviewMode.Hidden, syncCameraOnClose: false);
        });
        Attempt(() => _cameraBookmarkFeedback?.Dispose());
        Attempt(() => { CancelInteraction(); });
        _annotationCards = Array.Empty<PlaceholderAnnotation>();
        _zoneTool.Completed -= OnZoneToolCompleted;
        Attempt(ClearCardTransitions);
        Attempt(() => _cardAnimationTimer?.Dispose());
        Attempt(() => _windowContextMenu?.Dispose());
        Attempt(_inputs.Dispose);
        Attempt(ClearDesktopStoppingClick);
        Attempt(RestoreDesktopPanResources);
        Attempt(CancelInertia);
        Attempt(() => { _momentumTimer?.Dispose(); _momentumTimer = null; });
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
