using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using CanvasDesktop;

namespace CanvasDesktop.Tests;

public class ZoneViewTests
{
    [Fact]
    public void ManagerHistoryButtonsFollowWorkspaceAndStopUpdatingAfterClose()
    {
        OnSta(() =>
        {
            using var rig = new WorkspaceHarness(); rig.Workspace.Start();
            rig.Workspace.Zones.Add(new WorldRect { W = 1000, H = 800 }, out var id);
            var window = new ZoneWindow(rig.Workspace.Zones, () => { }, _ => { }, () => null, rig.Workspace,
                redo => { if (redo) rig.Workspace.RedoZoneLayout(); else rig.Workspace.UndoZoneLayout(); });
            var undo = Descendants(window).OfType<Button>().Single(b => Equals(b.Content, "Undo"));
            var redo = Descendants(window).OfType<Button>().Single(b => Equals(b.Content, "Redo"));
            try
            {
                Assert.False(undo.IsEnabled); Assert.False(redo.IsEnabled);
                rig.Workspace.MoveZone(id, 100, 0);
                Assert.True(undo.IsEnabled); Assert.False(redo.IsEnabled);
                undo.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.False(undo.IsEnabled); Assert.True(redo.IsEnabled);
                redo.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(undo.IsEnabled); Assert.False(redo.IsEnabled);
            }
            finally { window.Close(); }
            rig.Workspace.UndoZoneLayout();
            Assert.True(undo.IsEnabled); Assert.False(redo.IsEnabled); // Closed view is unsubscribed.
        });
    }

    [Fact]
    public void MembershipEditorPreservesFilteredSelectionsAndCancelLeavesMembershipAlone()
    {
        OnSta(() =>
        {
            var first = new WorkspaceItem { Title = "Editor", Bounds = new WorldRect { W = 800, H = 600 } };
            var second = new WorkspaceItem { Title = "Reference", Bounds = first.Bounds };
            var document = new WorkspaceDocument { Items = new() { first, second } };
            var zones = new ZoneService(document, () => Guid.Empty, () => true);
            zones.Add(new WorldRect { W = 2000, H = 1500 }, out var id);
            zones.SetMembership(id, new[] { first.Id });
            var window = new ZoneMembersWindow(zones, zones.Items[0], () => null);
            var filter = Descendants(window).OfType<TextBox>().Single();
            filter.Text = "Reference";
            var reference = Assert.Single(Descendants(window).OfType<CheckBox>());
            reference.IsChecked = true;
            filter.Text = "Editor";
            Assert.True(Assert.Single(Descendants(window).OfType<CheckBox>()).IsChecked);
            Descendants(window).OfType<Button>().Single(b => Equals(b.Content, "Save membership"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(2, zones.Items[0].MemberCount);
            var cancelled = new ZoneMembersWindow(zones, zones.Items[0], () => null);
            foreach (var box in Descendants(cancelled).OfType<CheckBox>()) box.IsChecked = false;
            cancelled.Close();
            Assert.Equal(2, zones.Items[0].MemberCount);
        });
    }

    [Fact]
    public void MembershipEditorDisablesSaveAfterDesktopSwitch()
    {
        OnSta(() =>
        {
            Guid desktop = Guid.Empty;
            var zones = new ZoneService(new WorkspaceDocument(), () => desktop, () => true);
            zones.Add(new WorldRect { W = 800, H = 600 }, out _);
            var window = new ZoneMembersWindow(zones, zones.Items[0], () => null);
            try
            {
                desktop = Guid.NewGuid(); zones.NotifyDesktopChanged();
                Assert.False(Descendants(window).OfType<Button>().Single(b => Equals(b.Content, "Save membership")).IsEnabled);
            }
            finally { window.Close(); }
        });
    }

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Zone UI verification did not finish.");
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Fact]
    public void EditorSavesSelectedZoneRejectsInvalidColorAndNavigatesById()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var document = new WorkspaceDocument();
                var zones = new ZoneService(document, () => Guid.Empty, () => true);
                zones.Add(new WorldRect { W = 800, H = 600 }, out var id);
                Guid? jumped = null;
                bool drawing = false;
                var window = new ZoneWindow(zones, () => drawing = true, z => jumped = z.Id, () => null);
                try
                {
                    var elements = Descendants(window).ToArray();
                    var name = elements.OfType<TextBox>().Single(t => t.Name == "AreaName");
                    var color = elements.OfType<TextBox>().Single(t => t.Name == "AreaColor");
                    void Click(string label) => elements.OfType<Button>().Single(b => Equals(b.Content, label))
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    name.Text = "Research"; color.Text = "#34D399";
                    Click("Save changes");
                    Assert.Equal("Research", Assert.Single(zones.Items).Name);
                    Assert.Equal("#34D399", Assert.Single(zones.Items).Color);
                    color.Text = "invalid";
                    Click("Save changes");
                    Assert.Equal("#34D399", Assert.Single(zones.Items).Color);
                    Click("Jump to area"); Assert.Equal(id, jumped);
                    Click("+  Draw an area"); Assert.True(drawing);
                    zones.Remove(id);
                    Assert.False(name.IsEnabled);
                }
                finally { window.Close(); }
                // No callback may update the closed window.
                zones.Add(new WorldRect { W = 200, H = 200 }, out _);
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Zone UI verification did not finish.");
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
