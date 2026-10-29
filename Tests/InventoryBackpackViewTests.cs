using CanvasDesktop;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CanvasDesktop.Tests;

[Collection("ShellViews")]
public class InventoryBackpackViewTests
{
    private static WorkspaceItemView Item(string title, bool temporary, string state = "Open")
        => new(Guid.NewGuid(), title, @"C:\Apps\Editor.exe", "", false,
            new() { W = 800, H = 600, InInventory = true }, state, Temporary: temporary);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void BackpackRendersItsGridAndKeptSessionLabelsAtDifferentDpi(int scale)
        => WorkspaceViewTests.OnSta(() =>
        {
            var panel = new BackpackPanel(); panel.SetOpen(true); panel.SetAvailableHeight(640);
            panel.UpdateItems([Item("Project notes", false), Item("Browser reference", false), Item("Quick scratchpad", true),
                Item("Downloads", true, "Minimized"), Item("Design workspace", false, "Placeholder"), Item("Terminal", true)]);
            panel.Measure(new Size(450, 650)); panel.Arrange(new Rect(0, 0, 450, 650)); panel.UpdateLayout();
            var image = new RenderTargetBitmap(450 * scale, 650 * scale, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            image.Render(panel);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            string path = Path.Combine(AppContext.BaseDirectory, "TestArtifacts"); Directory.CreateDirectory(path);
            using var stream = File.Create(Path.Combine(path, $"backpack-{scale}x.png")); encoder.Save(stream);
            var text = Descendants(panel).OfType<TextBlock>().Select(t => t.Text).ToArray();
            Assert.Contains("KEPT", text); Assert.Contains("SESSION", text); Assert.Contains("BACKPACK", text);
        });

    [Fact]
    public void FilteringInventoryOnlyChangesTheVisibleGrid()
        => WorkspaceViewTests.OnSta(() =>
        {
            var panel = new BackpackPanel(); panel.SetOpen(true);
            var items = new[] { Item("Notes", false), Item("Terminal", true) }; panel.UpdateItems(items);
            Descendants(panel).OfType<TextBox>().Single().Text = "Notes";
            var titles = Descendants(panel).OfType<TextBlock>().Select(t => t.Text).ToArray();
            Assert.Contains("Notes", titles); Assert.DoesNotContain("Terminal", titles);
            Assert.Equal(2, items.Length);
            panel.SetOpen(false); Assert.False(panel.IsOpen);
        });

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
