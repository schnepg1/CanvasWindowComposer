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
    [InlineData(.49)]
    public void ZoomedOutTitlesStayReadableAndInsideTheMonitor(double zoom)
    {
        var label = Assert.Single(PlaceholderLabelLayout.Get(new[] { Card() }, 0, 0, zoom,
            new ScreenRect(0, 0, 1920, 1080), 1, 1));
        Assert.InRange(label.FontSize, 22, 32);
        Assert.True(new Rect(0, 0, 1920, 1080).Contains(label.Rect));
    }

    [Fact]
    public void MixedDpiAndNegativeMonitorOriginKeepLabelCenteredInDips()
    {
        var card = Card(x: -1800, y: 600);
        var label = Assert.Single(PlaceholderLabelLayout.Get(new[] { card }, -100, 100, .25,
            new ScreenRect(-1920, 0, 1920, 1080), 1.5, 1.5));
        double center = ((card.Bounds.X + card.Bounds.W / 2 + 100) * .25 + 1920) / 1.5;
        Assert.Equal(center, label.Rect.X + label.Rect.Width / 2, 6);
        Assert.Equal(22, label.FontSize);
    }

    [Fact]
    public void HoveredCardWinsCollisionsAndReservedUiRemainsClear()
    {
        var cards = new[] { Card(), Card(2, priority: true) };
        var label = Assert.Single(PlaceholderLabelLayout.Get(cards, 0, 0, .25,
            new ScreenRect(0, 0, 1920, 1080), 1, 1));
        Assert.Equal((IntPtr)2, label.Card.Handle);
        Assert.Empty(PlaceholderLabelLayout.Get(cards, 0, 0, .25,
            new ScreenRect(0, 0, 1920, 1080), 1, 1, new[] { label.Rect }));
    }

    [Fact]
    public void CloseZoomOffscreenCardsAndUnrecognizablyTinyCardsHaveNoBadge()
    {
        var monitor = new ScreenRect(0, 0, 1920, 1080);
        Assert.Empty(PlaceholderLabelLayout.Get(new[] { Card() }, 0, 0, 1, monitor, 1, 1));
        Assert.Empty(PlaceholderLabelLayout.Get(new[] { Card() }, 0, 0, .5, monitor, 1, 1));
        Assert.Empty(PlaceholderLabelLayout.Get(new[] { Card(x: -10000) }, 0, 0, .25, monitor, 1, 1));
        Assert.Empty(PlaceholderLabelLayout.Get(new[] { Card() }, 0, 0, .001, monitor, 1, 1));
        Assert.Single(PlaceholderLabelLayout.Get(new[] { Card(priority: true) }, 0, 0, .001, monitor, 1, 1));
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
