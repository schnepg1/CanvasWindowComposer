using CanvasDesktop.PerformanceHarness;

namespace CanvasDesktop.Tests;

public class PresentationProfileTests
{
    [Fact]
    public void DefaultsRetainExistingProfileAndMatrixProfileIncludesRealGestureScenarios()
    {
        Assert.Equal(new(false, 1), PresentationProfile.Parse([]));
        Assert.Equal(new(true, .25), PresentationProfile.Parse(["--presentation-only", "--overview-zoom=0.25"]));
        Assert.Equal(new[] { "StationaryOverview", "OverviewPan", "ZoomBursts", "WindowDrag", "Panning", "CanvasPanMomentum", "DesktopPanMomentum" }, PresentationProfile.Phases);
        Assert.False(PresentationProfile.IsOverview("Visible"));
    }

    [Theory]
    [InlineData("--no-desktop-momentum", false, true)]
    [InlineData("--no-canvas-momentum", true, false)]
    public void MomentumOptionsCanBeComparedIndependently(string option, bool desktop, bool canvas)
    {
        var profile = PresentationProfile.Parse(["--presentation-only", option]);
        Assert.Equal(desktop, profile.DesktopPanMomentum);
        Assert.Equal(canvas, profile.CanvasPanMomentum);
        Assert.True(PresentationProfile.IsOverview("CanvasPanMomentum"));
        Assert.True(PresentationProfile.IsOverview("DesktopPanMomentum"));
        var both = PresentationProfile.Parse(["--no-desktop-momentum", "--no-canvas-momentum"]);
        Assert.False(both.DesktopPanMomentum);
        Assert.False(both.CanvasPanMomentum);
    }

    [Fact]
    public void MomentumGestureReleasesAfterHeldMotionAndLeavesTheCoastUntouched()
    {
        var gesture = new PresentationPanGesture();
        Assert.Equal(new[] { new PresentationPanStep(PresentationPanAction.Begin, 0, 0, 0) }, gesture.Next(0));
        Assert.Equal(new[] { new PresentationPanStep(PresentationPanAction.Move, 0, 60, 30) }, gesture.Next(.1));
        Assert.Equal(new[] { new PresentationPanStep(PresentationPanAction.Move, 0, 120, 60),
            new PresentationPanStep(PresentationPanAction.Release, 0, 120, 60) }, gesture.Next(.2));
        Assert.Empty(gesture.Next(.5));
        Assert.Empty(gesture.Next(1.9));
        Assert.Equal(new[] { new PresentationPanStep(PresentationPanAction.Begin, 1, 0, 0) }, gesture.Next(2));
        Assert.Equal(new[] { new PresentationPanStep(PresentationPanAction.Move, 1, -60, -30) }, gesture.Next(2.1));
    }

    [Fact]
    public void LateMomentumPumpFinishesHeldDragWithoutReplayingMissedCycles()
    {
        var gesture = new PresentationPanGesture();
        gesture.Next(0);
        gesture.Next(.1);
        Assert.Equal(new[] { new PresentationPanStep(PresentationPanAction.Move, 0, 120, 60),
            new PresentationPanStep(PresentationPanAction.Release, 0, 120, 60) }, gesture.Next(5));
        Assert.Empty(gesture.Next(5.1));
        Assert.Equal(new[] { new PresentationPanStep(PresentationPanAction.Begin, 3, 0, 0) }, gesture.Next(6));
        Assert.Empty(new PresentationPanGesture().Next(1));
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("0")]
    [InlineData("0.049")]
    [InlineData("1.01")]
    public void InvalidZoomsAreRejected(string value)
        => Assert.Throws<ArgumentException>(() => PresentationProfile.Parse(["--overview-zoom=" + value]));

    [Fact]
    public void ConflictingProfilesAreRejected()
        => Assert.Throws<ArgumentException>(() => PresentationProfile.Parse(["--presentation-only", "--skip-overview"]));

    [Theory]
    [InlineData(1, .5, -600)]
    [InlineData(1, .25, -900)]
    [InlineData(.5, .25, -600)]
    public void ZoomSetupUsesProductionWheelScale(double from, double to, int expected)
        => Assert.Equal(expected, PresentationProfile.WheelDelta(from, to));

    [Fact]
    public void TimingSummaryUsesNearestRankAndDoesNotInventMissingSamples()
    {
        Assert.Equal(new TimingSummary(0, null, null, null, null), TimingSummary.From([]));
        var samples = Enumerable.Range(1, 100).Reverse().Select(i => (double)i).ToArray();
        Assert.Equal(new TimingSummary(100, 50, 95, 99, 100), TimingSummary.From(samples));
        Assert.Equal(100, samples[0]); // The caller's recording stays detached from sorting.
        Assert.Equal(new TimingSummary(1, 7, 7, 7, 7), TimingSummary.From([7]));
    }

    [Fact]
    public void ZoomBurstsFollowFixedTargetsAndLeaveIdleTimeForRestoration()
    {
        var burst = new PresentationZoomBursts(.5);
        Assert.Equal(.4875, burst.NextTarget(0));
        Assert.Null(burst.NextTarget(.01));
        Assert.Equal(.475, burst.NextTarget(.045));
        Assert.Equal(.4625, burst.NextTarget(.09));
        Assert.Null(burst.NextTarget(.3));
        Assert.Equal(.475, burst.NextTarget(.5));
        Assert.Equal(.4875, burst.NextTarget(.545));
        Assert.Equal(.5, burst.NextTarget(.59));
        Assert.Null(burst.NextTarget(.95));
    }

    [Fact]
    public void LateZoomPumpsCatchUpWithoutReplayingStaleInputsAndRespectMinimumZoom()
    {
        var burst = new PresentationZoomBursts(.5);
        Assert.Equal(.4625, burst.NextTarget(.3));
        Assert.Equal(.5, burst.NextTarget(.9));
        Assert.Equal(.05, new PresentationZoomBursts(.05).NextTarget(0));
    }
}
