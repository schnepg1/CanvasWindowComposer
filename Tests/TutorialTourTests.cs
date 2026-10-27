using CanvasDesktop;
using CanvasDesktop.CanvasView;

namespace CanvasDesktop.Tests;

public class TutorialTourTests
{
    private static TutorialTour Tour(int step=0) { var t=new TutorialTour(new(Guid.NewGuid(),step));t.Start();return t; }
    [Fact]
    public void PracticeChecksOffButNeverAdvancesWithoutNext()
    {
        var t=Tour();Assert.False(t.ObservePan(20));Assert.True(t.ObservePan(44));
        Assert.True(t.Practised);Assert.Equal(TutorialStep.Pan,t.Step);
        Assert.False(t.ObservePan(100));Assert.True(t.Next());
        Assert.Equal(TutorialStep.Zoom,t.Step);Assert.False(t.Practised);
        Assert.True(t.ObserveZoom(.5,.6));Assert.Equal(TutorialStep.Zoom,t.Step);
    }
    [Fact]
    public void AcknowledgementAlwaysAllowsProgressWithoutPerformingAnExercise()
    {
        var t=Tour();for(int i=0;i<TutorialTour.StepCount;i++)Assert.True(t.Next());
        Assert.False(t.Active);Assert.Equal(TutorialTourStatus.Completed,t.Progress.Status);Assert.Equal(0,t.Progress.Practised);
    }
    [Fact]
    public void TravelCannotCountPracticeOrAdvanceUntilItStops()
    {
        var t=Tour();t.BeginTravel();Assert.False(t.ObservePan(1000));Assert.False(t.Next());Assert.False(t.Back());
        t.Arrive();Assert.False(t.ObservePan(1));Assert.True(t.ObservePan(63));
        Assert.True(t.Next());t.BeginTravel();Assert.False(t.ObserveZoom(.2,.9));
        t.Arrive();Assert.True(t.ObserveZoom(.2,.3));
    }
    [Fact]
    public void PauseResumeBackAndDismissPreserveDesktopAndCheckedExercises()
    {
        var t=Tour();t.ObservePan(64);t.Next();t.Pause();var progress=t.Progress;
        Assert.False(t.Active);Assert.Equal(TutorialTourStatus.Paused,progress.Status);
        Assert.False(t.ObserveZoom(.3,.9));
        var resumed=new TutorialTour(progress);resumed.Start();Assert.Equal(TutorialStep.Zoom,resumed.Step);
        Assert.True(resumed.Back());Assert.True(resumed.Practised);Assert.False(resumed.Back());
        resumed.Dismiss();Assert.False(resumed.Next());Assert.Equal(TutorialTourStatus.Dismissed,resumed.Progress.Status);
        Assert.Equal(progress.DesktopId,resumed.Progress.DesktopId);
    }
    [Fact]
    public void WrongStepAndInvalidInputsCannotCheckAnExercise()
    {
        var t=Tour();Assert.False(t.ObservePan(double.NaN));Assert.False(t.ObservePan(-100));Assert.False(t.ObserveWindow());Assert.False(t.ObserveNote());
        t.Next();Assert.False(t.ObserveZoom(0,1));Assert.False(t.ObserveZoom(double.NaN,1));Assert.False(t.ObserveZoom(.5,.5));
        t.Next();Assert.True(t.ObserveWindow());t.Next();Assert.True(t.ObserveNote());
    }
    [Theory]
    [InlineData(-1,0)]
    [InlineData(7,0)]
    [InlineData(1,128)]
    public void InvalidSavedStepsAreRejected(int step,int mask)
        =>Assert.Throws<ArgumentException>(()=>new TutorialTour(new(Guid.Empty,step,mask)));
    [Fact]
    public void DesktopStepsCanResumeAndReopeningOnlyChecksTheReturnStep()
    {
        var t=Tour((int)TutorialStep.Desktop);
        Assert.False(t.ObserveCanvasReopened());t.Next();
        Assert.Equal(TutorialStep.ReopenCanvas,t.Step);t.Pause();
        t=new TutorialTour(t.Progress);t.Start();
        Assert.True(t.ObserveCanvasReopened());Assert.True(t.Active);
        Assert.True(t.Next());Assert.Equal(TutorialTourStatus.Completed,t.Progress.Status);
    }
    [Fact]
    public void CameraTravelIsSampledFiniteAndEndsExactlyAtItsTarget()
    {
        var from=new CanvasCamera(-1200,600,.4);var to=new CanvasCamera(2600,-900,1);
        var flight=new TutorialCameraFlight(from,to);
        Assert.Equal(from,flight.Sample(-1));Assert.Equal(from,flight.Sample(double.NaN));Assert.Equal(to,flight.Sample(.7));
        var middle=flight.Sample(.35);Assert.InRange(middle.Zoom,.4,1);Assert.True(double.IsFinite(middle.X));
        Assert.Equal(middle,flight.Sample(.35));
    }
    [Fact]
    public void EndingAPanWhileAnEditorOwnsInputDoesNotSuppressLaterNavigation()
    {
        var canvas=new Canvas();var input=new FakeInputRouter();var overview=new FakeOverviewController { GuidedTutorialActive=true };
        using var binding=new OverviewInputs(overview,input,canvas);overview.TransitionTo(OverviewMode.Zooming);
        input.RaiseDragStarted();overview.CanvasInputSuspended=true;
        input.PendingDragEnded=true;input.RaiseInputAvailable();
        overview.GuidedTutorialActive=false;overview.CanvasInputSuspended=false;
        overview.TransitionTo(OverviewMode.Hidden);input.PendingZoom=true;input.RaiseInputAvailable();Assert.Equal(OverviewMode.Zooming,overview.CurrentMode);
    }

    [Fact]
    public void GlobalPracticePanNeverMutatesTheDesktopAndCancelledHeldGesturesAreDrained()
    {
        var canvas=new Canvas();canvas.SetCamera(123,456);var saved=canvas.Camera;
        var input=new FakeInputRouter();var overview=new FakeOverviewController { GuidedTutorialActive=true };
        using var binding=new OverviewInputs(overview,input,canvas);
        overview.TransitionTo(OverviewMode.Zooming);input.RaiseDragStarted();input.QueuePanAndRaise(100,30);
        Assert.Equal(OverviewMode.Zooming,overview.CurrentMode);Assert.Equal(saved,canvas.Camera);
        Assert.Single(overview.TutorialPan);Assert.Empty(overview.RecordedDeltas);
        overview.GuidedTutorialActive=false;input.QueuePanAndRaise(500,500);
        input.PendingDragEnded=true;input.RaiseInputAvailable();Assert.Equal(saved,canvas.Camera);Assert.Equal(0,overview.ReleaseInertiaCalls);
        input.RaiseDragStarted();input.QueuePanAndRaise(10,20);Assert.NotEqual(saved,canvas.Camera);
    }
}
