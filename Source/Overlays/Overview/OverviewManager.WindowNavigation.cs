using System;
using System.Diagnostics;
using System.Windows.Forms;

namespace CanvasDesktop;

internal sealed partial class OverviewManager
{
    private WindowNavigationFlight? _windowFlight;
    private Timer? _windowFlightTimer;
    private readonly Stopwatch _windowFlightClock = new();
    private IntPtr _windowFlightTarget;
    private IntPtr _navigationDisplayTarget;
    private uint _windowFlightPid;
    internal bool WindowNavigationActive => _windowFlight != null;

    public bool NavigateToWindow(IntPtr window)
    {
        if (_disposed || CurrentMode == OverviewMode.Hidden
            || !_mainCanvas.Windows.TryGetValue(window, out var world) || world.PinnedToScreen
            || !_win32.IsWindowVisible(window) || _win32.GetWindowProcessId(window) == 0) return false;
        if (IsStartupIntroActive) FinishStartupIntro(refresh: false);
        PauseGuidedTutorial();
        CancelInertia();
        CancelInteraction();
        CloseActiveMenus();
        _tutorialReading = false;
        _startupIntroLanded = false;
        if ((_win32.GetWindowStyle(window) & (int)WINDOW_STYLE.WS_MINIMIZE) != 0)
        {
            // A taskbar click on the current app normally minimizes it. Treat
            // this explicit choice as navigation and retain the app's open state.
            if (!_win32.RestoreForNavigation(window).Accepted) return false;
            _mainCanvas.ExpandWindow(window);
            if ((_win32.GetWindowStyle(window) & (int)WINDOW_STYLE.WS_MAXIMIZE) != 0)
                _mainCanvas.MaximizeWindow(window);
        }
        if (CurrentMode != OverviewMode.Zooming) TransitionTo(OverviewMode.Zooming);
        _windowFlightTarget = window;
        _navigationDisplayTarget = window;
        RefreshOverviewWindows();
        _windowFlightPid = _win32.GetWindowProcessId(window);
        var target = WindowNavigationFlight.Target(world, 1, _screens.PrimaryWorkingArea);
        _windowFlight = new(ViewCamera, target);
        if (!_appConfig.AnimateWindowNavigation || _prefersReducedStartupMotion())
        {
            AdvanceWindowNavigation(WindowNavigationFlight.Duration);
            return true;
        }
        _windowFlightTimer ??= new Timer { Interval = 16 };
        _windowFlightTimer.Tick -= OnWindowFlightTick;
        _windowFlightTimer.Tick += OnWindowFlightTick;
        _windowFlightClock.Restart();
        _windowFlightTimer.Start();
        FocusOverview();
        return true;
    }

    private void OnWindowFlightTick(object? sender, EventArgs args)
        => AdvanceWindowNavigation(_windowFlightClock.Elapsed.TotalSeconds);

    internal void AdvanceWindowNavigation(double seconds)
    {
        if (_disposed || _windowFlight == null) return;
        if (CurrentMode != OverviewMode.Zooming || !_mainCanvas.HasWindow(_windowFlightTarget)
            || !_win32.IsWindowVisible(_windowFlightTarget)
            || _win32.GetWindowProcessId(_windowFlightTarget) != _windowFlightPid)
        { StopWindowNavigation(); return; }
        if (!_appConfig.AnimateWindowNavigation) seconds = WindowNavigationFlight.Duration;
        var frame = _windowFlight.Sample(seconds);
        bool complete = seconds >= WindowNavigationFlight.Duration;
        if (complete) StopWindowNavigation();
        else ObserveGestureMotion();
        _camera.SetTo(frame.X, frame.Y, frame.Zoom);
        ReconcileVisuals();
        if (complete && _mainCanvas.Windows.TryGetValue(_windowFlightTarget, out var world))
            GoToWindow(_windowFlightTarget, world);
    }

    private void StopWindowNavigation()
    {
        _windowFlight = null;
        _windowFlightClock.Stop();
        _windowFlightTimer?.Stop();
        if (CurrentMode == OverviewMode.Hidden) _navigationDisplayTarget = IntPtr.Zero;
    }
}
