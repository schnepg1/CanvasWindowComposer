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
    private bool _disposed, _discardIntroDrag, _guidedDrag;

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
        if (to == OverviewMode.Panning)
            _input.SetExtraPanSurfaces(_overview.MonitorHandles);
        else
            _input.ClearExtraPanSurfaces();

        // Panning overlay is WS_EX_TRANSPARENT; without a low-level block a
        // middle click during panning would reach whatever app is under the
        // cursor (Chrome closes tabs, etc.).
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
        if (_overview.CanvasInputSuspended || _overview.TrySkipStartupIntro() || _overview.StartupIntroInputSuppressed)
        {
            _discardIntroDrag = true;
            return;
        }
        _guidedDrag=_overview.GuidedTutorialActive;
        if (!_guidedDrag) _overview.TransitionTo(OverviewMode.Panning);
    }

    private void OnMouseButtonDown()
    {
        if (_disposed) return;
        if (_overview.TrySkipStartupIntro()) return;
        // A non-pan click while the panning overview is up — close it so the
        // click interacts with the underlying window normally.
        if (_overview.CurrentMode == OverviewMode.Panning)
            _overview.TransitionTo(OverviewMode.Hidden);
    }

    private void OnOverviewHotkey()
    {
        if (_disposed || _overview.CanvasInputSuspended) return;
        if (_overview.TrySkipStartupIntro()) return;
        if (_overview.CurrentMode == OverviewMode.Zooming)
            _overview.TransitionTo(OverviewMode.Hidden);
        else
            _overview.TransitionTo(OverviewMode.Zooming);
    }

    private void OnCanvasInput()
    {
        if (_disposed) return;
        // A gesture started in the tour must never leak into the real desktop after pause/close.
        if (_guidedDrag && !_overview.GuidedTutorialActive)
        {
            _input.TryDrainPanDelta(out _,out _);_input.TryDrainZoom();
            if(_input.TryDrainDragEnded())_guidedDrag=false;
            return;
        }
        if (_overview.CanvasInputSuspended || _overview.TrySkipStartupIntro() || _overview.StartupIntroInputSuppressed || _discardIntroDrag)
        {
            _input.TryDrainPanDelta(out _, out _);
            if (_input.TryDrainDragEnded()) _discardIntroDrag = false;
            _input.TryDrainZoom();
            return;
        }
        if (_input.TryDrainPanDelta(out int dx, out int dy))
        {
            if (_overview.GuidedTutorialActive) _overview.PanGuidedTutorial(dx,dy);
            else { _canvas.Pan(dx, dy);_overview.RecordPanDelta(dx, dy); }
        }

        if (_input.TryDrainDragEnded())
        {
            _guidedDrag=false;
            if(!_overview.GuidedTutorialActive)_overview.ReleaseInertia();
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
