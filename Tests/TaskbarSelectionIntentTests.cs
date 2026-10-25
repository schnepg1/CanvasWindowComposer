using CanvasDesktop;

namespace CanvasDesktop.Tests;

public class TaskbarSelectionIntentTests
{
    [Fact]
    public void FocusOrRestoreAloneDoesNotCountAsASelection()
    {
        var intent = new TaskbarSelectionIntent { Enabled = true };
        Assert.False(intent.TrySelect((IntPtr)42, 1000));
        intent.Click(1000, (IntPtr)42);
        Assert.True(intent.TrySelect((IntPtr)43, 1100));
        Assert.False(intent.TrySelect((IntPtr)44, 1150));
    }

    [Fact]
    public void OnlyTheForegroundAppCanBeSelectedByATaskbarMinimize()
    {
        var intent = new TaskbarSelectionIntent { Enabled = true, NavigateMinimize = true };
        intent.Click(1000, (IntPtr)42);
        // An unrelated automatic park while a taskbar click is pending is not navigation.
        Assert.False(intent.TrySelect((IntPtr)43, 1050, minimizing: true));
        Assert.True(intent.TrySelect((IntPtr)42, 1100, minimizing: true));
        Assert.False(intent.TrySelect((IntPtr)43, 1101));
    }

    [Fact]
    public void DesktopTaskbarSelectionNavigatesWhileItsMinimizeToggleRemainsNative()
    {
        var intent = new TaskbarSelectionIntent { Enabled = true };
        intent.Click(1000, (IntPtr)42);
        Assert.False(intent.TrySelect((IntPtr)42, 1010, minimizing: true));
        Assert.False(intent.TrySelect((IntPtr)43, 1020));
        intent.Click(2000, (IntPtr)42);
        Assert.True(intent.TrySelect((IntPtr)43, 2010));
    }

    [Fact]
    public void DisabledExpiredCancelledAndOlderEventsCannotClaimAClick()
    {
        var intent = new TaskbarSelectionIntent();
        intent.Click(1000, (IntPtr)42);
        Assert.False(intent.TrySelect((IntPtr)42, 1100));
        intent.Enabled = true;
        intent.Click(1000, (IntPtr)42);
        Assert.False(intent.TrySelect((IntPtr)42, 999));
        Assert.False(intent.TrySelect((IntPtr)42, 1000 + TaskbarSelectionIntent.LifetimeMs + 1));
        intent.Click(3000, (IntPtr)42);
        intent.Cancel();
        Assert.False(intent.TrySelect((IntPtr)42, 3100));
        intent.Click(4000, (IntPtr)42);
        intent.Enabled = false;
        intent.Enabled = true;
        Assert.False(intent.TrySelect((IntPtr)42, 4100));
    }

    [Fact]
    public void TimestampWraparoundStillMatchesTheActualClick()
    {
        var intent = new TaskbarSelectionIntent { Enabled = true };
        intent.Click(uint.MaxValue - 10, (IntPtr)42);
        Assert.True(intent.TrySelect((IntPtr)42, 20));
    }

    [Theory]
    [InlineData("Shell_TrayWnd", true)]
    [InlineData("Shell_SecondaryTrayWnd", true)]
    [InlineData("TaskListThumbnailWnd", true)]
    [InlineData("Chrome_WidgetWin_1", false)]
    [InlineData("NotifyIconOverflowWindow", false)]
    public void OnlyTaskbarsAndTheirWindowPickersArmNavigation(string name, bool expected)
        => Assert.Equal(expected, TaskbarSelectionIntent.IsTaskbarClass(name));
}
