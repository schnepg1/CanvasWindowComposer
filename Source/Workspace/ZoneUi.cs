using System;
using System.Windows.Forms.Integration;

namespace CanvasDesktop;

/// <summary>Owns the WPF project surface while the WinForms message loop owns application lifetime.</summary>
internal sealed class ZoneUi : IDisposable
{
    private readonly WorkspaceService _workspace;
    private readonly OverviewManager _overview;
    private readonly ZoneNavigation _navigation;

    private ZoneWorkspaceWindow? _window;
    internal ZoneWorkspaceWindow? Window => _window;
    private bool _disposed;

    public ZoneUi(WorkspaceService workspace, OverviewManager overview)
    {
        _workspace = workspace; _overview = overview;

        _navigation = new ZoneNavigation(workspace.Zones, workspace, overview.JumpToZone,
            () => overview.TransitionTo(OverviewMode.Hidden));
        _overview.AttachZones(workspace.Zones);
        _overview.AttachPlaceholderWorkspace(workspace);
        _overview.ZonePanelRequested += Show;
        _overview.ZoneDrawn += OnZoneDrawn;
        _overview.ZoneMoveRequested += OnZoneMoveRequested;
        _overview.ZoneResizeRequested += OnZoneResizeRequested;
        _overview.ZoneHistoryRequested += OnZoneHistoryRequested;
        _overview.AfterModeChanged += OnModeChanged;
    }

    public void Show()
    {
        if (_disposed) return;
        bool wasVisible = _window?.IsVisible == true;
        if (_window == null)
        {
            _window = new ZoneWorkspaceWindow(_workspace.Zones, _navigation, _overview.BeginZone,
                () => _workspace.Status, _workspace, OnZoneHistoryRequested,
                id => _overview.ExportCurrentWorkflowToClipboard(id), ChooseProjectFolder);
            _window.Closed += (_, _) => { _window = null; _overview.AttachZoneWindow(null); };
            ElementHost.EnableModelessKeyboardInterop(_window);
        }
        _overview.AttachZoneWindow(_window);
        _window.Show();
        _window.Activate();
        if (!wasVisible) _window.FocusSearch();
        _window.RaiseWithoutActivation();
    }

    private void ChooseProjectFolder(Guid id)
        => WorkspaceDialogs.ChooseProjectFolder(null, _workspace, id, report: msg => _window?.Report(msg), wpfOwner: _window);

    private void OnModeChanged(OverviewMode from, OverviewMode to) => _overview.AttachZoneWindow(_window);

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
        _overview.ZoneDrawn -= OnZoneDrawn;
        _overview.ZoneMoveRequested -= OnZoneMoveRequested;
        _overview.ZoneResizeRequested -= OnZoneResizeRequested;
        _overview.ZoneHistoryRequested -= OnZoneHistoryRequested;
        _overview.AfterModeChanged -= OnModeChanged; _overview.AttachZoneWindow(null);
        _window?.Close();
        _navigation.Dispose();
    }
}
