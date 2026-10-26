using CanvasDesktop;
using CanvasDesktop.CanvasView;
using System.Windows.Forms;
using System.Drawing;
using System.Drawing.Imaging;

namespace CanvasDesktop.Tests;

[Collection("ShellViews")]
public class GuidedTutorialOverviewTests
{
    private static void WithOverview(Action<OverviewManager,Canvas,FakeInputRouter,FakeVirtualDesktops,string,FakeWindowApi> action,bool reduced=true)
        =>WorkspaceViewTests.OnSta(()=>
        {
            string folder=Path.Combine(Path.GetTempPath(),"guided-tour-"+Guid.NewGuid().ToString("N"));
            try
            {
                Assert.True(GridRenderer.CompileShaders());
                var canvas=new Canvas();canvas.SetCamera(700,-300);var api=new FakeWindowApi();var input=new FakeInputRouter();var desktops=new FakeVirtualDesktops { CurrentDesktopId=Guid.NewGuid() };
                var config=new FakeAppConfig { EnableStartupIntro=false,ReduceStartupMotion=reduced };
                using var wm=new WindowManager(canvas,api,config,input,new FakeClock());
                using var overview=new OverviewManager(canvas,wm,api,input,config,prefersReducedStartupMotion:()=>false);
                overview.AttachAnnotations(desktops,Path.Combine(folder,"annotations.json"));overview.AttachTutorial(Path.Combine(folder,"tutorial.json"));
                action(overview,canvas,input,desktops,folder,api);
            }
            finally { if(Directory.Exists(folder))Directory.Delete(folder,true); }
        });

    [Fact]
    [Trait("Category","NativeRendering")]
    public void ControlsReferenceKeepsTheTourAtItsStepAndEscapeClosesHelpFirst()
        =>WithOverview((overview,canvas,input,desktops,folder,api)=>
        {
            overview.TransitionTo(OverviewMode.Zooming);overview.SetInteractMode(true);
            overview.StartGuidedTutorial();Assert.False(overview.InteractMode);
            Assert.True(overview.GuidedTour!.Travelling);
            var panel=overview.TourPanel!;
            panel.Controls.OfType<FlowLayoutPanel>().Single().Controls.OfType<Button>()
                .Single(b=>b.Text=="&Controls").PerformClick();
            Assert.True(overview.ControlsPopup!.Visible);Assert.True(overview.GuidedTutorialActive);
            Assert.False(overview.GuidedTour!.Travelling);
            Assert.Equal(TutorialStep.Pan,overview.GuidedTour!.Step);
            var pass=(OverviewOverlay)Control.FromHandle(overview.MonitorHandles[0])!;
            var tab=new KeyEventArgs(Keys.Tab);pass.OnKey!(pass,tab);Assert.False(tab.Handled);
            overview.HandleEscape();Assert.False(overview.ControlsPopup.Visible);
            Assert.True(overview.GuidedTutorialActive);
            overview.NextTutorialStep();Assert.Equal(TutorialStep.Zoom,overview.GuidedTour.Step);
        },reduced:false);

    [Fact]
    [Trait("Category","NativeRendering")]
    public void PanZoomPracticeWaitsForNextAndClosingPreservesTheDesktopAndOffersResume()
        =>WithOverview((overview,canvas,input,desktops,folder,api)=>
        {
            var saved=canvas.Camera;overview.StartGuidedTutorial();
            Assert.True(overview.GuidedTutorialActive);Assert.False(overview.GuidedTour!.Travelling);
            input.RaiseDragStarted();input.QueuePanAndRaise(100,20);input.PendingDragEnded=true;input.RaiseInputAvailable();
            Assert.True(overview.GuidedTour.Practised);Assert.Equal(TutorialStep.Pan,overview.GuidedTour.Step);Assert.Equal(saved,canvas.Camera);
            overview.NextTutorialStep();Assert.Equal(TutorialStep.Zoom,overview.GuidedTour.Step);
            var pass=(OverviewOverlay)Control.FromHandle(overview.MonitorHandles[0])!;
            pass.OnKey!(pass,new(Keys.Subtract));pass.OnKey!(pass,new(Keys.Subtract));
            Assert.True(overview.GuidedTour.Practised);Assert.Equal(TutorialStep.Zoom,overview.GuidedTour.Step);
            var tab=new KeyEventArgs(Keys.Tab);pass.OnKey!(pass,tab);Assert.True(tab.Handled);Assert.True(overview.TourPanel!.ContainsFocus);
            overview.TransitionTo(OverviewMode.Hidden);Assert.False(overview.GuidedTutorialActive);Assert.Equal(saved,canvas.Camera);
            var progress=new TutorialPersistence(Path.Combine(folder,"tutorial.json")).TourForDesktop(desktops.CurrentDesktopId)!;
            Assert.Equal(1,progress.Step);Assert.Equal(TutorialTourStatus.Paused,progress.Status);
            overview.TransitionTo(OverviewMode.Zooming);Assert.True(overview.TourPanel!.IsOffer);
            overview.StartGuidedTutorial();Assert.Equal(TutorialStep.Zoom,overview.GuidedTour!.Step);
            overview.PreviousTutorialStep();Assert.Equal(TutorialStep.Pan,overview.GuidedTour.Step);Assert.True(overview.GuidedTour.Practised);
        });

    [Fact]
    [Trait("Category","NativeRendering")]
    public void AnimationCannotCompletePracticeAndNavigationInterruptsTravelWithoutSkippingTheTour()
        =>WithOverview((overview,canvas,input,desktops,folder,api)=>
        {
            overview.StartGuidedTutorial();Assert.True(overview.GuidedTour!.Travelling);
            overview.AdvanceTutorialTravel(.35);Assert.False(overview.GuidedTour.Practised);
            var pass=(OverviewOverlay)Control.FromHandle(overview.MonitorHandles[0])!;
            pass.OnKey!(pass,new(Keys.Subtract));Assert.False(overview.GuidedTour.Travelling);Assert.True(overview.GuidedTutorialActive);
            Assert.Equal(TutorialStep.Pan,overview.GuidedTour.Step);Assert.False(overview.GuidedTour.Practised);
            input.RaiseDragStarted();input.QueuePanAndRaise(100,0);Assert.True(overview.GuidedTour.Practised);
            input.PendingDragEnded=true;input.RaiseInputAvailable();
            overview.NextTutorialStep();overview.AdvanceTutorialTravel(.7);
            Assert.False(overview.GuidedTour.Travelling);Assert.False(overview.GuidedTour.Practised);
            Assert.Equal(TutorialStep.Zoom,overview.GuidedTour.Step);
        },reduced:false);

    [Fact]
    [Trait("Category","NativeRendering")]
    public void NoteExerciseUsesNewPersonalNotesAndDoesNotTreatExistingEditsAsCreation()
        =>WithOverview((overview,canvas,input,desktops,folder,api)=>
        {
            var existing=new CanvasAnnotation(Guid.NewGuid(),desktops.CurrentDesktopId,AnnotationKind.Text,new(0,0),new(200,100),Text:"Existing");
            overview.Annotations!.Put(existing);overview.StartGuidedTutorial();
            overview.NextTutorialStep();overview.NextTutorialStep();overview.NextTutorialStep();
            Assert.Equal(TutorialStep.Notes,overview.GuidedTour!.Step);
            overview.Annotations.Put(existing with { Text="Edited" });Assert.False(overview.GuidedTour.Practised);
            overview.Annotations.Put(existing with { Id=Guid.NewGuid(),Text="New thought" });Assert.True(overview.GuidedTour.Practised);
            Assert.Equal(2,overview.Annotations.Items.Count);
            while(overview.GuidedTour.Active)overview.NextTutorialStep();
            Assert.False(overview.GuidedTutorialActive);
            Assert.Equal(TutorialTourStatus.Completed,new TutorialPersistence(Path.Combine(folder,"tutorial.json")).TourForDesktop(desktops.CurrentDesktopId)!.Status);
            Assert.True(overview.TutorialNotesVisible);
        });

    [Fact]
    [Trait("Category","NativeRendering")]
    public void DraggingAWindowThroughTheSelectionToolChecksPracticeOnlyOnCommit()
        =>WithOverview((overview,canvas,input,desktops,folder,api)=>
        {
            using var source=new Form();
            api.AddWindow(source.Handle,100,200,600,400);canvas.SetWindow(source.Handle,100,200,600,400);
            overview.StartGuidedTutorial();overview.NextTutorialStep();overview.NextTutorialStep();
            Assert.Equal(TutorialStep.Windows,overview.GuidedTour!.Step);
            var view=new CanvasViewTransform(0,0,1,new(0,0,1920,1080));
            CanvasPointer P(double x,double y)=>new(0,new(x,y),CanvasPointerButton.Left,CanvasModifiers.None,view);
            var tool=overview.SelectionTool!;
            Assert.True(tool.PointerDown(P(150,250)));tool.PointerUp(P(150,250));Assert.False(overview.GuidedTour.Practised);
            Assert.True(tool.PointerDown(P(150,250)));tool.PointerMove(P(250,270));Assert.False(overview.GuidedTour.Practised);
            tool.PointerUp(P(250,270));Assert.True(overview.GuidedTour.Practised);
            Assert.Equal(200,canvas.Windows[source.Handle].X);Assert.Equal(220,canvas.Windows[source.Handle].Y);
            Assert.Equal(TutorialStep.Windows,overview.GuidedTour.Step);
        });

    [Fact]
    [Trait("Category","NativeRendering")]
    public void DesktopSwitchPausesTheOriginalTourAndSkipStaysLocalToTheNewDesktop()
        =>WithOverview((overview,canvas,input,desktops,folder,api)=>
        {
            var first=desktops.CurrentDesktopId;overview.StartGuidedTutorial();overview.NextTutorialStep();
            var second=Guid.NewGuid();desktops.SwitchTo(second);Assert.False(overview.GuidedTutorialActive);
            Assert.Equal(TutorialTourStatus.Paused,new TutorialPersistence(Path.Combine(folder,"tutorial.json")).TourForDesktop(first)!.Status);
            overview.StartGuidedTutorial();Assert.Equal(second,overview.GuidedTour!.Progress.DesktopId);
            overview.SkipGuidedTutorial();Assert.False(overview.GuidedTutorialActive);Assert.True(overview.TutorialNotesVisible);
            var store=new TutorialPersistence(Path.Combine(folder,"tutorial.json"));
            Assert.Equal(TutorialTourStatus.Paused,store.TourForDesktop(first)!.Status);
            Assert.Equal(TutorialTourStatus.Dismissed,store.TourForDesktop(second)!.Status);
        });

    [Fact]
    [Trait("Category","NativeRendering")]
    public void DesktopExplanationSurvivesCanvasCloseAndBothShortcutsReopenWithoutMovingTheDesktop()
        =>WithOverview((overview,canvas,input,desktops,folder,api)=>
        {
            var saved=canvas.Camera;overview.StartGuidedTutorial();
            for(int i=0;i<5;i++)overview.NextTutorialStep();
            Assert.Equal(TutorialStep.Desktop,overview.GuidedTour!.Step);
            Assert.Equal(OverviewMode.Hidden,overview.CurrentMode);
            Assert.True(overview.GuidedTour.Active);Assert.False(overview.GuidedTutorialActive);
            Assert.True(overview.TourPanel!.Visible);Assert.Null(overview.TourPanel.Owner);
            Assert.Equal(saved,canvas.Camera);
            Assert.Contains("Canvas view is closed",overview.TourPanel.StatusText);
            var back=overview.TourPanel.Controls.OfType<FlowLayoutPanel>().Single().Controls.OfType<Button>().Single(b=>b.Text=="&Back");
            Assert.True(back.Visible);Assert.True(back.Enabled);
            if(Environment.GetEnvironmentVariable("GITHUB_WORKSPACE") is {} workspace)
            {
                string output=Path.Combine(workspace,"TestResults");Directory.CreateDirectory(output);
                using var card=new Bitmap(overview.TourPanel.Width,overview.TourPanel.Height);
                overview.TourPanel.DrawToBitmap(card,new Rectangle(Point.Empty,card.Size));
                card.Save(Path.Combine(output,"guided-tutorial-desktop-card.png"),ImageFormat.Png);
                // The STA test has no message loop. Paint and flush composition before
                // reading desktop pixels, otherwise the screen can contain stale surfaces.
                Application.DoEvents();overview.TourPanel.Refresh();Windows.Win32.PInvoke.DwmFlush();
                var bounds=Screen.PrimaryScreen!.Bounds;
                using var desktop=new Bitmap(bounds.Width,bounds.Height);
                using(var g=Graphics.FromImage(desktop))g.CopyFromScreen(bounds.Location,Point.Empty,bounds.Size);
                desktop.Save(Path.Combine(output,"guided-tutorial-desktop-screen.png"),ImageFormat.Png);
            }
            overview.NextTutorialStep();Assert.Equal(TutorialStep.ReopenCanvas,overview.GuidedTour.Step);
            Assert.Contains("Alt+Q",overview.TourPanel.StatusText);
            Assert.Contains("scroll out over an empty area",overview.TourPanel.StatusText);
            input.RaiseOverviewHotkey();
            Assert.Equal(OverviewMode.Zooming,overview.CurrentMode);Assert.True(overview.GuidedTour.Practised);
            Assert.NotNull(overview.TourPanel.Owner);Assert.Equal(saved,canvas.Camera);
            overview.PreviousTutorialStep();overview.NextTutorialStep();
            Assert.Equal(OverviewMode.Hidden,overview.CurrentMode);
            Assert.True(overview.GuidedTour.Practised);
            Assert.Contains("scroll out over an empty area",overview.TourPanel.StatusText);
            Assert.DoesNotContain("You're back",overview.TourPanel.StatusText);
            overview.NextTutorialStep();
            overview.NextTutorialStep();Assert.Equal(TutorialTourStatus.Completed,overview.GuidedTour.Progress.Status);
            Assert.False(overview.TourPanel.Visible);

            overview.StartGuidedTutorial(restart:true);
            for(int i=0;i<5;i++)overview.NextTutorialStep();
            // A real Alt+wheel drain can return straight from the first desktop explanation.
            input.PendingZoom=true;input.RaiseInputAvailable();
            Assert.Equal(TutorialStep.ReopenCanvas,overview.GuidedTour.Step);
            Assert.True(overview.GuidedTour.Practised);Assert.Equal(OverviewMode.Zooming,overview.CurrentMode);
            Assert.Equal(saved,canvas.Camera);
        });

    [Fact]
    [Trait("Category","NativeRendering")]
    public void DesktopBackPauseResumeRestartAndNormalPanCleanUpTheTour()
        =>WithOverview((overview,canvas,input,desktops,folder,api)=>
        {
            overview.StartGuidedTutorial();for(int i=0;i<5;i++)overview.NextTutorialStep();
            overview.TourPanel!.Controls.OfType<FlowLayoutPanel>().Single().Controls.OfType<Button>().Single(b=>b.Text=="&Back").PerformClick();
            Assert.Equal(TutorialStep.Projects,overview.GuidedTour!.Step);Assert.Equal(OverviewMode.Zooming,overview.CurrentMode);
            overview.NextTutorialStep();overview.NextTutorialStep();overview.PauseGuidedTutorial();
            Assert.False(overview.TourPanel!.Visible);Assert.Null(overview.TourPanel.Owner);
            overview.StartGuidedTutorial();
            Assert.Equal(TutorialStep.ReopenCanvas,overview.GuidedTour.Step);
            Assert.Equal(OverviewMode.Hidden,overview.CurrentMode);Assert.False(overview.GuidedTour.Practised);
            Assert.True(overview.TourPanel.Visible);
            overview.NextTutorialStep();Assert.Equal(OverviewMode.Zooming,overview.CurrentMode);
            overview.StartGuidedTutorial(restart:true);Assert.Equal(TutorialStep.Pan,overview.GuidedTour.Step);
            for(int i=0;i<5;i++)overview.NextTutorialStep();
            var saved=canvas.Camera;
            input.RaiseDragStarted();input.QueuePanAndRaise(35,20);
            Assert.False(overview.GuidedTour.Active);Assert.Equal(OverviewMode.Panning,overview.CurrentMode);
            Assert.NotEqual(saved,canvas.Camera);Assert.False(overview.TourPanel.Visible);
            input.PendingDragEnded=true;input.RaiseInputAvailable();
            overview.StartGuidedTutorial(restart:true);for(int i=0;i<5;i++)overview.NextTutorialStep();
            desktops.SwitchTo(Guid.NewGuid());Assert.False(overview.GuidedTour.Active);Assert.False(overview.TourPanel.Visible);
        });
}
