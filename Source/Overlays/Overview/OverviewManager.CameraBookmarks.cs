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
            bool saved = _cameraBookmarks.Save(_annotationDesktop(), slot, ViewCamera);
            message = saved ? $"Camera {slot} saved ({_camera.Zoom:P0}) · Press {slot} to return"
                : _cameraBookmarks.Status ?? "Camera bookmark could not be saved.";
        }
        else if (_cameraBookmarks.TryGet(_annotationDesktop(), slot, out var camera))
        {
            if (IsStartupIntroActive) FinishStartupIntro(refresh: false);
            PauseGuidedTutorial();
            CancelInertia();
            CancelInteraction();
            CloseActiveMenus();
            _tutorialReading = false;
            _startupIntroLanded = false;
            _camera.SetTo(camera.X, camera.Y, camera.Zoom);
            ReconcileVisuals();
            message = $"Camera {slot} · {camera.Zoom:P0}";
        }
        else message = $"Camera {slot} is empty · Ctrl+{slot} saves this view";

        _cameraBookmarkFeedback ??= new ToolTip();
        _cameraBookmarkFeedback.Show(message, pass, new Point(Math.Max(8, pass.ClientSize.Width / 2 - 120), 64), 2200);
        return true;
    }
}
