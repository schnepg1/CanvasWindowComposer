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
        if (_unifiedDock?.IsAlive == true) _unifiedDock.SetCanvasOwner(CanvasPanelOwner);
        _backpack?.SetCanvasOwner(CanvasPanelOwner);
        _temporaryDrawer?.SetCanvasOwner(CanvasPanelOwner);
        _backgroundEditor?.SetCanvasOwner(CanvasPanelOwner);
        _zoneWindow?.SetCanvasOwner(CanvasPanelOwner);
        _zoneNavigator?.SetCanvasOwner(CanvasPanelOwner);
    }

    internal event Action? CanvasPanelsRaised;

    private void RaiseCanvasPanels()
    {
        if (_disposed) return;
        if (_unifiedDock?.IsAlive == true) _unifiedDock.RaiseWithoutActivation();
        _peekWindow?.RaiseWithoutActivation();
        _backpack?.RaiseWithoutActivation();
        _temporaryDrawer?.RaiseWithoutActivation();
        _backgroundEditor?.RaiseWithoutActivation();
        _zoneWindow?.RaiseWithoutActivation();
        _zoneNavigator?.RaiseWithoutActivation();
        CanvasPanelsRaised?.Invoke();
    }

    private ZoneWindow? _zoneWindow;
    private ZoneNavigatorWindow? _zoneNavigator;

    internal void AttachZoneWindow(ZoneWindow? window)
    {
        _zoneWindow = window;
        if (_disposed) return;
        UpdateCanvasPanelOwners();
        _zoneWindow?.RaiseWithoutActivation();
    }

    internal void AttachZoneNavigator(ZoneNavigatorWindow? navigator)
    {
        _zoneNavigator = navigator;
        if (_disposed) return;
        UpdateCanvasPanelOwners();
        _zoneNavigator?.RaiseWithoutActivation();
        CanvasPanelsRaised?.Invoke();
    }
}
