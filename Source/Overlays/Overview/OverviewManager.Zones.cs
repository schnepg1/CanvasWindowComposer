using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace CanvasDesktop;

// Project-area input and annotations; native overview lifecycle stays in the coordinator.
internal sealed partial class OverviewManager
{
    private ZoneService? _zones;
    private Guid _zoneDesktop;
    private readonly OverviewZoneInteraction _zoneGesture = new();
    private WorkspaceService? _placeholderWorkspace;
    private IReadOnlyDictionary<IntPtr, string> _placeholderTitles = new Dictionary<IntPtr, string>();
    private IntPtr _hoveredPlaceholder;

    public void AttachPlaceholderWorkspace(WorkspaceService workspace)
    {
        if (_placeholderWorkspace != null) _placeholderWorkspace.Changed -= RefreshPlaceholderTitles;
        _placeholderWorkspace = workspace;
        workspace.Changed += RefreshPlaceholderTitles;
        RefreshPlaceholderTitles();
    }

    private void RefreshPlaceholderTitles()
    {
        if (_disposed) return;
        _placeholderTitles = _placeholderWorkspace?.GetPlaceholderTitles() ?? new Dictionary<IntPtr, string>();
        if (CurrentMode != OverviewMode.Hidden)
        {
            RefreshOverviewWindows();
            ReconcileVisuals();
        }
    }

    private void RefreshOverviewWindows()
        => _windows.Refresh(includeScreenFixedWindows: ShouldShowScreenFixedWindowsDuringPan,
            representedWindows: _placeholderWorkspace?.GetRepresentedWindows(), preserveSelection: true);

    private int HitOverviewWindow(OverviewOverlay pass, int x, int y, double wx, double wy)
    {
        var handle = pass.HitPlaceholderLabel(x, y);
        if (handle != IntPtr.Zero)
            for (int i = 0; i < _windows.Count; i++)
                if (_windows.Windows[i].HWnd == handle) return i;
        return _windows.HitTest(wx, wy);
    }
    public event Action? ZonePanelRequested;
    public event Action? ZoneNavigatorRequested;
    public event Action<WorldRect>? ZoneDrawn;
    public event Action<Guid, double, double>? ZoneMoveRequested;
    public event Action<Guid, WorldRect>? ZoneResizeRequested;
    public event Action<bool>? ZoneHistoryRequested;

    public void RefreshZoneLayout()
    {
        _windows.SyncGeometry();
        foreach (var entry in _windows.Windows) _thumbnails.UpdateWorldRect(entry.HWnd, entry.World);
        ReconcileVisuals();
    }

    public void AttachZones(ZoneService zones)
    {
        if (_zones != null) _zones.Changed -= RefreshZoneAnnotations;
        _zones = zones;
        _zoneDesktop = zones.DesktopId;
        zones.Changed += RefreshZoneAnnotations;
    }

    public void BeginZone()
    {
        TransitionTo(OverviewMode.Zooming);
        CancelZoneDrawing();
        _zoneGesture.Arm();
        RefreshZoneAnnotations();
        if (_passes.Count > 0) _passes[0].Activate();
    }

    public void JumpToZone(ZoneView zone)
    {
        TransitionTo(OverviewMode.Zooming);
        var fit = ZoneGeometry.Fit(zone.Bounds, _screens.PrimaryWorkingArea);
        _startupIntroLanded = false;
        _camera.SetTo(fit.X, fit.Y, fit.Zoom);
        foreach (var p in _passes) p.Grid?.UpdateCamera(_camera.X, _camera.Y, _camera.Zoom);
        ReconcileVisuals();
    }

    private void RefreshZoneAnnotations()
    {
        if (_zones != null && _zoneDesktop != _zones.DesktopId)
        {
            _zoneDesktop = _zones.DesktopId;
            CancelZoneDrawing();
        }
        if (CurrentMode == OverviewMode.Hidden) return;
        bool zooming = CurrentMode == OverviewMode.Zooming;
        var zones = !zooming || _zones == null ? new List<ZoneView>() : new List<ZoneView>(_zones.Items);
        if (_zoneGesture.MovingId is { } movingId && _zoneGesture.Preview is { } moved)
        {
            for (int i = 0; i < zones.Count; i++)
                if (zones[i].Id == movingId) zones[i] = zones[i] with { Bounds = moved };
        }
        RefreshCardIdentities();
        var inputs = new List<OverviewCardInput>();
        var occluders = new List<WorldRect>();
        for (int i = 0; i < _windows.Count; i++)
        {
            var entry = _windows.Windows[i];
            bool visible = !entry.ScreenFixed && _passes.Exists(pass =>
            {
                var b = pass.Screen.Bounds;
                return (entry.World.X - _camera.X) * _camera.Zoom < b.Right
                    && (entry.World.X + entry.World.W - _camera.X) * _camera.Zoom > b.Left
                    && (entry.World.Y - _camera.Y) * _camera.Zoom < b.Bottom
                    && (entry.World.Y + entry.World.H - _camera.Y) * _camera.Zoom > b.Top;
            });
            bool priority = i == _windows.SelectedIndex || entry.HWnd == _hoveredPlaceholder
                || (_draggingWindow && i == _dragIndex);
            if (visible && _overviewItemIds.TryGetValue(entry.HWnd, out var id))
            {
                if (_placeholderTitles.TryGetValue(entry.HWnd, out var title)
                    && Control.FromHandle(entry.HWnd) is PlaceholderForm card)
                {
                    bool simple = _wm.OverviewUsesInfoCards;
                    // The transition controller retains the outgoing frozen image
                    // only until the fade ends; distant cards create no new bitmap.
                    if (simple) card.ReleaseOverviewImage();
                    bool underlay = _cardSources.TryGetValue(entry.HWnd, out var source) && CanShowUnderlay(source);
                    inputs.Add(new(id, new(entry.HWnd, title, entry.World, priority,
                        simple ? null : card.GetOverviewImage(), occluders.ToArray()),
                        simple ? OverviewCardMode.Simple : OverviewCardMode.Detailed, underlay));
                }
                else
                    inputs.Add(new(id, new(entry.HWnd, "", entry.World, priority, Occluders: occluders.ToArray()),
                        OverviewCardMode.Live, !_wm.UsesCachedPreview(entry.HWnd) && _thumbnails.HasLiveSource(entry.HWnd)));
            }
            else if (_placeholderTitles.ContainsKey(entry.HWnd) && Control.FromHandle(entry.HWnd) is PlaceholderForm offscreen)
                offscreen.ReleaseOverviewImage();
            if (entry.ScreenFixed)
            {
                var rect = _win32.GetWindowRect(entry.HWnd);
                occluders.Add(new WorldRect { X = _camera.X + rect.x / _camera.Zoom,
                    Y = _camera.Y + rect.y / _camera.Zoom, W = rect.w / _camera.Zoom, H = rect.h / _camera.Zoom });
            }
            else occluders.Add(entry.World);
        }
        var cards = _cardTransitions.Update(inputs, Environment.TickCount64);
        UpdateCardAnimationTimer();
        foreach (var p in _passes)
            if (p.Visible) p.ShowZones(zones, _camera,
                _zoneGesture.Kind == ZoneGestureKind.Draw ? _zoneGesture.Preview : null,
                _zoneGesture.Armed || _zoneGesture.Kind == ZoneGestureKind.Draw, _zoneGesture.Ghosts, cards, zooming && !IsStartupIntroActive, _startupIntroFrame, p.Screen.Primary);
    }

    private ZoneView? HitZoneHeader(OverviewOverlay pass, int x, int y)
    {
        if (_zones == null) return null;
        var zones = _zones.Items;
        var bounds = pass.Screen.Bounds;
        var monitor = new ScreenRect(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        double dpi = pass.DeviceDpi / 96.0;
        for (int i = zones.Count - 1; i >= 0; i--)
            if (ZoneLabelLayout.Get(zones[i].Bounds, _camera.X, _camera.Y, _camera.Zoom, monitor, dpi, dpi)
                is { } label && label.Contains(x, y)) return zones[i];
        return null;
    }

    private (ZoneView Zone, ZoneResizeCorner Corner)? HitZoneCorner(OverviewOverlay pass, int x, int y)
    {
        if (_zones == null) return null;
        var zones = _zones.Items;
        var bounds = pass.Screen.Bounds;
        var monitor = new ScreenRect(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        double dpi = pass.DeviceDpi / 96.0;
        for (int i = zones.Count - 1; i >= 0; i--)
            foreach (var handle in ZoneResizeLayout.Get(zones[i].Bounds, _camera.X, _camera.Y,
                _camera.Zoom, monitor, dpi, dpi, hitTarget: true))
                if (handle.Rect.Contains(x, y)) return (zones[i], handle.Corner);
        return null;
    }

    private void CancelZoneDrawing()
    {
        _zoneGesture.Cancel();
        foreach (var p in _passes) p.Capture = false;
    }

    private bool HandleZoneKey(KeyEventArgs e)
    {
        if (_zones != null && e.Control && !e.Alt && (e.KeyCode == Keys.Z || e.KeyCode == Keys.Y))
        {
            CancelZoneDrawing();
            ZoneHistoryRequested?.Invoke(e.KeyCode == Keys.Y || e.Shift);
            RefreshZoneLayout();
            e.SuppressKeyPress = true; e.Handled = true; return true;
        }

        if (_zones != null && !e.Control && !e.Alt && e.KeyCode == Keys.N)
        {
            BeginZone(); e.Handled = true; return true;
        }
        if (_zones != null && !e.Control && !e.Alt && e.KeyCode == Keys.Z)
        {
            ZonePanelRequested?.Invoke(); e.Handled = true; return true;
        }
        if (_zones != null && !e.Control && !e.Alt && e.KeyCode == Keys.P)
        {
            CancelZoneDrawing();
            ZoneNavigatorRequested?.Invoke(); e.Handled = true; return true;
        }
        return false;
    }

    private bool TryBeginZoneGesture(OverviewOverlay pass, MouseEventArgs e, int vx, int vy)
    {
        if (e.Button == MouseButtons.Left && _zones != null && (_zoneGesture.Armed || (Control.ModifierKeys & Keys.Shift) != 0))
        {
            var start = _camera.WorldFromVirtual(vx, vy);
            _zoneGesture.BeginDraw(start.x, start.y);
            pass.Capture = true;
            RefreshZoneAnnotations();
            return true;
        }
        if (e.Button == MouseButtons.Left)
        {
            var (wx, wy) = _camera.WorldFromVirtual(vx, vy);
            if (HitZoneCorner(pass, e.X, e.Y) is { } resize)
            {
                _zoneGesture.BeginResize(resize.Zone, resize.Corner, wx, wy);
                pass.Capture = true;
                RefreshZoneAnnotations();
                return true;
            }
            var zone = HitZoneHeader(pass, e.X, e.Y);
            if (zone != null)
            {
                var members = new List<WorldRect>();
                foreach (var member in _zones!.Members(zone.Id))
                    if (member.Assigned && !member.Bounds.PinnedToScreen) members.Add(member.Bounds);
                _zoneGesture.BeginMove(zone, wx, wy, members);
                pass.Capture = true;
                RefreshZoneAnnotations();
                return true;
            }
        }
        return false;
    }

    private bool CompleteZoneGesture(OverviewOverlay pass, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && _zoneGesture.Active)
        {
            var end = _camera.WorldFromVirtual(e.X + pass.OriginX, e.Y + pass.OriginY);
            var result = _zoneGesture.Complete(end.x, end.y, _camera.Zoom);
            CancelZoneDrawing();
            if (result?.Kind == ZoneGestureKind.Draw) ZoneDrawn?.Invoke(result.Bounds);
            else if (result?.Kind == ZoneGestureKind.Move)
            {
                ZoneMoveRequested?.Invoke(result.ZoneId, result.Dx, result.Dy);
            }
            else if (result?.Kind == ZoneGestureKind.Resize)
                ZoneResizeRequested?.Invoke(result.ZoneId, result.Bounds);
            RefreshZoneLayout();
            return true;
        }
        return false;
    }
}
