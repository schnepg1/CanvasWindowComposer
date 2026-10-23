using System;
using System.IO;
using System.Linq;

namespace CanvasDesktop;

internal sealed partial class OverviewManager
{
    private TutorialPersistence? _tutorialStore;
    private TutorialAnnotationLayer? _tutorialLayer;
    private CanvasCamera? _tutorialIntroLanding;
    private bool _tutorialReading;
    internal TutorialPersistence.DesktopNotes? CurrentTutorial => _tutorialStore?.ForDesktop(_annotationDesktop());
    internal CanvasCamera OverviewView => new(_camera.X,_camera.Y,_camera.Zoom);
    public bool TutorialNotesVisible => CurrentTutorial?.Visible == true;

    public void AttachTutorial(string? path=null)
    {
        if(_annotationDocument==null) throw new InvalidOperationException("Attach annotations before the tutorial.");
        if(_tutorialStore!=null) throw new InvalidOperationException("The tutorial is already attached.");
        _tutorialStore=new(path??Path.Combine(AppConfig.ConfigDir,"tutorial.json"));
        _tutorialLayer=new(()=>CurrentTutorial,()=>CurrentMode==OverviewMode.Zooming);
        _drawingLayers=_drawingLayers.Append(_tutorialLayer).ToArray();
        foreach(var pass in _passes)pass.SetDrawingLayers(_drawingLayers);
        if(_tutorialStore.Status is {} error)TrayApp.Log(error);
    }

    private double TutorialDpiScale => Math.Clamp((_passes.FirstOrDefault(p=>p.Screen.Primary)?.DeviceDpi??96)/96.0,1,4);

    private void RefreshTutorialScale()
    {
        if(CurrentTutorial is not {} notes) return;
        try
        {
            var layout=notes.Layout.WithScale(TutorialDpiScale);
            if(ReferenceEquals(layout,notes.Layout)) return;
            _tutorialStore!.SetLayout(_annotationDesktop(),layout);
            if(_tutorialReading && (_startupIntroLanded || GuidedTutorialActive))
            {
                StopTutorialTravel();
                var view=_tour?.Active==true?TourTarget():TutorialReadingView(layout);_camera.SetTo(view.X,view.Y,view.Zoom);
            }
        }
        catch(Exception ex) { TrayApp.Log("Tutorial sizing could not be refreshed: "+ex.Message); }
    }

    private TutorialLayout EnsureTutorialLayout()
    {
        RefreshTutorialScale();
        if(CurrentTutorial is {} existing) return existing.Layout;
        var screen=_screens.PrimaryWorkingArea;
        var camera=_mainCanvas.Camera;
        var viewport=new CanvasRect(camera.X+screen.X/camera.Zoom,camera.Y+screen.Y/camera.Zoom,screen.Width/camera.Zoom,screen.Height/camera.Zoom);
        var windows=_mainCanvas.Windows.Values.Where(w=>!w.PinnedToScreen).Select(w=>new CanvasRect(w.X,w.Y,w.W,w.H)).ToArray();
        var content=_annotationDocument!.Items.Where(a=>a.DesktopId==_annotationDesktop()).Select(a=>a.Bounds)
            .Concat(_zones?.Items.Select(z=>new CanvasRect(z.Bounds.X,z.Bounds.Y,z.Bounds.W,z.Bounds.H))??[]).ToArray();
        double dpi=TutorialDpiScale;
        var layout=TutorialLayout.Create(_annotationDesktop(),viewport,windows,content,dpi,!_appConfig.DisableZoomHotkey,!_appConfig.DisableAltPan);
        _tutorialStore!.SetLayout(_annotationDesktop(),layout);
        return layout;
    }

    internal CanvasCamera TutorialReadingView(TutorialLayout layout) => TutorialReadingView(layout,layout.Welcome);

    private CanvasCamera TutorialReadingView(TutorialLayout layout,CanvasRect note)
    {
        var area=_screens.PrimaryWorkingArea;
        // Reserve space for the top hint and bottom toolbars. Fit the first note, never the whole desktop.
        double margin=32*layout.Scale, top=64*layout.Scale, bottom=200*layout.Scale;
        double width=Math.Max(1,area.Width-2*margin),height=Math.Max(1,area.Height-top-bottom);
        double zoom=Math.Clamp(Math.Min(width/note.Width,height/note.Height),OverviewCamera.ZoomMin,OverviewCamera.ZoomMax);
        double x=area.X+area.Width/2.0, y=area.Y+top+height/2;
        return new(note.X+note.Width/2-x/zoom,note.Y+note.Height/2-y/zoom,zoom);
    }

    private CanvasCamera? PrepareStartupTutorial()
    {
        if(_tutorialStore?.CanPresent!=true) return null;
        try
        {
            var layout=EnsureTutorialLayout();
            return TutorialReadingView(layout);
        }
        catch(Exception ex)
        {
            TrayApp.Log("Tutorial notes skipped: "+ex.Message);
            return null;
        }
    }

    private void LandOnTutorial(CanvasCamera view)
    {
        _camera.SetTo(view.X,view.Y,view.Zoom);
        // Opening/closing a help view without navigating must not replace the desktop camera.
        _startupIntroLanded=true;
        _tutorialReading=true;
        _tutorialStore?.MarkPresented();
        ReconcileVisuals();
    }

    public void ShowTutorialNotes()
    {
        if(_disposed || _tutorialStore==null || !_input.Enabled) return;
        try
        {
            if(IsStartupIntroActive)FinishStartupIntro(refresh:false);
            PauseGuidedTutorial();
            CancelInteraction();
            Warmup();
            var layout=EnsureTutorialLayout();
            _tutorialStore.SetVisible(_annotationDesktop(),true);
            TransitionTo(OverviewMode.Zooming);
            SelectAnnotationMode(AnnotationMode.Navigate);
            LandOnTutorial(TutorialReadingView(layout));
        }
        catch(Exception ex) { TrayApp.Log("Tutorial notes could not be shown: "+ex.Message); }
    }

    public void HideTutorialNotes()
    {
        if(_disposed || _tutorialStore==null || CurrentTutorial==null) return;
        PauseGuidedTutorial();
        if(IsStartupIntroActive)FinishStartupIntro(refresh:false);
        _tutorialStore.SetVisible(_annotationDesktop(),false);
        _tutorialStore.MarkPresented();
        if(CurrentMode!=OverviewMode.Hidden)RefreshToolPreview();
    }
}
