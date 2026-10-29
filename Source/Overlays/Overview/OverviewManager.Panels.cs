using System;

namespace CanvasDesktop;

internal sealed partial class OverviewManager
{
    internal TaskbarPreviewSnapshot TaskbarDiagnostics => _taskbarPreviews.Snapshot();
    internal object InteractionDiagnostics => new
    {
        Armed = _interactMode, Active = _windowInteraction.Active, Captured = _windowInteraction.Captured,
        MouseHook = _interactionMouseHook?.Diagnostics
    };

    private IntPtr CanvasPanelOwner => CurrentMode == OverviewMode.Hidden ? IntPtr.Zero
        : (_passes.Find(p => p.Screen.Primary) ?? _passes.Find(p => p.IsHandleCreated))?.Handle ?? IntPtr.Zero;

    private void UpdateCanvasPanelOwners()
    {
        _backpack?.SetCanvasOwner(CanvasPanelOwner);
        _temporaryDrawer?.SetCanvasOwner(CanvasPanelOwner);
    }

    private void RaiseCanvasPanels()
    {
        if (_disposed) return;
        _backpack?.RaiseWithoutActivation();
        _temporaryDrawer?.RaiseWithoutActivation();
    }
}
