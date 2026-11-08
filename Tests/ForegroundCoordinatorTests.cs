using System;
using Xunit;
using CanvasDesktop;

namespace CanvasDesktop.Tests;

public class ForegroundCoordinatorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeliberateSelectionNavigatesEvenDuringRecentFocusSuppression(bool canvasOpen)
    {
        var h = new Harness();
        using var foreground = h.Foreground;
        IntPtr selected = (IntPtr)2;
        h.Canvas.SetWindow(selected, 5000, 5000, 400, 300);
        h.Clock.Now = 10000;
        h.Input.RaiseWindowMinimized((IntPtr)1);
        if (canvasOpen) h.Overview.TransitionTo(OverviewMode.Zooming);
        h.Input.RaiseWindowSelected(selected);
        Assert.Equal(selected, Assert.Single(h.Overview.NavigatedWindows));
        Assert.Equal(0, h.Canvas.CamX);
        if (canvasOpen)
        {
            Assert.Equal(OverviewMode.Zooming, h.Overview.CurrentMode);
        }
    }

    [Fact]
    public void FastSelectionAfterClosingCanvasIsNotLostToOverlaySuppression()
    {
        var h = new Harness();
        using var foreground = h.Foreground;
        h.Canvas.LoadState(new() { CamX = 0, CamY = 0, Zoom = .25, Windows = new() });
        h.Canvas.SetWindow((IntPtr)2, 5000, 5000, 400, 300);
        h.Clock.Now = 10000;
        h.Overview.TransitionTo(OverviewMode.Zooming);
        Assert.True(h.Input.WindowSelectionTarget!((IntPtr)2));
        Assert.False(h.Input.WindowSelectionTarget!((IntPtr)999));
        h.Overview.TransitionTo(OverviewMode.Hidden);
        h.Input.RaiseWindowSelected((IntPtr)2);
        Assert.Equal((IntPtr)2, Assert.Single(h.Overview.NavigatedWindows));
    }

    [Fact]
    public void IncidentalFocusAndAutomaticRestoreDoNotNavigateTheOverview()
    {
        var h = new Harness();
        using var foreground = h.Foreground;
        h.Canvas.SetWindow((IntPtr)2, 5000, 5000, 400, 300);
        h.Clock.Now = 10000;
        h.Overview.TransitionTo(OverviewMode.Zooming);
        h.Input.RaiseWindowRestored((IntPtr)2);
        h.Input.RaiseWindowFocused((IntPtr)2);
        Assert.Empty(h.Overview.NavigatedWindows);
        Assert.Equal(0, h.Canvas.CamX);
        Assert.True(h.Input.TaskbarNavigationEnabled);
        h.Overview.TransitionTo(OverviewMode.Panning);
        Assert.False(h.Input.TaskbarNavigationEnabled);
        h.Overview.TransitionTo(OverviewMode.Hidden);
        Assert.False(h.Input.TaskbarNavigationEnabled);
    }

    [Fact]
    public void DisabledInputUntrackedAndPinnedWindowsAreNotNavigationTargets()
    {
        var h = new Harness();
        using var foreground = h.Foreground;
        h.Overview.TransitionTo(OverviewMode.Zooming);
        h.Input.RaiseWindowSelected((IntPtr)999);
        h.Canvas.SetWindow((IntPtr)2, 5000, 5000, 400, 300);
        h.Input.Enabled = false;
        h.Input.RaiseWindowSelected((IntPtr)2);
        h.Input.Enabled = true;
        h.Canvas.SetPinnedToScreen((IntPtr)2, true);
        h.Input.RaiseWindowSelected((IntPtr)2);
        Assert.Empty(h.Overview.NavigatedWindows);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeliberateSelectionOfInventoryWindowNavigatesWithoutMovingCamera(bool canvasOpen)
    {
        var h = new Harness();
        using var foreground = h.Foreground;
        IntPtr selected = (IntPtr)2;
        h.Canvas.RestoreWindow(selected, new WorldRect { X = 5000, Y = 5000, W = 400, H = 300, InInventory = true });
        h.Clock.Now = 10000;
        if (canvasOpen) h.Overview.TransitionTo(OverviewMode.Zooming);
        else h.Overview.TransitionTo(OverviewMode.Hidden);

        h.Input.RaiseWindowSelected(selected);

        Assert.Equal(selected, Assert.Single(h.Overview.NavigatedWindows));
        Assert.Equal(0, h.Canvas.CamX);
        Assert.Equal(0, h.Canvas.CamY);

        h.Input.RaiseWindowFocused(selected);
        Assert.Equal(0, h.Canvas.CamX);
        Assert.Equal(0, h.Canvas.CamY);
    }

    private sealed class Harness
    {
        public Canvas Canvas = new();
        public FakeClock Clock = new();
        public FakeScreens Screens = new();
        public FakeInputRouter Input = new();
        public FakeOverviewController Overview = new();
        public ForegroundCoordinator Foreground = null!;

        public Harness()
        {
            Foreground = new ForegroundCoordinator(Canvas, Overview, Input, Clock, Screens);
        }
    }

    [Fact]
    public void WindowMinimized_StampsLastWindowLostTick()
    {
        var h = new Harness();
        h.Clock.Now = 12345;
        h.Input.RaiseWindowMinimized((IntPtr)1);

        // Verify the stamp was applied: an immediate WindowFocused on an
        // off-screen tracked window should NOT recenter.
        h.Canvas.SetWindow((IntPtr)1, 5000, 5000, 400, 300);
        double camBefore = h.Canvas.CamX;
        h.Input.RaiseWindowFocused((IntPtr)1);
        Assert.Equal(camBefore, h.Canvas.CamX);
    }

    [Fact]
    public void WindowFocused_OffScreenWindow_RecentersCamera()
    {
        var h = new Harness();
        h.Canvas.SetWindow((IntPtr)1, 5000, 5000, 400, 300);

        h.Clock.Now = 10000;
        h.Input.RaiseWindowFocused((IntPtr)1);

        // Camera should now be centered roughly on the window
        var (sx, sy) = h.Canvas.WorldToScreen(5200, 5150);
        Assert.InRange(sx, 1920 / 2 - 2, 1920 / 2 + 2);
        Assert.InRange(sy, 1040 / 2 - 2, 1040 / 2 + 2);
    }

    [Fact]
    public void WindowFocused_OnScreenWindow_DoesNotRecenter()
    {
        var h = new Harness();
        h.Canvas.SetWindow((IntPtr)1, 100, 100, 400, 300);

        h.Clock.Now = 10000;
        double camBefore = h.Canvas.CamX;
        h.Input.RaiseWindowFocused((IntPtr)1);

        Assert.Equal(camBefore, h.Canvas.CamX);
    }

    [Fact]
    public void WindowFocused_WithinSuppressionWindowAfterDestroy_DoesNotRecenter()
    {
        var h = new Harness();
        h.Canvas.SetWindow((IntPtr)2, 5000, 5000, 400, 300);

        h.Clock.Now = 10000;
        h.Input.RaiseWindowDestroyed((IntPtr)1);

        // Within 500ms — focused event should be suppressed
        h.Clock.Now = 10499;
        double camBefore = h.Canvas.CamX;
        h.Input.RaiseWindowFocused((IntPtr)2);

        Assert.Equal(camBefore, h.Canvas.CamX);
    }

    [Fact]
    public void WindowFocused_AfterSuppressionWindowExpires_RecentersAgain()
    {
        var h = new Harness();
        h.Canvas.SetWindow((IntPtr)2, 5000, 5000, 400, 300);

        h.Clock.Now = 10000;
        h.Input.RaiseWindowDestroyed((IntPtr)1);

        // Past 500ms suppression window — focus should recenter
        h.Clock.Now = 10501;
        h.Input.RaiseWindowFocused((IntPtr)2);

        var (sx, _) = h.Canvas.WorldToScreen(5200, 5150);
        Assert.InRange(sx, 1920 / 2 - 2, 1920 / 2 + 2);
    }

    [Fact]
    public void OverviewClose_StampsOverlayClosedTick_SuppressesNextFocus()
    {
        var h = new Harness();
        h.Canvas.SetWindow((IntPtr)1, 5000, 5000, 400, 300);

        h.Clock.Now = 10000;
        h.Overview.TransitionTo(OverviewMode.Zooming);
        h.Overview.TransitionTo(OverviewMode.Hidden);

        h.Clock.Now = 10100;
        double camBefore = h.Canvas.CamX;
        h.Input.RaiseWindowFocused((IntPtr)1);

        Assert.Equal(camBefore, h.Canvas.CamX);
    }
}
