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

    private bool ShouldClampCanvasPanelsBelowTaskbar =>
        CurrentMode == OverviewMode.Hidden || _appConfig.ShowTaskbarInCanvas;

    private IntPtr CanvasPanelOwner => CurrentMode == OverviewMode.Hidden ? IntPtr.Zero
        : (_passes.Find(p => p.Screen.Primary) ?? _passes.Find(p => p.IsHandleCreated))?.Handle ?? IntPtr.Zero;

    private void UpdateCanvasPanelOwners()
    {
        Func<bool> clamp = () => ShouldClampCanvasPanelsBelowTaskbar;
        if (_unifiedDock?.IsAlive == true)
        {
            _unifiedDock.ClampBelowTaskbar = clamp;
            _unifiedDock.SetCanvasOwner(CanvasPanelOwner);
        }
        if (_backpack != null)
        {
            _backpack.ClampBelowTaskbar = clamp;
            _backpack.SetCanvasOwner(CanvasPanelOwner);
        }
        if (_temporaryDrawer != null)
        {
            _temporaryDrawer.ClampBelowTaskbar = clamp;
            _temporaryDrawer.SetCanvasOwner(CanvasPanelOwner);
        }
        if (_backgroundEditor != null)
        {
            _backgroundEditor.ClampBelowTaskbar = clamp;
            _backgroundEditor.SetCanvasOwner(CanvasPanelOwner);
        }
        if (_zoneWindow != null)
        {
            _zoneWindow.ClampBelowTaskbar = clamp;
            _zoneWindow.SetCanvasOwner(CanvasPanelOwner);
        }
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
            () => _unifiedDock is { IsAlive: true, IsDockVisible: true });

        _layerCoordinator.Register(CanvasLayerTier.PeekPreview, "PeekPreview",
            () => _peekWindow?.RaiseWithoutActivation(),
            () => _peekWindow is { IsDisposed: false, Visible: true });
    }

    private void EnsurePassesBelowCeilingIfNeeded()
    {
        if (_disposed || CurrentMode == OverviewMode.Hidden || _composerWaitingForBrowser)
            return;

        bool clampBelowTaskbar = _appConfig.ShowTaskbarInCanvas;
        IntPtr ceiling = CanvasZOrder.ResolveCeilingInsertAfter(clampBelowTaskbar);
        if (ceiling == CanvasZOrder.HwndTopmost)
            return;

        bool anyAbove = false;
        foreach (var p in _passes)
        {
            if (p.IsHandleCreated && p.Visible && p.IsAnySurfaceAbove(ceiling))
            {
                anyAbove = true;
                break;
            }
        }

        if (!anyAbove) return;

        PlaceOverviewPassesAtCeiling();
        foreach (var p in _passes)
        {
            if (p.IsHandleCreated && p.Visible)
                p.RaiseOwnedChrome(clampBelowTaskbar);
        }
    }

    private void RaiseCanvasPanels()
    {
        if (_disposed) return;
        EnsurePassesBelowCeilingIfNeeded();
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
        EnsurePassesBelowCeilingIfNeeded();
        _zoneWindow?.RaiseWithoutActivation();
        CanvasPanelsRaised?.Invoke();
    }
}
