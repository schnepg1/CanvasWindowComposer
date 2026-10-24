using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows.Forms;

namespace CanvasDesktop;

internal sealed partial class OverviewManager
{
    private readonly Func<bool> _prefersReducedStartupMotion;
    private StartupIntroFlight? _startupFlight;
    private StartupIntroLanding? _startupLanding;
    private double _startupIntroElapsed, _startupLandingStartedAt;
    private StartupIntroFrame? _startupIntroFrame;
    private readonly Stopwatch _startupIntroClock = new();
    private Timer? _startupIntroTimer, _startupIntroInputCleanupTimer;
    private StartupIntroInputFilter? _startupIntroFilter;
    private bool _startupIntroAttempted, _openingStartupIntro, _parkingWasSuspended;
    private bool _startupIntroLanded;
    public bool StartupIntroInputSuppressed => _startupIntroFilter != null && !IsStartupIntroActive;
    internal bool IsStartupIntroActive => _startupFlight != null;
    internal double StartupIntroDurationSeconds => _startupFlight?.DurationSeconds ?? 0;

    public bool TryStartStartupIntro()
    {
        if (_disposed || _startupIntroAttempted) return false;
        _startupIntroAttempted = true;
        if (!_appConfig.EnableStartupIntro || !_input.Enabled || CurrentMode != OverviewMode.Hidden) return false;
        try
        {
            Warmup();
            if (_passes.Count == 0 || _passes.Exists(p => p.Grid == null)) return false;
            var nearby = new List<WorldRect>(_mainCanvas.Windows.Values);
            var regions = new List<WorldRect>();
            if (_zones != null)
                foreach (var zone in _zones.Items) regions.Add(zone.Bounds);
            bool reduced = _appConfig.ReduceStartupMotion || _prefersReducedStartupMotion();
            _tutorialIntroLanding = PrepareStartupTutorial();
            _startupFlight = new StartupIntroFlight(_mainCanvas.Camera, _screens.PrimaryBounds,
                nearby, reduced, _tutorialIntroLanding, regions);
            _startupIntroElapsed = 0;
            _startupLanding = null;
            _startupIntroFrame = _startupFlight.Sample(0);
            _parkingWasSuspended = _wm.SuspendAutomaticParking;
            _wm.SuspendAutomaticParking = true;
            _startupIntroFilter = new StartupIntroInputFilter(TrySkipStartupIntro);
            Application.AddMessageFilter(_startupIntroFilter);
            _openingStartupIntro = true;
            try { TransitionTo(OverviewMode.Zooming); }
            finally { _openingStartupIntro = false; }
            _startupIntroTimer = new Timer { Interval = 16 };
            _startupIntroTimer.Tick += OnStartupIntroTick;
            _startupIntroClock.Restart();
            _startupIntroTimer.Start();
            return true;
        }
        catch (Exception ex) { FailStartupIntro(ex); return false; }
    }

    private void OnStartupIntroTick(object? sender, EventArgs e)
        => AdvanceStartupIntro(_startupIntroClock.Elapsed.TotalSeconds);

    internal void AdvanceStartupIntro(double elapsedSeconds)
    {
        if (_disposed || _startupFlight == null) return;
        try
        {
            _startupIntroElapsed = elapsedSeconds;
            var frame = _startupLanding?.Sample(elapsedSeconds - _startupLandingStartedAt)
                ?? _startupFlight.Sample(elapsedSeconds);
            _startupIntroFrame = frame;
            if (frame.Complete)
            {
                FinishStartupIntro(refresh: true, keepInputFilter: _startupLanding != null);
                return;
            }
            _camera.SetTo(frame.X, frame.Y, frame.Zoom);
            foreach (var pass in _passes)
            {
                pass.SetIntroOpacity(frame.RevealOpacity);
            }
            ReconcileVisuals();
        }
        catch (Exception ex) { FailStartupIntro(ex); }
    }

    public bool TrySkipStartupIntro()
    {
        if (!IsStartupIntroActive) return false;
        if (_startupLanding != null) return true; // Repeats must not restart the landing.
        _startupIntroFilter?.CaptureHeldInput();
        try
        {
            _startupLanding = _startupFlight!.CreateLanding(_startupIntroElapsed);
            _startupLandingStartedAt = _startupIntroElapsed;
            AdvanceStartupIntro(_startupIntroElapsed); // Reduced motion lands immediately.
            if (_startupIntroFilter != null)
            {
                _startupIntroInputCleanupTimer = new Timer { Interval = 50 };
                _startupIntroInputCleanupTimer.Tick += (_, _) =>
                {
                    if (_disposed || (!IsStartupIntroActive && _startupIntroFilter?.HasPendingGesture != true))
                        ClearStartupIntroInputFilter();
                };
                _startupIntroInputCleanupTimer.Start();
            }
        }
        catch (Exception ex) { FailStartupIntro(ex); }
        return true;
    }

    public void CancelStartupIntro()
    {
        if (!IsStartupIntroActive) return;
        FinishStartupIntro(refresh: false);
        TransitionTo(OverviewMode.Hidden, syncCameraOnClose: false);
    }

    private void ClearStartupIntroInputFilter()
    {
        var timer = _startupIntroInputCleanupTimer; _startupIntroInputCleanupTimer = null;
        timer?.Stop(); timer?.Dispose();
        if (_startupIntroFilter == null) return;
        Application.RemoveMessageFilter(_startupIntroFilter);
        _startupIntroFilter = null;
    }

    private void FinishStartupIntro(bool refresh, bool keepInputFilter = false)
    {
        if (_startupFlight == null)
        {
            if (!keepInputFilter) ClearStartupIntroInputFilter();
            return;
        }
        var tutorialLanding = _tutorialIntroLanding;
        _tutorialIntroLanding = null;
        _startupFlight = null;
        _startupLanding = null;
        _startupIntroFrame = null;
        _startupIntroClock.Stop();
        var timer = _startupIntroTimer; _startupIntroTimer = null;
        timer?.Stop(); timer?.Dispose();
        if (!keepInputFilter) ClearStartupIntroInputFilter();
        // Release the policy guard before any native/visual cleanup can fail.
        _wm.SuspendAutomaticParking = _parkingWasSuspended;
        _camera.SyncFrom(_mainCanvas); // The persistent desktop camera never follows the flight.
        _startupIntroLanded = true;
        foreach (var pass in _passes)
        {
            if (pass.IsDisposed) continue;
            pass.SetIntroOpacity(1);
            pass.SetModeStyle(CurrentMode == OverviewMode.Panning, CurrentMode == OverviewMode.Panning);
        }
        if (refresh && !_disposed && CurrentMode != OverviewMode.Hidden)
        {
            if (tutorialLanding is {} landing) LandOnTutorial(landing);
            else ReconcileVisuals();
            OfferTutorialTour();
        }
    }

    private void FailStartupIntro(Exception ex)
    {
        TrayApp.Log("Startup intro skipped after a rendering failure: " + ex.Message);
        try { FinishStartupIntro(refresh: false); }
        catch (Exception cleanup) { TrayApp.Log("Startup intro cleanup: " + cleanup.Message); }
        try { TransitionTo(OverviewMode.Hidden, syncCameraOnClose: false); }
        catch (Exception cleanup) { TrayApp.Log("Startup intro close: " + cleanup.Message); }
    }
}
