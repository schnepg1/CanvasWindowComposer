using System;

namespace CanvasDesktop;

/// <summary>
/// Wires an <see cref="IOverviewController"/> to its input/canvas signals
/// (drag, hotkey, button, pan/zoom drains, camera sync, pan-surface registration).
/// Lives in its own class so the binding logic can be exercised against a
/// fake controller without instantiating the real form-bound OverviewManager.
/// </summary>
internal sealed class OverviewInputs : IDisposable
{
    private readonly IOverviewController _overview;
    private readonly IInputRouter _input;
    private readonly Canvas _canvas;
    private bool _disposed, _discardIntroDrag, _guidedDrag, _rawDragActive;

    public OverviewInputs(IOverviewController overview, IInputRouter input, Canvas canvas)
    {
        _overview = overview;
        _input = input;
        _canvas = canvas;

        canvas.CameraChanged       += OnCanvasCameraChanged;
        overview.BeforeModeChanged += OnOverviewModeChanged;
        input.DragStarted          += OnDragStarted;
        input.ButtonDown           += OnMouseButtonDown;
        input.OverviewHotkey       += OnOverviewHotkey;
        input.InputAvailable       += OnCanvasInput;
        input.EscPressed           += OnEscPressed;
    }

    private void OnCanvasCameraChanged()
    {
        if (_disposed) return;
        _overview.SyncCamera();
    }

    private void OnOverviewModeChanged(OverviewMode from, OverviewMode to)
    {
        if (_disposed) return;
        if (from == OverviewMode.Panning && to != OverviewMode.Panning && _rawDragActive)
            _discardIntroDrag = true;
        if (to == OverviewMode.Panning)
            _input.SetExtraPanSurfaces(_overview.MonitorHandles);
        else
            _input.ClearExtraPanSurfaces();

        // Keep native middle clicks blocked for the desktop gesture, including
        // the initial transition and exposed taskbar/screen-fixed surfaces.
        // Raw input retains ownership of the actual drag and release.
        if (to == OverviewMode.Panning)
            _input.EnableMiddleButtonBlock();
        else if (from == OverviewMode.Panning)
            _input.DisableMiddleButtonBlock();

        // Esc closes Zooming via a global hotkey rather than form KeyDown:
        // only _passes[0] gets Activate() and keyboard focus can drift in
        // multi-monitor setups, so the form-level handler isn't reliable.
        // Panning is by design — Esc is left alone there.
        if (to == OverviewMode.Zooming)
            _input.EnableEscHotkey();
        else if (from == OverviewMode.Zooming)
            _input.DisableEscHotkey();
    }

    private void OnEscPressed()
    {
        if (_disposed) return;
        if (_overview.TrySkipStartupIntro()) return;
        if (_overview.CurrentMode == OverviewMode.Zooming)
            _overview.HandleEscape();
    }

    private void OnDragStarted()
    {
        if (_disposed) return;
        _rawDragActive = true;
        _discardIntroDrag = false;
        if (_overview.CanvasInputSuspended || _overview.TrySkipStartupIntro() || _overview.StartupIntroInputSuppressed)
        {
            _discardIntroDrag = true;
            return;
        }
        _guidedDrag=_overview.GuidedTutorialActive;
        if (!_guidedDrag) _overview.TransitionTo(OverviewMode.Panning);
        _overview.BeginPan(_input.PanStartTimestamp);
    }

    private void OnMouseButtonDown()
    {
        if (_disposed) return;
        if (_overview.TrySkipStartupIntro()) return;
        // Desktop pan overlays own stopping clicks until button-up. Closing
        // here would expose stale native positions before Windows routes the click.
    }

    private void OnOverviewHotkey()
    {
        if (_disposed || _overview.CanvasInputSuspended) return;
        if (_overview.TrySkipStartupIntro()) return;
        if (_overview.CurrentMode == OverviewMode.Zooming)
            _overview.TransitionTo(OverviewMode.Hidden);
        else
            _overview.TransitionTo(OverviewMode.Zooming, zoomOutOnOpen: true);
    }

    private void OnCanvasInput()
    {
        if (_disposed) return;
        var samples = _input.DrainPanSamples();
        // A gesture started in the tour must never leak into the real desktop after pause/close.
        if (_guidedDrag && !_overview.GuidedTutorialActive)
        {
            _input.TryDrainPanDelta(out _,out _);_input.TryDrainZoom();
            if(_input.TryDrainDragEnded()) { _guidedDrag=false; _rawDragActive=false; }
            return;
        }
        if (_overview.CanvasInputSuspended || _overview.TrySkipStartupIntro() || _overview.StartupIntroInputSuppressed || _discardIntroDrag)
        {
            if (_overview.CanvasInputSuspended && _rawDragActive) _discardIntroDrag = true;
            _input.TryDrainPanDelta(out _, out _);
            if (_input.TryDrainDragEnded()) { _discardIntroDrag = false;_guidedDrag=false;_rawDragActive=false; }
            _input.TryDrainZoom();
            return;
        }
        if (_input.TryDrainPanDelta(out int dx, out int dy))
        {
            if (_overview.GuidedTutorialActive) _overview.PanGuidedTutorial(dx,dy);
            else
            {
                _canvas.Pan(dx, dy);
                if (samples.Count == 0) _overview.RecordPanDelta(dx, dy);
            }
        }

        if (!_overview.GuidedTutorialActive)
            foreach (var sample in samples) _overview.RecordPanDelta(sample.Dx, sample.Dy, sample.Timestamp);

        if (_input.TryDrainDragEnded())
        {
            _rawDragActive=false;
            _guidedDrag=false;
            if(!_overview.GuidedTutorialActive)_overview.ReleaseInertia(_input.PanReleaseTimestamp);
        }

        if (_input.TryDrainZoom())
        {
            if (_overview.CurrentMode == OverviewMode.Zooming)
                _overview.TransitionTo(OverviewMode.Hidden);
            else
                _overview.TransitionTo(OverviewMode.Zooming);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _canvas.CameraChanged -= OnCanvasCameraChanged;
        _overview.BeforeModeChanged -= OnOverviewModeChanged;
        _input.DragStarted -= OnDragStarted;
        _input.ButtonDown -= OnMouseButtonDown;
        _input.OverviewHotkey -= OnOverviewHotkey;
        _input.InputAvailable -= OnCanvasInput;
        _input.EscPressed -= OnEscPressed;
    }
}
