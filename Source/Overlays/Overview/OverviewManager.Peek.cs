using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace CanvasDesktop;

internal sealed partial class OverviewManager
{
    private readonly CanvasPeekHover _peekHover = new();
    private readonly IDwmThumbnailApi _peekDwm;
    private readonly IWindowInteractionApi _interactionApi;
    private CanvasPeekWindow? _peekWindow;
    private OverviewOverlay? _peekPass;
    private Timer? _peekTimer;
    private IntPtr _peekSource, _peekCard, _peekRoot;
    private uint _peekPid;
    private Rectangle _peekOrigin, _peekTarget;
    private long _peekOpenedAt;
    private long? _peekLeaveAt;
    private bool _peekAnimating, _peekConsumeUp, _peekClosing, _peekEnterPending;
    internal CanvasPeekWindow? PeekWindow => _peekWindow;
    internal bool PeekWorking => _peekWindow?.Working == true;

    private bool PeekToolsReady => !_disposed && _appConfig.EnableCanvasPeek
        && CurrentMode == OverviewMode.Zooming && _cfg.InputEnabled && InteractionToolReady
        && !CanvasInputSuspended && !WindowNavigationActive && !_inertia.IsActive
        && !GesturePreviewsActive && !IsStartupIntroActive && !GuidedTutorialActive
        && _controlsPopup?.Visible != true
        && _backpack?.Panel.IsOpen != true && _temporaryDrawer?.State.IsOpen != true
        && _windowContextMenu?.Visible != true && _interactionMenu?.Visible != true
        && _activeCardMenu?.Visible != true && _annotationToolbar?.HasOpenDropDown != true;

    private void PrepareOverviewLiveSources(IEnumerable<IntPtr> handles)
    {
        // Keep exactly this extra source unclipped while a readable peek is
        // displayed, even if the overview otherwise shows only icon cards.
        var sources = new HashSet<IntPtr>(handles);
        if (_peekSource != IntPtr.Zero && !_wm.UsesCachedPreview(_peekSource)) sources.Add(_peekSource);
        _wm.SetLivePreviewWindows(sources);
    }

    private void ObserveWindowPeek(OverviewOverlay pass, Point screen)
    {
        if (_peekWindow != null || _windowInteraction.Active) return;
        if (!PeekToolsReady || _camera.Zoom >= CanvasPeekLayout.MaximumHoverZoom)
        { ResetPeekHover(); return; }
        var world = _camera.WorldFromVirtual(screen.X, screen.Y);
        int index = HitOverviewWindow(pass, screen.X - pass.OriginX, screen.Y - pass.OriginY, world.x, world.y);
        IntPtr candidate = IntPtr.Zero, source = IntPtr.Zero;
        if (index >= 0 && TryResolvePeekSource(_windows.Windows[index].HWnd, out source, out _))
            candidate = _windows.Windows[index].HWnd;
        bool changed = candidate != _peekHover.Candidate;
        // The dwell itself is the activation: keep this single source awake at
        // info-card zoom so the coming preview can render live. Nothing else
        // changes the zoom tier's global resource policy.
        _wm.SetTransientPreviewWindow(source);
        _peekHover.Observe(candidate, screen, _gestureClock.TickCount64, Math.Max(4, 6 * pass.DeviceDpi / 96));
        if (candidate == IntPtr.Zero)
        {
            if (changed) _visualFrames.Request(); // erase the activation border
            ResetPeekHover();
            return;
        }
        _peekPass = pass;
        EnsurePeekTimer();
        if (changed) _visualFrames.Request();
    }

    /// <summary>Dwell progress for one card while its peek is arming; null outside a dwell.</summary>
    internal double? PeekHoverProgress(IntPtr card, long now)
    {
        if (card == IntPtr.Zero || card != _peekHover.Candidate || !PeekToolsReady
            || _camera.Zoom >= CanvasPeekLayout.MaximumHoverZoom) return null;
        return Math.Clamp((now - _peekHover.Since) / (double)_appConfig.CanvasPeekDelayMs, 0, 1);
    }

    private bool TryResolvePeekSource(IntPtr card, out IntPtr source, out Rectangle origin)
    {
        source = IntPtr.Zero; origin = default;
        if (card == IntPtr.Zero) return false;
        // Saved launchers have no running source: hover must never open them.
        if (_cardSources.TryGetValue(card, out var represented)) source = represented;
        else if (!_placeholderTitles.ContainsKey(card)) source = card;
        if (source == IntPtr.Zero || !_mainCanvas.Windows.TryGetValue(source, out var sourceWorld)
            || sourceWorld.InInventory || !_win32.IsWindowVisible(source) || _win32.GetWindowProcessId(source) == 0) return false;
        foreach (var entry in _windows.Windows)
        {
            if (entry.HWnd != card || entry.ScreenFixed) continue;
            origin = new((int)((entry.World.X - _camera.X) * _camera.Zoom),
                (int)((entry.World.Y - _camera.Y) * _camera.Zoom),
                Math.Max(1, (int)(entry.World.W * _camera.Zoom)), Math.Max(1, (int)(entry.World.H * _camera.Zoom)));
            return origin.Width > 0 && origin.Height > 0;
        }
        return false;
    }

    private Rectangle PeekSourceCrop()
    {
        var native = _win32.GetWindowRect(_peekSource);
        var inset = _win32.GetFrameInset(_peekSource);
        return new(inset.left, inset.top, Math.Max(1, native.w - inset.left - inset.right),
            Math.Max(1, native.h - inset.top - inset.bottom));
    }

    private bool PeekRootCurrent => _peekRoot != IntPtr.Zero && _win32.IsWindowVisible(_peekRoot)
        && _win32.GetWindowProcessId(_peekRoot) == _peekPid
        && _mainCanvas.Windows.TryGetValue(_peekRoot, out var world) && !world.InInventory;

    private bool PeekIdentityCurrent => PeekRootCurrent && _peekSource != IntPtr.Zero && _win32.IsWindowVisible(_peekSource)
        && _win32.GetWindowProcessId(_peekSource) == _peekPid
        && (_peekSource == _peekRoot || _interactionApi.IsOwnedBy(_peekSource, _peekRoot));

    internal bool ShowWindowPeek(OverviewOverlay pass, IntPtr card)
    {
        if (!PeekToolsReady || _windowInteraction.Active || !TryResolvePeekSource(card, out var source, out var origin)) return false;
        CloseWindowPeek();
        _peekSource = _peekRoot = source; _peekCard = card; _peekPass = pass; _peekOrigin = origin;
        _wm.SetTransientPreviewWindow(source);
        _wm.WakeForTransientPreview(source); // Auto-parked sources become live again for this peek.
        _peekPid = _win32.GetWindowProcessId(source);
        var crop = PeekSourceCrop();
        bool live = !_wm.UsesCachedPreview(source) && _interactionApi.IsAvailable(source);
        var area = pass.Screen.WorkingArea;
        _peekTarget = CanvasPeekLayout.Place(origin, crop.Size, area, pass.DeviceDpi);
        _peekAnimating = live && !_appConfig.LowQualityMode && !_appConfig.ReduceStartupMotion && !_prefersReducedStartupMotion();
        _peekOpenedAt = _gestureClock.TickCount64;
        var window = _peekWindow = new CanvasPeekWindow(_peekDwm);
        window.WorkRequested += BeginPeekInteraction;
        window.ReturnRequested += () => CloseWindowPeek(focus: true);
        window.OpenRequested += OpenPeekAtDesktop;
        window.PreviewFailed += () => CloseWindowPeek();
        window.ContentPointer += (e, down, up) =>
        {
            if (_peekWindow != window) return;
            if (!PeekWorking && down && e.Button == MouseButtons.Left && _interactMode && !_peekAnimating)
            {
                _peekConsumeUp = true;
                BeginPeekInteraction();
                _peekConsumeUp = PeekWorking;
                return; // The activation gesture must not also click the source app.
            }
            if (PeekWorking) RoutePeekMouse(new(window.PointToScreen(e.Location), e.Button, down, up, Clicks: e.Clicks,
                Modifiers: WindowInteractionMouseHook.ReadModifiers()));
        };
        try
        {
            _thumbnails.Reconcile(); // Unclip only the requested live source before registering the extra thumbnail.
            var firstFrame = _peekAnimating ? origin : _peekTarget;
            if (!window.ShowPreview(pass, live ? source : IntPtr.Zero, crop, _win32.GetWindowTitle(source), firstFrame, _peekAnimating))
            { CloseWindowPeek(); return false; }
            window.SetWorking(false);
            _peekHover.Reset();
            _peekLeaveAt = null;
            EnsurePeekTimer();
            RefreshToolPreview();
            return true;
        }
        catch (Exception ex)
        {
            TrayApp.Log("Canvas Peek could not open: " + ex.Message);
            CloseWindowPeek();
            return false;
        }
    }

    private void EnsurePeekTimer()
    {
        if (_peekTimer == null)
        {
            _peekTimer = new Timer();
            _peekTimer.Tick += (_, _) => TickWindowPeek();
        }
        _peekTimer.Interval = _peekAnimating ? 16 : 50;
        _peekTimer.Start();
    }

    // Explicit pointer input makes dwell, cancellation and travel deterministic in tests.
    internal void TickWindowPeek(Point? pointer = null)
    {
        if (_disposed || CurrentMode != OverviewMode.Zooming || !_appConfig.EnableCanvasPeek)
        { CloseWindowPeek(); return; }
        var point = pointer ?? Cursor.Position;
        long now = _gestureClock.TickCount64;
        if (_peekWindow is { } window)
        {
            if (RecoverUnavailablePeekSource()) return;
            if (!PeekIdentityCurrent) { CloseWindowPeek(); return; }
            if (window.Live && (_wm.UsesCachedPreview(_peekSource) || !_interactionApi.IsAvailable(_peekSource)))
            { CloseWindowPeek(); return; }
            if (_peekAnimating)
            {
                double progress = (now - _peekOpenedAt) / (double)CanvasPeekLayout.AnimationMs;
                _peekAnimating = progress < 1;
                if (!_peekAnimating && _peekTimer != null) _peekTimer.Interval = 50;
                window.SetFrame(CanvasPeekLayout.Animate(_peekOrigin, _peekTarget, progress), _peekAnimating);
                if (_peekWindow != window) return;
            }
            if (window.Live) window.UpdateSource(PeekSourceCrop());
            if (_peekWindow != window || PeekWorking) return;
            if (!PeekToolsReady) { CloseWindowPeek(); return; }
            // Treat the source and preview as one hover region, with enough
            // grace to cross the gap. Other windows never receive hover through it.
            if (window.Bounds.Contains(point) || _peekOrigin.Contains(point)) _peekLeaveAt = null;
            else
            {
                _peekLeaveAt ??= now;
                if (now - _peekLeaveAt >= CanvasPeekLayout.DismissDelayMs) CloseWindowPeek();
            }
            return;
        }
        if (!PeekToolsReady || _windowInteraction.Active || _peekPass == null)
        { ResetPeekHover(); return; }
        var pass = _peekPass;
        // Do not open over a settings dialog, menu, taskbar or another native app.
        if (pointer == null && PInvoke.WindowFromPoint(point) != (HWND)pass.Handle)
        { ResetPeekHover(); return; }
        ObserveWindowPeek(pass, point);
        if (_peekHover.Ready(now, _appConfig.CanvasPeekDelayMs))
            ShowWindowPeek(pass, _peekHover.Candidate);
        // Drive the card-border activation bar at the dwell poll rate; explicit
        // test pointers keep their deterministic frame behavior.
        else if (_peekHover.Candidate != IntPtr.Zero && pointer == null)
            _visualFrames.Request();
    }

    internal void BeginPeekInteraction()
    {
        var window = _peekWindow;
        var pass = _peekPass;
        if (window == null || pass == null || _peekAnimating || !window.Live || PeekWorking || !PeekIdentityCurrent) return;
        _peekLeaveAt = null;
        window.SetWorking(true);
        _input.DisableEscHotkey(); // Escape, including autocomplete cancellation, belongs to the source app.
        if (!StartWindowInteraction(pass, _peekSource))
        {
            _peekConsumeUp = false;
            if (_peekWindow == window) window.SetWorking(false, _interactionError ?? "Could not focus this app. Choose Open app.");
            if (!_disposed && CurrentMode == OverviewMode.Zooming) _input.EnableEscHotkey();
        }
        else window.RaiseWithoutActivation();
    }

    private bool RoutePeekMouse(InteractionMouseEvent input)
    {
        var window = _peekWindow;
        if (window == null || !PeekWorking) return false;
        if (_peekConsumeUp)
        {
            if (input.Up && input.Button == MouseButtons.Left) _peekConsumeUp = false;
            return true;
        }
        bool captured = _windowInteraction.Captured;
        if (!captured && window.ChromeCaptured)
            return window.RouteChrome(input.Screen, input.Button, input.Down, input.Up);
        if (!captured && !window.Bounds.Contains(input.Screen))
        {
            // Working peeks remain pinned, but canvas clicks explicitly leave
            // interaction first. Consume that click instead of starting a drag.
            if (input.Down && !window.ShowingOwnedDialog) CloseWindowPeek(focus: true);
            else _windowInteraction.Leave();
            return true;
        }
        if (!captured && window.RouteChrome(input.Screen, input.Button, input.Down, input.Up)) return true;
        if (RecoverUnavailablePeekSource()) return true;
        if (!PeekIdentityCurrent) { CloseWindowPeek(); return true; }
        var native = _win32.GetWindowRect(_peekSource);
        var crop = PeekSourceCrop();
        var geometry = new WindowInteractionGeometry(window.RectangleToScreen(window.ContentBounds),
            new(native.x + crop.X, native.y + crop.Y, crop.Width, crop.Height));
        uint modifiers = ((input.Modifiers & Keys.Shift) != 0 ? 4u : 0) | ((input.Modifiers & Keys.Control) != 0 ? 8u : 0);
        if (input.Wheel) _windowInteraction.Wheel(geometry, input.Screen, input.Delta, input.Horizontal, modifiers);
        else _windowInteraction.Pointer(geometry, input.Screen, input.Down || input.Up ? input.Button : MouseButtons.None,
            input.Down, input.Up, input.Down && input.Clicks > 1, modifiers);
        if (!_windowInteraction.Active)
        {
            if (RecoverUnavailablePeekSource()) return true;
            EndWindowInteraction();
            if (_peekWindow == window) window.SetWorking(false, "This app rejected interaction. Choose Open app.");
            FocusOverview();
        }
        return true;
    }

    private void OpenPeekAtDesktop()
    {
        var source = _peekRoot;
        var dialog = _peekSource != _peekRoot ? _peekSource : IntPtr.Zero;
        if (!PeekRootCurrent || !_mainCanvas.Windows.TryGetValue(source, out var world)) { CloseWindowPeek(); return; }
        CloseWindowPeek();
        if ((_win32.GetWindowStyle(source) & (int)WINDOW_STYLE.WS_MINIMIZE) != 0 && !_win32.RestoreForNavigation(source).Accepted) return;
        GoToWindow(source, world);
        if (dialog != IntPtr.Zero) PInvoke.SetForegroundWindow((HWND)dialog);
    }

    private bool TryUsePeekForeground(IntPtr window)
    {
        if (!PeekWorking || !PeekRootCurrent || window == IntPtr.Zero || !_win32.IsWindowVisible(window)
            || _win32.GetWindowProcessId(window) != _peekPid || !_interactionApi.IsAvailable(window)) return false;
        if (window == _peekSource) return true;
        if (window != _peekRoot && !_interactionApi.IsOwnedBy(window, _peekRoot)) return false;
        _peekSource = window;
        if (!_peekWindow!.ChangeSource(window, PeekSourceCrop(), _win32.GetWindowTitle(window), window != _peekRoot)
            || !_windowInteraction.Begin(window))
        {
            // Native dialogs must retain Escape even if their thumbnail or input
            // cannot be hosted. Leave the overlay rather than trapping the dialog.
            HandOffPeekToDesktop();
            return true;
        }
        _peekWindow.RaiseWithoutActivation();
        return true;
    }

    private void HandOffPeekToDesktop()
    {
        CloseWindowPeek();
        TransitionTo(OverviewMode.Hidden, syncCameraOnClose: false);
    }

    private bool RecoverUnavailablePeekSource()
    {
        if (!PeekWorking || (PeekIdentityCurrent && _interactionApi.IsAvailable(_peekSource))) return false;
        if (TryUsePeekForeground(_win32.GetForegroundWindow())) return true;
        if (PeekRootCurrent && (_win32.GetWindowStyle(_peekRoot) & (int)WINDOW_STYLE.WS_MINIMIZE) == 0)
            HandOffPeekToDesktop();
        else CloseWindowPeek();
        return true;
    }

    private void ResetPeekHover()
    {
        bool dwellWasActive = _peekHover.Candidate != IntPtr.Zero;
        _peekHover.Reset();
        if (_peekWindow == null)
        {
            _peekTimer?.Stop();
            _peekPass = null;
            // The hover wake ends with the dwell; an open peek renews its own lease.
            _wm.SetTransientPreviewWindow(IntPtr.Zero);
            if (dwellWasActive) _visualFrames.Request(); // erase the activation border
        }
    }

    private bool CloseWindowPeek(bool focus = false)
    {
        if (_peekClosing) return false;
        _peekClosing = true;
        var window = _peekWindow;
        bool wasWorking = PeekWorking;
        _peekWindow = null;
        _peekSource = _peekCard = _peekRoot = IntPtr.Zero;
        _wm.SetTransientPreviewWindow(IntPtr.Zero);
        _peekPass = null;
        _peekAnimating = _peekConsumeUp = false;
        _peekEnterPending = false;
        _peekLeaveAt = null;
        ResetPeekHover();
        try
        {
            if (wasWorking)
            {
                EndWindowInteraction();
                if (!_disposed && CurrentMode == OverviewMode.Zooming) _input.EnableEscHotkey();
            }
            window?.Dispose();
            if (window != null && !_disposed && CurrentMode != OverviewMode.Hidden)
            {
                _thumbnails.Reconcile();
                RefreshToolPreview();
            }
            if (focus && window != null && !_disposed) FocusOverview();
        }
        finally { _peekClosing = false; }
        return window != null;
    }
}
