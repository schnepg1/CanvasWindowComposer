using System.Windows;
using CanvasDesktop;

namespace CanvasDesktop.Tests;

public class PlaceholderLabelTests
{
    private static PlaceholderAnnotation Card(int handle = 1, double x = 500, double y = 500, bool priority = false)
        => new((IntPtr)handle, "Canvas project", new WorldRect { X = x, Y = y, W = 800, H = 550 }, priority);

    [Theory]
    [InlineData(.1)]
    [InlineData(.25)]
    [InlineData(.39)]
    public void ZoomedOutTitlesStayCenteredInsideTheirCardsAndScaleWithZoom(double zoom)
    {
        var card = Card();
        var label = Assert.Single(PlaceholderLabelLayout.Get(new[] { card }, 0, 0, zoom,
            new ScreenRect(0, 0, 1920, 1080), 1, 1));
        var projectedCard = new Rect(500 * zoom, 500 * zoom, 800 * zoom, 550 * zoom);
        Assert.True(projectedCard.Contains(label.Rect));
        Assert.Equal(projectedCard.X + projectedCard.Width / 2, label.Rect.X + label.Rect.Width / 2, 6);
        Assert.Equal(projectedCard.Y + projectedCard.Height / 2, label.Rect.Y + label.Rect.Height / 2, 6);
        Assert.Equal(84 * zoom, label.FontSize, 6);
        Assert.Equal(752 * zoom, label.Rect.Width, 6);
        Assert.Equal(502 * zoom, label.Rect.Height, 6);
    }

    [Fact]
    public void MixedDpiAndNegativeMonitorOriginKeepLabelCenteredInDips()
    {
        var card = Card(x: -1800, y: 600);
        var label = Assert.Single(PlaceholderLabelLayout.Get(new[] { card }, -100, 100, .25,
            new ScreenRect(-1920, 0, 1920, 1080), 1.5, 1.5));
        double center = ((card.Bounds.X + card.Bounds.W / 2 + 100) * .25 + 1920) / 1.5;
        Assert.Equal(center, label.Rect.X + label.Rect.Width / 2, 6);
        Assert.Equal(84 * .25 / 1.5, label.FontSize, 6);
    }

    [Fact]
    public void AdjacentTitlesRemainVisibleWhenHoverPriorityOrEnumerationChanges()
    {
        var first = Card();
        var second = Card(2, x: 1320, priority: true);
        var monitor = new ScreenRect(0, 0, 1920, 1080);
        var labels = PlaceholderLabelLayout.Get(new[] { first, second }, 0, 0, .1, monitor, 1, 1);
        Assert.Equal(2, labels.Count);
        Assert.False(labels[0].Rect.IntersectsWith(labels[1].Rect));
        var changed = PlaceholderLabelLayout.Get(new[] { second with { Priority = false }, first with { Priority = true } },
            0, 0, .1, monitor, 1, 1);
        Assert.Equal(2, changed.Count);
        foreach (var label in labels)
            Assert.Equal(label.Rect, Assert.Single(changed, item => item.Card.Handle == label.Card.Handle).Rect);
    }

    [Fact]
    public void PartlyOffscreenTitleStaysAnchoredInsteadOfSlidingOntoTheMonitor()
    {
        var label = Assert.Single(PlaceholderLabelLayout.Get(new[] { Card(x: -600, y: -100) }, 0, 0, .25,
            new ScreenRect(0, 0, 1920, 1080), 1, 1));
        Assert.Equal(-50, label.Rect.X + label.Rect.Width / 2, 6);
        Assert.Equal(43.75, label.Rect.Y + label.Rect.Height / 2, 6);
        Assert.True(label.Rect.Left < 0);
    }

    [Fact]
    public void CloseZoomOffscreenCardsAndSubpixelCardsHaveNoTitle()
    {
        var monitor = new ScreenRect(0, 0, 1920, 1080);
        Assert.Empty(PlaceholderLabelLayout.Get(new[] { Card() }, 0, 0, 1, monitor, 1, 1));
        Assert.Empty(PlaceholderLabelLayout.Get(new[] { Card() }, 0, 0, WindowManager.OverviewLivePreviewZoom, monitor, 1, 1));
        Assert.Empty(PlaceholderLabelLayout.Get(new[] { Card(x: -10000) }, 0, 0, .25, monitor, 1, 1));
        Assert.Empty(PlaceholderLabelLayout.Get(new[] { Card() }, 0, 0, .001, monitor, 1, 1));
        Assert.Empty(PlaceholderLabelLayout.Get(new[] { Card(priority: true) }, 0, 0, .001, monitor, 1, 1));
    }

    [Fact]
    public void OnlyCurrentDesktopCardsAreAnnotatedAndRenamingUsesTheCustomName()
    {
        var item = new WorkspaceItem { Title = "Original title", IsFolder = true, ExecutablePath = @"C:\Windows\explorer.exe",
            Target = @"C:\Projects", Bounds = new WorldRect { W = 800, H = 550 } };
        using var rig = new WorkspaceHarness(new WorkspaceDocument { Items = new() { item } });
        rig.Workspace.Start();
        var card = Assert.Single(rig.Cards.Created);
        Assert.True(card.Item.IsFolder);
        Assert.Equal("Original title", rig.Workspace.GetPlaceholderTitles()[card.Handle]);
        rig.Workspace.SetName(item.Id, "My project");
        Assert.Equal("My project", rig.Workspace.GetPlaceholderTitles()[card.Handle]);
        // The first real desktop ID resolves Guid.Empty at startup; the next
        // switch actually leaves that desktop and must remove its annotations.
        rig.Desktops.SwitchTo(Guid.NewGuid());
        rig.Desktops.SwitchTo(Guid.NewGuid());
        Assert.Empty(rig.Workspace.GetPlaceholderTitles());
    }
}
