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
    [InlineData(1, false, 650)]
    [InlineData(2, false, 650)]
    [InlineData(1, true, 650)]
    [InlineData(2, true, 650)]
    [InlineData(1, false, 340)]
    [InlineData(1, true, 340)]
    public void BackpackRendersItsGridAndKeptSessionLabelsAtDifferentDpi(int scale, bool dismissed, int height)
        => WorkspaceViewTests.OnSta(() =>
        {
            var panel = new BackpackPanel(); panel.SetOpen(true); panel.SetAvailableHeight(height); panel.SetHelpDismissed(dismissed);
            panel.UpdateItems([Item("Project notes", false), Item("Browser reference", false), Item("Quick scratchpad", true),
                Item("Downloads", true, "Minimized"), Item("Design workspace", false, "Placeholder"), Item("Terminal", true)]);
            panel.Measure(new Size(450, height)); panel.Arrange(new Rect(0, 0, 450, height)); panel.UpdateLayout();
            var image = new RenderTargetBitmap(450 * scale, height * scale, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            image.Render(panel);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            string path = Path.Combine(AppContext.BaseDirectory, "TestArtifacts"); Directory.CreateDirectory(path);
            using var stream = File.Create(Path.Combine(path, $"backpack-{scale}x-{height}-{dismissed}.png")); encoder.Save(stream);
            var text = Descendants(panel).OfType<TextBlock>().Select(t => t.Text).ToArray();
            Assert.Contains("KEPT", text); Assert.Contains("SESSION", text); Assert.Contains("BACKPACK", text);
            var fold = ButtonNamed(panel, "Fold backpack");
            var bottom = fold.TranslatePoint(new Point(0, fold.ActualHeight), panel).Y;
            Assert.InRange(bottom, height - 20, height);
        });

    [Fact]
    public void DismissingTipsFreesSpaceAndKeepsHelpStatusAndFoldControlsAvailable()
        => WorkspaceViewTests.OnSta(() =>
        {
            var panel = new BackpackPanel(); panel.SetOpen(true); panel.UpdateItems([Item("Notes", false)]);
            void Layout() { panel.Measure(new Size(450, 650)); panel.Arrange(new Rect(0, 0, 450, 650)); panel.UpdateLayout(); }
            Layout();
            var scroll = Descendants(panel).OfType<ScrollViewer>().Single(); double originalHeight = scroll.ActualHeight;
            var checkbox = Descendants(panel).OfType<CheckBox>().Single();
            var help = Assert.IsType<Border>(Assert.IsType<StackPanel>(checkbox.Parent).Parent);
            var info = ButtonNamed(panel, "Backpack tips");
            Assert.Equal(Visibility.Collapsed, info.Visibility);
            var preferences = new List<bool>(); panel.HelpDismissedChanged += preferences.Add;
            checkbox.IsChecked = true; checkbox.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Layout();
            Assert.Equal(Visibility.Collapsed, help.Visibility);
            Assert.Equal(Visibility.Visible, info.Visibility);
            Assert.True(scroll.ActualHeight > originalHeight, $"List height changed from {originalHeight} to {scroll.ActualHeight}");
            Assert.Contains("KEPT", Assert.IsType<TextBlock>(info.ToolTip).Text);
            panel.SetHint("No clear landing space here."); Layout();
            Assert.Equal(Visibility.Visible, Descendants(panel).OfType<TextBlock>().Single(t => t.Text == "No clear landing space here.").Visibility);
            info.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); Layout();
            Assert.Equal(Visibility.Visible, help.Visibility); Assert.False(checkbox.IsChecked); Assert.Equal(Visibility.Collapsed, info.Visibility);
            Assert.Equal(new[] { true, false }, preferences);
            panel.SetHelpDismissed(true); panel.SetOpen(false); panel.SetOpen(true); Layout();
            Assert.Equal(Visibility.Visible, info.Visibility); Assert.Equal(2, preferences.Count);
            bool? requested = null; panel.OpenRequested += open => requested = open;
            ButtonNamed(panel, "Fold backpack").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.Equal(false, requested);
        });

    private static Button ButtonNamed(BackpackPanel panel, string name) => Descendants(panel).OfType<Button>()
        .Single(b => System.Windows.Automation.AutomationProperties.GetName(b) == name);

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

    [Fact]
    public void UnplacedItemWarningAnimatesShakeAndShowsConnectedExplanationPopup()
        => WorkspaceViewTests.OnSta(() =>
        {
            var panel = new BackpackPanel();
            panel.SetOpen(true);
            var item = Item("Editor Window", false);
            panel.UpdateItems([item]);
            panel.Measure(new Size(450, 650));
            panel.Arrange(new Rect(0, 0, 450, 650));
            panel.UpdateLayout();

            // Test shaking initiation
            panel.ShakeTile(item.Id);
            var borders = Descendants(panel).OfType<Border>().Where(b => b.Width == 122 && b.Height == 136).ToArray();
            var tile = Assert.Single(borders);
            Assert.IsType<TranslateTransform>(tile.RenderTransform);

            // Trigger warning with immediate popup
            panel.WarnUnplacedItem(item.Id, immediate: true);
            panel.UpdateLayout();

            Assert.True(panel.IsWarningPopupOpen);
            Assert.Equal(item.Id, panel.WarningTargetId);
            Assert.Contains("not placed anywhere on the canvas", panel.WarningMessage);

            var texts = Descendants(panel).OfType<TextBlock>().Select(t => t.Text).ToArray();
            Assert.Contains("Window Not Placed", texts);
            Assert.Contains("Drag and drop it onto the canvas to place and open it.", texts);

            // Acknowledge button dismisses
            var gotIt = ButtonNamed(panel, "Acknowledge unplaced window warning");
            gotIt.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.False(panel.IsWarningPopupOpen);
            Assert.Null(panel.WarningTargetId);
        });

    [Fact]
    public void UnplacedWarningDismissesOnCloseButtonAndEscapeAndFold()
        => WorkspaceViewTests.OnSta(() =>
        {
            var panel = new BackpackPanel();
            panel.SetOpen(true);
            var item = Item("Code", false);
            panel.UpdateItems([item]);
            panel.Measure(new Size(450, 650));
            panel.Arrange(new Rect(0, 0, 450, 650));
            panel.UpdateLayout();

            // 1. Dismiss via close "×" button
            panel.WarnUnplacedItem(item.Id, immediate: true);
            Assert.True(panel.IsWarningPopupOpen);
            var close = ButtonNamed(panel, "Dismiss unplaced window warning");
            close.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.False(panel.IsWarningPopupOpen);

            // 2. Dismiss via Escape key
            panel.WarnUnplacedItem(item.Id, immediate: true);
            Assert.True(panel.IsWarningPopupOpen);
            using var source = new System.Windows.Interop.HwndSource(0, 0, 0, 0, 0, "test", IntPtr.Zero);
            panel.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                source, 0, System.Windows.Input.Key.Escape)
            {
                RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent
            });
            Assert.False(panel.IsWarningPopupOpen);
            Assert.True(panel.IsOpen); // Backpack itself stayed open

            // 3. Dismiss on folding backpack
            panel.WarnUnplacedItem(item.Id, immediate: true);
            Assert.True(panel.IsWarningPopupOpen);
            panel.SetOpen(false);
            Assert.False(panel.IsWarningPopupOpen);
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
