using CanvasDesktop;
using System.Drawing;

namespace CanvasDesktop.Tests;

public class CanvasPeekPolicyTests
{
    [Fact]
    public void DwellRequiresAStableTargetAndToleratesSmallPointerJitter()
    {
        var hover = new CanvasPeekHover();
        hover.Observe((IntPtr)1, new(10, 10), 100, 6);
        hover.Observe((IntPtr)1, new(13, 14), 700, 6);
        Assert.False(hover.Ready(1099, 1000));
        Assert.True(hover.Ready(1100, 1000));
        hover.Observe((IntPtr)1, new(20, 10), 1101, 6);
        Assert.False(hover.Ready(1101, 1000));
        hover.Observe((IntPtr)2, new(20, 10), 1800, 6);
        Assert.False(hover.Ready(2101, 1000));
        Assert.True(hover.Ready(2800, 1000));
    }

    [Fact]
    public void LeavingTheTargetDiscardsElapsedDwell()
    {
        var hover = new CanvasPeekHover();
        hover.Observe((IntPtr)1, new(-1200, 100), 0, 6);
        hover.Observe(IntPtr.Zero, new(-800, 100), 999, 6);
        Assert.False(hover.Ready(2000, 1000));
        hover.Observe((IntPtr)1, new(-1200, 100), 2000, 6);
        Assert.False(hover.Ready(2999, 1000));
    }

    [Theory]
    [InlineData(-1920, -200, 1920, 1040, 96)]
    [InlineData(0, 40, 1920, 1040, 144)]
    [InlineData(1920, 0, 1280, 720, 192)]
    [InlineData(-800, 0, 800, 600, 144)]
    [InlineData(0, 0, 320, 300, 192)]
    public void NearFullSizePeekStaysInsideTheMonitorWorkArea(int x, int y, int w, int h, int dpi)
    {
        var area = new Rectangle(x, y, w, h);
        foreach (var source in new[] { new Rectangle(x, y, 30, 20), new Rectangle(x + w - 40, y + h - 30, 40, 30),
            new Rectangle(x + w / 2, y + h / 2, 100, 80) })
        {
            var bounds = CanvasPeekLayout.Place(source, new(3440, 1440), area, dpi);
            Assert.True(area.Contains(bounds), $"{bounds} escaped {area}");
            Assert.InRange(bounds.Width, 1, w);
            Assert.InRange(bounds.Height, 1, h);
        }
    }

    [Theory]
    [InlineData(3440, 1440, 700, 500)]
    [InlineData(400, 900, 700, 500)]
    [InlineData(300, 200, 700, 500)]
    public void ContentKeepsItsAspectRatioWithoutUpscaling(int w, int h, int availableW, int availableH)
    {
        var area = new Rectangle(15, 90, availableW, availableH);
        var fitted = CanvasPeekLayout.Fit(new(w, h), area);
        Assert.True(area.Contains(fitted));
        Assert.InRange(fitted.Width, 1, w);
        Assert.InRange(fitted.Height, 1, h);
        Assert.InRange(Math.Abs(fitted.Width / (double)fitted.Height - w / (double)h), 0, .01);
    }

    [Fact]
    public void MorphHasExactEndpointsAndNeverOvershoots()
    {
        var from = new Rectangle(-1000, 70, 30, 20);
        var to = new Rectangle(-900, 100, 900, 600);
        Assert.Equal(from, CanvasPeekLayout.Animate(from, to, -1));
        Assert.Equal(to, CanvasPeekLayout.Animate(from, to, 2));
        var middle = CanvasPeekLayout.Animate(from, to, .5);
        Assert.InRange(middle.X, from.X, to.X);
        Assert.InRange(middle.Width, from.Width, to.Width);
    }
}
