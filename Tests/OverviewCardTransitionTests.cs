using System.Windows.Media;
using CanvasDesktop;

namespace CanvasDesktop.Tests;

public class OverviewCardTransitionTests
{
    private readonly Guid _id = Guid.NewGuid();
    private OverviewCardInput Input(OverviewCardMode mode, bool ready = true, ImageSource? image = null, int handle = 42)
        => new(_id, new((IntPtr)handle, mode == OverviewCardMode.Live ? "" : "Notes",
            new WorldRect { X = 100, Y = 100, W = 400, H = 300 }, Image: image), mode, ready);

    [Fact]
    public void LiveToCardFadeKeepsUnderlayOnlyUntilTheCardIsOpaque()
    {
        var transitions = new OverviewCardTransitions();
        Assert.Empty(transitions.Update([Input(OverviewCardMode.Live)], 0));
        Assert.Empty(transitions.Update([Input(OverviewCardMode.Simple, handle: 99)], 10));
        Assert.True(transitions.IsAnimating);
        var middle = Assert.Single(transitions.Update([Input(OverviewCardMode.Simple, handle: 99)], 85));
        Assert.Equal(.5, middle.Opacity, 6);
        Assert.True(transitions.NeedsLiveUnderlay(_id));
        Assert.Equal(1, middle.LabelOpacity);
        var end = Assert.Single(transitions.Update([Input(OverviewCardMode.Simple, handle: 99)], 160));
        Assert.Equal(1, end.Opacity);
        Assert.False(transitions.NeedsLiveUnderlay(_id));
        Assert.False(transitions.IsAnimating);
        Assert.Equal(0, transitions.RetainedImages);
    }

    [Fact]
    public void RestorationHoldsCardUntilLiveRegistrationThenFadesWithoutLosingTitle()
    {
        var transitions = new OverviewCardTransitions();
        transitions.Update([Input(OverviewCardMode.Simple, handle: 99)], 0);
        var waiting = Assert.Single(transitions.Update([Input(OverviewCardMode.Live, ready: false)], 1000));
        Assert.Equal(1, waiting.Opacity);
        Assert.Equal("Notes", waiting.Title);
        Assert.Equal((IntPtr)42, waiting.Handle); // Input targets the existing app, not the disposed card.
        Assert.False(transitions.IsAnimating);
        transitions.Update([Input(OverviewCardMode.Live)], 1010);
        Assert.Equal(.5, Assert.Single(transitions.Update([Input(OverviewCardMode.Live)], 1085)).Opacity, 6);
        Assert.Empty(transitions.Update([Input(OverviewCardMode.Live)], 1160));
        Assert.False(transitions.IsAnimating);
    }

    [Fact]
    public void QuickReversalStartsFromCurrentOpacityInsteadOfJumping()
    {
        var transitions = new OverviewCardTransitions();
        transitions.Update([Input(OverviewCardMode.Simple)], 0);
        transitions.Update([Input(OverviewCardMode.Live)], 10);
        var before = Assert.Single(transitions.Update([Input(OverviewCardMode.Live)], 70));
        var reversed = Assert.Single(transitions.Update([Input(OverviewCardMode.Simple)], 70));
        Assert.Equal(before.Opacity, reversed.Opacity);
        Assert.Equal(1, Assert.Single(transitions.Update([Input(OverviewCardMode.Simple)], 220)).Opacity);
    }

    [Fact]
    public void DetailedAndFloatingTitlesNeverShowTogetherAndDistantImagesAreReleased()
        => WorkspaceViewTests.OnSta(() =>
        {
            var image = new DrawingImage(); image.Freeze();
            var transitions = new OverviewCardTransitions();
            transitions.Update([Input(OverviewCardMode.Detailed, image: image)], 0);
            transitions.Update([Input(OverviewCardMode.Simple)], 10);
            for (int time = 10; time < 160; time += 10)
            {
                var frame = Assert.Single(transitions.Update([Input(OverviewCardMode.Simple)], time));
                Assert.False(frame.ImageOpacity > 0 && frame.LabelOpacity > 0);
                Assert.Equal(1, frame.Opacity); // No transparent hole between title treatments.
            }
            var end = Assert.Single(transitions.Update([Input(OverviewCardMode.Simple)], 160));
            Assert.Null(end.Image);
            Assert.Equal(0, transitions.RetainedImages);
            Assert.False(transitions.IsAnimating);
        });

    [Fact]
    public void PanAndOverlapGeometryStayCurrentWhileFading()
    {
        var transitions = new OverviewCardTransitions();
        transitions.Update([Input(OverviewCardMode.Simple)], 0);
        transitions.Update([Input(OverviewCardMode.Live)], 10);
        var moved = Input(OverviewCardMode.Live);
        moved = moved with { Card = moved.Card with { Bounds = new WorldRect { X = 900, Y = -100, W = 400, H = 300 },
            Occluders = [new WorldRect { X = 950, Y = -100, W = 100, H = 300 }] } };
        var frame = Assert.Single(transitions.Update([moved], 85));
        Assert.Equal(900, frame.Bounds.X);
        Assert.Single(frame.Occluders!);
    }

    [Fact]
    public void AlreadyMinimizedWindowGetsAnOpaqueCardWithoutAnUnavailableUnderlay()
    {
        var transitions = new OverviewCardTransitions();
        transitions.Update([Input(OverviewCardMode.Live)], 0);
        var card = Assert.Single(transitions.Update([Input(OverviewCardMode.Simple, ready: false)], 10));
        Assert.Equal(1, card.Opacity);
        Assert.False(transitions.IsAnimating);
    }

    [Fact]
    public void OffscreenOrReplacedIdentityDoesNotRetainAnimationOrSnapshots()
        => WorkspaceViewTests.OnSta(() =>
        {
            var image = new DrawingImage(); image.Freeze();
            var transitions = new OverviewCardTransitions();
            transitions.Update([Input(OverviewCardMode.Detailed, image: image)], 0);
            transitions.Update([Input(OverviewCardMode.Live)], 10);
            var other = Input(OverviewCardMode.Live) with { Id = Guid.NewGuid() };
            Assert.Empty(transitions.Update([other], 20));
            Assert.Equal(0, transitions.RetainedImages);
            Assert.False(transitions.IsAnimating);
            transitions.Clear();
            Assert.False(transitions.NeedsLiveUnderlay(other.Id));
        });
}
