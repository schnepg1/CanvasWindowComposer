using System;

namespace CanvasDesktop;

internal sealed partial class OverviewManager
{
    private readonly CanvasLayerCoordinator _layerCoordinator = new();
    internal CanvasLayerCoordinator LayerCoordinator => _layerCoordinator;

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
    }

    internal event Action? CanvasPanelsRaised;

    private void RegisterCanvasLayers()
    {
        _layerCoordinator.Register(CanvasLayerTier.Zones, "Zones",
            () => _zoneWindow?.RaiseWithoutActivation(),
            () => _zoneWindow is { IsVisible: true });

        _layerCoordinator.Register(CanvasLayerTier.BackpackAndDrawers, "BackgroundEditor",
            () => _backgroundEditor?.RaiseWithoutActivation(),
            () => _backgroundEditor is { IsVisible: true });

        _layerCoordinator.Register(CanvasLayerTier.BackpackAndDrawers, "Backpack",
            () => _backpack?.RaiseWithoutActivation(),
            () => _backpack is { IsVisible: true });

        _layerCoordinator.Register(CanvasLayerTier.BackpackAndDrawers, "TemporaryDrawer",
            () => _temporaryDrawer?.RaiseWithoutActivation(),
            () => _temporaryDrawer is { IsVisible: true });

        _layerCoordinator.Register(CanvasLayerTier.DockAndToolbars, "UnifiedDock",
            () => _unifiedDock?.RaiseWithoutActivation(),
            () => _unifiedDock is { IsAlive: true, IsVisible: true });

        _layerCoordinator.Register(CanvasLayerTier.PeekPreview, "PeekPreview",
            () => _peekWindow?.RaiseWithoutActivation(),
            () => _peekWindow is { IsDisposed: false, Visible: true });
    }

    private void RaiseCanvasPanels()
    {
        if (_disposed) return;
        RegisterCanvasLayers();
        _layerCoordinator.RaiseAll();
        CanvasPanelsRaised?.Invoke();
    }

    private ZoneWorkspaceWindow? _zoneWindow;

    internal void AttachZoneWindow(ZoneWorkspaceWindow? window)
    {
        _zoneWindow = window;
        if (_disposed) return;
        UpdateCanvasPanelOwners();
        _zoneWindow?.RaiseWithoutActivation();
        CanvasPanelsRaised?.Invoke();
    }
}
