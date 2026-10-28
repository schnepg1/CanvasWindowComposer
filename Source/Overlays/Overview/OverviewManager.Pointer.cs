using System;
using System.Drawing;
using System.Windows.Forms;

namespace CanvasDesktop;

// Pointer routing, window dragging and camera navigation.
internal sealed partial class OverviewManager
{
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

        _annotationEditor?.Complete(true, returnFocus: false);
        if (_shapeEditor != null || (_composerWindow != null && e.Button == MouseButtons.Left)) return;
        if (RouteCanvasWindowPointer(pass, e, down: true)) return;
        int vx = e.X + pass.OriginX;
        int vy = e.Y + pass.OriginY;

        var pointer = Pointer(pass, e);
        if (e.Button == MouseButtons.Right)
        {
            if (TryShowWindowContextMenu(pass, new(e.X, e.Y))) return;

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
                _windowDragMoved = false;
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
            if (dx != 0 || dy != 0) { _windowDragMoved = true; ObserveGestureMotion(); }
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
            if (_windowDragMoved && _dragIndex >= 0 && _dragIndex < _windows.Count)
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
}
