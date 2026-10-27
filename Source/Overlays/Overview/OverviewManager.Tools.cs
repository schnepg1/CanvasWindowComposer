using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace CanvasDesktop;

// The host alone translates WinForms input and owns native pointer capture.
internal sealed partial class OverviewManager
{
    private readonly CanvasToolController _tools = new();
    private readonly ZoneCanvasTool _zoneTool;
    private IReadOnlyList<IOverviewAnnotationLayer> _drawingLayers = Array.Empty<IOverviewAnnotationLayer>();

    public void SetCanvasTool(ICanvasTool? tool, IReadOnlyList<IOverviewAnnotationLayer>? layers = null)
    {
        CancelInteraction();
        if (tool == null) _annotationTool?.SetMode(AnnotationMode.Select);
        _tools.SetTool(tool ?? _annotationRouter ?? _zoneTool);
        var attached = new List<IOverviewAnnotationLayer>();
        if (_tutorialLayer != null) attached.Add(_tutorialLayer);
        if (_annotationLayer != null) attached.Add(_annotationLayer);
        if (_composerLayer != null) attached.Add(_composerLayer);
        if (_selectionLayer != null) attached.Add(_selectionLayer);
        if (_interactionLayer != null) attached.Add(_interactionLayer);
        if (layers != null) attached.AddRange(layers);
        _drawingLayers = attached;
        foreach (var pass in _passes) pass.SetDrawingLayers(_drawingLayers);
        RefreshToolPreview();
    }

    private CanvasPointer Pointer(OverviewOverlay pass, MouseEventArgs e)
    {
        var b = pass.Screen.Bounds;
        double dpi = pass.DeviceDpi / 96.0;
        var view = new CanvasViewTransform(_camera.X, _camera.Y, _camera.Zoom,
            new(b.X, b.Y, b.Width, b.Height), dpi, dpi);
        var modifiers = _dispatchInteractionKeys ?? Control.ModifierKeys;
        var flags = CanvasModifiers.None;
        if ((modifiers & Keys.Shift) != 0) flags |= CanvasModifiers.Shift;
        if ((modifiers & Keys.Control) != 0) flags |= CanvasModifiers.Control;
        if ((modifiers & Keys.Alt) != 0) flags |= CanvasModifiers.Alt;
        var button = e.Button switch
        {
            MouseButtons.Left => CanvasPointerButton.Left,
            MouseButtons.Middle => CanvasPointerButton.Middle,
            MouseButtons.Right => CanvasPointerButton.Right,
            _ => CanvasPointerButton.None
        };
        return new(_passes.IndexOf(pass), new(e.X, e.Y), button, flags, view);
    }

    private IReadOnlyList<WorldRect> ZoneMoveMembers(Guid id)
    {
        var members = new List<WorldRect>();
        if (_zones != null)
            foreach (var member in _zones.Members(id))
                if (member.Assigned && !member.Bounds.PinnedToScreen) members.Add(member.Bounds);
        return members;
    }

    private void OnZoneToolCompleted(ZoneGestureResult result)
    {
        ApplyPointerCapture();
        if (result.Kind == ZoneGestureKind.Draw) ZoneDrawn?.Invoke(result.Bounds);
        else if (result.Kind == ZoneGestureKind.Move) ZoneMoveRequested?.Invoke(result.ZoneId, result.Dx, result.Dy);
        else if (result.Kind == ZoneGestureKind.Resize) ZoneResizeRequested?.Invoke(result.ZoneId, result.Bounds);
        RefreshZoneLayout();
    }

    private void ApplyPointerCapture()
    {
        for (int i = 0; i < _passes.Count; i++)
        {
            bool capture = _tools.CapturedSurface == i;
            if (_passes[i].Capture != capture) _passes[i].Capture = capture;
        }
    }

    public bool CancelInteraction()
    {
        CancelInertia();
        return CancelInteractionCore();
    }

    private bool CancelInteractionCore()
    {
        StopWindowNavigation();
        StopTutorialTravel();
        bool windowDrag = _tools.Kind == CanvasInteractionKind.Window;
        bool cancelled = EndWindowInteraction();
        cancelled |= CancelCanvasComposer();
        cancelled |= CancelAnnotationEdit();
        cancelled |= _tools.Cancel(); // Clear owner before releasing native capture.
        _dragIndex = -1;
        ApplyPointerCapture();
        if (windowDrag && !_disposed) _wm.Reproject(true);
        if (cancelled && !_disposed)
        {
            RefreshToolPreview();
            _visualFrames.Request();
        }
        return cancelled;
    }

    public void HandleEscape()
    {
        if (_disposed || TrySkipStartupIntro() || CurrentMode != OverviewMode.Zooming) return;
        if (CloseSettingsPopup()) return;
        if (CloseActiveMenus()) return;
        if (_controlsPopup != null && _controlsPopup.Visible) { _controlsPopup.Hide(); FocusOverview(); return; }
        if (_interactMode) { SetInteractMode(false); return; }
        if (EndWindowInteraction(focus: true)) return;
        if (StopTutorialTravel()) { RefreshTourUi();return; }
        if (_annotationEditor==null && _shapeEditor==null && _composerWindow==null && GuidedTutorialActive && !_tools.Active)
        { PauseGuidedTutorial();FocusOverview();return; }
        if (_composerWindow != null) { CancelCanvasComposer(); RefreshToolPreview(); FocusOverview(); return; }
        if (_annotationEditor != null || _shapeEditor != null) { CancelAnnotationEdit(); RefreshToolPreview(); FocusOverview(); return; }
        if (CancelInteraction()) return;
        if (Selection.Ids.Count > 0) { Selection.Clear(); _proximityCircle=null; RefreshToolPreview(); return; }
        if (_annotationTool is { Mode: not (AnnotationMode.Select or AnnotationMode.Navigate) }) { SelectAnnotationMode(AnnotationMode.Select); return; }
        TransitionTo(OverviewMode.Hidden);
    }
}
