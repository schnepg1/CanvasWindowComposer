using System;

namespace CanvasDesktop;

/// <summary>
/// Foreground-suppression policy: ignore <see cref="IInputRouter.WindowFocused"/>
/// events that fire shortly after a tracked window vanishes (minimize/destroy)
/// or an overview overlay closes, so the camera doesn't recenter on transient
/// focus blips. When a focused window is genuinely off-screen, recenter the
/// canvas on it.
/// </summary>
internal sealed class ForegroundCoordinator : IDisposable
{
    private const long ForegroundSuppressionMs = 500;

    private readonly Canvas _canvas;
    private readonly IOverviewController _overview;
    private readonly IClock _clock;
    private readonly IScreens _screens;
    private readonly IInputRouter _input;
    private bool _disposed;

    private long _lastWindowLostTick;
    private long _lastOverlayClosedTick;

    public ForegroundCoordinator(
        Canvas canvas,
        IOverviewController overview,
        IInputRouter input,
        IClock clock,
        IScreens screens)
    {
        _canvas = canvas;
        _overview = overview;
        _clock = clock;
        _screens = screens;
        _input = input;
        _input.SetWindowSelectionTarget(_canvas.HasWindow);

        overview.BeforeModeChanged += OnOverviewModeChanged;

        input.WindowDestroyed += OnWindowDestroyed;
        input.WindowFocused   += OnWindowFocused;
        input.WindowMinimized += OnWindowMinimized;
        input.WindowSelected += OnWindowSelected;
    }

    private void OnOverviewModeChanged(OverviewMode from, OverviewMode to)
    {
        if (_disposed) return;
        _input.SetTaskbarNavigationEnabled(to == OverviewMode.Zooming);
        if (to == OverviewMode.Hidden)
            _lastOverlayClosedTick = _clock.TickCount64;
    }

    private void OnWindowMinimized(IntPtr hWnd)
    {
        if (_disposed) return;
        _lastWindowLostTick = _clock.TickCount64;
    }

    private void OnWindowDestroyed(IntPtr hWnd)
    {
        if (_disposed) return;
        _lastWindowLostTick = _clock.TickCount64;
    }

    private void OnWindowFocused(IntPtr hwnd)
    {
        if (_disposed) return;
        if (_overview.CurrentMode != OverviewMode.Hidden)
            return;

        long now = _clock.TickCount64;
        if (now - _lastWindowLostTick    < ForegroundSuppressionMs ||
            now - _lastOverlayClosedTick < ForegroundSuppressionMs)
            return;

        if (_canvas.HasWindow(hwnd))
        {
            var world = _canvas.Windows[hwnd];
            if (world.PinnedToScreen)
                return;

            var r = _canvas.WorldToScreen(world);
            if (!IsOnAnyScreen(r))
            {
                var screen = _screens.PrimaryWorkingArea;
                _canvas.CenterOn(world.X, world.Y, world.W, world.H, screen.Width, screen.Height);
                _canvas.Commit();
            }
        }
    }

    private void OnWindowSelected(IntPtr window)
    {
        if (_disposed || !_input.Enabled || !_canvas.Windows.TryGetValue(window, out var world)
            || world.PinnedToScreen) return;
        // A deliberate Alt+Tab/taskbar choice must survive the short suppression
        // interval used for incidental focus after minimize, close, or overlay exit.
        if (_overview.CurrentMode != OverviewMode.Hidden)
        {
            _overview.NavigateToWindow(window);
            return;
        }
        _overview.CancelInertia();
        var target = WindowNavigationFlight.Target(world, 1, _screens.PrimaryWorkingArea);
        _canvas.LoadState(new CanvasState { CamX = target.X, CamY = target.Y, Zoom = 1,
            Windows = new System.Collections.Generic.Dictionary<IntPtr, WorldRect>(_canvas.Windows) });
        _canvas.Commit();
    }

    private bool IsOnAnyScreen(WindowRect r)
    {
        foreach (var bounds in _screens.AllBounds)
        {
            if (r.X + r.W > bounds.X && r.X < bounds.Right &&
                r.Y + r.H > bounds.Y && r.Y < bounds.Bottom)
                return true;
        }
        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _overview.BeforeModeChanged -= OnOverviewModeChanged;
        _input.WindowDestroyed -= OnWindowDestroyed;
        _input.WindowFocused -= OnWindowFocused;
        _input.WindowMinimized -= OnWindowMinimized;
        _input.WindowSelected -= OnWindowSelected;
        _input.SetTaskbarNavigationEnabled(false);
        _input.SetWindowSelectionTarget(null);
    }
}
