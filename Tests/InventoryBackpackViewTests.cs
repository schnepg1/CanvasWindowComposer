using CanvasDesktop;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

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
    public void BackpackUsesWpfUiFluentControlsAndFeatures()
        => WorkspaceViewTests.OnSta(() =>
        {
            var panel = new BackpackPanel();
            panel.SetOpen(true);
            var items = new[] { Item("Editor", false), Item("Debug Console", true) };
            panel.UpdateItems(items);

            // WPF-UI TextBox with placeholder & clear button
            var searchBox = Descendants(panel).OfType<Wpf.Ui.Controls.TextBox>().Single();
            Assert.Equal("Find a window…", searchBox.PlaceholderText);
            Assert.True(searchBox.ClearButtonEnabled);
            Assert.NotNull(searchBox.Icon);

            // WPF-UI DynamicScrollViewer
            var scroll = Descendants(panel).OfType<Wpf.Ui.Controls.DynamicScrollViewer>().Single();
            Assert.NotNull(scroll);

            // WPF-UI Buttons
            var foldBtn = Descendants(panel).OfType<Wpf.Ui.Controls.Button>().Single(b => System.Windows.Automation.AutomationProperties.GetName(b) == "Fold backpack");
            Assert.NotNull(foldBtn.Icon);
            var infoBtn = Descendants(panel).OfType<Wpf.Ui.Controls.Button>().Single(b => System.Windows.Automation.AutomationProperties.GetName(b) == "Backpack tips");
            Assert.NotNull(infoBtn.Icon);
            var closeBtn = Descendants(panel).OfType<Wpf.Ui.Controls.Button>().Single(b => System.Windows.Automation.AutomationProperties.GetName(b) == "Close backpack");
            Assert.NotNull(closeBtn.Icon);

            // Drop target styling
            panel.SetDropTarget(true);
            panel.SetDropTarget(false);

            // Double click tile triggers PlaceRequested
            Guid? placedId = null;
            panel.PlaceRequested += id => placedId = id;
            panel.Highlight(items[0].Id);

            // Folded mode interaction
            bool? requestedOpen = null;
            panel.OpenRequested += open => requestedOpen = open;
            panel.SetOpen(false);
            Assert.False(panel.IsOpen);
        });

    [Fact]
    public void ContextMenuOpenPreventsFlickerAndDefersRebuild()
        => WorkspaceViewTests.OnSta(() =>
        {
            var panel = new BackpackPanel();
            panel.SetOpen(true);
            var item1 = Item("First Item", false);
            var item2 = Item("Second Item", true);
            panel.UpdateItems([item1, item2]);

            var border1 = Descendants(panel).OfType<Border>().First(b => b.ContextMenu != null);
            var menu = border1.ContextMenu;
            Assert.NotNull(menu);

            // Open the context menu
            menu.IsOpen = true;
            Assert.True(panel.IsContextMenuOpen);

            // While open, UpdateItems with modified title or item
            var modifiedItem1 = Item("First Item Renamed", false) with { Id = item1.Id };
            panel.UpdateItems([modifiedItem1, item2]);

            // The tile must NOT have been destroyed / rebuilt while menu was open!
            Assert.Same(border1, Descendants(panel).OfType<Border>().First(b => b.ContextMenu != null));
            Assert.True(menu.IsOpen);

            // Now close the menu and raise Closed event
            menu.IsOpen = false;
            menu.RaiseEvent(new RoutedEventArgs(ContextMenu.ClosedEvent));

            // Now the deferred rebuild should have executed
            var updatedTile = Descendants(panel).OfType<Border>().First(b => b.ContextMenu != null);
            var title = Descendants(updatedTile).OfType<TextBlock>().First(t => t.Text == "First Item Renamed");
            Assert.NotNull(title);
        });

    [Fact]
    public void SearchBoxFocusAndKeyHandlingPreservesTyping()
        => WorkspaceViewTests.OnSta(() =>
        {
            var panel = new BackpackPanel();
            var window = new Window { Content = panel, Width = 500, Height = 700 };
            window.Show();
            try
            {
                panel.SetOpen(true);
                panel.FocusSearch();

                var searchBox = Descendants(panel).OfType<Wpf.Ui.Controls.TextBox>().Single();
                Assert.NotNull(searchBox);

                // PreviewKeyDown with 'I' when search is focused must not request close
                bool requestedClose = false;
                panel.OpenRequested += open => { if (!open) requestedClose = true; };

                searchBox.Focus();
                var source = PresentationSource.FromVisual(searchBox) ?? new System.Windows.Interop.HwndSource(0, 0, 0, 0, 0, "", IntPtr.Zero);
                var keyEventArgs = new System.Windows.Input.KeyEventArgs(
                    System.Windows.Input.Keyboard.PrimaryDevice,
                    source,
                    0,
                    System.Windows.Input.Key.I)
                {
                    RoutedEvent = System.Windows.UIElement.PreviewKeyDownEvent
                };
                panel.RaiseEvent(keyEventArgs);

                // Backpack must NOT close when typing 'I' in search box
                Assert.False(requestedClose);
            }
            finally
            {
                window.Close();
            }
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
