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
    private bool _cameraFlightLandsInDesktop;

    public bool WindowNavigationActive => _windowFlight != null;

    public bool NavigateToWindow(IntPtr window)
    {
        if (!_disposed && _wm.NavigateInventoryWindow(window)) return true;
        if (_disposed
            || !_mainCanvas.Windows.TryGetValue(window, out var world) || world.PinnedToScreen
            || !_win32.IsWindowVisible(window) || _win32.GetWindowProcessId(window) == 0) return false;
        if (_windowFlightTarget == window && WindowNavigationActive) return true;
        if (IsStartupIntroActive) FinishStartupIntro(refresh: false);
        PauseGuidedTutorial();
        CancelInertia();
        CancelSmoothZoom();
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
        _cameraFlightLandsInDesktop = false;
        RefreshOverviewWindows();
        _windowFlightPid = _win32.GetWindowProcessId(window);
        var target = WindowNavigationFlight.Target(world, 1, _screens.PrimaryWorkingArea);
        _windowFlight = new(ViewCamera, target, _screens.PrimaryWorkingArea,
            _appConfig.FlightDurationSeconds, _appConfig.FlightCruisingScale, _appConfig.FlightProfile,
            _appConfig.FlightFullDurationDistance, _appConfig.FlightFullClimbDistance);
        if (!_appConfig.AnimateWindowNavigation || _prefersReducedStartupMotion())
        {
            AdvanceWindowNavigation(_windowFlight.FlightDuration);
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

    public bool NavigateToCamera(CanvasCamera target, bool landInDesktopView = false)
    {
        if (_disposed) return false;
        if (IsStartupIntroActive) FinishStartupIntro(refresh: false);
        PauseGuidedTutorial();
        CancelInertia();
        CancelSmoothZoom();
        CancelInteraction();
        CloseActiveMenus();
        _tutorialReading = false;
        _startupIntroLanded = false;

        _windowFlightTarget = IntPtr.Zero;
        _navigationDisplayTarget = IntPtr.Zero;
        _cameraFlightLandsInDesktop = landInDesktopView;

        if (CurrentMode != OverviewMode.Zooming) TransitionTo(OverviewMode.Zooming);
        RefreshOverviewWindows();

        _windowFlight = new(ViewCamera, target, _screens.PrimaryWorkingArea,
            _appConfig.FlightDurationSeconds, _appConfig.FlightCruisingScale, _appConfig.FlightProfile,
            _appConfig.FlightFullDurationDistance, _appConfig.FlightFullClimbDistance);
        if (!_appConfig.AnimateWindowNavigation || _prefersReducedStartupMotion())
        {
            AdvanceWindowNavigation(_windowFlight.FlightDuration);
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
        if (CurrentMode != OverviewMode.Zooming)
        { StopWindowNavigation(); return; }

        if (_windowFlightTarget != IntPtr.Zero)
        {
            if (!_mainCanvas.HasWindow(_windowFlightTarget)
                || !_win32.IsWindowVisible(_windowFlightTarget)
                || _win32.GetWindowProcessId(_windowFlightTarget) != _windowFlightPid)
            { StopWindowNavigation(); return; }
        }

        double flightDuration = _windowFlight.FlightDuration;
        if (!_appConfig.AnimateWindowNavigation) seconds = flightDuration;
        var frame = _windowFlight.Sample(seconds);
        bool complete = seconds >= flightDuration;
        if (complete) StopWindowNavigation();
        else ObserveGestureMotion();
        _camera.SetTo(frame.X, frame.Y, frame.Zoom);
        ReconcileVisuals();

        if (complete)
        {
            if (_windowFlightTarget != IntPtr.Zero && _mainCanvas.Windows.TryGetValue(_windowFlightTarget, out var world))
            {
                GoToWindow(_windowFlightTarget, world);
            }
            else if (_cameraFlightLandsInDesktop)
            {
                _cameraFlightLandsInDesktop = false;
                TransitionTo(OverviewMode.Hidden, syncCameraOnClose: true);
            }
        }
    }

    private void StopWindowNavigation()
    {
        _windowFlight = null;
        _windowFlightClock.Stop();
        _windowFlightTimer?.Stop();
        if (CurrentMode == OverviewMode.Hidden) _navigationDisplayTarget = IntPtr.Zero;
    }
}
