using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using static CanvasDesktop.ZoneTheme;

namespace CanvasDesktop;

/// <summary>Explicit membership editing by stable IDs; filtering never changes hidden selections.</summary>
internal sealed class ZoneMembersWindow : Window
{
    private readonly IZoneCommands _zones;
    private readonly Guid _zoneId;
    private readonly Func<string?> _status;
    private readonly Dictionary<Guid, bool> _selected = new();
    private readonly TextBox _filter = new() { Name = "MemberFilter" };
    private readonly StackPanel _members = new();
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly Button _apply;

    public ZoneMembersWindow(IZoneCommands zones, ZoneView zone, Func<string?> status)
    {
        _zones = zones; _zoneId = zone.Id; _status = status;
        Title = "Members · " + zone.Name;
        Width = 480; Height = 580; MinWidth = 360; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ZoneTheme.Apply(this);
        var root = new DockPanel { Margin = new Thickness(24) };
        Content = root;
        var header = new StackPanel(); DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        header.Children.Add(new TextBlock { Text = "What belongs here?", FontSize = 24, FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock { Text = "Choose saved places to move with this area. A place can belong to several areas. Pinned places stay fixed.",
            TextWrapping = TextWrapping.Wrap, Foreground = Brush("#A7B3C9"), Margin = new Thickness(0, 8, 0, 16) });
        header.Children.Add(new TextBlock { Text = "Filter places", Margin = new Thickness(0, 0, 0, 6) });
        header.Children.Add(_filter);
        System.Windows.Automation.AutomationProperties.SetName(_filter, "Filter saved places");
        _filter.TextChanged += (_, _) => Refresh();
        var footer = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        _message.Foreground = Brush("#A7B3C9"); footer.Children.Add(_message);
        _apply = Button("Save membership", Save); footer.Children.Add(_apply);
        footer.Children.Add(Button("Cancel", Close));
        root.Children.Add(new ScrollViewer { Content = _members, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 12, 0, 0) });
        _zones.Changed += Refresh;
        Closed += (_, _) => _zones.Changed -= Refresh;
        Refresh();
    }

    private void Refresh()
    {
        bool exists = _zones.Items.Any(z => z.Id == _zoneId);
        _apply.IsEnabled = exists;
        var members = _zones.Members(_zoneId);
        var available = members.Select(m => m.Id).ToHashSet();
        foreach (var id in _selected.Keys.Where(id => !available.Contains(id)).ToArray()) _selected.Remove(id);
        _members.Children.Clear();
        foreach (var member in members.OrderBy(m => m.Title, StringComparer.CurrentCultureIgnoreCase))
        {
            if (!_selected.ContainsKey(member.Id)) _selected[member.Id] = member.Assigned;
            if (!member.Title.Contains(_filter.Text, StringComparison.CurrentCultureIgnoreCase)
                && !member.Details.Contains(_filter.Text, StringComparison.CurrentCultureIgnoreCase)) continue;
            var checkbox = new CheckBox { Content = new TextBlock { Text = member.Title + (member.Bounds.PinnedToScreen ? " · Pinned" : ""),
                    TextWrapping = TextWrapping.Wrap }, ToolTip = member.Details,
                Tag = member.Id, IsChecked = _selected[member.Id], Margin = new Thickness(4, 9, 4, 9) };
            System.Windows.Automation.AutomationProperties.SetName(checkbox, member.Title);
            checkbox.Checked += (_, _) => _selected[member.Id] = true;
            checkbox.Unchecked += (_, _) => _selected[member.Id] = false;
            _members.Children.Add(checkbox);
        }
        _message.Text = !exists ? "This area is no longer available on the active desktop. Close this window to continue."
            : members.Count == 0 ? "No saved places on this desktop yet."
            : "Drag the area's name in overview to move its members together.";
    }

    private void Save()
    {
        if (_zones.SetMembership(_zoneId, _selected.Where(p => p.Value).Select(p => p.Key).ToArray())) Close();
        else _message.Text = _status() ?? "Membership could not be saved. Check that the area and places still exist on this desktop.";
    }
}
