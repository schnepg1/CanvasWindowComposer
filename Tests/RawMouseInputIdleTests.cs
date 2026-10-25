using CanvasDesktop;

namespace CanvasDesktop.Tests;

[Collection("ShellViews")]
[Trait("Category", "NativeRendering")]
public class RawMouseInputIdleTests
{
    [Fact]
    public void EmptyInputQueueSleepsUntilShutdown()
    {
        using var shutdown = new ManualResetEvent(false);
        using var entered = new ManualResetEventSlim();
        using var completed = new ManualResetEventSlim();
        bool? result = null;
        var worker = new Thread(() =>
        {
            entered.Set();
            result = RawMouseInput.WaitForInput(shutdown);
            completed.Set();
        }) { IsBackground = true };
        worker.Start();
        try
        {
            Assert.True(entered.Wait(5000));
            Assert.False(completed.Wait(200), "An empty input queue must block instead of spinning.");
        }
        finally
        {
            shutdown.Set();
            Assert.True(worker.Join(5000), "Shutdown must wake the input wait.");
        }
        Assert.Equal(false, result);
    }

    [Fact]
    public void ShutdownAlreadySignaledDoesNotStartAnotherDrain()
    {
        using var shutdown = new ManualResetEvent(true);
        Assert.False(RawMouseInput.WaitForInput(shutdown));
    }
}
