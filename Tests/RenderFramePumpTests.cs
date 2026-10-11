using CanvasDesktop;

namespace CanvasDesktop.Tests;

public class RenderFramePumpTests
{
    [Fact]
    public void StaticFramesCoalesceAndInvalidationDuringDrawSurvives()
    {
        using var entered = new ManualResetEventSlim();
        using var finish = new ManualResetEventSlim();
        using var second = new ManualResetEventSlim();
        using var extra = new ManualResetEventSlim();
        int frames = 0;
        using var pump = new RenderFramePump("TestFrames", () =>
        {
            int count = Interlocked.Increment(ref frames);
            if (count == 1) { entered.Set(); finish.Wait(); }
            else if (count == 2) second.Set(); else extra.Set();
            return true;
        }, () => false, () => { });
        pump.StartThread(); pump.Start();
        try
        {
            Assert.True(entered.Wait(5000));
            for (int i = 0; i < 100; i++) pump.Invalidate();
            finish.Set();
            Assert.True(second.Wait(5000));
            Assert.False(extra.Wait(150));
            Assert.Equal(2, Volatile.Read(ref frames));
        }
        finally { finish.Set(); }
    }

    [Fact]
    public void StopSleepsAndStartAlwaysRequestsAFrame()
    {
        using var frame = new AutoResetEvent(false);
        using var pump = new RenderFramePump("TestRestart", () => { frame.Set(); return true; }, () => false, () => { });
        pump.StartThread(); pump.Start(); Assert.True(frame.WaitOne(5000));
        pump.Stop(); pump.Invalidate(); Assert.False(frame.WaitOne(100));
        pump.Start(); Assert.True(frame.WaitOne(5000));
    }

    [Fact]
    public void ContinuousTicksCanStopWithoutLosingTheNextInvalidation()
    {
        using var third = new ManualResetEventSlim();
        using var fourth = new ManualResetEventSlim();
        int frames = 0, continuous = 1;
        using var pump = new RenderFramePump("TestTicks", () =>
        {
            int count = Interlocked.Increment(ref frames);
            if (count == 3) { Volatile.Write(ref continuous, 0); third.Set(); }
            if (count == 4) fourth.Set();
            return true;
        }, () => Volatile.Read(ref continuous) != 0, () => { });
        pump.StartThread(); pump.Start();
        Assert.True(third.Wait(5000)); Assert.False(fourth.Wait(100));
        pump.Invalidate(); Assert.True(fourth.Wait(5000));
    }

    [Fact]
    public void OccludedPresentationBacksOffAndKeepsTheFramePending()
    {
        using var retried = new ManualResetEventSlim();
        int frames = 0;
        using var pump = new RenderFramePump("TestOcclusion", () =>
        {
            int count = Interlocked.Increment(ref frames);
            if (count > 1) retried.Set();
            return count > 1;
        }, () => false, () => { });
        pump.StartThread(); pump.Start();
        Assert.True(retried.Wait(5000));
        Thread.Sleep(100);
        Assert.Equal(2, Volatile.Read(ref frames));
    }

    [Fact]
    public async Task SlowPresentRetainsResourcesUntilTheRenderThreadReturns()
    {
        using var entered = new ManualResetEventSlim();
        using var finish = new ManualResetEventSlim();
        using var released = new ManualResetEventSlim();
        using var pump = new RenderFramePump("TestSlowPresent", () =>
            { entered.Set(); finish.Wait(); return true; }, () => false, released.Set);
        pump.StartThread(); pump.Start();
        try
        {
            Assert.True(entered.Wait(5000));
            await Task.Run(pump.Dispose).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(released.IsSet);
            pump.Invalidate(); pump.Start(); pump.Stop(); // late callbacks are harmless
        }
        finally { finish.Set(); Assert.True(released.Wait(5000)); }
    }

    [Fact]
    public void DrawFailureReleasesOnceWithoutEscapingTheBackgroundThread()
    {
        using var released = new ManualResetEventSlim();
        int releases = 0;
        using var pump = new RenderFramePump("TestFailure", () => throw new InvalidOperationException("removed device"),
            () => false, () => { Interlocked.Increment(ref releases); released.Set(); });
        pump.StartThread(); pump.Start(); Assert.True(released.Wait(5000));
        pump.Dispose(); pump.Invalidate();
        Assert.Equal(1, Volatile.Read(ref releases));
    }

    [Fact]
    public void UnstartedPumpReleasesOnceAndIgnoresLateInvalidations()
    {
        int releases = 0;
        var pump = new RenderFramePump("TestUnstarted", () => true, () => false, () => releases++);
        pump.Dispose(); pump.Dispose(); pump.Invalidate(); pump.Start(); pump.Stop();
        pump.Synchronize(() => throw new Exception("Must not run"));
        Assert.Equal(1, releases);
    }
}
