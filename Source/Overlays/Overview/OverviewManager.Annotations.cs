using System;
using System.Collections.Generic;
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
    private ConnectionCanvasTool? _connectionTool;
    private ICanvasTool? _annotationRouter;
    private CanvasAnnotationLayer? _annotationLayer;
    private ITeamTelemetryProvider? _telemetryProvider;
    internal ITeamTelemetryProvider? TelemetryProvider => _telemetryProvider;
    private AiOperationsSectorManager? _sectorManager;
    internal AiOperationsSectorManager? SectorManager => _sectorManager;
    private Func<Guid> _annotationDesktop=()=>Guid.Empty;
    private IVirtualDesktops? _annotationDesktops;
    private AnnotationToolbar? _annotationToolbar;
    private CanvasControlsButton? _controlsButton;
    private CanvasControlsPopup? _controlsPopup;
    internal CanvasControlsButton? ControlsButton => _controlsButton;
    internal CanvasControlsPopup? ControlsPopup => _controlsPopup;
    private AnnotationTextEditor? _annotationEditor;
    private AnnotationShapeEditor? _shapeEditor;
    private long _annotationEditGeneration;
    private string? _annotationError;
    public bool CanvasInputSuspended => BackgroundEditorVisible || CanvasEditorInputSuspended;
    public bool OverviewHotkeySuspended => CanvasEditorInputSuspended || _backgroundEditor?.IsChoosingImage == true;
    private bool CanvasEditorInputSuspended => PeekWorking || PlacingWebResource || _webResourceDialogOpen || _desktopStopSurface != null || _settingsWindow?.IsVisible == true || _composerWindow != null || _annotationEditor != null || _shapeEditor != null || (_annotationTool?.HasPendingInteraction == true) || (_selectionTool?.HasPendingInteraction == true) || (_connectionTool?.HasPendingInteraction == true);

    internal AnnotationDocument? Annotations => _annotationDocument;
    internal AnnotationToolbar? AnnotationToolbar => _annotationToolbar;
    internal AnnotationCanvasTool? AnnotationTool => _annotationTool;
    internal ConnectionCanvasTool? ConnectionTool => _connectionTool;
    internal AnnotationTextEditor? AnnotationEditor => _annotationEditor;
    internal AnnotationShapeEditor? ShapeEditor => _shapeEditor;

    public void AttachAnnotations(IVirtualDesktops desktops,string? path=null)
    {
        if(_annotationDocument!=null) throw new InvalidOperationException("Annotations are already attached.");
        _annotationDesktop=()=>desktops.CurrentDesktopId;
        _annotationDesktops=desktops;
        _annotationStore=new(path??Path.Combine(AppConfig.ConfigDir,"annotations.json"));
        _cameraBookmarks=new(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path??Path.Combine(AppConfig.ConfigDir,"annotations.json")))!,"camera-bookmarks.json"));
        _annotationDocument=_annotationStore.Document;
        _annotationTool=new(_annotationDocument,_annotationDesktop, item => AnnotationVisual.FitText(item));
        _annotationTool.SetMode(AnnotationMode.Select);
        _connectionTool=new(_annotationDocument,_annotationDesktop,SelectionItems,Selection);
        _connectionTool.ConnectionCreated += OnConnectionCreated;
        try
        {
            string repoRoot = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", ".."));
            string statePath = Path.Combine(repoRoot, ".agents", "teams", "state.json");
            _telemetryProvider = new WindowsNativeTelemetryProvider(statePath);
            if (_placeholderWorkspace != null)
            {
                _sectorManager = new AiOperationsSectorManager(_placeholderWorkspace, _annotationDocument, _annotationStore, _telemetryProvider);
            }
            _telemetryProvider.TelemetryUpdated += snapshot =>
            {
                if (_disposed) return;
                if (_passes.Count > 0 && _passes[0].IsHandleCreated)
                {
                    try
                    {
                        Action action = () =>
                        {
                            if (_disposed) return;
                            ReconcileTeamPods(snapshot);
                            ReconcileVisuals();
                        };
                        if (_passes[0].InvokeRequired)
                            _passes[0].BeginInvoke(action);
                        else
                            action();
                    }
                    catch (ObjectDisposedException) { }
                    catch (InvalidOperationException) { }
                }
            };
        }
        catch { }
        _annotationLayer=new(_annotationDocument,_annotationTool,_annotationDesktop,()=>CurrentMode==OverviewMode.Zooming && !IsStartupIntroActive, id => _composerPreviewIds.Contains(id) || _annotationEditor?.CurrentItem.Id == id, () => _windows.Windows.Where(w => !w.ScreenFixed).Select(w => new CanvasRect(w.World.X, w.World.Y, w.World.W, w.World.H)).ToArray(), () => _connectionTool?.Preview, item => _telemetryProvider?.GetStateForAnnotation(item));
        AttachSelection();
        _annotationRouter=new OverviewAnnotationToolRouter(_annotationTool,_zoneTool,_selectionTool!, _connectionTool, () => { if (_annotationTool.SelectedId is {} id) Selection.Replace([new(CanvasItemKind.Annotation,id)]); });
        _composerLayer = new(() => CurrentMode == OverviewMode.Zooming && !IsStartupIntroActive);
        _drawingLayers=[_annotationLayer, _composerLayer, _selectionLayer!, _interactionLayer!];
        foreach(var pass in _passes) pass.SetDrawingLayers(_drawingLayers);
        _tools.SetTool(_annotationRouter);
        _annotationDocument.Changed+=OnAnnotationsChanged;
        _annotationTool.EditTextRequested+=QueueAnnotationTextEdit;
        desktops.DesktopChanged+=OnAnnotationDesktopChanged;
    }
    private void OnConnectionCreated(CanvasAnnotation arrow)
    {
        Selection.Replace([new(CanvasItemKind.Annotation, arrow.Id)]);
        _annotationTool?.Select(arrow.Id);
        if (arrow.TargetId != null)
        {
            QueueAnnotationTextEdit(arrow);
        }
        RefreshToolPreview();
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
        CancelInteraction(); Selection.SwitchDesktop(_annotationDesktop()); _proximityCircle=null; _annotationTool?.ClearSelection(); _annotationTool?.SetMode(AnnotationMode.Select);
        RefreshToolPreview();
    }
    internal void SelectAnnotationMode(AnnotationMode mode)
    {
        if(_annotationTool==null) return;
        if (_annotationEditor is {} activeEditor && !activeEditor.Complete(true, returnFocus: false)) return;
        if (mode == AnnotationMode.Navigate) mode = AnnotationMode.Select;
        _backgroundTool?.Deselect();
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
    private string? AnnotationHint => PeekWorking ? "Working in Canvas Peek · Mouse and keyboard go to app · Choose Return to resume the canvas" : _windowInteraction.Active
        ? "Interacting: mouse and keyboard go to app  ·  Drag header: move  ·  Ctrl+click: select  ·  Esc: Arrange"
        : _interactionError ?? (_interactMode && InteractionToolReady ? "Interact: click live content  ·  Drag header: move window  ·  Ctrl+click: select  ·  Esc: Arrange" : null) ?? (_annotationTool?.Mode switch
    {
        AnnotationMode.Select => $"{Selection.Ids.Count} selected  ·  Drag empty space: select  ·  Ctrl+click: toggle  ·  Drag: move  ·  Space / Right drag: pan  ·  Middle drag: pan",
        AnnotationMode.Arrow => "Drag to draw an arrow  ·  Shift: constrain direction  ·  Middle drag: pan  ·  Esc: Select mode",
        AnnotationMode.Rectangle or AnnotationMode.Ellipse => "Drag to draw a shape  ·  Shift: equal sides  ·  Middle drag: pan  ·  Esc: Select mode",
        AnnotationMode.Text => "Click and type to add a note  \u00b7  Click outside: finish  \u00b7  Corners: scale text  \u00b7  Esc: cancel  \u00b7  Middle drag: pan",
        AnnotationMode.Navigate => $"{Selection.Ids.Count} selected  ·  Drag empty space: select  ·  Ctrl+click: toggle  ·  Drag: move  ·  Space / Right drag: pan  ·  Middle drag: pan",
        _ => null
    });
    private void HideAnnotationUi()
    {
        _annotationToolbar?.Hide();
        _controlsButton?.Hide();
        _controlsPopup?.Hide();
    }

    private void RefreshAnnotationUi()
    {
        if(_annotationTool==null) return;
        if(_disposed || CurrentMode!=OverviewMode.Zooming || IsStartupIntroActive || _composerWaitingForBrowser || _annotationEditor != null)
        {
            HideAnnotationUi();
            return;
        }
        var pass=_passes.FirstOrDefault(p=>p.Visible && p.Screen.Primary)??_passes.FirstOrDefault(p=>p.Visible);
        if(pass==null) return;
        var area=pass.Screen.WorkingArea;

        if (_controlsButton?.IsDisposed == true) _controlsButton = null;
        if (_controlsButton == null)
        {
            _controlsButton = new();
            _controlsButton.ControlsClicked += () => ToggleControlsPopup(pass);
            _controlsButton.InteractionModeRequested += SetInteractMode;
            _controlsButton.KeyDown += (_, e) => HandleKeyDown(pass, e);
        }
        _controlsButton.SetInteractMode(_interactMode);
        if (!_controlsButton.Visible)
        {
            _controlsButton.Show(pass);
            _controlsButton.Location = new Point(area.Right - _controlsButton.Width - 16, area.Top + 14);
        }

        if (_annotationToolbar?.IsDisposed == true) _annotationToolbar = null;
        if(_annotationToolbar==null)
        {
            _annotationToolbar=new();
            _annotationToolbar.ModeRequested+=SelectAnnotationMode;
            _annotationToolbar.ComposerRequested+=() => OpenCanvasComposer();
            _annotationToolbar.WebResourceRequested += ShowAddWebResource;
            _annotationToolbar.BackgroundsRequested += () => BackgroundsRequested?.Invoke();
            _annotationToolbar.ProjectNavigatorRequested += () => ZoneNavigatorRequested?.Invoke();
            _annotationToolbar.ProjectAreasRequested += () => ZonePanelRequested?.Invoke();
            _annotationToolbar.ExportAllWorkflowsRequested += () => ExportCurrentWorkflowToClipboard(null);
            _annotationToolbar.ImportWorkflowRequested += ShowImportWorkflowDialog;
            _annotationToolbar.NearbyRequested+=radius => SelectNearby(radius);
            _annotationToolbar.SetSelectionRadius(_selectionRadius);
            _annotationToolbar.ColorRequested+=color=>ChangeAnnotationStyle(color,_annotationTool.Selected?.Stroke ?? _annotationTool.Stroke);
            _annotationToolbar.StrokeRequested+=stroke=>ChangeAnnotationStyle(_annotationTool.Selected?.Color ?? _annotationTool.Color,stroke);
            _annotationToolbar.EditRequested+=()=> { if (_annotationTool?.Selected is {} item) { if (item.Kind == AnnotationKind.Text) QueueAnnotationTextEdit(item); else if (item.Kind is AnnotationKind.Rectangle or AnnotationKind.Ellipse) QueueAnnotationShapeEdit(item); } };
            _annotationToolbar.FontSizeRequested+=ChangeAnnotationFontSize;
            _annotationToolbar.TextAlignRequested+=align=>ChangeAnnotationTextStyle(align:align);
            _annotationToolbar.EmphasisRequested+=emphasis=>ChangeAnnotationTextStyle(emphasis:emphasis);
            _annotationToolbar.CardFillRequested+=fill=>ChangeAnnotationTextStyle(fill:fill);
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
        var toolbarArea = _appConfig.ShowTaskbarInCanvas ? area : pass.Screen.Bounds;
        _annotationToolbar.UpdateState(new AnnotationToolbarState(
            _annotationTool.Mode, _annotationError ?? _annotationStore?.Status, Selection.Ids.Count, _annotationTool.Selected,
            _annotationTool.Color, _annotationTool.Stroke, _annotationTool.FontSize, _annotationTool.TextAlign,
            _annotationTool.Emphasis, _annotationTool.CardFill,
            CanUndo: (_annotationDocument?.HistoryOrder(_annotationDesktop(), false) ?? 0) > 0 || (_placeholderWorkspace?.ZoneHistoryOrder(false) ?? 0) > 0,
            CanRedo: (_annotationDocument?.HistoryOrder(_annotationDesktop(), true) ?? 0) > 0 || (_placeholderWorkspace?.ZoneHistoryOrder(true) ?? 0) > 0,
            HasSelectedAnnotations: Selection.Ids.Any(id => id.Kind == CanvasItemKind.Annotation),
            AnimateFeedback: !_appConfig.ReduceStartupMotion && !_appConfig.LowQualityMode));
        _annotationToolbar.SetAvailableWidth(toolbarArea.Width);
        if(!_annotationToolbar.Visible)
        {
            _annotationToolbar.Show(pass);
            _annotationToolbar.Location=new Point(Math.Max(toolbarArea.Left,toolbarArea.Left+(toolbarArea.Width-_annotationToolbar.Width)/2),toolbarArea.Bottom-_annotationToolbar.Height);
        }
        else
        {
            _annotationToolbar.Location=new Point(Math.Max(toolbarArea.Left,toolbarArea.Left+(toolbarArea.Width-_annotationToolbar.Width)/2),toolbarArea.Bottom-_annotationToolbar.Height);
        }
    }

    private void ToggleControlsPopup(OverviewOverlay pass)
    {
        if (_disposed || CurrentMode != OverviewMode.Zooming || IsStartupIntroActive || _composerWaitingForBrowser)
        {
            HideAnnotationUi();
            return;
        }
        if (_controlsPopup?.IsDisposed == true) _controlsPopup = null;
        if (_controlsPopup == null)
        {
            _controlsPopup = new();
            _controlsPopup.ClosedRequested += () => { _controlsPopup.Hide(); FocusOverview(); };
            _controlsPopup.KeyDown += (_, e) => HandleKeyDown(pass, e);
        }
        if (_controlsPopup.Visible)
        {
            _controlsPopup.Hide();
            FocusOverview();
        }
        else
        {
            _controlsPopup.Show(pass);
            var area = pass.Screen.WorkingArea;
            _controlsPopup.Location = new Point(Math.Max(area.Left, area.Right - _controlsPopup.Width - 16), area.Top + 50);
            _controlsPopup.Activate();
        }
    }
    private void ChangeAnnotationStyle(string color,double stroke)
    {
        CancelInteraction();
        try { _annotationTool?.SetStyle(color,stroke); }
        catch(ArgumentException ex) { _annotationError=ex.Message; }
        RefreshToolPreview(); FocusOverview();
    }
    private void ChangeAnnotationFontSize(double fontSize)
    {
        CancelInteraction();
        try { _annotationTool?.SetFontSize(fontSize); }
        catch(ArgumentException ex) { _annotationError=ex.Message; }
        RefreshToolPreview(); FocusOverview();
    }
    private void ChangeAnnotationTextStyle(AnnotationTextAlign? align=null,AnnotationTextEmphasis? emphasis=null,AnnotationCardFill? fill=null)
    {
        CancelInteraction();
        try { _annotationTool?.SetTextStyle(align,emphasis,fill); }
        catch(ArgumentException ex) { _annotationError=ex.Message; }
        RefreshToolPreview(); FocusOverview();
    }
    private void DeleteAnnotation()
    {
        CancelInteraction();
        var selectedAnnotations = Selection.Ids.Where(id => id.Kind == CanvasItemKind.Annotation).Select(id => id.Id).ToArray();
        if (selectedAnnotations.Length > 0 && _annotationDocument != null)
        {
            foreach (var id in selectedAnnotations)
                _annotationDocument.Delete(_annotationDesktop(), id);
            Selection.Replace(Selection.Ids.Where(id => id.Kind != CanvasItemKind.Annotation));
        }
        else
        {
            _annotationTool?.Delete();
        }
        CleanOrphanedArrows();
        RefreshToolPreview(); FocusOverview();
    }
    private void AnnotationHistory(bool redo)
    {
        CancelInteraction();
        if (!ReplayCanvasHistory(redo)) _annotationError=_placeholderWorkspace?.Status??"No matching undo step is available. The group may have changed.";
        RefreshZoneLayout(); RefreshToolPreview(); FocusOverview();
    }
    private bool HandleAnnotationKey(KeyEventArgs e)
    {
        if(_annotationEditor!=null || _shapeEditor!=null || _composerWindow!=null) return true;
        if(_annotationTool==null || _annotationTool.Mode is AnnotationMode.Navigate or AnnotationMode.Select)
        {
            if(e.Control && !e.Alt && (e.KeyCode==Keys.Z || e.KeyCode==Keys.Y)) { AnnotationHistory(e.KeyCode==Keys.Y || e.Shift); e.Handled=true; e.SuppressKeyPress=true; return true; }
            if((e.KeyCode==Keys.Delete || e.KeyCode==Keys.Back) && (Selection.Ids.Count > 0 || _annotationTool?.Selected != null)) { DeleteAnnotation(); e.Handled=true; e.SuppressKeyPress=true; return true; }
            if(e.KeyCode==Keys.Enter && _annotationTool?.Selected is { } item)
            {
                if (item.Kind == AnnotationKind.Text) { QueueAnnotationTextEdit(item); e.Handled=true; e.SuppressKeyPress=true; return true; }
                if (item.Kind is AnnotationKind.Rectangle or AnnotationKind.Ellipse) { QueueAnnotationShapeEdit(item); e.Handled=true; e.SuppressKeyPress=true; return true; }
            }
            return false;
        }
        if(e.Control && !e.Alt && (e.KeyCode==Keys.Z || e.KeyCode==Keys.Y)) AnnotationHistory(e.KeyCode==Keys.Y || e.Shift);
        else if(e.KeyCode==Keys.Delete || e.KeyCode==Keys.Back) DeleteAnnotation();
        else if(e.KeyCode==Keys.Enter && _annotationTool.Selected is { } item) { if (item.Kind == AnnotationKind.Text) QueueAnnotationTextEdit(item); else if (item.Kind is AnnotationKind.Rectangle or AnnotationKind.Ellipse) QueueAnnotationShapeEdit(item); }
        else
        {
            e.Handled=true; e.SuppressKeyPress=true; return true;
        }
        e.Handled=true; e.SuppressKeyPress=true; return true;
    }
    private void QueueAnnotationTextEdit(CanvasAnnotation item) => QueueAnnotationTextEdit(item,null);

    private void QueueAnnotationTextEdit(CanvasAnnotation item,Point? screenPoint)
    {
        if(_passes.Count==0) return;
        long generation=++_annotationEditGeneration;
        _passes[0].BeginInvoke((Action)(()=>
        {
            if(_disposed || generation!=_annotationEditGeneration || item.DesktopId!=_annotationDesktop() || CurrentMode!=OverviewMode.Zooming) return;
            OpenAnnotationTextEdit(item,screenPoint);
        }));
    }
    private void OpenAnnotationTextEdit(CanvasAnnotation item,Point? screenPoint)
    {
        CancelAnnotationEdit();
        if (_annotationDocument!.Items.Any(a => a.Id == item.Id)) Selection.Replace([new(CanvasItemKind.Annotation, item.Id)]);
        _annotationTool?.SetMode(AnnotationMode.Select);
        _annotationTool?.Select(item.Id);
        var point = new Point((int)Math.Round((item.Bounds.X - _camera.X) * _camera.Zoom),
            (int)Math.Round((item.Bounds.Y - _camera.Y) * _camera.Zoom));
        var projected = new System.Windows.Rect(point.X,point.Y,item.Bounds.Width*_camera.Zoom,item.Bounds.Height*_camera.Zoom);
        var clickedPass = screenPoint is {} click ? _passes.FirstOrDefault(p => p.Screen.Bounds.Contains(click)) : null;
        var pass = clickedPass ?? _passes.OrderByDescending(p =>
        {
            var screen = p.Screen.WorkingArea;
            var intersection = System.Windows.Rect.Intersect(projected,new System.Windows.Rect(screen.X,screen.Y,screen.Width,screen.Height));
            return intersection.IsEmpty ? 0 : intersection.Width*intersection.Height;
        }).First();
        var editor = new AnnotationTextEditor(item);
        _annotationEditor = editor;
        double dpi = pass.DeviceDpi / 96.0;
        var area = pass.Screen.Bounds;
        editor.SetPlacement(new CanvasViewTransform(_camera.X, _camera.Y, _camera.Zoom,
            new(area.X, area.Y, area.Width, area.Height), dpi, dpi), pass.Screen.WorkingArea);
        editor.Finished += save =>
        {
            if (_annotationEditor != editor) return;
            bool focus = editor.ReturnFocusOnFinish;
            if (save && item.DesktopId == _annotationDesktop())
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(editor.Editor.Text))
                    {
                        if (item.Kind != AnnotationKind.Arrow)
                        {
                            _annotationDocument!.Delete(item.DesktopId, item.Id);
                            Selection.Clear(); _annotationTool?.ClearSelection();
                        }
                        else
                        {
                            _annotationDocument!.Put(editor.CurrentItem with { Text = "" });
                            Selection.Replace([new(CanvasItemKind.Annotation, item.Id)]); _annotationTool?.Select(item.Id);
                        }
                    }
                    else
                    {
                        _annotationDocument!.Put(editor.CurrentItem);
                        Selection.Replace([new(CanvasItemKind.Annotation, item.Id)]); _annotationTool?.Select(item.Id);
                    }
                }
                catch (ArgumentException ex) { _annotationError = ex.Message; editor.RejectCompletion(ex.Message); return; }
            }
            CancelAnnotationEdit(); RefreshToolPreview();
            if (focus) FocusOverview();
        };
        RefreshToolPreview();
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
    internal void CleanOrphanedArrows()
    {
        if (_annotationDocument == null) return;
        var validIds = SelectionItems().Select(i => i.Key.Id).ToHashSet();
        var currentDesktop = _annotationDesktop();
        var arrows = _annotationDocument.Items
            .Where(a => a.DesktopId == currentDesktop && a.Kind == AnnotationKind.Arrow)
            .ToList();

        bool anyRemoved = false;
        foreach (var arrow in arrows)
        {
            bool sourceInvalid = arrow.SourceId.HasValue && !validIds.Contains(arrow.SourceId.Value);
            bool targetInvalid = arrow.TargetId.HasValue && !validIds.Contains(arrow.TargetId.Value);
            if (sourceInvalid || targetInvalid)
            {
                _annotationDocument.Delete(currentDesktop, arrow.Id);
                anyRemoved = true;
            }
        }
        if (anyRemoved)
        {
            _annotationStore?.Save();
        }
    }

    internal void ReconcileConnectedArrows()
    {
        if (_annotationDocument == null) return;
        var currentDesktop = _annotationDesktop();
        var connectedArrows = _annotationDocument.Items
            .Where(a => a.DesktopId == currentDesktop && a.Kind == AnnotationKind.Arrow && (a.SourceId.HasValue || a.TargetId.HasValue))
            .ToList();
        if (connectedArrows.Count == 0) return;

        var boundsById = new Dictionary<Guid, CanvasRect>();
        foreach (var item in SelectionItems())
        {
            boundsById[item.Key.Id] = item.Bounds;
        }

        var updated = new List<CanvasAnnotation>();
        foreach (var arrow in connectedArrows)
        {
            CanvasRect srcBounds = default;
            bool hasSrc = arrow.SourceId.HasValue && boundsById.TryGetValue(arrow.SourceId.Value, out srcBounds);
            CanvasRect tgtBounds = default;
            bool hasTgt = arrow.TargetId.HasValue && boundsById.TryGetValue(arrow.TargetId.Value, out tgtBounds);
            if (!hasSrc && !hasTgt) continue;

            CanvasPoint newA, newB;
            if (hasSrc && hasTgt)
            {
                (newA, newB) = AnnotationGeometry.ComputePortAnchors(srcBounds, arrow.SourcePort, tgtBounds, arrow.TargetPort);
            }
            else if (hasSrc)
            {
                (newA, _) = AnnotationGeometry.ComputePortAnchors(srcBounds, arrow.SourcePort, new CanvasRect(arrow.B.X, arrow.B.Y, 0, 0), ConnectionPort.Auto);
                newB = arrow.B;
            }
            else
            {
                (_, newB) = AnnotationGeometry.ComputePortAnchors(new CanvasRect(arrow.A.X, arrow.A.Y, 0, 0), ConnectionPort.Auto, tgtBounds, arrow.TargetPort);
                newA = arrow.A;
            }

            if (Math.Abs(newA.X - arrow.A.X) > 0.5 || Math.Abs(newA.Y - arrow.A.Y) > 0.5 ||
                Math.Abs(newB.X - arrow.B.X) > 0.5 || Math.Abs(newB.Y - arrow.B.Y) > 0.5)
            {
                updated.Add(arrow with { A = newA, B = newB });
            }
        }

        if (updated.Count > 0)
        {
            foreach (var arrow in updated)
            {
                _annotationDocument.Put(arrow);
            }
            _annotationStore?.Save();
        }
    }

    internal void ExportCurrentWorkflowToClipboard(Guid? contextEntityId = null)
    {
        if (_placeholderWorkspace == null || _annotationDocument == null) return;
        var desktop = _annotationDesktop();
        Guid? targetZoneId = null;

        if (contextEntityId.HasValue)
        {
            var zone = _placeholderWorkspace.Document.Zones.FirstOrDefault(z => z.DesktopId == desktop && (z.Id == contextEntityId.Value || z.ItemIds.Contains(contextEntityId.Value)));
            if (zone != null) targetZoneId = zone.Id;
        }
        else
        {
            var selectedZone = Selection.Ids.FirstOrDefault(id => id.Kind == CanvasItemKind.Project);
            if (selectedZone != default)
            {
                targetZoneId = selectedZone.Id;
            }
            else
            {
                var selectedWindow = Selection.Ids.FirstOrDefault(id => id.Kind == CanvasItemKind.Window);
                if (selectedWindow != default)
                {
                    var zone = _placeholderWorkspace.Document.Zones.FirstOrDefault(z => z.DesktopId == desktop && z.ItemIds.Contains(selectedWindow.Id));
                    if (zone != null) targetZoneId = zone.Id;
                }
            }
        }

        var workflowService = new WorkflowService(_placeholderWorkspace, _annotationDocument, _annotationStore);
        string json = workflowService.ExportWorkflowJson(desktop, targetZoneId);
        try
        {
            Clipboard.SetText(json);
            _annotationError = null;
        }
        catch (Exception ex)
        {
            _annotationError = $"Could not copy workflow JSON to clipboard: {ex.Message}";
        }
    }

    internal void ShowImportWorkflowDialog()
    {
        if (_placeholderWorkspace == null || _annotationDocument == null) return;
        var desktop = _annotationDesktop();
        var center = _camera.WorldFromVirtual(_screens.PrimaryWorkingArea.X + _screens.PrimaryWorkingArea.Width / 2, _screens.PrimaryWorkingArea.Y + _screens.PrimaryWorkingArea.Height / 2);
        var origin = new CanvasPoint(center.x - 400, center.y - 300);

        string? jsonToImport = null;

        try
        {
            if (Clipboard.ContainsText())
            {
                string clip = Clipboard.GetText();
                if (clip.Contains("nodes", StringComparison.OrdinalIgnoreCase) || clip.Contains("windows", StringComparison.OrdinalIgnoreCase))
                {
                    var choice = MessageBox.Show(
                        "Found workflow data on the clipboard. Do you want to import from clipboard?\n\n(Choose 'No' to pick a .json file instead)",
                        "Import Workflow",
                        MessageBoxButtons.YesNoCancel,
                        MessageBoxIcon.Question);

                    if (choice == DialogResult.Yes) jsonToImport = clip;
                    else if (choice == DialogResult.Cancel) return;
                }
            }
        }
        catch { }

        if (jsonToImport == null)
        {
            using var dialog = new OpenFileDialog
            {
                Title = "Import Canvas Workflow",
                Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
                RestoreDirectory = true
            };
            if (dialog.ShowDialog() != DialogResult.OK) return;
            try
            {
                jsonToImport = File.ReadAllText(dialog.FileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not read workflow file: {ex.Message}", "Import Workflow", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
        }

        var workflowService = new WorkflowService(_placeholderWorkspace, _annotationDocument, _annotationStore);
        bool success = workflowService.ImportWorkflowJson(desktop, jsonToImport, origin);
        if (success)
        {
            RefreshZoneLayout();
            RefreshOverviewWindows();
            ReconcileVisuals();
            MessageBox.Show("Workflow imported successfully.", "Canvas Workflows", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        else
        {
            MessageBox.Show("The provided data is not a valid Canvas workflow JSON.", "Import Workflow", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void DisposeAnnotations()
    {
        CancelCanvasComposer();
        CancelAnnotationEdit();
        _wm.WindowRemoving-=OnSelectionWindowRemoving;
        if(_annotationDesktops!=null) _annotationDesktops.DesktopChanged-=OnAnnotationDesktopChanged;
        if(_annotationDocument!=null) _annotationDocument.Changed-=OnAnnotationsChanged;
        _annotationToolbar?.Dispose(); _annotationToolbar=null;
        _controlsPopup?.Dispose(); _controlsPopup=null;
        _controlsButton?.Dispose(); _controlsButton=null;
        _telemetryProvider?.Dispose(); _telemetryProvider=null;
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

    private void ReconcileTeamPods(TeamTelemetrySnapshot snapshot)
    {
        if (_sectorManager == null || _placeholderWorkspace == null)
            return;

        var desktop = _annotationDesktop();
        bool podsMutated = false;

        if (snapshot.Teams != null && snapshot.Teams.Count > 0)
        {
            string? cvlJson = null;

            foreach (var team in snapshot.Teams.Values)
            {
                if (string.Equals(team.Status, "Active", StringComparison.OrdinalIgnoreCase))
                {
                    if (!_sectorManager.HasPod(team.TaskId))
                    {
                        cvlJson ??= LoadTeamArchitectureCvl();
                        if (!string.IsNullOrEmpty(cvlJson))
                        {
                            _sectorManager.SpawnPod(desktop, team, cvlJson);
                            podsMutated = true;
                        }
                    }
                }
                else if (string.Equals(team.Status, "Completed", StringComparison.OrdinalIgnoreCase) ||
                         (team.Roles.TryGetValue("branch-cleaner", out var cleaner) && cleaner.State == AgentExecutionState.Done))
                {
                    if (_sectorManager.HasPod(team.TaskId))
                    {
                        _sectorManager.CleanPod(desktop, team.TaskId);
                        podsMutated = true;
                    }
                }
            }

            // Orphan sweep: sweep any pods that are no longer active in snapshot
            var activeTaskIds = new HashSet<string>(
                snapshot.Teams.Values.Where(t => string.Equals(t.Status, "Active", StringComparison.OrdinalIgnoreCase)).Select(t => t.TaskId),
                StringComparer.OrdinalIgnoreCase);

            foreach (var spawnedTaskId in _sectorManager.SpawnedPods.Keys.ToList())
            {
                if (!activeTaskIds.Contains(spawnedTaskId))
                {
                    _sectorManager.CleanPod(desktop, spawnedTaskId);
                    podsMutated = true;
                }
            }
        }
        else
        {
            // If teams is null or empty, all existing pods are orphaned
            foreach (var spawnedTaskId in _sectorManager.SpawnedPods.Keys.ToList())
            {
                _sectorManager.CleanPod(desktop, spawnedTaskId);
                podsMutated = true;
            }
        }

        if (podsMutated)
        {
            RefreshZoneLayout();
        }
    }

    private static string? LoadTeamArchitectureCvl(string? templatePath = null)
    {
        try
        {
            if (string.IsNullOrEmpty(templatePath))
            {
                var config = TeamTelemetryConfig.Load();
                templatePath = !string.IsNullOrEmpty(config.CvlTemplate)
                    ? config.CvlTemplate
                    : TeamTelemetryConfig.DefaultCvlTemplate;
            }

            if (Path.IsPathRooted(templatePath) && File.Exists(templatePath))
            {
                return File.ReadAllText(templatePath);
            }

            var candidates = new[]
            {
                templatePath,
                Path.Combine(Environment.CurrentDirectory, templatePath),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, templatePath),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", templatePath),
                // Fallbacks to default template
                Path.Combine(Environment.CurrentDirectory, TeamTelemetryConfig.DefaultCvlTemplate),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, TeamTelemetryConfig.DefaultCvlTemplate),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", TeamTelemetryConfig.DefaultCvlTemplate)
            };

            foreach (var c in candidates)
            {
                if (!string.IsNullOrEmpty(c) && File.Exists(c))
                    return File.ReadAllText(c);
            }
        }
        catch { }
        return null;
    }
}

internal sealed class OverviewAnnotationToolRouter(AnnotationCanvasTool annotations, ZoneCanvasTool zones, CanvasSelectionTool selection, ConnectionCanvasTool connections, Action annotationCompleted) : ICanvasTool
{
    private ICanvasTool? _owner;
    public bool HasPendingInteraction=>annotations.HasPendingInteraction || zones.HasPendingInteraction || selection.HasPendingInteraction || connections.HasPendingInteraction;
    public bool PointerDown(CanvasPointer p)
    {
        bool Begin(ICanvasTool tool) { if (!tool.PointerDown(p)) return false; _owner=tool; return true; }
        if (annotations.Mode is not (AnnotationMode.Navigate or AnnotationMode.Select)) return Begin(annotations);
        if (annotations.Mode==AnnotationMode.Select && !p.Modifiers.HasFlag(CanvasModifiers.Control) && annotations.HasHandleAt(p)) return Begin(annotations);
        if (annotations.Mode==AnnotationMode.Select && !p.Modifiers.HasFlag(CanvasModifiers.Control) && connections.FindHandleAt(p) != null && Begin(connections)) return true;
        if (zones.IsBoundaryGesture(p) && Begin(zones)) return true;
        return Begin(selection);
    }
    public void PointerMove(CanvasPointer p)
    {
        if (_owner != null) _owner.PointerMove(p);
        else connections.UpdateHover(p);
    }
    public void PointerUp(CanvasPointer p) { var owner=_owner; _owner=null; owner?.PointerUp(p); if (ReferenceEquals(owner,annotations)) annotationCompleted(); }
    public void Cancel() { _owner=null; annotations.Cancel(); zones.Cancel(); selection.Cancel(); connections.Cancel(); }
    public CanvasCursor CursorAt(CanvasPointer p)
    {
        if (connections.HasPendingInteraction) return CanvasCursor.Crosshair;
        if (annotations.Mode is not (AnnotationMode.Navigate or AnnotationMode.Select)) return annotations.CursorAt(p);
        if (annotations.HasHandleAt(p)) return annotations.CursorAt(p);
        if (annotations.Mode==AnnotationMode.Select && connections.FindHandleAt(p) != null) return CanvasCursor.Crosshair;
        var zoneCursor = zones.CursorAt(p);
        if (zoneCursor != CanvasCursor.Default) return zoneCursor;
        return annotations.CursorAt(p);
    }

}
