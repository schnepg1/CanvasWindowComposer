using System.Threading;

namespace CanvasDesktop;

/// <summary>Retain invalidations arriving during Present; animate only visible time-based content.</summary>
internal sealed class GridFrameSchedule
{
    private long _revision = 1, _presented;
    public long Revision => Interlocked.Read(ref _revision);
    public void Invalidate() => Interlocked.Increment(ref _revision);
    public void Presented(long revision) => Interlocked.Exchange(ref _presented, revision);
    public static bool NeedsContinuousFrames(bool drawGrid, float zoom, bool needsTicks, bool lowQuality)
        => needsTicks || (drawGrid && !lowQuality && zoom < 0.7f);

    public bool ShouldRender(bool drawGrid, float zoom, bool needsTicks, bool lowQuality = false)
        => Revision != Interlocked.Read(ref _presented) || NeedsContinuousFrames(drawGrid, zoom, needsTicks, lowQuality);
}
