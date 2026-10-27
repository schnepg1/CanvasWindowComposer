using System;

namespace CanvasDesktop;

internal sealed partial class OverviewManager
{
    private bool _webResourceDialogOpen;

    private void ShowAddWebResource()
    {
        if (_placeholderWorkspace == null || _webResourceDialogOpen || _disposed) return;
        CancelInteraction(); CancelInertia();
        _webResourceDialogOpen = true;
        try
        {
            var area = _screens.PrimaryWorkingArea;
            var camera = SceneCamera;
            var bounds = new WorldRect { X = camera.X + area.X / camera.Zoom + 80,
                Y = camera.Y + area.Y / camera.Zoom + 80, W = 1000, H = 700 };
            WorkspaceInteractionDialogs.WebResourceSettings(_passes.Count > 0 ? _passes[0].Handle : IntPtr.Zero, _placeholderWorkspace, bounds: bounds);
        }
        finally { _webResourceDialogOpen = false; }
    }
}
