using System;
using System.Collections.Generic;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;

namespace CanvasDesktop;

/// <summary>
/// All DWM thumbnail state for the overview: desktop wallpaper, taskbars,
/// and per-canvas-window thumbnails. Registered per pass
/// (<see cref="OverviewOverlay"/>); the manager only drives lifecycle
/// (<see cref="Show"/> / <see cref="Hide"/> / <see cref="Reconcile"/>).
///
/// Window thumbnails are throttled: a window is kept registered on a pass
/// iff its screen-space rect intersects that pass's client bounds, so
/// DwmUpdateThumbnailProperties work scales with what's on-screen rather
/// than with the total number of canvas windows.
/// </summary>
internal sealed class OverviewThumbnails
{
    // Desktop opacity floor when fully zoomed in (Zooming mode only). The
    // ramp interpolates between this and the mode's configured DesktopOpacity
    // based on camera zoom.
    private const byte DesktopOpacityZoomedMin = 30;

    private readonly IDwmThumbnailApi _dwm;
    private readonly Func<IntPtr, bool> _useCached;
    private readonly Func<IntPtr, IntPtr> _cachedSource;
    private readonly IReadOnlyList<OverviewOverlay> _passes;
    private readonly OverviewWindowList _windows;
    private readonly OverviewCamera _camera;
    private readonly OverviewState _state;
    private readonly IWindowApi _win32;
    private readonly IScreens _screens;
    private readonly Action<IEnumerable<IntPtr>>? _prepareLiveSources;
    private readonly Func<IntPtr, bool> _useNativeSource;
    private readonly Func<IntPtr, IntPtr> _transitionSource;
    private readonly HashSet<IntPtr> _liveSources = new();
    private bool _simplifyDuringMotion;
    public bool SimplifyDuringMotion
    {
        get => _simplifyDuringMotion;
        set
        {
            if (_simplifyDuringMotion == value) return;
            _simplifyDuringMotion = value;
            InvalidateCameraCache();
        }
    }

    // Shell HWND caches — stable across overview opens, invalidated on
    // display topology change via InvalidateShellCache. Self-healing via
    // IsWindow() guards (explorer.exe restart destroys Progman / WorkerW /
    // Shell_TrayWnd and recreates them with new handles).
    private IntPtr _cachedDesktopWallpaperHwnd;
    private List<IntPtr>? _cachedTaskbarHwnds;

    // Desktop + taskbar per-pass state. Rects are captured once at registration
    // (they don't move during a session) — UpdateDesktop / UpdateTaskbars only
    // push deltas (opacity / visibility) when those actually change.
    private readonly Dictionary<OverviewOverlay, IntPtr> _desktopByPass = new();
    private readonly Dictionary<OverviewOverlay, List<TaskbarEntry>> _taskbarsByPass = new();
    private int _lastDesktopOpacity = -1;
    private bool? _lastTaskbarsVisible;

    // UpdateWindowRects early-out: cache last-pushed camera. Same camera + same
    // world rects => same screen rects, so skip the per-window DWM push loop.
    // Use InvalidateCameraCache on paths that need a fresh push (new entries,
    // re-register, drag-time world-rect update); NaN != anything forces the
    // inequality check.
    private double _lastPushedCamX = double.NaN;
    private double _lastPushedCamY = double.NaN;
    private double _lastPushedZoom = double.NaN;

    private void InvalidateCameraCache()
    {
        _lastPushedCamX = double.NaN;
    }

    private struct TaskbarEntry
    {
        public IntPtr Hwnd;
        public IntPtr Thumb;
    }

    // Window thumbnails: per-pass list in DWM registration order (z-descending).
    // The list IS the authoritative state — its order mirrors what DWM will
    // draw (first-registered at the bottom, last-registered on top). Z-index
    // is implicit in list position; no separate cutoff / sort is needed.
    private struct ActiveEntry
    {
        public IntPtr HWnd;
        public IntPtr Source;
        public IntPtr Thumb;
        public WorldRect World;
        public bool ScreenFixed;
        public bool Ready, Visible, Suppressed;
        public WindowRect? LastDestination;
        // DWM frame inset, cached at registration time. Stable while the entry
        // is in the target list — maximize/unmaximize force a re-register which
        // refreshes this, so the only drift case is custom-chrome apps that
        // toggle frame style without a state change (rare).
        public int InsetL, InsetT, InsetR, InsetB;
    }
    private readonly Dictionary<OverviewOverlay, List<ActiveEntry>> _windowsByPass = new();

    // Scratch target per pass (z-descending), reused across Reconcile calls.
    private readonly record struct TargetEntry(OverviewWindowList.Entry Window, IntPtr Source, bool Cached, bool Visible)
    {
        public IntPtr HWnd => Window.HWnd;
    }
    private readonly Dictionary<OverviewOverlay, List<TargetEntry>> _scratchTargetByPass = new();
    private readonly HashSet<OverviewOverlay> _appendedPasses = new();

    public OverviewThumbnails(
        IReadOnlyList<OverviewOverlay> passes,
        OverviewWindowList windows,
        OverviewCamera camera,
        OverviewState state,
        IWindowApi win32,
        IScreens screens,
        Func<IntPtr, bool>? useCached = null,
        Func<IntPtr, IntPtr>? cachedSource = null,
        IDwmThumbnailApi? dwm = null,
        Action<IEnumerable<IntPtr>>? prepareLiveSources = null,
        Func<IntPtr, bool>? useNativeSource = null,
        Func<IntPtr, IntPtr>? transitionSource = null)
    {
        _passes = passes;
        _windows = windows;
        _camera = camera;
        _state = state;
        _win32 = win32;
        _screens = screens;
        _dwm = dwm ?? new DwmThumbnailApi();
        _useCached = useCached ?? (_ => false);
        _cachedSource = cachedSource ?? (_ => IntPtr.Zero);
        _prepareLiveSources = prepareLiveSources;
        _useNativeSource = useNativeSource ?? (_ => true);
        _transitionSource = transitionSource ?? (_ => IntPtr.Zero);
    }

    /// <summary>Register desktop + taskbar thumbnails on every pass. Window thumbnails are registered lazily by <see cref="Reconcile"/>.</summary>
    public void Show()
    {
        foreach (var pass in _passes)
        {
            RegisterDesktop(pass);
            RegisterTaskbars(pass);
        }
    }

    /// <summary>Unregister everything. Call when the overview closes.</summary>
    public void Hide()
    {
        _liveSources.Clear();
        _prepareLiveSources?.Invoke(_liveSources);
        foreach (var kv in _windowsByPass)
        {
            foreach (var entry in kv.Value)
                _dwm.Unregister(entry.Thumb);
        }
        _windowsByPass.Clear();

        foreach (var pass in _passes)
        {
            UnregisterTaskbars(pass);
            UnregisterDesktop(pass);
        }
    }

    /// <summary>
    /// Reconcile the window-thumbnail set against the current camera +
    /// window list, then update props on desktop, taskbar, and window
    /// thumbnails. Call after any camera / mode / window-list change.
    /// </summary>
    public void Reconcile(CanvasCamera? view = null)
    {
        var camera = view ?? new CanvasCamera(_camera.X, _camera.Y, _camera.Zoom);
        UpdateDesktop(camera.Zoom);

        ComputeTarget(camera);
        _liveSources.Clear();
        foreach (var target in _scratchTargetByPass.Values)
            foreach (var entry in target)
                if (entry.Visible && !entry.Cached && entry.Source != IntPtr.Zero) _liveSources.Add(entry.HWnd);
        _prepareLiveSources?.Invoke(_liveSources);
        _appendedPasses.Clear();
        RebuildWindows();

        // Taskbars must sit on top of window thumbnails. DWM stacks in
        // registration order, so any window append lands above the taskbars
        // until we cycle them back to the top.
        foreach (var pass in _appendedPasses) CycleTaskbarsOnTop(pass);

        UpdateTaskbars();

        UpdateWindowRects(camera);
    }

    /// <summary>
    /// Re-register a window's thumbnail so it lands at the top of DWM's
    /// registration-order z-stack. No-op on passes where the window isn't
    /// currently active.
    /// </summary>
    public void BringToFront(IntPtr hWnd)
    {
        bool moved = false;
        foreach (var pass in _passes)
        {
            if (!_windowsByPass.TryGetValue(pass, out var list)) continue;
            int idx = IndexOfHWnd(list, hWnd);
            if (idx < 0 || idx == list.Count - 1) continue;

            var entry = list[idx];
            _dwm.Unregister(entry.Thumb);
            list.RemoveAt(idx);

            HRESULT hr = _dwm.Register((HWND)pass.Handle, (HWND)entry.Source, out nint newThumb);
            if (hr.Succeeded)
            {
                entry.Thumb = newThumb;
                entry.Ready = entry.Visible = false;
                list.Add(entry);
                moved = true;
                CycleTaskbarsOnTop(pass);
            }
        }

        // Re-registered window now sits above taskbars — cycle them. The fresh
        // thumb also needs its rect re-pushed on the next UpdateWindowRects.
        if (moved)
        {
            InvalidateCameraCache();
            UpdateTaskbars();
        }
    }

    private void CycleTaskbarsOnTop(OverviewOverlay pass)
    {
        UnregisterTaskbars(pass);
        RegisterTaskbars(pass);
    }

    /// <summary>Sync the stored world rect for a window during a drag.</summary>
    public void UpdateWorldRect(IntPtr hWnd, WorldRect world)
    {
        foreach (var kv in _windowsByPass)
        {
            var list = kv.Value;
            int idx = IndexOfHWnd(list, hWnd);
            if (idx < 0) continue;
            var entry = list[idx];
            entry.World = world;
            list[idx] = entry;
        }
        InvalidateCameraCache();
    }

    /// <summary>Forget cached shell HWNDs — next <see cref="Show"/> re-enumerates.</summary>
    public void InvalidateShellCache()
    {
        _cachedDesktopWallpaperHwnd = IntPtr.Zero;
        _cachedTaskbarHwnds = null;
    }

    private static int IndexOfHWnd(List<ActiveEntry> list, IntPtr hWnd)
    {
        for (int i = 0; i < list.Count; i++)
            if (list[i].HWnd == hWnd) return i;
        return -1;
    }

    // ==================== DESKTOP ====================

    private void RegisterDesktop(OverviewOverlay pass)
    {
        IntPtr desktopWnd = GetDesktopWallpaperHwnd();
        if (desktopWnd == IntPtr.Zero) return;

        HRESULT hr = _dwm.Register((HWND)pass.Handle, (HWND)desktopWnd, out nint thumb);
        if (hr.Failed) { _desktopByPass[pass] = IntPtr.Zero; return; }

        // WorkerW spans the entire virtual screen. Push this pass's slice once
        // at register time — neither source nor dest moves during a session;
        // opacity is the only thing UpdateDesktop pushes per frame.
        var b = pass.Screen.Bounds;
        var vs = _screens.VirtualScreen;
        var props = new DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = PInvoke.DWM_TNP_RECTDESTINATION | PInvoke.DWM_TNP_RECTSOURCE | PInvoke.DWM_TNP_VISIBLE,
            rcDestination = new RECT { left = 0, top = 0, right = b.Width, bottom = b.Height },
            rcSource = new RECT
            {
                left   = b.X - vs.X,
                top    = b.Y - vs.Y,
                right  = b.X - vs.X + b.Width,
                bottom = b.Y - vs.Y + b.Height
            },
            fVisible = true
        };
        _dwm.Update(thumb, props);

        _desktopByPass[pass] = thumb;
        _lastDesktopOpacity = -1; // force opacity push on the next UpdateDesktop
    }

    private void UnregisterDesktop(OverviewOverlay pass)
    {
        if (_desktopByPass.TryGetValue(pass, out var thumb) && thumb != IntPtr.Zero)
            _dwm.Unregister(thumb);
        _desktopByPass.Remove(pass);
    }

    /// <summary>
    /// Push desktop opacity to every pass if it changed since the last push.
    /// Opacity is the same for all passes (driven by mode + camera zoom), so
    /// we track a single "last applied" value and fan out on change.
    /// </summary>
    private void UpdateDesktop(double zoom)
    {
        var cfg = _state.CurrentConfig;
        byte opacity = cfg.DesktopOpacity;
        if (_state.CurrentMode == OverviewMode.Zooming)
        {
            double t = (zoom - OverviewCamera.ZoomMin) / (OverviewCamera.ZoomMax - OverviewCamera.ZoomMin);
            t = Math.Clamp(t, 0.0, 1.0);
            double min = DesktopOpacityZoomedMin;
            double max = cfg.DesktopOpacity;
            opacity = (byte)(min + (max - min) * t);
        }

        if (opacity == _lastDesktopOpacity) return;
        _lastDesktopOpacity = opacity;

        var props = new DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = PInvoke.DWM_TNP_OPACITY,
            opacity = opacity
        };
        foreach (var kv in _desktopByPass)
        {
            if (kv.Value != IntPtr.Zero)
                _dwm.Update(kv.Value, props);
        }
    }

    private IntPtr GetDesktopWallpaperHwnd()
    {
        if (_cachedDesktopWallpaperHwnd == IntPtr.Zero
            || !PInvoke.IsWindow((HWND)_cachedDesktopWallpaperHwnd))
        {
            _cachedDesktopWallpaperHwnd = FindDesktopWallpaperWindow();
        }
        return _cachedDesktopWallpaperHwnd;
    }

    private static IntPtr FindDesktopWallpaperWindow()
    {
        HWND progman = PInvoke.FindWindow("Progman", null);
        if (progman == HWND.Null) return IntPtr.Zero;

        PInvoke.SendMessage(progman, 0x052C, 0, 0);

        HWND workerW = HWND.Null;
        WNDENUMPROC proc = (HWND hWnd, LPARAM _) =>
        {
            HWND shell = PInvoke.FindWindowEx(hWnd, HWND.Null, "SHELLDLL_DefView", null);
            if (shell != HWND.Null)
                workerW = PInvoke.FindWindowEx(HWND.Null, hWnd, "WorkerW", null);
            return true;
        };
        PInvoke.EnumWindows(proc, 0);
        GC.KeepAlive(proc);

        return workerW != HWND.Null ? workerW : progman;
    }

    // ==================== TASKBARS ====================

    private void RegisterTaskbars(OverviewOverlay pass)
    {
        var list = new List<TaskbarEntry>();
        foreach (var hwnd in GetTaskbarHwnds())
        {
            HRESULT hr = _dwm.Register((HWND)pass.Handle, (HWND)hwnd, out nint thumb);
            if (hr.Failed) continue;

            // Taskbar position is fixed; push rect once here. Visibility is
            // driven per-config via UpdateTaskbars.
            PInvoke.GetWindowRect((HWND)hwnd, out RECT r);
            var props = new DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = PInvoke.DWM_TNP_RECTDESTINATION | PInvoke.DWM_TNP_VISIBLE,
                fVisible = _state.CurrentConfig.TaskbarVisible,
                rcDestination = new RECT
                {
                    left   = r.left   - pass.OriginX,
                    top    = r.top    - pass.OriginY,
                    right  = r.right  - pass.OriginX,
                    bottom = r.bottom - pass.OriginY
                }
            };
            _dwm.Update(thumb, props);

            list.Add(new TaskbarEntry { Hwnd = hwnd, Thumb = thumb });
        }
        _taskbarsByPass[pass] = list;
    }

    private void UnregisterTaskbars(OverviewOverlay pass)
    {
        if (!_taskbarsByPass.TryGetValue(pass, out var list)) return;
        foreach (var entry in list)
            _dwm.Unregister(entry.Thumb);
        _taskbarsByPass.Remove(pass);
    }

    /// <summary>
    /// Push taskbar visibility if it changed since the last push. Visibility
    /// applies uniformly to every pass's taskbars, so a single tracking flag
    /// gates fan-out.
    /// </summary>
    private void UpdateTaskbars()
    {
        bool visible = _state.CurrentConfig.TaskbarVisible;
        if (_lastTaskbarsVisible == visible) return;
        _lastTaskbarsVisible = visible;

        var props = new DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = PInvoke.DWM_TNP_VISIBLE,
            fVisible = visible
        };
        foreach (var kv in _taskbarsByPass)
        {
            foreach (var entry in kv.Value)
                _dwm.Update(entry.Thumb, props);
        }
    }

    private List<IntPtr> GetTaskbarHwnds()
    {
        if (_cachedTaskbarHwnds == null || !AllAlive(_cachedTaskbarHwnds))
            _cachedTaskbarHwnds = EnumerateTaskbarHwnds();
        return _cachedTaskbarHwnds;
    }

    private static bool AllAlive(List<IntPtr> hwnds)
    {
        foreach (var h in hwnds)
        {
            if (!PInvoke.IsWindow((HWND)h)) return false;
        }
        return true;
    }

    private static unsafe List<IntPtr> EnumerateTaskbarHwnds()
    {
        var result = new List<IntPtr>();

        HWND primary = PInvoke.FindWindow("Shell_TrayWnd", null);
        if (primary != HWND.Null) result.Add(primary);

        WNDENUMPROC proc = (HWND hWnd, LPARAM _) =>
        {
            Span<char> buf = stackalloc char[64];
            int len;
            fixed (char* p = buf)
            {
                len = PInvoke.GetClassName(hWnd, new PWSTR(p), buf.Length);
            }
            if (len > 0 && new string(buf[..len]) == "Shell_SecondaryTrayWnd")
                result.Add(hWnd);
            return true;
        };
        PInvoke.EnumWindows(proc, 0);
        GC.KeepAlive(proc);

        return result;
    }

    // ==================== WINDOWS ====================

    /// <summary>
    /// Fill <see cref="_scratchTargetByPass"/> with the windows that should
    /// be registered on each pass, in z-descending order (bottom-most first,
    /// topmost last — matching DWM registration order for correct stacking).
    /// </summary>
    private void ComputeTarget(CanvasCamera camera)
    {
        foreach (var kv in _scratchTargetByPass)
            kv.Value.Clear();

        double zoom = camera.Zoom;
        double camX = camera.X;
        double camY = camera.Y;

        // _windows is z-ascending (index 0 = topmost). Build per-pass lists
        // in that order, then reverse once at the end to flip to z-descending.
        for (int i = 0; i < _windows.Count; i++)
        {
            var entry = _windows.Windows[i];
            if (!_useNativeSource(entry.HWnd))
            {
                var underlying = _transitionSource(entry.HWnd);
                if (underlying == IntPtr.Zero) continue;
                entry = entry with { HWnd = underlying }; // Preserve the proxy's geometry and z-order during its fade.
            }

            // A bounded, previously captured image can remain a DWM source.
            // Uncached live windows are represented by annotation cards instead.
            // Pinned/screen-fixed windows remain live because they are not moving.
            bool cached = _useCached(entry.HWnd);
            var source = cached ? _cachedSource(entry.HWnd) : entry.HWnd;
            bool visible = !SimplifyDuringMotion || entry.ScreenFixed || cached;
            var target = new TargetEntry(entry, source, cached, visible);

            var screen = GetScreenRect(entry, camX, camY, zoom);
            int right = screen.X + screen.W;
            int bottom = screen.Y + screen.H;

            foreach (var pass in _passes)
            {
                var bounds = pass.Screen.Bounds;
                if (right <= bounds.Left || screen.X >= bounds.Right ||
                    bottom <= bounds.Top || screen.Y >= bounds.Bottom) continue;

                if (!_scratchTargetByPass.TryGetValue(pass, out var list))
                {
                    list = new List<TargetEntry>();
                    _scratchTargetByPass[pass] = list;
                }
                list.Add(target);
            }
        }

        foreach (var kv in _scratchTargetByPass)
            kv.Value.Reverse();
    }

    // DWM only appends at the top of its registration stack. Preserve the
    // matching target prefix, and rebuild the suffix only AFTER a new registration
    // succeeds. An unavailable lower source must not churn healthy higher windows.
    private void RebuildWindows()
    {
        foreach (var pass in _passes)
        {
            _scratchTargetByPass.TryGetValue(pass, out var target);
            int targetCount = target?.Count ?? 0;
            if (!_windowsByPass.TryGetValue(pass, out var current))
            {
                if (targetCount == 0) continue;
                current = new List<ActiveEntry>();
                _windowsByPass[pass] = current;
            }

            int write = 0;
            for (int k = 0; k < targetCount; k++)
            {
                var cand = target![k];
                if (cand.Source == IntPtr.Zero) continue;
                int match = write;
                while (match < current.Count && (current[match].HWnd != cand.HWnd || current[match].Source != cand.Source)) match++;
                if (match < current.Count)
                {
                    RemoveEntries(current, write, match - write);
                    var entry = current[write];
                    if (!entry.World.Equals(cand.Window.World) || entry.ScreenFixed != cand.Window.ScreenFixed
                        || entry.Suppressed != !cand.Visible || (cand.Visible && !entry.Visible)) InvalidateCameraCache();
                    entry.World = cand.Window.World;
                    entry.ScreenFixed = cand.Window.ScreenFixed;
                    bool wasSuppressed = entry.Suppressed;
                    entry.Suppressed = !cand.Visible;
                    if (!cand.Visible && (!wasSuppressed || entry.Visible))
                    {
                        var hidden = new DWM_THUMBNAIL_PROPERTIES { dwFlags = PInvoke.DWM_TNP_VISIBLE, fVisible = false };
                        if (_dwm.Update(entry.Thumb, hidden).Failed)
                        {
                            // If hiding fails, release it rather than leaving live
                            // content behind the gesture's lightweight card.
                            RemoveEntries(current, write, 1);
                            continue;
                        }
                        entry.Visible = false;
                    }
                    current[write++] = entry;
                    continue;
                }

                // Retain already-registered, still-visible geometry while hidden,
                // but never register a new uncached source just for a gesture.
                if (!cand.Visible) continue;
                HRESULT hr = _dwm.Register((HWND)pass.Handle, (HWND)cand.Source, out nint thumb);
                if (hr.Failed) continue;
                RemoveEntries(current, write, current.Count - write);
                var (iL, iT, iR, iB) = cand.Source == cand.HWnd ? _win32.GetFrameInset(cand.HWnd) : (0, 0, 0, 0);
                current.Add(new ActiveEntry
                {
                    HWnd = cand.HWnd, Source = cand.Source, Thumb = thumb, World = cand.Window.World,
                    ScreenFixed = cand.Window.ScreenFixed,
                    InsetL = iL, InsetT = iT, InsetR = iR, InsetB = iB
                });
                write++;
                InvalidateCameraCache();
                _appendedPasses.Add(pass);
            }
            // Off-screen, removed and replaced sources are released immediately,
            // including hidden entries. Retention never grows beyond the view.
            RemoveEntries(current, write, current.Count - write);
        }
    }

    private void RemoveEntries(List<ActiveEntry> list, int start, int count)
    {
        for (int i = start; i < start + count; i++) _dwm.Unregister(list[i].Thumb);
        if (count > 0) list.RemoveRange(start, count);
    }

    public bool HasLiveSource(IntPtr handle) => HasReadySource(handle, requireLive: true);

    public bool HasVisibleSource(IntPtr handle) => HasReadySource(handle, requireLive: false);

    private bool HasReadySource(IntPtr handle, bool requireLive)
    {
        bool found = false;
        foreach (var pair in _scratchTargetByPass)
        {
            if (!pair.Value.Exists(e => e.HWnd == handle && e.Visible)) continue;
            found = true;
            if (!_windowsByPass.TryGetValue(pair.Key, out var current)
                || !current.Exists(e => e.HWnd == handle && (!requireLive || e.Source == handle) && e.Ready && e.Visible && !e.Suppressed)) return false;
        }
        return found;
    }

    public bool TryGetInteractionGeometry(OverviewOverlay pass, IntPtr handle, out WindowInteractionGeometry geometry)
    {
        geometry = default;
        if (!_windowsByPass.TryGetValue(pass, out var entries)) return false;
        foreach (var entry in entries)
        {
            if (entry.HWnd != handle || entry.Source != handle || !entry.Ready) continue;
            var screen = GetScreenRect(entry, _camera.X, _camera.Y, _camera.Zoom);
            double scale = entry.ScreenFixed ? 1 : _camera.Zoom;
            int left = (int)(entry.InsetL * scale), top = (int)(entry.InsetT * scale);
            int right = (int)(entry.InsetR * scale), bottom = (int)(entry.InsetB * scale);
            var native = _win32.GetWindowRect(handle);
            geometry = new(new(screen.X + left, screen.Y + top, screen.W - left - right, screen.H - top - bottom),
                new(native.x + entry.InsetL, native.y + entry.InsetT,
                    native.w - entry.InsetL - entry.InsetR, native.h - entry.InsetT - entry.InsetB));
            return geometry.Destination.Width > 0 && geometry.Destination.Height > 0
                && geometry.Source.Width > 0 && geometry.Source.Height > 0;
        }
        return false;
    }

    private IntPtr ResolveSource(IntPtr handle) => _useCached(handle) ? _cachedSource(handle) : handle;
    // A destroyed source HWND may be recycled for a new cache surface before the
    // queued reconciliation. Force re-registration even when its numeric value matches.
    public void InvalidateCachedSources()
    {
        foreach (var list in _windowsByPass.Values)
            for (int i = 0; i < list.Count; i++)
            {
                var entry = list[i];
                if (entry.Source == entry.HWnd) continue;
                entry.Source = IntPtr.Zero;
                list[i] = entry;
            }
    }

    private void UpdateWindowRects(CanvasCamera camera)
    {
        double zoom = camera.Zoom;
        double camX = camera.X;
        double camY = camera.Y;

        if (camX == _lastPushedCamX && camY == _lastPushedCamY && zoom == _lastPushedZoom)
            return;
        _lastPushedCamX = camX;
        _lastPushedCamY = camY;
        _lastPushedZoom = zoom;

        foreach (var kv in _windowsByPass)
        {
            var pass = kv.Key;
            var list = kv.Value;

            for (int i = 0; i < list.Count; i++)
            {
                var entry = list[i];
                if (entry.Suppressed) continue;
                var screen = GetScreenRect(entry, camX, camY, zoom);

                int fL = entry.ScreenFixed ? entry.InsetL : (int)(entry.InsetL * zoom);
                int fT = entry.ScreenFixed ? entry.InsetT : (int)(entry.InsetT * zoom);
                int fR = entry.ScreenFixed ? entry.InsetR : (int)(entry.InsetR * zoom);
                int fB = entry.ScreenFixed ? entry.InsetB : (int)(entry.InsetB * zoom);

                int left   = screen.X + fL - pass.OriginX;
                int top    = screen.Y + fT - pass.OriginY;
                int right  = screen.X + screen.W - fR - pass.OriginX;
                int bottom = screen.Y + screen.H - fB - pass.OriginY;

                // A drag invalidates geometry, but stationary neighbours still
                // have the same integer destination. Likewise, subpixel camera
                // changes do not require another native thumbnail update.
                var destination = new WindowRect(left, top, right - left, bottom - top);
                if (entry.Ready && entry.Visible && entry.LastDestination == destination) continue;

                var props = new DWM_THUMBNAIL_PROPERTIES
                {
                    dwFlags = PInvoke.DWM_TNP_RECTDESTINATION | PInvoke.DWM_TNP_VISIBLE,
                    fVisible = true,
                    rcDestination = new RECT { left = left, top = top, right = right, bottom = bottom }
                };
                entry.Ready = _dwm.Update(entry.Thumb, props).Succeeded;
                if (entry.Ready) { entry.LastDestination = destination; entry.Visible = true; }
                list[i] = entry;
                if (!entry.Ready) InvalidateCameraCache();
            }
        }
    }

    private WindowRect GetScreenRect(OverviewWindowList.Entry entry, double camX, double camY, double zoom)
    {
        if (entry.ScreenFixed)
        {
            var (x, y, w, h) = _win32.GetWindowRect(entry.HWnd);
            return new WindowRect(x, y, Math.Max(1, w), Math.Max(1, h));
        }

        return ProjectWorldToScreen(entry.World, camX, camY, zoom);
    }

    private WindowRect GetScreenRect(ActiveEntry entry, double camX, double camY, double zoom)
    {
        if (entry.ScreenFixed)
        {
            var (x, y, w, h) = _win32.GetWindowRect(entry.HWnd);
            return new WindowRect(x, y, Math.Max(1, w), Math.Max(1, h));
        }

        return ProjectWorldToScreen(entry.World, camX, camY, zoom);
    }

    private static WindowRect ProjectWorldToScreen(WorldRect world, double camX, double camY, double zoom)
    {
        return new WindowRect(
            (int)((world.X - camX) * zoom),
            (int)((world.Y - camY) * zoom),
            Math.Max(1, (int)(world.W * zoom)),
            Math.Max(1, (int)(world.H * zoom)));
    }
}
