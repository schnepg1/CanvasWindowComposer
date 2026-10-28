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
            _placeholderWorkspace.TemporaryWindowsArrived -= OnTemporaryWindowsArrived;
            _placeholderWorkspace.AdditionalPlacementObstacles = null;
        }
        _placeholderWorkspace = workspace;
        workspace.Changed += RefreshPlaceholderTitles;
        workspace.ZoneLayoutRecorded += OnZoneLayoutRecorded;
        workspace.TemporaryWindowsArrived += OnTemporaryWindowsArrived;
        workspace.AdditionalPlacementObstacles = () => (_annotationDocument?.Items
            .Where(a => a.DesktopId == _annotationDesktop()) ?? Enumerable.Empty<CanvasAnnotation>())
            .Concat(CurrentTutorial is { Visible: true } notes ? notes.Layout.Items : Array.Empty<CanvasAnnotation>())
            .Select(a => new WorldRect { X = a.Bounds.X, Y = a.Bounds.Y, W = a.Bounds.Width, H = a.Bounds.Height }).ToArray();
        RefreshPlaceholderTitles();
    }

    private void RefreshPlaceholderTitles()
    {
        if (_disposed) return;
        _placeholderTitles = _placeholderWorkspace?.GetPlaceholderTitles() ?? new Dictionary<IntPtr, string>();
        _temporaryNotice?.UpdateCount(_placeholderWorkspace?.TemporaryArea?.Count ?? 0,
            _placeholderWorkspace?.TemporaryArea?.Name ?? "Temporary area");
        if (CurrentMode != OverviewMode.Hidden)
        {
            RefreshOverviewWindows();
            ReconcileVisuals();
        }
    }

    private void RefreshOverviewWindows()
        => _windows.Refresh(includeScreenFixedWindows: ShouldShowScreenFixedWindowsDuringPan,
            representedWindows: _placeholderWorkspace?.GetRepresentedWindows(), preserveSelection: true,
            navigationTarget: _navigationDisplayTarget);

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
        var camera = SceneCamera;
        var focal = GetTransitionFocalPoint(camera);
        var inputs = new List<OverviewCardInput>();
        _cardImages.BeginFrame();
        List<PlaceholderAnnotation>? gestureCards = null;
        bool hasRestoreFallback = false;
        // Each card borrows a prefix of one detached frame array. Copying every
        // previous window per card used quadratic memory in dense overviews.
        var occluders = new WorldRect[_windows.Count];
        for (int i = 0; i < _windows.Count; i++)
        {
            var entry = _windows.Windows[i];
            bool visible = !entry.ScreenFixed && _passes.Exists(pass =>
            {
                var b = pass.Screen.Bounds;
                return (entry.World.X - camera.X) * camera.Zoom < b.Right
                    && (entry.World.X + entry.World.W - camera.X) * camera.Zoom > b.Left
                    && (entry.World.Y - camera.Y) * camera.Zoom < b.Bottom
                    && (entry.World.Y + entry.World.H - camera.Y) * camera.Zoom > b.Top;
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
                var logo = (Control.FromHandle(entry.HWnd) as PlaceholderForm)?.OverviewLogo;
                bool cachedThumbnail = _thumbnails.HasVisibleSource(entry.HWnd)
                    || (_cardSources.TryGetValue(entry.HWnd, out var cachedSource) && _thumbnails.HasVisibleSource(cachedSource));
                if (cachedThumbnail) image = null;
                (gestureCards ??= new()).Add(new(entry.HWnd, GestureTitle(entry.HWnd), entry.World, priority,
                    image, new ArraySegment<WorldRect>(occluders, 0, i), ImageOpacity: image != null || cachedThumbnail ? 1 : 0,
                    LabelOpacity: image != null ? 0 : 1, Logo: logo,
                    LaunchStatus: (Control.FromHandle(entry.HWnd) as PlaceholderForm)?.LaunchStatus));
                hasRestoreFallback |= restoreFallback;
            }
            else if (visible && _overviewItemIds.TryGetValue(entry.HWnd, out var id))
            {
                long stagger = CalculateStagger(entry.World, camera, focal);
                if (_placeholderTitles.TryGetValue(entry.HWnd, out var title)
                    && Control.FromHandle(entry.HWnd) is PlaceholderForm card)
                {
                    bool simple = _wm.OverviewUsesSimpleCards;
                    // The transition controller retains the outgoing frozen image
                    // only until the fade ends; distant cards create no new bitmap.
                    if (simple) card.ReleaseOverviewImage();
                    var image = simple ? null : card.CachedOverviewImage;
                    double projectedEdge = Math.Max(entry.World.W, entry.World.H) * camera.Zoom;
                    int dimension = OverviewCardImageQueue.ImageDimension(projectedEdge);
                    bool upgrade = image is System.Windows.Media.Imaging.BitmapSource bitmap
                        && dimension > Math.Max(bitmap.PixelWidth, bitmap.PixelHeight)
                        && projectedEdge > Math.Max(bitmap.PixelWidth, bitmap.PixelHeight) * 1.25;
                    if (!simple && (image == null || upgrade))
                        _cardImages.RequestImage(id, priority ? -1 : stagger, () =>
                        {
                            if (!card.IsDisposed) card.GetOverviewImage(dimension);
                        });
                    bool underlay = _cardSources.TryGetValue(entry.HWnd, out var source) && CanShowUnderlay(source);
                    inputs.Add(new(id, new(entry.HWnd, title, entry.World, priority,
                        image, new ArraySegment<WorldRect>(occluders, 0, i),
                        Logo: card.OverviewLogo, LaunchStatus: card.LaunchStatus),
                        simple ? OverviewCardMode.Simple : OverviewCardMode.Detailed, underlay, stagger,
                        DetailReady: simple || image != null));
                }
                else
                    inputs.Add(new(id, new(entry.HWnd, "", entry.World, priority, Occluders: new ArraySegment<WorldRect>(occluders, 0, i)),
                        OverviewCardMode.Live, !_wm.UsesCachedPreview(entry.HWnd) && _thumbnails.HasLiveSource(entry.HWnd), stagger));
            }
            else if (_placeholderTitles.ContainsKey(entry.HWnd) && Control.FromHandle(entry.HWnd) is PlaceholderForm offscreen)
                offscreen.ReleaseOverviewImage();
            if (entry.ScreenFixed)
            {
                var rect = _win32.GetWindowRect(entry.HWnd);
                occluders[i] = new WorldRect { X = camera.X + rect.x / camera.Zoom,
                    Y = camera.Y + rect.y / camera.Zoom, W = rect.w / camera.Zoom, H = rect.h / camera.Zoom };
            }
            else occluders[i] = entry.World;
        }
        _cardImages.EndFrame();
        IReadOnlyList<PlaceholderAnnotation> cards;
        if (GesturePreviewsActive)
        {
            _cardAnimationTimer?.Stop();
            cards = gestureCards is { } moving ? moving : Array.Empty<PlaceholderAnnotation>();
        }
        else
        {
            var normalCards = _cardTransitions.Update(inputs, Environment.TickCount64);
            cards = gestureCards == null ? normalCards : normalCards.Concat(gestureCards).ToArray();
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
        if (_composerWindow is { WaitingForBrowser: true })
        {
            if (_annotationToolbar is { IsDisposed: false } toolbar) toolbar.Hide();
            return;
        }
        var camera = SceneCamera;
        bool zooming = CurrentMode == OverviewMode.Zooming;
        _selectionRenderItems=zooming && _selectionLayer!=null?SelectionItems().Where(i=>i.Key.Kind!=CanvasItemKind.Annotation || !_composerPreviewIds.Contains(i.Key.Id)).ToArray():[];
        if (_annotationTool?.Preview is {} annotationPreview)
            _selectionRenderItems=_selectionRenderItems.Select(i=>i.Key.Kind==CanvasItemKind.Annotation && i.Key.Id==annotationPreview.Id
                ?i with { Bounds=annotationPreview.Bounds }:i).ToArray();
        var zones = !zooming || _zones == null ? new List<ZoneView>() : new List<ZoneView>(_zones.Items);
        if (_zoneTool.Gesture.MovingId is { } movingId && _zoneTool.Gesture.Preview is { } moved)
        {
            var movingIds = new HashSet<Guid> { movingId };
            for (int i = 0; i < zones.Count; i++)
            {
                if (_zoneTool.Gesture.Kind == ZoneGestureKind.Move && zones[i].ParentId is { } parent && movingIds.Contains(parent))
                {
                    movingIds.Add(zones[i].Id);
                    if (ZoneLayoutPlanner.TryTranslate(zones[i].Bounds, _zoneTool.Gesture.Dx, _zoneTool.Gesture.Dy, out var child, area:true))
                        zones[i] = zones[i] with { Bounds = child };
                }
                if (zones[i].Id == movingId) zones[i] = zones[i] with { Bounds = moved };
            }
        }
        OverviewAnnotationSnapshot? frame = null;
        OverviewOverlay? firstPass = null;
        foreach (var p in _passes)
            if (p.Visible)
            {
                // Detach the lists once; each monitor only changes viewport data.
                frame ??= OverviewAnnotationSnapshot.Create(zones, camera.X, camera.Y, camera.Zoom, p.Screen.Bounds,
                _zoneTool.Gesture.Kind == ZoneGestureKind.Draw ? _zoneTool.Gesture.Preview : null,
                _zoneTool.Gesture.Armed || _zoneTool.Gesture.Kind == ZoneGestureKind.Draw, _zoneTool.Gesture.Ghosts, _annotationCards,
                !IsStartupIntroActive, _startupIntroFrame, p.Screen.Primary, _interactionError ?? _annotationError)
                with { WindowStats = WindowStats(), MinimizationStatus = _appConfig.EnableAutomaticMinimization
                    ? "Auto-minimize: on" : "Auto-minimize: off" };
                var b = p.Screen.Bounds;
                p.StageAnnotations(frame with { Screen = new(b.X, b.Y, b.Width, b.Height), ShowIntroTitle = p.Screen.Primary });
                firstPass ??= p;
            }
        // Every annotation window is owned by this UI thread/Dispatcher. Prepare
        // all viewports, then service render-priority work once for the whole frame.
        foreach (var p in _passes) if (p.Visible) p.PrepareAnnotations();
        firstPass?.FlushAnnotations();
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
