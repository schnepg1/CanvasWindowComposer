using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using CanvasDesktop;

namespace CanvasDesktop.Tests;

public class ZoneNavigatorViewTests
{
    [Fact]
    public void NestedProjectsAppearUnderParentAndSearchRetainsAncestors()
    {
        OnSta(() =>
        {
            var document = Document(); var parent = document.Zones[0]; var child = document.Zones[1];
            child.Bounds = new WorldRect { X = 100, Y = 100, W = 1200, H = 900 };
            using var rig = new WorkspaceHarness(document); rig.Workspace.Start();
            using var navigation = new ZoneNavigation(rig.Workspace.Zones, rig.Workspace, _ => { }, () => { });
            var window = new ZoneNavigatorWindow(navigation, _ => { });
            try
            {
                var tree = Descendants(window).OfType<TreeView>().Single();
                var root = Assert.Single(tree.Items.Cast<TreeViewItem>());
                Assert.Equal(2, root.Items.Count); // One saved member and one sub-project.
                var filter = Descendants(window).OfType<TextBox>().Single();
                filter.Text = child.Name;
                root = Assert.Single(tree.Items.Cast<TreeViewItem>());
                Assert.True(root.IsExpanded);
                var nested = Assert.Single(root.Items.Cast<TreeViewItem>());
                Assert.Contains(Descendants(nested).OfType<TextBlock>(), t => t.Text == child.Name);
                var result = navigation.Read(child.Name);
                Assert.Equal(parent.Id, result.Single(e => e.Area.Id == child.Id).Area.ParentId);
                Assert.Equal(2, result.Count);
            }
            finally { window.Close(); }
        });
    }

    private static WorkspaceDocument Document()
    {
        var item = new WorkspaceItem { Title = "Reference", ExecutablePath = @"C:\Apps\Editor.exe",
            Bounds = new WorldRect { X = 100, Y = 200, W = 800, H = 600 } };
        return new WorkspaceDocument { Items = new() { item }, Zones = new()
        {
            new WorkspaceZone { Name = "Website", Bounds = new WorldRect { W = 1800, H = 1200 }, ItemIds = new() { item.Id } },
            new WorkspaceZone { Name = "Empty research", Bounds = new WorldRect { X = 3000, W = 1200, H = 900 } }
        } };
    }

    [Fact]
    public void SearchAutoExpandsMemberMatchesAndEmptySearchRestoresExpansion()
    {
        OnSta(() =>
        {
            using var rig = new WorkspaceHarness(Document()); rig.Workspace.Start();
            using var navigation = new ZoneNavigation(rig.Workspace.Zones, rig.Workspace, _ => { }, () => { });
            var window = new ZoneNavigatorWindow(navigation, _ => { });
            try
            {
                var tree = Descendants(window).OfType<TreeView>().Single();
                var filter = Descendants(window).OfType<TextBox>().Single();
                Assert.Equal(2, tree.Items.Count);
                var website = (TreeViewItem)tree.Items[1]; website.IsExpanded = true;
                filter.Text = "reference";
                var match = Assert.Single(tree.Items.Cast<TreeViewItem>());
                Assert.True(match.IsExpanded); Assert.Single(match.Items.Cast<TreeViewItem>());
                filter.Text = "nothing here";
                Assert.Empty(tree.Items);
                Assert.Contains(Descendants(window).OfType<TextBlock>(), t => t.Text.StartsWith("No matching"));
                filter.Clear();
                Assert.Equal(2, tree.Items.Count); Assert.True(((TreeViewItem)tree.Items[1]).IsExpanded);
                Assert.False(((TreeViewItem)tree.Items[0]).IsExpanded);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void RefreshRetainsMemberSelectionAndUnchangedTicksReuseTreeRows()
    {
        OnSta(() =>
        {
            var document = Document(); var item = document.Items[0];
            using var rig = new WorkspaceHarness(document); rig.Workspace.Start();
            using var navigation = new ZoneNavigation(rig.Workspace.Zones, rig.Workspace, _ => { }, () => { });
            Guid? managed = null;
            var window = new ZoneNavigatorWindow(navigation, id => managed = id);
            try
            {
                var tree = Descendants(window).OfType<TreeView>().Single();
                var area = (TreeViewItem)tree.Items[1]; area.IsExpanded = true;
                var member = (TreeViewItem)area.Items[0]; member.IsSelected = true;
                rig.Clock.Advance(2000); rig.Workspace.Tick();
                Assert.Same(area, tree.Items[1]); Assert.Same(member, tree.SelectedItem);
                rig.Workspace.SetName(item.Id, "Renamed reference");
                var rebuilt = (TreeViewItem)tree.Items[1]; Assert.NotSame(area, rebuilt);
                Assert.True(rebuilt.IsExpanded); Assert.Same(rebuilt.Items[0], tree.SelectedItem);
                var buttons = Descendants(window).OfType<Button>().ToArray();
                var reopen = buttons.Single(b => Equals(b.Content, "Reopen member"));
                Assert.True(reopen.IsEnabled);
                buttons.Single(b => Equals(b.Content, "Manage areas…")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(document.Zones[0].Id, managed);
                reopen.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(item.Id, Assert.Single(rig.Launcher.Opened));
                Assert.False(Descendants(window).OfType<Button>().Single(b => Equals(b.Content, "Opening…")).IsEnabled);
            }
            finally { window.Close(); }
            int oldCount = Descendants(window).OfType<TreeView>().Single().Items.Count;
            rig.Workspace.Zones.Remove(document.Zones[0].Id);
            Assert.Equal(oldCount, Descendants(window).OfType<TreeView>().Single().Items.Count);
        });
    }

    [Fact]
    public void DesktopChangeClearsRowsAndDisablesActionsWithoutOpeningAnything()
    {
        OnSta(() =>
        {
            using var rig = new WorkspaceHarness(Document()); rig.Workspace.Start();
            using var navigation = new ZoneNavigation(rig.Workspace.Zones, rig.Workspace, _ => { }, () => { });
            var window = new ZoneNavigatorWindow(navigation, _ => { });
            try
            {
                rig.Desktops.SwitchTo(Guid.NewGuid()); rig.Desktops.SwitchTo(Guid.NewGuid());
                Assert.Empty(Descendants(window).OfType<TreeView>().Single().Items);
                var buttons = Descendants(window).OfType<Button>().ToArray();
                Assert.False(buttons.Single(b => Equals(b.Content, "Jump to area")).IsEnabled);
                Assert.False(buttons.Single(b => Equals(b.Content, "Open member")).IsEnabled);
                Assert.Empty(rig.Launcher.Opened);
            }
            finally { window.Close(); }
        });
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        { yield return child; foreach (var descendant in Descendants(child)) yield return descendant; }
    }
    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Navigator UI verification did not finish.");
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
