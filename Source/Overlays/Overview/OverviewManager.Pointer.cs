using System;
using System.Drawing;
using System.Windows.Forms;

namespace CanvasDesktop;

// Pointer routing, window dragging and camera navigation.
internal sealed partial class OverviewManager
{
    private void HandleMouseDown(OverviewOverlay pass, MouseEventArgs e)
    {
        if (PeekWorking && RoutePeekMouse(new(new(e.X + pass.OriginX, e.Y + pass.OriginY), e.Button, true, false,
            Clicks: e.Clicks, Modifiers: InteractionKeys))) return;
        CloseWindowPeek();
        ResetPeekHover();
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

        if (PlacingWebResource)
        {
            if (e.Button == MouseButtons.Right) { CancelWebResourcePlacement(); return; }
            _webResourcePlacementPressed = e.Button == MouseButtons.Left;
            return;
        }

        if (_annotationEditor is {} activeEditor && !activeEditor.Complete(true, returnFocus: false)) return;
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
            var resizeHit = HitWindowResizeEdge(pass, e.X, e.Y);
            if (resizeHit.Index >= 0)
            {
                BringWindowToFront(resizeHit.Index);
                _resizeIndex = 0;
                _resizeEdge = resizeHit.Edge;
                _resizeInitialWorld = _windows.Windows[0].World;
                _resizeOriginVx = vx;
                _resizeOriginVy = vy;
                _resizeMoved = false;
                _hoveredResizeWindow = _windows.Windows[0].HWnd;
                _hoveredResizeEdge = resizeHit.Edge;

                _tools.TryBeginNavigation(CanvasInteractionKind.WindowResize, pointer);
                ApplyPointerCapture();
                _visualFrames.Request();
                return;
            }

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
        if (PeekWorking && RoutePeekMouse(new(new(e.X + pass.OriginX, e.Y + pass.OriginY), MouseButtons.None, false, false,
            Modifiers: InteractionKeys))) return;
        if (PlacingWebResource)
        {
            _webResourcePlacement!.Move(new(e.X + pass.OriginX, e.Y + pass.OriginY), "New web resource");
            return;
        }

        int vx = e.X + pass.OriginX;
        int vy = e.Y + pass.OriginY;

        if (_tools.Kind == CanvasInteractionKind.Pan)
        {
            int dx = vx - _panStartVx;
            int dy = vy - _panStartVy;
            _panStartVx = vx;
            _panStartVy = vy;

            if (dx != 0 || dy != 0)
            {
                PanOverview(dx, dy);
                RecordPanDelta(dx, dy);
                _visualFrames.Request();
            }
            return;
        }
        if (_tools.Kind == CanvasInteractionKind.WindowResize && _resizeIndex >= 0 && _resizeIndex < _windows.Count)
        {
            double totalDx = (vx - _resizeOriginVx) / _camera.Zoom;
            double totalDy = (vy - _resizeOriginVy) / _camera.Zoom;

            if (WindowResizeLayout.TryResize(_resizeInitialWorld, _resizeEdge, totalDx, totalDy, out var newWorld))
            {
                _windows.SetBoundsAt(_resizeIndex, newWorld);
                var entry = _windows.Windows[_resizeIndex];
                _thumbnails.UpdateWorldRect(entry.HWnd, entry.World);
                _mainCanvas.SetWindow(entry.HWnd, entry.World.X, entry.World.Y, entry.World.W, entry.World.H);
                _resizeMoved = true;
                _visualFrames.Request();
            }
            pass.Cursor = WindowResizeLayout.CursorForEdge(_resizeEdge);
            return;
        }
        UpdateInventoryDropHover(pass, e);
        if (!_cfg.InputEnabled || _annotationEditor != null || _shapeEditor != null) return;
        ObserveWindowPeek(pass, new(vx, vy));
        if (RouteCanvasWindowPointer(pass, e)) return;

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
        if (!_tools.Active && !isSpaceDown && _annotationEditor == null && _shapeEditor == null)
        {
            var resizeHit = HitWindowResizeEdge(pass, e.X, e.Y);
            if (resizeHit.Index >= 0)
            {
                var hitHwnd = _windows.Windows[resizeHit.Index].HWnd;
                if (_hoveredResizeWindow != hitHwnd || _hoveredResizeEdge != resizeHit.Edge)
                {
                    _hoveredResizeWindow = hitHwnd;
                    _hoveredResizeEdge = resizeHit.Edge;
                    _visualFrames.Request();
                }
                pass.Cursor = WindowResizeLayout.CursorForEdge(resizeHit.Edge);
            }
            else if (_hoveredResizeWindow != IntPtr.Zero)
            {
                _hoveredResizeWindow = IntPtr.Zero;
                _hoveredResizeEdge = WindowResizeEdge.None;
                _visualFrames.Request();
            }
        }
        else if (_hoveredResizeWindow != IntPtr.Zero && _tools.Kind != CanvasInteractionKind.WindowResize)
        {
            _hoveredResizeWindow = IntPtr.Zero;
            _hoveredResizeEdge = WindowResizeEdge.None;
            _visualFrames.Request();
        }
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

    }

    private void HandleMouseUp(OverviewOverlay pass, MouseEventArgs e)
    {
        if (PeekWorking && RoutePeekMouse(new(new(e.X + pass.OriginX, e.Y + pass.OriginY), e.Button, false, true,
            Modifiers: InteractionKeys))) return;
        if (PlacingWebResource) { CompleteWebResourcePlacement(pass, e); return; }
        if (TryCollectInventoryDrop(pass, e)) return;
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
        if (ended is CanvasInteractionKind.Window or CanvasInteractionKind.WindowResize or CanvasInteractionKind.Pan) _visualFrames.RequestAndFlush();
        if (ended == CanvasInteractionKind.Pan && !GuidedTutorialActive) ReleaseInertia();
        if (ended == CanvasInteractionKind.Window)
        {
            if (_windowDragMoved && _dragIndex >= 0 && _dragIndex < _windows.Count)
                _placeholderWorkspace?.CommitWindowPlacement(_windows.Windows[_dragIndex].HWnd);
            _wm.Reproject(true);
            _dragIndex = -1;
        }
        if (ended == CanvasInteractionKind.WindowResize)
        {
            if (_resizeMoved && _resizeIndex >= 0 && _resizeIndex < _windows.Count)
            {
                var hwnd = _windows.Windows[_resizeIndex].HWnd;
                _placeholderWorkspace?.CommitWindowPlacement(hwnd);
                _wm.ReprojectWindow(hwnd);
            }
            _wm.Reproject(true);
            _resizeIndex = -1;
            _resizeEdge = WindowResizeEdge.None;
            _hoveredResizeWindow = IntPtr.Zero;
            _hoveredResizeEdge = WindowResizeEdge.None;
        }
    }

    private void HandleMouseWheel(OverviewOverlay pass, int x, int y, int delta, bool horizontal, bool control, bool shift)
    {
        if (PeekWorking && RoutePeekMouse(new(new(x + pass.OriginX, y + pass.OriginY), MouseButtons.None, false, false,
            Wheel: true, Delta: delta, Horizontal: horizontal, Modifiers: InteractionKeys))) return;
        CloseWindowPeek();
        ResetPeekHover();
        if (TrySkipStartupIntro()) return;
        if (!_cfg.InputEnabled || _tools.Active || _annotationEditor != null || _shapeEditor != null) return;
        CancelInertia();
        if (_composerWindow is { WaitingForBrowser: false }) return;
        bool canvasNavigation = control || ((_windowInteraction.Active ? InteractionKeys : Control.ModifierKeys) & Keys.Alt) != 0;
        if (!PlacingWebResource && RouteWindowWheel(pass, x, y, delta, horizontal, canvasNavigation)) return;
        if (_windowInteraction.Active && _windowInteraction.Captured) return;
        var (panX, panY, zoomNotches) = OverviewNavigationInput.TranslateWheel(delta, horizontal, control, shift);
        if (zoomNotches != 0) ZoomOverview(x + pass.OriginX, y + pass.OriginY, zoomNotches);
        else { PanOverview(panX, panY); _visualFrames.RequestAndFlush(); }
        if (PlacingWebResource)
        {
            _visualFrames.Flush();
            _webResourcePlacement!.Move(new(x + pass.OriginX, y + pass.OriginY), "New web resource");
            _webResourcePlacement.RefreshPreview();
        }
    }

    private void PanOverview(double virtualDx, double virtualDy)
    {
        if (virtualDx == 0 && virtualDy == 0) return;
        CloseWindowPeek();
        ResetPeekHover();
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
        CloseWindowPeek();
        ResetPeekHover();
        StopTutorialTravel();
        StopWindowNavigation();
        double before=_camera.Zoom;
        _lastZoomFocalWorld = new CanvasPoint(virtualX / before + _camera.X, virtualY / before + _camera.Y);
        if (SmoothZoomAllowed)
        {
            StartOrUpdateSmoothZoom(virtualX, virtualY, notches);
            return;
        }
        CancelSmoothZoom();
        if (!_camera.ZoomToCursor(virtualX, virtualY, notches)) { RefreshTourUi();return; }
        ObserveGestureMotion();
        if(_tour?.ObserveZoom(before,_camera.Zoom)==true)SaveTourProgress();
        _startupIntroLanded = false;
        _visualFrames.RequestAndFlush();
    }

    private void HandleDoubleClick(OverviewOverlay pass, MouseEventArgs e)
    {
        if (PlacingWebResource) return;
        CancelSmoothZoom();
        _visualFrames.RequestAndFlush();
        if (CloseActiveMenus()) return;
        if (TrySkipStartupIntro()) return;
        if (!_cfg.InputEnabled) return;
        if (_windowInteraction.Active) return;
        if (_resizeMoved)
        {
            _resizeMoved = false;
            return;
        }
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
                    QueueAnnotationTextEdit(note,new Point(e.X+pass.OriginX,e.Y+pass.OriginY));
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
            if (!NavigateToWindow(entry.HWnd)) GoToWindow(entry.HWnd, entry.World);
        }
    }

    // ==================== HELPERS ====================

    private void GoToWindow(IntPtr hWnd, WorldRect world)
    {
        if (world.InInventory) { SetInventoryVisible(true); return; }
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

    private (int Index, WindowResizeEdge Edge) HitWindowResizeEdge(OverviewOverlay pass, int x, int y)
    {
        if (CurrentMode != OverviewMode.Zooming || IsStartupIntroActive)
            return (-1, WindowResizeEdge.None);

        var b = pass.Screen.Bounds;
        double dpi = pass.DeviceDpi / 96.0;
        var view = new CanvasViewTransform(_camera.X, _camera.Y, _camera.Zoom,
            new(b.X, b.Y, b.Width, b.Height), dpi, dpi);

        for (int i = 0; i < _windows.Count; i++)
        {
            var entry = _windows.Windows[i];
            if (entry.ScreenFixed || entry.World.InInventory) continue;
            var localRect = view.WorldToLocal(new CanvasRect(entry.World.X, entry.World.Y, entry.World.W, entry.World.H));
            var edge = WindowResizeLayout.HitTest(localRect, x, y);
            if (edge != WindowResizeEdge.None)
                return (i, edge);

            if (x >= localRect.X && x <= localRect.X + localRect.Width &&
                y >= localRect.Y && y <= localRect.Y + localRect.Height)
            {
                break;
            }
        }
        return (-1, WindowResizeEdge.None);
    }
}
