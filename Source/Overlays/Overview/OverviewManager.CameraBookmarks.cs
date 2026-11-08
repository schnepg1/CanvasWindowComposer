using System;
using System.Drawing;
using System.Windows.Forms;

namespace CanvasDesktop;

internal sealed partial class OverviewManager
{
    private CameraBookmarkStore? _cameraBookmarks;
    private ToolTip? _cameraBookmarkFeedback;

    internal static bool TryCameraBookmarkKey(Keys keyData, out int slot, out bool save)
    {
        var key = keyData & Keys.KeyCode;
        slot = key is >= Keys.D0 and <= Keys.D9 ? key - Keys.D0
            : key is >= Keys.NumPad0 and <= Keys.NumPad9 ? key - Keys.NumPad0 : -1;
        var modifiers = keyData & Keys.Modifiers;
        save = modifiers == Keys.Control;
        return slot >= 0 && modifiers is Keys.None or Keys.Control;
    }

    public bool RecallBookmark(int slot)
    {
        if (_disposed || _cameraBookmarks == null) return false;
        if (!_cameraBookmarks.TryGet(_annotationDesktop(), slot, out var camera)) return false;

        // Decision 3: Option A
        // If bookmark has Zoom >= 0.98 and invoked from Desktop View, land in Desktop View.
        // If bookmark has Zoom < 0.98, transition to Canvas View and remain in Canvas View!
        bool landInDesktop = CurrentMode == OverviewMode.Hidden && camera.Zoom >= 0.98;
        bool navigated = NavigateToCamera(camera, landInDesktopView: landInDesktop);
        if (navigated && CurrentMode == OverviewMode.Zooming && _passes.Count > 0)
        {
            var pass = _passes[0];
            string message = $"Camera {slot} · {camera.Zoom:P0}";
            _cameraBookmarkFeedback ??= new ToolTip();
            _cameraBookmarkFeedback.Show(message, pass, new Point(Math.Max(8, pass.ClientSize.Width / 2 - 120), 64), 2200);
        }
        return navigated;
    }

    private bool HandleCameraBookmarkKey(OverviewOverlay pass, KeyEventArgs e)
    {
        // Local canvas shortcuts never intercept an app's keyboard or a text/number editor.
        if (_disposed || CurrentMode != OverviewMode.Zooming || !_cfg.InputEnabled || _cameraBookmarks == null
            || CanvasInputSuspended || _tools.Active || _tools.ToolHasPendingInteraction || WindowInteractionActive
            || _annotationToolbar?.HasOpenDropDown == true
            || !TryCameraBookmarkKey(e.KeyData, out int slot, out bool save)) return false;

        e.Handled = true;
        e.SuppressKeyPress = true;
        string message;
        if (save)
        {
            CancelInertia();
            CancelSmoothZoom();
            bool saved = _cameraBookmarks.Save(_annotationDesktop(), slot, ViewCamera);
            message = saved ? $"Camera {slot} saved ({_camera.Zoom:P0}) · Press {slot} to return"
                : _cameraBookmarks.Status ?? "Camera bookmark could not be saved.";
        }
        else if (RecallBookmark(slot))
        {
            return true;
        }
        else message = $"Camera {slot} is empty · Ctrl+{slot} saves this view";

        _cameraBookmarkFeedback ??= new ToolTip();
        _cameraBookmarkFeedback.Show(message, pass, new Point(Math.Max(8, pass.ClientSize.Width / 2 - 120), 64), 2200);
        return true;
    }
}
