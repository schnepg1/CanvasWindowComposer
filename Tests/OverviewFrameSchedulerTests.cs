using CanvasDesktop;

namespace CanvasDesktop.Tests;

public class OverviewFrameSchedulerTests
{
    [Fact]
    public void InputBurstDrawsTheLatestStateOnce()
    {
        var callbacks = new Queue<Action>();
        int camera = 0, drawnCamera = 0, draws = 0;
        var frames = new OverviewFrameScheduler(callbacks.Enqueue, () => { drawnCamera = camera; draws++; });
        for (int i = 1; i <= 100; i++) { camera = i; frames.Request(); }
        Assert.Single(callbacks);
        Assert.Equal(0, draws);
        callbacks.Dequeue()();
        Assert.Equal(100, drawnCamera);
        Assert.Equal(1, draws);
        Assert.Empty(callbacks);
    }

    [Fact]
    public void GestureEndFlushesAndStaleCallbackCannotDrawANewerSessionEarly()
    {
        var callbacks = new Queue<Action>();
        int draws = 0;
        var frames = new OverviewFrameScheduler(callbacks.Enqueue, () => draws++);
        frames.Request(); frames.Flush();
        Assert.Equal(1, draws);
        frames.Request();
        callbacks.Dequeue()(); // Callback superseded by the synchronous flush.
        Assert.Equal(1, draws);
        callbacks.Dequeue()();
        Assert.Equal(2, draws);
        frames.Request(); frames.Cancel();
        callbacks.Dequeue()();
        Assert.Equal(2, draws);
    }

    [Fact]
    public void InvalidationDuringDrawingIsDeferredAndNeverLostOrReentrant()
    {
        var callbacks = new Queue<Action>();
        int draws = 0;
        OverviewFrameScheduler frames = null!;
        frames = new(callbacks.Enqueue, () =>
        {
            if (++draws == 1) { frames.Request(); frames.Flush(); Assert.Empty(callbacks); }
        });
        frames.Request(); callbacks.Dequeue()();
        Assert.Equal(1, draws); Assert.Single(callbacks);
        callbacks.Dequeue()();
        Assert.Equal(2, draws); Assert.Empty(callbacks);
    }
}
