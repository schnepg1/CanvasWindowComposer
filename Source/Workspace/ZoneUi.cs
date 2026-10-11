using System;
using System.Windows;
using System.Windows.Forms.Integration;

namespace CanvasDesktop;

/// <summary>Owns the WPF zone UI while the WinForms message loop owns application lifetime.</summary>
internal sealed class ZoneUi : IDisposable
{
    private readonly WorkspaceService _workspace;
    private readonly OverviewManager _overview;
    private readonly System.Windows.Application? _application;
    private ZoneWindow? _window;
    private ZoneNavigatorWindow? _navigator;
    private readonly ZoneNavigation _navigation;
    private bool _disposed;

    public ZoneUi(WorkspaceService workspace, OverviewManager overview)
    {
        _workspace = workspace; _overview = overview;
        if (System.Windows.Application.Current == null)
            _application = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        _navigation = new ZoneNavigation(workspace.Zones, workspace, overview.JumpToZone,
            () => overview.TransitionTo(OverviewMode.Hidden));
        _overview.AttachZones(workspace.Zones);
        _overview.ZonePanelRequested += Show;
        _overview.ZoneNavigatorRequested += ShowNavigator;
        _overview.ZoneDrawn += OnZoneDrawn;
        _overview.ZoneMoveRequested += OnZoneMoveRequested;
        _overview.ZoneResizeRequested += OnZoneResizeRequested;
        _overview.ZoneHistoryRequested += OnZoneHistoryRequested;
        _overview.AfterModeChanged += OnModeChanged;
    }

    public void Show()
    {
        if (_disposed) return;
        if (_window == null)
        {
            _window = new ZoneWindow(_workspace.Zones, _overview.BeginZone, _overview.JumpToZone,
                () => _workspace.Status, _workspace, OnZoneHistoryRequested, ShowNavigator);
            _window.Closed += (_, _) => _window = null;
            ElementHost.EnableModelessKeyboardInterop(_window);
        }
        _window.Topmost = _overview.CurrentMode == OverviewMode.Zooming;
        _window.Show();
        _window.Activate();
    }

    private void OnModeChanged(OverviewMode from, OverviewMode to)
    {
        if (_window != null) _window.Topmost = to == OverviewMode.Zooming;
        if (_navigator != null) _navigator.Topmost = to == OverviewMode.Zooming;
    }

    public void ShowNavigator()
    {
        if (_disposed) return;
        if (_navigator == null)
        {
            _navigator = new ZoneNavigatorWindow(_navigation, id =>
            {
                Show();
                if (id is { } areaId) _window!.Select(areaId);
            });
            _navigator.Closed += (_, _) => _navigator = null;
            ElementHost.EnableModelessKeyboardInterop(_navigator);
        }
        _navigator.Topmost = _overview.CurrentMode == OverviewMode.Zooming;
        _navigator.Show(); _navigator.Activate(); _navigator.FocusSearch();
    }

    private void OnZoneMoveRequested(Guid id, double dx, double dy)
    {
        if (_workspace.MoveZone(id, dx, dy)) return;
        Show();
        _window!.Report(_workspace.Status ?? "The area could not be moved or saved.");
    }

    private void OnZoneResizeRequested(Guid id, WorldRect bounds)
    {
        if (_workspace.ResizeZone(id, bounds)) return;
        Show(); _window!.Report(_workspace.Status ?? "The area could not be resized or saved.");
    }

    private void OnZoneHistoryRequested(bool redo)
    {
        if (redo ? !_workspace.CanRedoZoneLayout : !_workspace.CanUndoZoneLayout) return;
        bool applied = redo ? _workspace.RedoZoneLayout() : _workspace.UndoZoneLayout();
        _overview.RefreshZoneLayout();
        if (!applied) { Show(); _window!.Report(_workspace.Status ?? "The area layout could not be restored or saved."); }
    }

    private void OnZoneDrawn(WorldRect bounds)
    {
        bool saved = _workspace.Zones.Add(bounds, out var id);
        Show();
        _window!.Select(id);
        if (!saved) _window.Report(_workspace.Status ?? "Area is too large, or the workspace has reached its area limit.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _overview.ZonePanelRequested -= Show;
        _overview.ZoneNavigatorRequested -= ShowNavigator;
        _overview.ZoneDrawn -= OnZoneDrawn;
        _overview.ZoneMoveRequested -= OnZoneMoveRequested;
        _overview.ZoneResizeRequested -= OnZoneResizeRequested;
        _overview.ZoneHistoryRequested -= OnZoneHistoryRequested;
        _overview.AfterModeChanged -= OnModeChanged;
        _window?.Close();
        _navigator?.Close();
        _navigation.Dispose();
        _application?.Shutdown();
    }
}
