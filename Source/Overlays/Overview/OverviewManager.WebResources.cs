using System;
using System.Windows;
using System.Windows.Forms;

namespace CanvasDesktop;

internal sealed partial class OverviewManager
{
    private bool _webResourceDialogOpen;
    private CanvasPlacementFeedback? _webResourcePlacement;
    private WorldRect? _webResourceLanding;
    private bool _webResourcePlacementPressed;
    internal Window? WebResourceDialog { get; private set; }
    internal bool PlacingWebResource => _webResourcePlacement != null;
    internal WorldRect? WebResourcePlacementBounds => _webResourceLanding;

    internal void ShowAddWebResource()
    {
        if (_placeholderWorkspace == null || _webResourceDialogOpen || _disposed || CurrentMode != OverviewMode.Zooming) return;
        if (_annotationEditor is { } editor && !editor.Complete(true, returnFocus: false)) return;
        CancelInteraction(); CancelInertia();
        CloseActiveMenus(); CloseSettingsPopup();
        _visualFrames.Flush();
        _webResourcePlacement = new(PreviewWebResourceLanding, new CanvasPlacementFeedbackView(_screens, clickToPlace: true));
        foreach (var pass in _passes) pass.Cursor = Cursors.Cross;
        var pointer = Cursor.Position;
        _webResourcePlacement.Move(new(pointer.X, pointer.Y), "New web resource");
        FocusOverview();
    }

    private WindowRect? PreviewWebResourceLanding(System.Windows.Point screen)
    {
        _webResourceLanding = null;
        if (_disposed || _placeholderWorkspace == null || CurrentMode != OverviewMode.Zooming) return null;
        var view = InventoryViewport();
        if (!CanvasPlacementGeometry.TryWorldAnchor(view, screen, WorkspaceService.NewWebResourceBounds, true, out var x, out var y)
            || !_placeholderWorkspace.TryPreviewWebResourcePlacement(view, out var placed, x, y)) return null;
        _webResourceLanding = placed;
        return CanvasPlacementGeometry.Project(placed, view.Camera, desktop: false);
    }

    private bool CancelWebResourcePlacement()
    {
        if (_webResourcePlacement == null) return false;
        _webResourcePlacement.Dispose(); _webResourcePlacement = null;
        _webResourceLanding = null; _webResourcePlacementPressed = false;
        foreach (var pass in _passes) pass.Cursor = Cursors.Default;
        return true;
    }

    private void CompleteWebResourcePlacement(OverviewOverlay pass, MouseEventArgs e)
    {
        bool pressed = _webResourcePlacementPressed;
        _webResourcePlacementPressed = false;
        if (!pressed || e.Button != MouseButtons.Left || _webResourcePlacement == null) return;
        // Requery at the release position; the throttled preview may still describe the previous pointer position.
        _webResourcePlacement.Move(new(e.X + pass.OriginX, e.Y + pass.OriginY), "New web resource");
        _webResourcePlacement.RefreshPreview();
        if (_webResourceLanding is not { } bounds) return;
        CancelWebResourcePlacement();
        _webResourceDialogOpen = true;
        try
        {
            WebResourceDialog = WorkspaceInteractionDialogs.CreateWebResource(pass.Handle, _placeholderWorkspace!, bounds: bounds);
            WebResourceDialog.ShowDialog();
        }
        finally
        {
            WebResourceDialog = null; _webResourceDialogOpen = false;
            FocusOverview();
        }
    }
}
