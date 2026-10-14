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
        filter.CaptureHeldInput(); // Raw mouse input skips before the matching form message.
        var mouseDown = Message.Create(IntPtr.Zero, 0x201, IntPtr.Zero, IntPtr.Zero);
        Assert.True(filter.PreFilterMessage(ref mouseDown));
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
    [InlineData(0x312)]
    [InlineData(0x20A)]
    [InlineData(0x20E)]
    public void NonMouseSkipAllowsImmediateDoubleClickNavigation(int skipMessage)
    {
        bool active = true;
        var down = new HashSet<int>();
        StartupIntroInputFilter filter = null!;
        filter = new StartupIntroInputFilter(() =>
        {
            if (!active) return false;
            active = false; filter.CaptureHeldInput(); return true;
        }, down.Contains, () => 0);
        var message = Message.Create(IntPtr.Zero, skipMessage, (IntPtr)13, IntPtr.Zero);
        Assert.True(filter.PreFilterMessage(ref message));
        message = Message.Create(IntPtr.Zero, 0x201, IntPtr.Zero, IntPtr.Zero);
        Assert.False(filter.PreFilterMessage(ref message));
        message.Msg = 0x203;
        Assert.False(filter.PreFilterMessage(ref message));
        Assert.False(filter.HasPendingGesture);
    }

    [Theory]
    [InlineData(0x201, 0x202, 0x203, 0)]
    [InlineData(0x204, 0x205, 0x206, 0)]
    [InlineData(0x207, 0x208, 0x209, 0)]
    [InlineData(0x20B, 0x20C, 0x20D, 65536)]
    public void MouseSkipSuppressesOnlyTheSameButtonsDoubleClickUntilTheSystemIntervalEnds(
        int mouseDown, int mouseUp, int doubleClick, int wParam)
    {
        long now = 0;
        bool active = true;
        var filter = new StartupIntroInputFilter(() =>
        {
            if (!active) return false;
            active = false; return true;
        }, _ => false, () => now);
        var message = Message.Create(IntPtr.Zero, mouseDown, (IntPtr)wParam, IntPtr.Zero);
        Assert.True(filter.PreFilterMessage(ref message));
        message.Msg = mouseUp;
        Assert.True(filter.PreFilterMessage(ref message));
        message.Msg = doubleClick;
        Assert.True(filter.PreFilterMessage(ref message));
        var otherButton = Message.Create(IntPtr.Zero, doubleClick == 0x203 ? 0x206 : 0x203, IntPtr.Zero, IntPtr.Zero);
        Assert.False(filter.PreFilterMessage(ref otherButton));
        Assert.True(filter.HasPendingGesture);
        now = SystemInformation.DoubleClickTime + 1;
        Assert.False(filter.PreFilterMessage(ref message));
        Assert.False(filter.HasPendingGesture);
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
