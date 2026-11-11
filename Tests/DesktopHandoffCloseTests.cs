using Xunit;

namespace CanvasDesktop.Tests;

public sealed class DesktopHandoffCloseTests
{
    [Fact]
    public void WaitsForBothPlacementAndButtonUp()
    {
        var close = new DesktopHandoffClose();
        Assert.True(close.HoldStoppingClick());
        Assert.False(close.HoldStoppingClick());
        long request = close.TryBeginPlacement()!.Value;

        Assert.Equal(DesktopHandoffCloseDecision.WaitForButtonUp, close.CompletePlacement(request, true));
        Assert.Null(close.TryBeginPlacement());
        Assert.Equal(DesktopHandoffCloseDecision.ExposeDesktop, close.ReleaseStoppingClick());
        Assert.Equal(DesktopHandoffCloseDecision.KeepCovered, close.ReleaseStoppingClick());
        Assert.Null(close.TryBeginPlacement());
    }

    [Fact]
    public void ButtonUpBeforeCompletionKeepsTheCoverUntilPlacementIsVerified()
    {
        var close = new DesktopHandoffClose();
        long request = close.TryBeginPlacement()!.Value;
        Assert.True(close.HoldStoppingClick());

        Assert.Equal(DesktopHandoffCloseDecision.KeepCovered, close.ReleaseStoppingClick());
        Assert.Equal(DesktopHandoffCloseDecision.ExposeDesktop, close.CompletePlacement(request, true));
        Assert.Equal(DesktopHandoffCloseDecision.KeepCovered, close.CompletePlacement(request, true));
        Assert.False(close.HoldStoppingClick());
    }

    [Fact]
    public void InvalidatedPlacementMustDrainAndCannotRetireItsReplacement()
    {
        var close = new DesktopHandoffClose();
        long obsolete = close.TryBeginPlacement()!.Value;
        close.Invalidate();
        Assert.Null(close.TryBeginPlacement());
        Assert.Equal(DesktopHandoffCloseDecision.KeepCovered, close.CompletePlacement(obsolete, true));

        long current = close.TryBeginPlacement()!.Value;
        Assert.NotEqual(obsolete, current);
        Assert.Equal(DesktopHandoffCloseDecision.KeepCovered, close.CompletePlacement(obsolete, true));
        Assert.Null(close.TryBeginPlacement());
        Assert.Equal(DesktopHandoffCloseDecision.ExposeDesktop, close.CompletePlacement(current, true));
    }

    [Fact]
    public void UnverifiedPlacementKeepsCoverAndAllowsReplanning()
    {
        var close = new DesktopHandoffClose();
        long request = close.TryBeginPlacement()!.Value;
        Assert.True(close.HoldStoppingClick());

        Assert.Equal(DesktopHandoffCloseDecision.KeepCovered, close.CompletePlacement(request, false));
        Assert.Equal(DesktopHandoffCloseDecision.KeepCovered, close.ReleaseStoppingClick());
        long retry = close.TryBeginPlacement()!.Value;
        Assert.Equal(DesktopHandoffCloseDecision.ExposeDesktop, close.CompletePlacement(retry, true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CaptureLossRevokesAuthorizationBeforeOrAfterCompletion(bool completed)
    {
        var close = new DesktopHandoffClose();
        long request = close.TryBeginPlacement()!.Value;
        Assert.True(close.HoldStoppingClick());

        if (completed)
            Assert.Equal(DesktopHandoffCloseDecision.WaitForButtonUp, close.CompletePlacement(request, true));

        close.LoseStoppingClickCapture();
        Assert.False(close.StoppingClickHeld);
        Assert.Equal(DesktopHandoffCloseDecision.KeepCovered, close.ReleaseStoppingClick());

        if (!completed)
        {
            Assert.Null(close.TryBeginPlacement());
            Assert.Equal(DesktopHandoffCloseDecision.KeepCovered, close.CompletePlacement(request, true));
        }

        long retry = close.TryBeginPlacement()!.Value;
        Assert.Equal(DesktopHandoffCloseDecision.ExposeDesktop, close.CompletePlacement(retry, true));
    }

    [Fact]
    public void LifecycleInvalidationRevokesACompletedCloseWaitingForButtonUp()
    {
        var close = new DesktopHandoffClose();
        long request = close.TryBeginPlacement()!.Value;
        Assert.True(close.HoldStoppingClick());
        Assert.Equal(DesktopHandoffCloseDecision.WaitForButtonUp, close.CompletePlacement(request, true));

        close.Invalidate();
        Assert.True(close.StoppingClickHeld);
        Assert.Equal(DesktopHandoffCloseDecision.KeepCovered, close.ReleaseStoppingClick());
        long retry = close.TryBeginPlacement()!.Value;
        Assert.Equal(DesktopHandoffCloseDecision.ExposeDesktop, close.CompletePlacement(retry, true));
    }
}
