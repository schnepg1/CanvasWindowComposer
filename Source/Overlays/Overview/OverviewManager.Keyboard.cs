using System;
using System.Drawing;
using System.Windows.Forms;

namespace CanvasDesktop;

// Keyboard shortcuts and selected-window navigation.
internal sealed partial class OverviewManager
{
    private void HandleKeyDown(OverviewOverlay pass, KeyEventArgs e)
    {
        CancelInertia();
        if (HandleCameraBookmarkKey(pass, e)) return;
        if (TrySkipStartupIntro() || StopTutorialTravel()) { RefreshTourUi();e.SuppressKeyPress = true; e.Handled = true; return; }
        if (!_cfg.InputEnabled) return;
        if (HandleTutorialControlKey(e)) return;

        if (e.KeyCode == Keys.Space && _annotationEditor == null && _composerWindow == null)
        {
            foreach (var p in _passes) p.Cursor = Cursors.Hand;
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }

        if (e.KeyCode == Keys.Escape)
        {
            HandleEscape();
            e.Handled = true;
            return;
        }

        if (_annotationEditor != null || _shapeEditor != null || _composerWindow != null) return;
        if (HandleCanvasHistoryKey(e)) return;
        if (e.KeyCode is Keys.Oemplus or Keys.Add or Keys.OemMinus or Keys.Subtract)
        {
            if (!_tools.Active)
            {
                var center = pass.Screen.Bounds;
                double notches = e.KeyCode is Keys.Oemplus or Keys.Add ? 1 : -1;
                ZoomOverview(center.Left + center.Width / 2, center.Top + center.Height / 2, notches);
            }
            e.SuppressKeyPress = true;
            e.Handled = true;
            return;
        }
        if (HandleAnnotationKey(e)) return;
        if (HandleZoneKey(e)) return;
        if (_tools.Active) { e.Handled = true; return; }
        if (_windows.Count == 0) return;

        if (e.KeyCode == Keys.Right || e.KeyCode == Keys.Down)
        {
            _windows.SelectNext();
            NavigateToSelected();
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Up)
        {
            _windows.SelectPrev();
            NavigateToSelected();
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Enter && _windows.SelectedIndex >= 0)
        {
            var entry = _windows.Windows[_windows.SelectedIndex];
            GoToWindow(entry.HWnd, entry.World);
            e.Handled = true;
        }
    }

    private void NavigateToSelected()
    {
        if (_windows.SelectedIndex < 0 || _windows.SelectedIndex >= _windows.Count) return;
        var world = _windows.Windows[_windows.SelectedIndex].World;

        // Center overview camera on selected window (do NOT change zoom)
        _startupIntroLanded = false;
        _camera.CenterOnWorld(world.X, world.Y, world.W, world.H);

        foreach (var p in _passes)
            p.Grid?.UpdateCamera(_camera.X, _camera.Y, _camera.Zoom);
        ReconcileVisuals();
    }

    private void HandleKeyUp(OverviewOverlay pass, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space && _annotationEditor == null && _composerWindow == null)
        {
            var pointer = Pointer(pass, new MouseEventArgs(MouseButtons.None, 0, pass.PointToClient(Cursor.Position).X, pass.PointToClient(Cursor.Position).Y, 0));
            pass.Cursor = _tools.CursorAt(pointer) switch
            {
                CanvasCursor.Crosshair => Cursors.Cross,
                CanvasCursor.ResizeNorthWestSouthEast => Cursors.SizeNWSE,
                CanvasCursor.ResizeNorthEastSouthWest => Cursors.SizeNESW,
                CanvasCursor.ResizeNorthSouth => Cursors.SizeNS,
                CanvasCursor.ResizeEastWest => Cursors.SizeWE,
                CanvasCursor.Move => Cursors.SizeAll,
                _ => Cursors.Default
            };
            e.Handled = true;
        }
    }
}
