using CanvasDesktop;

namespace CanvasDesktop.Tests;

public class OverviewZoneInteractionTests
{
    private static ZoneView Zone() => new(Guid.NewGuid(), "Project", "#60A5FA", new WorldRect { X = 100, Y = 200, W = 800, H = 600 });

    [Fact]
    public void MovePreviewIsRelativeToOriginalAndCancelNeverCommits()
    {
        var zone = Zone(); var gesture = new OverviewZoneInteraction();
        gesture.BeginMove(zone, 120, 230);
        gesture.Update(140, 250); gesture.Update(160, 280);
        Assert.Equal(140, gesture.Preview!.Value.X); Assert.Equal(250, gesture.Preview.Value.Y);
        Assert.Equal(100, zone.Bounds.X);
        gesture.Cancel();
        Assert.False(gesture.Active); Assert.Null(gesture.Preview);
        Assert.Null(gesture.Complete(160, 280, 1));
    }

    [Fact]
    public void ReleaseReturnsFinalWorldDeltaAndSmallClickDoesNothing()
    {
        var gesture = new OverviewZoneInteraction(); var zone = Zone();
        gesture.BeginMove(zone, 0, 0);
        Assert.Null(gesture.Complete(2, 2, 1));
        gesture.BeginMove(zone, 0, 0);
        var result = gesture.Complete(-40, 80, 0.5);
        Assert.Equal(zone.Id, result!.ZoneId); Assert.Equal(-40, result.Dx); Assert.Equal(80, result.Dy);
        Assert.False(gesture.Active);
    }

    [Fact]
    public void GhostPreviewUsesOriginalMemberPositionsAndInvalidDestinationsCannotCommit()
    {
        var gesture = new OverviewZoneInteraction(); var zone = Zone();
        var member = new WorldRect { X = 9999900, Y = 200, W = 400, H = 300 };
        gesture.BeginMove(zone, 0, 0, new[] { member });
        gesture.Update(-100, 20);
        Assert.Equal(9999800, Assert.Single(gesture.Ghosts).X);
        Assert.Null(gesture.Complete(200, 0, 1));
        Assert.Empty(gesture.Ghosts);
    }

    [Fact]
    public void DrawingKeepsExistingMinimumScreenSizeAndResetsArmedState()
    {
        var gesture = new OverviewZoneInteraction(); gesture.Arm(); Assert.True(gesture.Armed);
        gesture.BeginDraw(100, 200);
        Assert.False(gesture.Armed); Assert.Null(gesture.Complete(110, 300, 1));
        gesture.BeginDraw(100, 200);
        var result = gesture.Complete(-100, -200, 0.25);
        Assert.Equal(ZoneGestureKind.Draw, result!.Kind);
        Assert.Equal(-100, result.Bounds.X); Assert.Equal(400, result.Bounds.H);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void HeaderHitAreaUsesSameDpiAndMonitorOffsetAsRendering(double dpi)
    {
        var monitor = new ScreenRect(-1920, 100, 1920, 1080);
        var zone = new WorldRect { X = -1800, Y = 250, W = 900, H = 700 };
        var header = ZoneLabelLayout.Get(zone, 0, 0, 1, monitor, dpi, dpi)!.Value;
        Assert.Equal(120 + 10 * dpi, header.X);
        Assert.True(header.Contains(header.X + 2, header.Y + 2));
        Assert.False(header.Contains(header.X, header.Y + header.Height));
        Assert.Equal(28 * dpi, header.Height);
    }
}
