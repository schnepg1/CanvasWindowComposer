using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace CanvasDesktop;

internal sealed partial class OverviewManager
{
    internal CanvasSelection Selection { get; } = new();
    private CanvasSelectionTool? _selectionTool;
    private SelectionAnnotationLayer? _selectionLayer;
    private IReadOnlyList<CanvasSelectionItem> _selectionRenderItems=[];
    internal CanvasSelectionTool? SelectionTool => _selectionTool;
    private readonly Dictionary<IntPtr, (Guid Id, uint Pid)> _selectionWindows = [];
    private double _selectionRadius = 1200;
    private (CanvasPoint Center, double Radius)? _proximityCircle;

    private void AttachSelection()
    {
        _selectionRadius = _appConfig.SelectionRadius;
        Selection.SwitchDesktop(_annotationDesktop());
        _wm.WindowRemoving+=OnSelectionWindowRemoving;
        _selectionTool = new(Selection, SelectionItems, HitSelectionItem, () => _annotationTool?.Mode == AnnotationMode.Select);
        _selectionTool.MoveRequested += MoveSelection;
        _selectionLayer = new(Selection, () => _selectionRenderItems, _selectionTool, () => _proximityCircle,
            () => CurrentMode == OverviewMode.Zooming && !IsStartupIntroActive);
        Selection.Changed += () =>
        {
            _proximityCircle=null;
            var ids = Selection.Ids;
            if (ids.Count == 1 && ids.First().Kind == CanvasItemKind.Annotation) _annotationTool?.Select(ids.First().Id);
            else _annotationTool?.ClearSelection();
            _composerWindow?.Discard("Selection changed. Reopen the composer to share the new selection.");
        };
    }

    private void OnSelectionWindowRemoving(IntPtr handle) => _selectionWindows.Remove(handle);

    internal IReadOnlyList<CanvasSelectionItem> SelectionItems()
    {
        var result = new List<CanvasSelectionItem>();
        static CanvasRect Rect(WorldRect b) => new(b.X,b.Y,b.W,b.H);
        if (_annotationDocument != null)
            result.AddRange(_annotationDocument.Items.Where(a => a.DesktopId == _annotationDesktop())
                .Select(a => new CanvasSelectionItem(new(CanvasItemKind.Annotation,a.Id),a.Bounds,a.Text,Annotation:a)));
        if (_placeholderWorkspace != null)
            result.AddRange(_placeholderWorkspace.Document.Items.Where(i => i.DesktopId == _annotationDesktop() && !i.Bounds.PinnedToScreen)
                .Select(i => new CanvasSelectionItem(new(CanvasItemKind.Window,i.Id),Rect(i.Bounds),i.Name.Length>0?i.Name:i.Title)));
        var mapped = _overviewItemIds;
        var visible = _windows.Windows.Where(w => !w.ScreenFixed).ToArray();
        foreach (var stale in _selectionWindows.Keys.Where(h => !_mainCanvas.HasWindow(h) || _win32.GetWindowProcessId(h)!=_selectionWindows[h].Pid).ToArray()) _selectionWindows.Remove(stale);
        foreach (var w in visible)
        {
            if (mapped.ContainsKey(w.HWnd)) continue;
            uint pid = _win32.GetWindowProcessId(w.HWnd);
            if (!_selectionWindows.TryGetValue(w.HWnd,out var identity) || identity.Pid != pid)
                _selectionWindows[w.HWnd] = identity = (Guid.NewGuid(),pid);
            result.Add(new(new(CanvasItemKind.Window,identity.Id),Rect(w.World),_win32.GetWindowTitle(w.HWnd)));
        }
        if (_zones != null)
            result.AddRange(_zones.Items.Select(z => new CanvasSelectionItem(new(CanvasItemKind.Project,z.Id),Rect(z.Bounds),z.Name,
                _zones.Members(z.Id).Where(m => m.Assigned).Select(m => new CanvasItemId(CanvasItemKind.Window,m.Id)).ToArray())));
        return result;
    }

    private CanvasItemId? HitSelectionItem(CanvasPointer p)
    {
        if (_zones != null)
            foreach (var z in _zones.Items.Reverse())
                if (ZoneLabelLayout.Get(z.Bounds,p.View) is {} label && label.Contains(p.Position.X,p.Position.Y))
                    return new(CanvasItemKind.Project,z.Id);
        if (_annotationTool?.Hit(p) is {} annotation) return new(CanvasItemKind.Annotation,annotation.Id);
        var pass = _passes.ElementAtOrDefault(p.SurfaceId);
        int index = pass == null ? _windows.HitTest(p.World.X,p.World.Y)
            : HitOverviewWindow(pass,(int)p.Position.X,(int)p.Position.Y,p.World.X,p.World.Y);
        if (index >= 0 && !_windows.Windows[index].ScreenFixed)
        {
            var handle = _windows.Windows[index].HWnd;
            if (_overviewItemIds.TryGetValue(handle,out var id)) return new(CanvasItemKind.Window,id);
            SelectionItems();
            if (_selectionWindows.TryGetValue(handle,out var identity)) return new(CanvasItemKind.Window,identity.Id);
        }
        return null;
    }

    internal void SelectNearby(double radius, bool add = false)
    {
        CancelInteraction();
        var screen = _screens.PrimaryWorkingArea;
        var center = _camera.WorldFromVirtual(screen.X+screen.Width/2,screen.Y+screen.Height/2);
        _selectionRadius = radius;
        if (_appConfig is AppConfig config)
        {
            try { config.SetSelectionRadius(radius); }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            { _annotationError="The radius works for this session, but its preference could not be saved."; }
        }
        Selection.Nearby(new(center.x,center.y),radius,SelectionItems(),add);
        _proximityCircle = (new(center.x,center.y),radius);
        _annotationTool?.SetMode(AnnotationMode.Select);
        RefreshToolPreview(); FocusOverview();
    }

    internal void MoveSelection(CanvasSelectionMove move)
    {
        if (_annotationDocument == null || Selection.Desktop != _annotationDesktop()) return;
        var desktop=_annotationDesktop();
        try
        {
            var current = SelectionItems().ToDictionary(i => i.Key);
            if (move.Before.Any(i => !current.TryGetValue(i.Key,out var now) || now.Bounds != i.Bounds
                || i.Key.Kind==CanvasItemKind.Project && !(i.Members??[]).ToHashSet().SetEquals(now.Members??[])))
                throw new InvalidOperationException("The group changed during the drag. Select it again.");
            var moved = _annotationDocument.Items.Where(a => move.Before.Any(i => i.Key == new CanvasItemId(CanvasItemKind.Annotation,a.Id)))
                .Select(a => a.Translate(move.Dx,move.Dy)).ToArray();
            AnnotationDocument.Validate(moved);
            var savedWindowIds = move.Before.Where(i => i.Key.Kind == CanvasItemKind.Window && _placeholderWorkspace?.Document.Items.Any(w => w.Id == i.Key.Id) == true).Select(i => i.Key.Id).ToArray();
            var areaIds = move.Before.Where(i => i.Key.Kind == CanvasItemKind.Project).Select(i => i.Key.Id).ToArray();
            var before = savedWindowIds.Length+areaIds.Length>0 ? _placeholderWorkspace?.SelectionSnapshot(savedWindowIds,areaIds) : null;
            WorkspaceSelectionPlacement? after = null;
            if (before != null)
            {
                if (before.Items.Count != savedWindowIds.Length || before.Areas.Count != areaIds.Length) throw new InvalidOperationException("A selected item is unavailable.");
                ZoneItemPlacement Translate(ZoneItemPlacement p)
                {
                    if (!ZoneLayoutPlanner.TryTranslate(p.Bounds,move.Dx,move.Dy,out var b)) throw new ArgumentException("The destination is outside the canvas limits.");
                    return p with { Bounds=b };
                }
                if (before.Items.Concat(before.Areas).Any(p => !move.Before.Any(i => i.Key.Id==p.Id && i.Bounds==new CanvasRect(p.Bounds.X,p.Bounds.Y,p.Bounds.W,p.Bounds.H))))
                    throw new InvalidOperationException("The group changed during the drag. Select it again.");
                after = before with { Items=before.Items.Select(Translate).ToArray(),Areas=before.Areas.Select(Translate).ToArray() };
            }
            else if (areaIds.Length > 0) throw new InvalidOperationException("Attach the workspace before moving project areas.");
            var session = new List<(IntPtr Handle, Guid Id, uint Pid, WorldRect Before, WorldRect After)>();
            foreach (var item in move.Before.Where(i => i.Key.Kind == CanvasItemKind.Window && !savedWindowIds.Contains(i.Key.Id)))
            {
                var pair = _selectionWindows.FirstOrDefault(p => p.Value.Id == item.Key.Id);
                if (pair.Key == IntPtr.Zero || _win32.GetWindowProcessId(pair.Key) != pair.Value.Pid
                    || !_mainCanvas.Windows.TryGetValue(pair.Key,out var b) || b.PinnedToScreen || !ZoneLayoutPlanner.TryTranslate(b,move.Dx,move.Dy,out var destination))
                    throw new InvalidOperationException("A selected window changed. Select it again.");
                session.Add((pair.Key,pair.Value.Id,pair.Value.Pid,b,destination));
            }
            string? nativeWarning=null;
            bool Place(bool redo)
            {
                if (_annotationDesktop() != desktop) return false;
                var mapped=_placeholderWorkspace?.GetOverviewItemIds();
                foreach (var p in session)
                    if (mapped?.ContainsKey(p.Handle)==true || !_selectionWindows.TryGetValue(p.Handle,out var identity) || identity.Id!=p.Id || _win32.GetWindowProcessId(p.Handle)!=p.Pid
                        || !_mainCanvas.Windows.TryGetValue(p.Handle,out var b) || !ZoneLayoutPlanner.SamePlacement(b,redo?p.Before:p.After)) return false;
                if (before != null && after != null && !_placeholderWorkspace!.ApplySelectionPlacement(redo?before:after,redo?after:before)) return false;
                bool accepted=CanvasWindowPlacement.ApplySelection(_mainCanvas,_win32,session.Select(p=>(p.Handle,redo?p.After:p.Before)).ToArray());
                nativeWarning=accepted?null:"The group moved, but Windows could not update some restore positions.";
                _annotationError=nativeWarning??_placeholderWorkspace?.Status;
                _wm.Reproject(true); RefreshZoneLayout(); return true;
            }
            // Validate the complete annotation batch before applying the workspace half.
            AnnotationDocument.Validate(_annotationDocument.Items.Select(a => moved.FirstOrDefault(b => b.Id==a.Id)??a));
            bool external = savedWindowIds.Length+areaIds.Length+session.Count>0;
            _annotationDocument.ApplyBatch(_annotationDocument.Revision,_annotationDesktop(),moved,external?Place:null);
            _proximityCircle=null; RefreshZoneLayout();
            _annotationError=nativeWarning??_placeholderWorkspace?.Status;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { _annotationError=ex.Message; }
        RefreshToolPreview();
    }
}
