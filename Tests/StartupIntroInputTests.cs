using System.Windows.Forms;
using CanvasDesktop;

namespace CanvasDesktop.Tests;

public class StartupIntroInputTests
{
    [Fact]
    public void HeldSkipKeyCannotBecomeAnOverviewCommandAfterAQuarterSecond()
    {
        long now = 0;
        bool active = true;
        var down = new HashSet<int> { 13 };
        StartupIntroInputFilter filter = null!;
        filter = new StartupIntroInputFilter(() =>
        {
            if (!active) return false;
            active = false; filter.CaptureHeldInput(); return true;
        }, down.Contains, () => now);
        var message = Message.Create(IntPtr.Zero, 0x100, (IntPtr)13, IntPtr.Zero);
        Assert.True(filter.PreFilterMessage(ref message));
        now = 2000;
        Assert.True(filter.PreFilterMessage(ref message));
        Assert.True(filter.HasPendingGesture);
        message.Msg = 0x101;
        Assert.True(filter.PreFilterMessage(ref message));
        down.Clear();
        Assert.False(filter.HasPendingGesture);
        message.Msg = 0x100;
        Assert.False(filter.PreFilterMessage(ref message));
    }

    [Fact]
    public void DelayedMouseDoubleClickAndExternalKeyReleaseAreCleanedUp()
    {
        long now = 0;
        var down = new HashSet<int> { 1, 18 };
        var filter = new StartupIntroInputFilter(() => false, down.Contains, () => now);
        filter.CaptureHeldInput();
        down.Clear(); // Release can be delivered to another application after Alt+Tab.
        now = Math.Max(1, SystemInformation.DoubleClickTime - 1);
        var message = Message.Create(IntPtr.Zero, 0x203, IntPtr.Zero, IntPtr.Zero);
        Assert.True(filter.PreFilterMessage(ref message));
        Assert.True(filter.HasPendingGesture);
        now = SystemInformation.DoubleClickTime + 1;
        Assert.False(filter.HasPendingGesture);
        Assert.False(filter.PreFilterMessage(ref message));
    }

    [Theory]
    [InlineData(0x100)]
    [InlineData(0x104)]
    [InlineData(0x201)]
    [InlineData(0x204)]
    [InlineData(0x207)]
    [InlineData(0x20A)]
    [InlineData(0x20E)]
    [InlineData(0x312)]
    public void SkipMessagesAreConsumedButMouseMovementDoesNotCancelTheFlight(int messageId)
    {
        int skipped = 0;
        var filter = new StartupIntroInputFilter(() => { skipped++; return true; }, _ => false);
        var message = Message.Create(IntPtr.Zero, 0x200, IntPtr.Zero, IntPtr.Zero);
        Assert.False(filter.PreFilterMessage(ref message));
        Assert.Equal(0, skipped);
        message.Msg = messageId;
        Assert.True(filter.PreFilterMessage(ref message));
        Assert.Equal(1, skipped);
    }
}
