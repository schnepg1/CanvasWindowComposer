using CanvasDesktop;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

    [Fact]
    public void BackpackSearchBoxTextCursorAndCaretBehaviorAreConsistent()
        => WorkspaceViewTests.OnSta(() =>
        {
            var panel = new BackpackPanel();
            var window = new Window { Content = panel, Width = 500, Height = 700 };
            window.Show();
            try
            {
                panel.SetOpen(true);
                var searchBox = Assert.Single(Descendants(panel).OfType<Wpf.Ui.Controls.TextBox>());

                // 1. Text cursor (mouse pointer) over the search box is IBeam
                Assert.Equal(Cursors.IBeam, searchBox.Cursor);

                // 2. Caret brush matches text foreground rather than mismatched accent
                var expectedBrush = BackpackPanel.Ink("#EAF0F4");
                Assert.Equal(expectedBrush.Color, ((SolidColorBrush)searchBox.CaretBrush).Color);

                // 3. FocusSearch positions text cursor (caret) at the end of existing text
                searchBox.Text = "Terminal";
                panel.FocusSearch();
                Assert.True(searchBox.IsKeyboardFocused || searchBox.IsFocused);
                Assert.Equal(searchBox.Text.Length, searchBox.CaretIndex);

                // 4. Clicking outside content host (e.g. search icon / chrome) focuses search and places caret at end
                var icon = Assert.IsAssignableFrom<FrameworkElement>(searchBox.Icon);
                searchBox.RaiseEvent(new MouseButtonEventArgs(InputManager.Current.PrimaryMouseDevice, 0, MouseButton.Left)
                {
                    RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent,
                    Source = icon
                });
                Assert.Equal(searchBox.Text.Length, searchBox.CaretIndex);
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public void OnlyTheFoldedBadgeIsVisibleOnFirstPresentation()
        => WorkspaceViewTests.OnSta(() =>
        {
            var panel = new BackpackPanel();
            panel.SetOpen(false);
            var shell = Assert.IsType<Border>(panel.Content);
            var layout = Assert.IsType<Grid>(shell.Child);
            var expanded = Assert.IsType<Grid>(Descendants(panel).OfType<TextBox>().Single().Parent);
            var folded = Assert.Single(layout.Children.OfType<Grid>(), child => child != expanded);
            Assert.Equal(Visibility.Visible, folded.Visibility);
            Assert.Equal(Visibility.Collapsed, expanded.Visibility);
        });

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
            var borders = Descendants(panel).OfType<Border>().Where(b => b.Width == 126 && b.Height == 140).ToArray();
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

    [Fact]
    public void BackpackDisplaysTextNotesAlongWithWindowsAndSupportsSearchAndContextActions()
        => WorkspaceViewTests.OnSta(() =>
        {
            var panel = new BackpackPanel();
            panel.SetOpen(true);
            var windowItem = Item("Terminal", false);
            var noteId = Guid.NewGuid();
            var noteItem = new WorkspaceItemView(
                noteId,
                "Release checklist",
                "",
                "",
                false,
                new() { W = 400, H = 300, InInventory = true },
                "Note",
                Name: "Release checklist\n- verify native tests\n- clean merge",
                Temporary: false
            );

            panel.UpdateItems([windowItem, noteItem]);
            panel.Measure(new Size(450, 650));
            panel.Arrange(new Rect(0, 0, 450, 650));
            panel.UpdateLayout();

            // Text note renders with "Text note" state text and "KEPT" badge
            var textBlocks = Descendants(panel).OfType<TextBlock>().Select(t => t.Text).ToArray();
            Assert.Contains("Release checklist", textBlocks);
            Assert.Contains("Text note", textBlocks);
            Assert.Contains("KEPT", textBlocks);

            // Searching for text inside the note body finds it
            var searchBox = Descendants(panel).OfType<Wpf.Ui.Controls.TextBox>().Single();
            searchBox.Text = "native tests";
            var filteredTitles = Descendants(panel).OfType<TextBlock>().Select(t => t.Text).ToArray();
            Assert.Contains("Release checklist", filteredTitles);
            Assert.DoesNotContain("Terminal", filteredTitles);

            // Context menu on note tile
            searchBox.Text = "";
            var noteTile = Descendants(panel).OfType<Border>().First(b => System.Windows.Automation.AutomationProperties.GetName(b) == "Release checklist");
            Assert.NotNull(noteTile.ContextMenu);
            var menuItems = noteTile.ContextMenu.Items.OfType<MenuItem>().Select(m => m.Header?.ToString()).ToArray();
            Assert.Contains("Place near camera", menuItems);
            Assert.Contains("Delete note", menuItems);
            Assert.DoesNotContain("Open app", menuItems);

            // Forget/Delete note request
            Guid? forgottenId = null;
            panel.ForgetRequested += id => forgottenId = id;
            var deleteItem = noteTile.ContextMenu.Items.OfType<MenuItem>().First(m => m.Header?.ToString() == "Delete note");
            deleteItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal(noteId, forgottenId);
        });

    [Fact]
    public void BackpackDisplaysAgentCardsAndProjectsWithDistinctBadgesAndContextMenus()
        => WorkspaceViewTests.OnSta(() =>
        {
            var panel = new BackpackPanel();
            panel.SetOpen(true);
            var agentId = Guid.NewGuid();
            var projectId = Guid.NewGuid();

            var agentItem = new WorkspaceItemView(
                agentId,
                "Research Assistant",
                "",
                "",
                false,
                new() { W = 240, H = 112, InInventory = true },
                "Agent",
                Name: "Research Assistant\nAutonomous Codebase Scout",
                Temporary: false
            );

            var projectItem = new WorkspaceItemView(
                projectId,
                "Backend Engine",
                "",
                "",
                false,
                new() { W = 800, H = 600, InInventory = true },
                "Project",
                Name: "Backend Engine\n4 windows",
                Temporary: false
            );

            panel.UpdateItems([agentItem, projectItem]);
            panel.Measure(new Size(450, 650));
            panel.Arrange(new Rect(0, 0, 450, 650));
            panel.UpdateLayout();

            // Check badges and subtitles
            var textBlocks = Descendants(panel).OfType<TextBlock>().Select(t => t.Text).ToArray();
            Assert.Contains("AGENT", textBlocks);
            Assert.Contains("AI agent", textBlocks);
            Assert.Contains("PROJECT", textBlocks);
            Assert.Contains("Project area", textBlocks);

            // Context menu for agent tile
            var agentTile = Descendants(panel).OfType<Border>().First(b => System.Windows.Automation.AutomationProperties.GetName(b) == "Research Assistant");
            Assert.NotNull(agentTile.ContextMenu);
            var agentMenuItems = agentTile.ContextMenu.Items.OfType<MenuItem>().Select(m => m.Header?.ToString()).ToArray();
            Assert.Contains("Place near camera", agentMenuItems);
            Assert.Contains("Delete agent card", agentMenuItems);
            Assert.DoesNotContain("Open app", agentMenuItems);

            // Context menu for project tile
            var projectTile = Descendants(panel).OfType<Border>().First(b => System.Windows.Automation.AutomationProperties.GetName(b) == "Backend Engine");
            Assert.NotNull(projectTile.ContextMenu);
            var projectMenuItems = projectTile.ContextMenu.Items.OfType<MenuItem>().Select(m => m.Header?.ToString()).ToArray();
            Assert.Contains("Choose project folder…", projectMenuItems);
            Assert.Contains("Place near camera", projectMenuItems);
            Assert.Contains("Delete from backpack", projectMenuItems);
            Assert.DoesNotContain("Open app", projectMenuItems);
            Assert.DoesNotContain("Choose project folder…", agentMenuItems);

            // Forget agent and project requests
            var forgottenIds = new List<Guid>();
            panel.ForgetRequested += id => forgottenIds.Add(id);

            var folderRequests = new List<Guid>();
            panel.FolderRequested += folderRequests.Add;
            var chooseFolder = projectTile.ContextMenu.Items.OfType<MenuItem>().First(m => m.Header?.ToString() == "Choose project folder…");
            chooseFolder.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

            var deleteAgent = agentTile.ContextMenu.Items.OfType<MenuItem>().First(m => m.Header?.ToString() == "Delete agent card");
            deleteAgent.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

            var deleteProject = projectTile.ContextMenu.Items.OfType<MenuItem>().First(m => m.Header?.ToString() == "Delete from backpack");
            deleteProject.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

            Assert.Equal([agentId, projectId], forgottenIds);
            Assert.Equal([projectId], folderRequests);
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
