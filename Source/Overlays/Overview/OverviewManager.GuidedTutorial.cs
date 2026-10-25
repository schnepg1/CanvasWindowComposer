using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows.Forms;

namespace CanvasDesktop;

internal sealed partial class OverviewManager
{
    private TutorialTour? _tour;
    private TutorialTourPanel? _tourPanel;
    private bool _tutorialOfferVisible;
    private Guid _tutorialOfferDesktop;
    private TutorialCameraFlight? _tourFlight;
    private readonly Stopwatch _tourClock=new();
    private Timer? _tourTimer;
    private HashSet<Guid> _tourKnownNotes=[];
    internal TutorialTour? GuidedTour => _tour;
    internal TutorialTourPanel? TourPanel => _tourPanel;
    public bool GuidedTutorialActive => _tour?.Active==true;

    private void OfferTutorialTour(bool explicitly=false)
    {
        if(_tutorialStore==null || !TutorialNotesVisible || GuidedTutorialActive)return;
        var progress=_tutorialStore.TourForDesktop(_annotationDesktop());
        if(!explicitly && progress?.Status is TutorialTourStatus.Completed or TutorialTourStatus.Dismissed)return;
        _tutorialOfferVisible=true;_tutorialOfferDesktop=_annotationDesktop();
        RefreshTourUi();
    }

    public void StartGuidedTutorial(bool restart=false)
    {
        if(_disposed || _tutorialStore==null || !_input.Enabled)return;
        try
        {
            if(IsStartupIntroActive)FinishStartupIntro(refresh:false);
            PauseGuidedTutorial();SetInteractMode(false);Warmup();EnsureTutorialLayout();
            _tutorialStore.SetVisible(_annotationDesktop(),true);_tutorialStore.MarkPresented();
            var saved=_tutorialStore.TourForDesktop(_annotationDesktop());
            var progress=!restart && saved?.Status is TutorialTourStatus.Paused or TutorialTourStatus.InProgress
                ? saved : new TutorialTourProgress(_annotationDesktop());
            _tour=new(progress!);_tour.Start();_tutorialOfferVisible=false;
            _tourKnownNotes=_annotationDocument!.Items.Where(a=>a.DesktopId==_annotationDesktop() && a.Kind==AnnotationKind.Text).Select(a=>a.Id).ToHashSet();
            TransitionTo(OverviewMode.Zooming);SelectAnnotationMode(AnnotationMode.Navigate);
            SaveTourProgress();TravelToTourStep();
        }
        catch(Exception ex)
        {
            PauseGuidedTutorial();TrayApp.Log("Guided tutorial could not start: "+ex.Message);
        }
    }

    private CanvasCamera TourTarget()
    {
        var layout=CurrentTutorial!.Layout;
        var notes=layout.Items.Where(a=>a.Kind==AnnotationKind.Text).ToArray();
        if(_tour!.Step!=TutorialStep.Projects)
        {
            var target=notes[Math.Min(_tour.Progress.Step,notes.Length-1)].Bounds;
            return TutorialReadingView(layout,target);
        }
        // The final stop surveys the neighbourhood and explains how to leave and return.
        double x=notes.Min(a=>a.Bounds.X),y=notes.Min(a=>a.Bounds.Y);
        return TutorialReadingView(layout,new(x,y,notes.Max(a=>a.Bounds.X+a.Bounds.Width)-x,notes.Max(a=>a.Bounds.Y+a.Bounds.Height)-y));
    }

    private void TravelToTourStep()
    {
        if(!GuidedTutorialActive || CurrentTutorial==null)return;
        StopTutorialTravel();CancelInteraction();
        // Each reading/travel position is temporary, including the user's practice pans.
        _tutorialReading=true;_startupIntroLanded=true;
        var target=TourTarget();
        if(_appConfig.ReduceStartupMotion || _prefersReducedStartupMotion())
        {
            SetTourCamera(target);RefreshTourUi();return;
        }
        _tour!.BeginTravel();_tourFlight=new(OverviewView,target);
        _tourTimer=new Timer { Interval=16 };_tourTimer.Tick+=(_,_)=>AdvanceTutorialTravel(_tourClock.Elapsed.TotalSeconds);
        _tourClock.Restart();_tourTimer.Start();RefreshTourUi();
    }
    internal void AdvanceTutorialTravel(double seconds)
    {
        if(_disposed || _tourFlight==null)return;
        try
        {
            var camera=_tourFlight.Sample(seconds);
            if(seconds>=TutorialCameraFlight.Duration)StopTutorialTravel();
            SetTourCamera(camera);
        }
        catch(Exception ex) { PauseGuidedTutorial();TrayApp.Log("Tutorial travel stopped: "+ex.Message); }
    }
    private void SetTourCamera(CanvasCamera camera)
    {
        _camera.SetTo(camera.X,camera.Y,camera.Zoom);
        ReconcileVisuals();
    }
    internal bool StopTutorialTravel()
    {
        bool moving=_tourFlight!=null;
        _tourFlight=null;_tourClock.Stop();
        var timer=_tourTimer;_tourTimer=null;timer?.Stop();timer?.Dispose();_tour?.Arrive();
        return moving;
    }
    internal void NextTutorialStep()
    {
        if(_tour?.Next()!=true)return;
        SaveTourProgress();
        if(!_tour.Active)
        {
            StopTutorialTravel();_tutorialReading=true;_startupIntroLanded=true;
            RefreshToolPreview();FocusOverview();return;
        }
        SelectAnnotationMode(AnnotationMode.Navigate);TravelToTourStep();
    }
    internal void PreviousTutorialStep()
    {
        if(_tour?.Back()!=true)return;
        SaveTourProgress();SelectAnnotationMode(AnnotationMode.Navigate);TravelToTourStep();
    }
    internal void PauseGuidedTutorial()
    {
        StopTutorialTravel();
        if(_tour?.Active==true) { _tour.Pause();SaveTourProgress();_startupIntroLanded=true; }
        _tutorialOfferVisible=false;_tourPanel?.Hide();
    }
    internal void SkipGuidedTutorial()
    {
        StopTutorialTravel();
        if(_tutorialStore!=null)
        {
            if(!GuidedTutorialActive || _tour!.Progress.DesktopId!=_annotationDesktop())
                _tour=new(_tutorialStore.TourForDesktop(_annotationDesktop())??new(_annotationDesktop()));
            _tour.Dismiss();SaveTourProgress();
        }
        _tutorialOfferVisible=false;_startupIntroLanded=true;_tourPanel?.Hide();FocusOverview();
    }
    private void SaveTourProgress() { if(_tour!=null)_tutorialStore?.SetTour(_tour.Progress); }
    public void PanGuidedTutorial(int x,int y) { if(GuidedTutorialActive)PanOverview(x,y); }
    private void ObserveTourPan(double x,double y)
    {
        double dpi=TutorialDpiScale;
        if(_tour?.ObservePan(Math.Sqrt(x*x+y*y)/dpi)==true)SaveTourProgress();
    }
    private void ObserveTourWindow()
    { if(_tour?.ObserveWindow()==true) { SaveTourProgress();RefreshTourUi(); } }
    private void ObserveTourNotes()
    {
        if(!GuidedTutorialActive || _annotationDocument==null || _tour!.Progress.DesktopId!=_annotationDesktop())return;
        var notes=_annotationDocument.Items.Where(a=>a.DesktopId==_annotationDesktop() && a.Kind==AnnotationKind.Text).Select(a=>a.Id).ToHashSet();
        bool created=_annotationDocument.LastChange==AnnotationChangeKind.Edit && notes.Except(_tourKnownNotes).Any();
        _tourKnownNotes.UnionWith(notes);
        if(created && _tour.ObserveNote())SaveTourProgress();
    }
    private bool HandleTutorialControlKey(KeyEventArgs e)
    {
        if(e.KeyCode!=Keys.Tab || e.Control || e.Alt || _tools.Active || _annotationEditor!=null || _shapeEditor!=null || _composerWindow!=null || _controlsPopup is { Visible:true }
            || (!GuidedTutorialActive && !_tutorialOfferVisible))return false;
        RefreshTourUi();
        if(_tourPanel is not { Visible:true })return false;
        _tourPanel.Activate();_tourPanel.SelectNextControl(null,!e.Shift,true,true,false);
        e.Handled=true;e.SuppressKeyPress=true;return true;
    }

    private void RefreshTourUi()
    {
        bool offer=_tutorialOfferVisible && _tutorialOfferDesktop==_annotationDesktop();
        if(_disposed || CurrentMode!=OverviewMode.Zooming || IsStartupIntroActive || _annotationEditor!=null || _shapeEditor!=null || _composerWindow!=null || _tools.Active
            || (!GuidedTutorialActive && !offer)) { _tourPanel?.Hide();return; }
        var pass=_passes.FirstOrDefault(p=>p.Visible && p.Screen.Primary)??_passes.FirstOrDefault(p=>p.Visible);
        if(pass==null)return;
        if(_tourPanel==null)
        {
            _tourPanel=new();
            _tourPanel.StartRequested+=()=>StartGuidedTutorial();
            _tourPanel.ExploreRequested+=SkipGuidedTutorial;
            _tourPanel.SkipRequested+=SkipGuidedTutorial;
            _tourPanel.BackRequested+=PreviousTutorialStep;
            _tourPanel.NextRequested+=NextTutorialStep;
            _tourPanel.RecenterRequested+=TravelToTourStep;
            _tourPanel.ControlsRequested+=()=>
            {
                StopTutorialTravel();RefreshTourUi();ToggleControlsPopup(pass);
            };
            _tourPanel.KeyDown+=(_,e)=>
            {
                if(e.KeyCode==Keys.Escape)
                {
                    if(GuidedTutorialActive) { PauseGuidedTutorial();FocusOverview(); }
                    else if(_tutorialOfferVisible)SkipGuidedTutorial();
                    e.Handled=true;e.SuppressKeyPress=true;
                }
                else if(e.KeyCode is Keys.Oemplus or Keys.Add or Keys.OemMinus or Keys.Subtract)
                    HandleKeyDown(pass,e);
            };
        }
        if(GuidedTutorialActive)_tourPanel.UpdateTour(_tour!,_tutorialStore?.Status,!_appConfig.DisableZoomHotkey);
        else _tourPanel.Offer(_tutorialStore?.TourForDesktop(_annotationDesktop())?.Status is TutorialTourStatus.Paused or TutorialTourStatus.InProgress,_tutorialStore?.Status);
        if(!_tourPanel.Visible)_tourPanel.Show(pass);
        var area=pass.Screen.WorkingArea;
        int dpiPadding=(int)(12*TutorialDpiScale);
        _tourPanel.Width=Math.Min((int)(560*TutorialDpiScale),area.Width-2*dpiPadding);
        int bottom=_annotationToolbar is { Visible:true }?_annotationToolbar.Top-dpiPadding:area.Bottom-dpiPadding;
        _tourPanel.Location=new(area.Left+Math.Max(dpiPadding,(area.Width-_tourPanel.Width)/2),Math.Max(area.Top,bottom-_tourPanel.Height));
    }
    private void DisposeGuidedTutorial()
    {
        PauseGuidedTutorial();_tourPanel?.Dispose();_tourPanel=null;
    }
}
