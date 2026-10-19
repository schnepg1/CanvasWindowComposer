using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace CanvasDesktop;

// Project-area input and annotations; native overview lifecycle stays in the coordinator.
internal sealed partial class OverviewManager
{
    private ZoneService? _zones;
    private Guid _zoneDesktop;
    private WorkspaceService? _placeholderWorkspace;
    private IReadOnlyDictionary<IntPtr, string> _placeholderTitles = new Dictionary<IntPtr, string>();
    private IntPtr _hoveredPlaceholder;

    public void AttachPlaceholderWorkspace(WorkspaceService workspace)
    {
        if (_placeholderWorkspace != null)
        {
            _placeholderWorkspace.Changed -= RefreshPlaceholderTitles;
            _placeholderWorkspace.ZoneLayoutRecorded -= OnZoneLayoutRecorded;
        }
        _placeholderWorkspace = workspace;
        workspace.Changed += RefreshPlaceholderTitles;
        workspace.ZoneLayoutRecorded += OnZoneLayoutRecorded;
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
        SetCanvasTool(null);
        _zoneTool.Arm();
        RefreshZoneAnnotations();
        if (_passes.Count > 0) _passes[0].Activate();
    }

    public void JumpToZone(ZoneView zone)
    {
        CancelInteraction();
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
            CancelInteraction();
        }
        if (CurrentMode == OverviewMode.Hidden) return;
        var inputs = new List<OverviewCardInput>();
        var gestureCards = new List<PlaceholderAnnotation>();
        bool hasRestoreFallback = false;
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
                || (_tools.Kind == CanvasInteractionKind.Window && i == _dragIndex);
            bool restoreFallback = visible && _restoringGesturePreviews
                && !_placeholderTitles.ContainsKey(entry.HWnd) && !_thumbnails.HasVisibleSource(entry.HWnd);
            if (visible && (GesturePreviewsActive || restoreFallback))
            {
                // Reuse an existing immutable card image or cached native source.
                // A title card is immediate when no image exists; never capture here.
                var image = (Control.FromHandle(entry.HWnd) as PlaceholderForm)?.CachedOverviewImage;
                bool cachedThumbnail = _thumbnails.HasVisibleSource(entry.HWnd)
                    || (_cardSources.TryGetValue(entry.HWnd, out var cachedSource) && _thumbnails.HasVisibleSource(cachedSource));
                if (cachedThumbnail) image = null;
                gestureCards.Add(new(entry.HWnd, GestureTitle(entry.HWnd), entry.World, priority,
                    image, occluders.ToArray(), ImageOpacity: image != null || cachedThumbnail ? 1 : 0,
                    LabelOpacity: image != null ? 0 : 1));
                hasRestoreFallback |= restoreFallback;
            }
            else if (visible && _overviewItemIds.TryGetValue(entry.HWnd, out var id))
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
        IReadOnlyList<PlaceholderAnnotation> cards;
        if (GesturePreviewsActive)
        {
            _cardAnimationTimer?.Stop();
            cards = gestureCards;
        }
        else
        {
            cards = _cardTransitions.Update(inputs, Environment.TickCount64).Concat(gestureCards).ToArray();
            UpdateCardAnimationTimer();
        }
        RetryGestureRestoration(hasRestoreFallback);
        _annotationCards = cards;
        RefreshToolPreview();
    }

    private IReadOnlyList<PlaceholderAnnotation> _annotationCards = Array.Empty<PlaceholderAnnotation>();

    private void RefreshToolPreview()
    {
        if (_disposed || CurrentMode == OverviewMode.Hidden) return;
        bool zooming = CurrentMode == OverviewMode.Zooming;
        _selectionRenderItems=zooming && _selectionLayer!=null?SelectionItems().Where(i=>i.Key.Kind!=CanvasItemKind.Annotation || !_composerPreviewIds.Contains(i.Key.Id)).ToArray():[];
        if (_annotationTool?.Preview is {} annotationPreview)
            _selectionRenderItems=_selectionRenderItems.Select(i=>i.Key.Kind==CanvasItemKind.Annotation && i.Key.Id==annotationPreview.Id
                ?i with { Bounds=annotationPreview.Bounds }:i).ToArray();
        var zones = !zooming || _zones == null ? new List<ZoneView>() : new List<ZoneView>(_zones.Items);
        if (_zoneTool.Gesture.MovingId is { } movingId && _zoneTool.Gesture.Preview is { } moved)
        {
            for (int i = 0; i < zones.Count; i++)
                if (zones[i].Id == movingId) zones[i] = zones[i] with { Bounds = moved };
        }
        foreach (var p in _passes)
            if (p.Visible) p.ShowAnnotations(OverviewAnnotationSnapshot.Create(zones, _camera.X, _camera.Y, _camera.Zoom, p.Screen.Bounds,
                _zoneTool.Gesture.Kind == ZoneGestureKind.Draw ? _zoneTool.Gesture.Preview : null,
                _zoneTool.Gesture.Armed || _zoneTool.Gesture.Kind == ZoneGestureKind.Draw, _zoneTool.Gesture.Ghosts, _annotationCards, zooming && !IsStartupIntroActive, _startupIntroFrame, p.Screen.Primary, AnnotationHint));
        RefreshAnnotationUi();
        RefreshTourUi();
    }

    private bool HandleZoneKey(KeyEventArgs e)
    {
        if (_zones != null && e.Control && !e.Alt && (e.KeyCode == Keys.Z || e.KeyCode == Keys.Y))
        {
            CancelInteraction();
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
            CancelInteraction();
            ZoneNavigatorRequested?.Invoke(); e.Handled = true; return true;
        }
        return false;
    }

}
