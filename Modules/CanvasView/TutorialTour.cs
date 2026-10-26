namespace CanvasDesktop.CanvasView;

public enum TutorialTourStatus { InProgress, Paused, Completed, Dismissed }
public enum TutorialStep { Pan, Zoom, Windows, Notes, Projects, Desktop, ReopenCanvas }
public sealed record TutorialTourProgress(Guid DesktopId, int Step = 0, int Practised = 0,
    TutorialTourStatus Status = TutorialTourStatus.Paused);

/// <summary>Pure guided-tour state. Only explicit Next advances; camera animation never counts as practice.</summary>
public sealed class TutorialTour
{
    public const int StepCount = 7;
    public TutorialTourProgress Progress { get; private set; }
    public bool Active { get; private set; }
    public bool Travelling { get; private set; }
    private double _panDistance, _zoomDistance;
    public TutorialStep Step => (TutorialStep)Progress.Step;
    public bool Practised => (Progress.Practised & (1 << Progress.Step)) != 0;
    public TutorialTour(TutorialTourProgress progress) { Validate(progress); Progress=progress; }
    public static void Validate(TutorialTourProgress progress)
    {
        if(progress is null || progress.Step is < 0 or >= StepCount || progress.Practised < 0 || progress.Practised >= (1 << StepCount) || !Enum.IsDefined(progress.Status))
            throw new ArgumentException("Invalid tutorial progress.");
    }
    public void Start() { Active=true;Travelling=false;Progress=Progress with { Status=TutorialTourStatus.InProgress }; }
    public void BeginTravel() { if(Active)Travelling=true; }
    public void Arrive() => Travelling=false;
    public bool Next()
    {
        if(!Active || Travelling)return false;
        if(Progress.Step==StepCount-1) { Active=false;Progress=Progress with { Status=TutorialTourStatus.Completed }; }
        else Progress=Progress with { Step=Progress.Step+1 };
        _panDistance=0;_zoomDistance=0;return true;
    }
    public bool Back()
    {
        if(!Active || Travelling || Progress.Step==0)return false;
        Progress=Progress with { Step=Progress.Step-1 };_panDistance=0;_zoomDistance=0;return true;
    }
    public void Pause() { if(Active)Progress=Progress with { Status=TutorialTourStatus.Paused };Active=false;Travelling=false; }
    public void Dismiss() { Active=false;Travelling=false;Progress=Progress with { Status=TutorialTourStatus.Dismissed }; }
    private bool Mark(TutorialStep step)
    {
        if(!Active || Travelling || Step!=step || Practised)return false;
        Progress=Progress with { Practised=Progress.Practised | (1 << Progress.Step) };return true;
    }
    public bool ObservePan(double distance)
    {
        if(!Active || Travelling || Step!=TutorialStep.Pan || !double.IsFinite(distance))return false;
        _panDistance+=Math.Clamp(distance,0,10000);return _panDistance>=64 && Mark(TutorialStep.Pan);
    }
    public bool ObserveZoom(double before,double after)
    {
        if(!Active || Travelling || Step!=TutorialStep.Zoom || !double.IsFinite(before) || !double.IsFinite(after) || before<=0 || after<=0)return false;
        _zoomDistance+=Math.Abs(Math.Log(after/before));return _zoomDistance>=.12 && Mark(TutorialStep.Zoom);
    }
    public bool ObserveWindow() => Mark(TutorialStep.Windows);
    public bool ObserveNote() => Mark(TutorialStep.Notes);
    public bool ObserveCanvasReopened() => Mark(TutorialStep.ReopenCanvas);
}
