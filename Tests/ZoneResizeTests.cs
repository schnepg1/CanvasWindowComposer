using CanvasDesktop;

namespace CanvasDesktop.Tests;

public class ZoneResizeTests
{
    private static WorldRect Bounds => new() { X = 100, Y = 200, W = 800, H = 600 };

    [Theory]
    [InlineData(0, 140, 280, 760, 520)]
    [InlineData(1, 100, 280, 840, 520)]
    [InlineData(2, 100, 200, 840, 680)]
    [InlineData(3, 140, 200, 760, 680)]
    public void EveryCornerKeepsOppositeCornerFixed(int corner, double x, double y, double w, double h)
    {
        Assert.True(ZoneResizeLayout.TryResize(Bounds, (ZoneResizeCorner)corner, 40, 80, out var b));
        Assert.Equal(x, b.X); Assert.Equal(y, b.Y); Assert.Equal(w, b.W); Assert.Equal(h, b.H);
    }

    [Fact]
    public void CrossingCornerClampsWithoutFlippingAndInvalidDestinationsAreRejected()
    {
        Assert.True(ZoneResizeLayout.TryResize(Bounds, ZoneResizeCorner.TopLeft, 5000, 5000, out var b));
        Assert.Equal(64, b.W); Assert.Equal(64, b.H); Assert.Equal(900, b.X + b.W); Assert.Equal(800, b.Y + b.H);
        Assert.True(ZoneResizeLayout.TryResize(Bounds, ZoneResizeCorner.BottomRight, 40000, 0, out _));
        Assert.False(ZoneResizeLayout.TryResize(Bounds, ZoneResizeCorner.TopLeft, double.PositiveInfinity, 0, out _));
        var small = Bounds; small.W = 12; small.H = 20;
        Assert.True(ZoneResizeLayout.TryResize(small, ZoneResizeCorner.BottomRight, 0, 0, out b));
        Assert.Equal(small, b);
    }

    [Fact]
    public void ResizePreviewUsesOriginalBoundsAndCommitOrCancelNeverMovesMembers()
    {
        var zone = new ZoneView(Guid.NewGuid(), "Area", "#60A5FA", Bounds);
        var gesture = new OverviewZoneInteraction();
        gesture.BeginResize(zone, ZoneResizeCorner.TopLeft, 100, 200);
        gesture.Update(120, 240); gesture.Update(140, 280);
        Assert.Equal(760, gesture.Preview!.Value.W); Assert.Equal(zone.Id, gesture.MovingId);
        Assert.Empty(gesture.Ghosts); Assert.Equal(Bounds, zone.Bounds);
        var result = gesture.Complete(150, 290, 0.5);
        Assert.Equal(ZoneGestureKind.Resize, result!.Kind); Assert.Equal(750, result.Bounds.W);
        gesture.BeginResize(zone, ZoneResizeCorner.BottomRight, 900, 800);
        Assert.Null(gesture.Complete(902, 802, 1));
        gesture.BeginResize(zone, ZoneResizeCorner.BottomRight, 900, 800);
        gesture.Update(1100, 1000); gesture.Cancel(); Assert.Null(gesture.Complete(1100, 1000, 1));
        gesture.BeginResize(zone, ZoneResizeCorner.BottomRight, 900, 800);
        Assert.Equal(49900, gesture.Complete(50000, 800, 1)!.Bounds.W);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void VisibleCornersAndHitTargetsShareZoomDpiAndMonitorCoordinates(double dpi)
    {
        var monitor = new ScreenRect(-1920, 100, 1920, 1080);
        var b = new WorldRect { X = -1800, Y = 250, W = 900, H = 700 };
        var drawn = ZoneResizeLayout.Get(b, 0, 0, 1, monitor, dpi, dpi);
        var targets = ZoneResizeLayout.Get(b, 0, 0, 1, monitor, dpi, dpi, true);
        Assert.Equal(4, drawn.Count);
        for (int i = 0; i < drawn.Count; i++)
        {
            var r = drawn[i].Rect;
            Assert.Equal(8 * dpi, r.Width); Assert.Equal(18 * dpi, targets[i].Rect.Width);
            Assert.True(targets[i].Rect.Contains(r.X + r.Width / 2, r.Y + r.Height / 2));
        }
        Assert.Equal(120 - 4 * dpi, drawn[0].Rect.X);
        Assert.Equal(150 - 4 * dpi, drawn[0].Rect.Y);
        var clipped = ZoneResizeLayout.Get(b, -1500, 0, 1, new ScreenRect(0, 0, 1920, 1080), dpi, dpi);
        Assert.Equal(2, clipped.Count); Assert.All(clipped, h => Assert.True(h.Corner is ZoneResizeCorner.TopRight or ZoneResizeCorner.BottomRight));
        Assert.Empty(ZoneResizeLayout.Get(b, 0, 0, 0.01, monitor, dpi, dpi));
    }
}
