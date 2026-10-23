using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace CanvasDesktop;

internal sealed partial class OverviewManager
{
    private AnnotationPersistence? _annotationStore;
    private AnnotationDocument? _annotationDocument;
    private AnnotationCanvasTool? _annotationTool;
    private ICanvasTool? _annotationRouter;
    private CanvasAnnotationLayer? _annotationLayer;
    private Func<Guid> _annotationDesktop=()=>Guid.Empty;
    private IVirtualDesktops? _annotationDesktops;
    private AnnotationToolbar? _annotationToolbar;
    private AnnotationTextEditor? _annotationEditor;
    private AnnotationShapeEditor? _shapeEditor;
    private long _annotationEditGeneration;
    private string? _annotationError;
    public bool CanvasInputSuspended => _composerWindow != null || _annotationEditor != null || _shapeEditor != null || (_annotationTool?.HasPendingInteraction == true) || (_selectionTool?.HasPendingInteraction == true);
    internal AnnotationDocument? Annotations => _annotationDocument;
    internal AnnotationCanvasTool? AnnotationTool => _annotationTool;
    internal AnnotationTextEditor? AnnotationEditor => _annotationEditor;
    internal AnnotationShapeEditor? ShapeEditor => _shapeEditor;

    public void AttachAnnotations(IVirtualDesktops desktops,string? path=null)
    {
        if(_annotationDocument!=null) throw new InvalidOperationException("Annotations are already attached.");
        _annotationDesktop=()=>desktops.CurrentDesktopId;
        _annotationDesktops=desktops;
        _annotationStore=new(path??Path.Combine(AppConfig.ConfigDir,"annotations.json"));
        _annotationDocument=_annotationStore.Document;
        _annotationTool=new(_annotationDocument,_annotationDesktop);
        _annotationLayer=new(_annotationDocument,_annotationTool,_annotationDesktop,()=>CurrentMode==OverviewMode.Zooming && !IsStartupIntroActive, id => _composerPreviewIds.Contains(id));
        AttachSelection();
        _annotationRouter=new OverviewAnnotationToolRouter(_annotationTool,_zoneTool,_selectionTool!, () => { if (_annotationTool.SelectedId is {} id) Selection.Replace([new(CanvasItemKind.Annotation,id)]); });
        _composerLayer = new(() => CurrentMode == OverviewMode.Zooming && !IsStartupIntroActive);
        _drawingLayers=[_annotationLayer, _composerLayer, _selectionLayer!];
        foreach(var pass in _passes) pass.SetDrawingLayers(_drawingLayers);
        _tools.SetTool(_annotationRouter);
        _annotationDocument.Changed+=OnAnnotationsChanged;
        _annotationTool.EditTextRequested+=QueueAnnotationTextEdit;
        desktops.DesktopChanged+=OnAnnotationDesktopChanged;
    }
    private void OnAnnotationsChanged()
    {
        ObserveTourNotes();
        _annotationStore?.Save();
        if (_annotationDocument!.LastChange==AnnotationChangeKind.Edit) _placeholderWorkspace?.ClearZoneRedoHistory(_annotationDocument.LastChangedDesktop);
        var available=SelectionItems().Select(i=>i.Key).ToHashSet();
        Selection.Replace(Selection.Ids.Where(available.Contains).ToArray());
        _composerWindow?.CanvasChanged(CurrentComposerNotes());
        RefreshToolPreview();
    }
    private void OnAnnotationDesktopChanged()
    {
        PauseGuidedTutorial();
        CancelInteraction(); Selection.SwitchDesktop(_annotationDesktop()); _proximityCircle=null; _annotationTool?.ClearSelection(); _annotationTool?.SetMode(AnnotationMode.Navigate);
        RefreshToolPreview();
    }
    internal void SelectAnnotationMode(AnnotationMode mode)
    {
        if(_annotationTool==null) return;
        StopTutorialTravel();
        CancelInteraction(); _zoneTool.Cancel(); _annotationTool.SetMode(mode); _annotationError=null;
        if (mode is not (AnnotationMode.Navigate or AnnotationMode.Select)) Selection.Clear();
        else if (mode==AnnotationMode.Select && Selection.Ids.Count==1 && Selection.Ids.First().Kind==CanvasItemKind.Annotation)
            _annotationTool.Select(Selection.Ids.First().Id);
        _tools.SetTool(_annotationRouter); RefreshToolPreview(); FocusOverview();
    }
    private void FocusOverview()
    {
        if(!_disposed && CurrentMode==OverviewMode.Zooming) _passes.FirstOrDefault(p=>p.Visible)?.Activate();
    }
    private string? AnnotationHint => _annotationTool?.Mode switch
    {
        AnnotationMode.Select => $"{Selection.Ids.Count} selected  ·  Drag empty space: select  ·  Ctrl+click: toggle  ·  Drag selected: move group  ·  Middle drag: pan",
        AnnotationMode.Arrow => "Drag to draw an arrow  ·  Shift: constrain direction  ·  Middle drag: pan  ·  Esc: cancel / Windows mode",
        AnnotationMode.Rectangle or AnnotationMode.Ellipse => "Drag to draw a shape  ·  Shift: equal sides  ·  Middle drag: pan  ·  Esc: cancel / Windows mode",
        AnnotationMode.Text => "Click to add a note  ·  Ctrl+Enter: save text  ·  Esc: cancel  ·  Middle drag: pan",
        AnnotationMode.Navigate => "Ctrl+click: select windows  ·  Select: rectangle  ·  Project name: select group  ·  Middle drag: pan",
        _ => null
    };
    private void RefreshAnnotationUi()
    {
        if(_annotationTool==null) return;
        if(_disposed || CurrentMode!=OverviewMode.Zooming || IsStartupIntroActive)
        { _annotationToolbar?.Hide(); return; }
        var pass=_passes.FirstOrDefault(p=>p.Visible && p.Screen.Primary)??_passes.FirstOrDefault(p=>p.Visible);
        if(pass==null) return;
        if (_annotationToolbar?.IsDisposed == true) _annotationToolbar = null;
        if(_annotationToolbar==null)
        {
            _annotationToolbar=new();
            _annotationToolbar.ModeRequested+=SelectAnnotationMode;
            _annotationToolbar.ComposerRequested+=() => OpenCanvasComposer();
            _annotationToolbar.NearbyRequested+=radius => SelectNearby(radius);
            _annotationToolbar.SetSelectionRadius(_selectionRadius);
            _annotationToolbar.ColorRequested+=color=>ChangeAnnotationStyle(color,_annotationTool.Selected?.Stroke ?? _annotationTool.Stroke);
            _annotationToolbar.StrokeRequested+=stroke=>ChangeAnnotationStyle(_annotationTool.Selected?.Color ?? _annotationTool.Color,stroke);
            _annotationToolbar.EditRequested+=()=> { if (_annotationTool?.Selected is {} item) { if (item.Kind == AnnotationKind.Text) QueueAnnotationTextEdit(item); else QueueAnnotationShapeEdit(item); } };
            _annotationToolbar.UndoRequested+=()=>AnnotationHistory(false);
            _annotationToolbar.RedoRequested+=()=>AnnotationHistory(true);
            _annotationToolbar.DeleteRequested+=DeleteAnnotation;
            _annotationToolbar.RetrySaveRequested+=()=>
            {
                _annotationStore?.Save(); RefreshAnnotationUi();
                if(_annotationStore?.Status is {} message) MessageBox.Show(_annotationToolbar,message,"Canvas annotations");
                FocusOverview();
            };
            _annotationToolbar.KeyDown+=(_,e)=>HandleKeyDown(pass,e);
        }
        _annotationToolbar.UpdateState(_annotationTool.Mode,_annotationError??_annotationStore?.Status,Selection.Ids.Count);
        if(!_annotationToolbar.Visible)
        {
            _annotationToolbar.Show(pass);
            var area=pass.Screen.WorkingArea;
            _annotationToolbar.Location=new Point(Math.Max(area.Left,area.Left+(area.Width-_annotationToolbar.Width)/2),area.Bottom-_annotationToolbar.Height-24);
        }
    }
    private void ChangeAnnotationStyle(string color,double stroke)
    {
        CancelInteraction();
        try { _annotationTool?.SetStyle(color,stroke); }
        catch(ArgumentException ex) { _annotationError=ex.Message; }
        RefreshToolPreview(); FocusOverview();
    }
    private void DeleteAnnotation() { CancelInteraction(); _annotationTool?.Delete(); RefreshToolPreview(); FocusOverview(); }
    private void AnnotationHistory(bool redo)
    {
        CancelInteraction();
        if (!ReplayCanvasHistory(redo)) _annotationError=_placeholderWorkspace?.Status??"No matching undo step is available. The group may have changed.";
        RefreshZoneLayout(); RefreshToolPreview(); FocusOverview();
    }
    private bool HandleAnnotationKey(KeyEventArgs e)
    {
        if(_annotationEditor!=null || _shapeEditor!=null || _composerWindow!=null) return true;
        if(_annotationTool==null || _annotationTool.Mode==AnnotationMode.Navigate) return false;
        if(e.Control && !e.Alt && (e.KeyCode==Keys.Z || e.KeyCode==Keys.Y)) AnnotationHistory(e.KeyCode==Keys.Y || e.Shift);
        else if(e.KeyCode==Keys.Delete || e.KeyCode==Keys.Back) DeleteAnnotation();
        else if(e.KeyCode==Keys.Enter && _annotationTool.Selected is { } item) { if (item.Kind == AnnotationKind.Text) QueueAnnotationTextEdit(item); else QueueAnnotationShapeEdit(item); }
        else
        {
            if (_annotationTool.Mode==AnnotationMode.Navigate) return false;
            // Annotation mode owns typing/navigation keys; project-area shortcuts stay in Windows mode.
            e.Handled=true; e.SuppressKeyPress=true; return true;
        }
        e.Handled=true; e.SuppressKeyPress=true; return true;
    }
    private void QueueAnnotationTextEdit(CanvasAnnotation item)
    {
        if(_passes.Count==0) return;
        long generation=++_annotationEditGeneration;
        _passes[0].BeginInvoke((Action)(()=>
        {
            if(_disposed || generation!=_annotationEditGeneration || item.DesktopId!=_annotationDesktop() || CurrentMode!=OverviewMode.Zooming) return;
            OpenAnnotationTextEdit(item);
        }));
    }
    private void OpenAnnotationTextEdit(CanvasAnnotation item)
    {
        CancelAnnotationEdit();
        if (_annotationDocument!.Items.Any(a=>a.Id==item.Id)) Selection.Replace([new(CanvasItemKind.Annotation,item.Id)]);
        _annotationTool?.Select(item.Id);
        var point=new Point((int)Math.Round((item.Bounds.X-_camera.X)*_camera.Zoom),(int)Math.Round((item.Bounds.Y-_camera.Y)*_camera.Zoom));
        var pass=_passes.FirstOrDefault(p=>p.Screen.Bounds.Contains(point))??_passes[0];
        var editor=new AnnotationTextEditor(item); _annotationEditor=editor;
        var area=pass.Screen.WorkingArea;
        editor.Location=new Point(Math.Clamp(point.X,area.Left,Math.Max(area.Left,area.Right-editor.Width)),Math.Clamp(point.Y,area.Top,Math.Max(area.Top,area.Bottom-editor.Height)));
        editor.Finished+=save=>
        {
            if(_annotationEditor!=editor) return;
            string text=editor.Editor.Text;
            if(save && !string.IsNullOrWhiteSpace(text) && item.DesktopId==_annotationDesktop())
            {
                try { _annotationDocument!.Put(item with { Text=text }); Selection.Replace([new(CanvasItemKind.Annotation,item.Id)]); _annotationTool?.Select(item.Id); }
                catch(ArgumentException ex) { MessageBox.Show(editor,ex.Message,"Canvas note"); return; }
            }
            CancelAnnotationEdit(); RefreshToolPreview(); FocusOverview();
        };
        editor.Show(pass); editor.Activate();
    }
    internal void QueueAnnotationShapeEdit(CanvasAnnotation item)
    {
        if(_passes.Count==0) return;
        long generation=++_annotationEditGeneration;
        _passes[0].BeginInvoke((Action)(()=>
        {
            if(_disposed || generation!=_annotationEditGeneration || item.DesktopId!=_annotationDesktop() || CurrentMode!=OverviewMode.Zooming) return;
            OpenAnnotationShapeEdit(item);
        }));
    }
    private void OpenAnnotationShapeEdit(CanvasAnnotation item)
    {
        CancelAnnotationEdit();
        if (_annotationDocument!.Items.Any(a=>a.Id==item.Id)) Selection.Replace([new(CanvasItemKind.Annotation,item.Id)]);
        _annotationTool?.Select(item.Id);
        var point=new Point((int)Math.Round((item.Bounds.X-_camera.X)*_camera.Zoom),(int)Math.Round((item.Bounds.Y-_camera.Y)*_camera.Zoom));
        var pass=_passes.FirstOrDefault(p=>p.Screen.Bounds.Contains(point))??_passes[0];
        var editor=new AnnotationShapeEditor(item, _annotationDocument, _annotationDesktop());
        _shapeEditor=editor;
        var area=pass.Screen.WorkingArea;
        editor.Location=new Point(Math.Clamp(point.X,area.Left,Math.Max(area.Left,area.Right-editor.Width)),Math.Clamp(point.Y,area.Top,Math.Max(area.Top,area.Bottom-editor.Height)));
        editor.Finished+=result=>
        {
            if(_shapeEditor!=editor) return;
            if(result.Delete)
            {
                _annotationDocument!.Delete(item.DesktopId, item.Id);
                Selection.Clear(); _annotationTool?.ClearSelection();
            }
            else if(result.Save && item.DesktopId==_annotationDesktop())
            {
                try
                {
                    _annotationDocument!.Update(result.UpdatedItem, result.ZOrder);
                    Selection.Replace([new(CanvasItemKind.Annotation,item.Id)]);
                    _annotationTool?.Select(item.Id);
                }
                catch(ArgumentException ex) { MessageBox.Show(editor,ex.Message,"Shape editor"); return; }
            }
            CancelAnnotationEdit(); RefreshToolPreview(); FocusOverview();
        };
        editor.Show(pass); editor.Activate();
    }
    private bool CancelAnnotationEdit()
    {
        ++_annotationEditGeneration;
        bool canceled = false;
        if(_annotationEditor is {} editor) { _annotationEditor=null; editor.Dispose(); canceled=true; }
        if(_shapeEditor is {} shapeEditor) { _shapeEditor=null; shapeEditor.Dispose(); canceled=true; }
        return canceled;
    }
    private void DisposeAnnotations()
    {
        CancelCanvasComposer();
        CancelAnnotationEdit();
        _wm.WindowRemoving-=OnSelectionWindowRemoving;
        if(_annotationDesktops!=null) _annotationDesktops.DesktopChanged-=OnAnnotationDesktopChanged;
        if(_annotationDocument!=null) _annotationDocument.Changed-=OnAnnotationsChanged;
        _annotationToolbar?.Dispose(); _annotationToolbar=null;
    }

    internal bool PrepareForUpdate(out string? message)
    {
        message = null;
        if (_annotationEditor != null || _shapeEditor != null || _composerWindow != null)
        { message = "Finish or cancel the open note or AI proposal before restarting to update."; return false; }
        CancelInteraction();
        if (_annotationStore?.Save() == false)
        { message = _annotationStore.Status ?? "Canvas notes could not be saved. The update will wait."; return false; }
        return true;
    }
}

internal sealed class OverviewAnnotationToolRouter(AnnotationCanvasTool annotations, ZoneCanvasTool zones, CanvasSelectionTool selection, Action annotationCompleted) : ICanvasTool
{
    private ICanvasTool? _owner;
    public bool HasPendingInteraction=>annotations.HasPendingInteraction || zones.HasPendingInteraction || selection.HasPendingInteraction;
    public bool PointerDown(CanvasPointer p)
    {
        bool Begin(ICanvasTool tool) { if (!tool.PointerDown(p)) return false; _owner=tool; return true; }
        if (annotations.Mode is not (AnnotationMode.Navigate or AnnotationMode.Select)) return Begin(annotations);
        if (annotations.Mode==AnnotationMode.Select && !p.Modifiers.HasFlag(CanvasModifiers.Control) && annotations.HasHandleAt(p)) return Begin(annotations);
        if (annotations.Mode==AnnotationMode.Navigate && zones.IsBoundaryGesture(p) && Begin(zones)) return true;
        return Begin(selection);
    }
    public void PointerMove(CanvasPointer p)=>_owner?.PointerMove(p);
    public void PointerUp(CanvasPointer p) { var owner=_owner; _owner=null; owner?.PointerUp(p); if (ReferenceEquals(owner,annotations)) annotationCompleted(); }
    public void Cancel() { _owner=null; annotations.Cancel(); zones.Cancel(); selection.Cancel(); }
    public CanvasCursor CursorAt(CanvasPointer p)=>annotations.Mode==AnnotationMode.Navigate?zones.CursorAt(p):annotations.CursorAt(p);
}
