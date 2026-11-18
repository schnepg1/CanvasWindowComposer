using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using Windows.Win32;
using Windows.Win32.Foundation;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Wpf.Ui.Markup;
using Button = Wpf.Ui.Controls.Button;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxResult = System.Windows.MessageBoxResult;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;
using TreeView = System.Windows.Controls.TreeView;
using TreeViewItem = System.Windows.Controls.TreeViewItem;

namespace CanvasDesktop;

/// <summary>
/// One Fluent surface for project areas: browse and search them like the old navigator,
/// and rename, recolor, choose members or remove the selected area in place.
/// </summary>
internal sealed class ZoneWorkspaceWindow : FluentWindow
{
    private sealed record Selection(Guid AreaId, Guid? MemberId = null);

    private static readonly string[] Palette = ["#60A5FA", "#A78BFA", "#34D399", "#FBBF24", "#FB7185", "#22D3EE"];

    private readonly IZoneCommands _zones;
    private readonly ZoneNavigation _navigation;
    private readonly Action _draw;
    private readonly Func<string?> _statusOf;
    private readonly IZoneLayoutCommands? _history;
    private readonly Action<bool>? _replay;
    private readonly Action<Guid>? _chooseProjectFolder;

    private readonly TextBox _search = new()
    {
        Name = "ProjectSearch",
        MaxLength = 240,
        PlaceholderText = "Search projects, members or paths"
    };
    private readonly TreeView _tree = new();
    private readonly TextBlock _subtitle = new() { FontSize = 11.5, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _hint = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _empty = new() { FontSize = 12, Margin = new Thickness(12, 18, 12, 0), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly TextBlock _detailTitle = new() { FontSize = 17, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock _detailMeta = new() { FontSize = 11.5, Margin = new Thickness(0, 3, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _name = new() { Name = "AreaName", MaxLength = 120 };
    private readonly TextBox _color = new() { Name = "AreaColor", MaxLength = 7, PlaceholderText = "#60A5FA" };
    private readonly StackPanel _editor = new();
    private readonly TextBlock _emptyDetails = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly Border _memberCard = new()
    {
        Visibility = Visibility.Collapsed, Padding = new Thickness(12, 10, 12, 12), Margin = new Thickness(0, 12, 0, 0),
        CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1)
    };
    private readonly TextBlock _memberTitle = new() { FontSize = 13, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _memberDetails = new() { FontSize = 11.5, Margin = new Thickness(0, 3, 0, 0), TextWrapping = TextWrapping.Wrap };
    private Button _memberAction = null!;
    private Button _jumpButton = null!;
    private Button _removeButton = null!;
    private Button _membersButton = null!;
    private Button _folderButton = null!;
    private Button _undo = null!;
    private Button _redo = null!;
    private readonly TextBlock _folderPath = new()
    {
        FontSize = 11, MaxHeight = 34, Margin = new Thickness(0, 4, 0, 0),
        TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis,
        Visibility = Visibility.Collapsed
    };
    private readonly TextBlock _message = new() { FontSize = 11.5, Margin = new Thickness(0, 12, 0, 0), TextWrapping = TextWrapping.Wrap };

    private readonly HashSet<Guid> _expanded = new();
    private IReadOnlyList<ZoneNavigationEntry>? _snapshot;
    private string _lastQuery = "";
    private Guid? _loadedZone;
    private string _loadedName = "", _loadedColor = "";
    private bool _refreshing;
    private bool _closed;

    public ZoneWorkspaceWindow(IZoneCommands zones, ZoneNavigation navigation, Action draw,
        Func<string?> status, IZoneLayoutCommands? history = null, Action<bool>? replay = null,
        Action<Guid>? exportWorkflow = null, Action<Guid>? chooseProjectFolder = null)
    {
        _zones = zones; _navigation = navigation; _draw = draw; _statusOf = status;
        _history = history; _replay = replay; _chooseProjectFolder = chooseProjectFolder;
        CanvasZOrder.AttachWpfClampHook(this, () => Topmost && (ClampBelowTaskbar?.Invoke() ?? true));

        Title = "Projects · Canvas";
        Width = 940; Height = 660; MinWidth = 700; MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ExtendsContentIntoTitleBar = true;
        Resources.MergedDictionaries.Add(new ThemesDictionary { Theme = ApplicationTheme.Dark });
        Resources.MergedDictionaries.Add(new ControlsDictionary());
        Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse(SurfaceStyles));
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        Background = Brush("#0F1622");
        Foreground = Brush("#E8EEF8");
        System.Windows.Automation.AutomationProperties.SetName(_search, "Search project areas and members");
        System.Windows.Automation.AutomationProperties.SetName(_tree, "Project areas and saved members");

        var root = new DockPanel();
        var title = new TitleBar { Title = Title, ShowMaximize = false, ShowMinimize = false };
        DockPanel.SetDock(title, Dock.Top);
        root.Children.Add(title);
        root.Children.Add(BuildSurface(exportWorkflow));
        Content = root;

        _tree.SelectedItemChanged += (_, _) => UpdateSelection();
        _tree.MouseDoubleClick += (_, e) =>
        {
            // Ignore empty space and expander toggles; activate only a row's text.
            if (e.OriginalSource is TextBlock) { ActivateSelection(); e.Handled = true; }
        };
        _tree.KeyDown += (_, e) => { if (e.Key == Key.Enter) { ActivateSelection(); e.Handled = true; } };
        _search.TextChanged += (_, _) => Refresh();
        _search.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Jump(); e.Handled = true; } };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                if (_search.Text.Length > 0) _search.Clear(); else Hide();
                e.Handled = true;
            }
            else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
            { FocusSearch(); e.Handled = true; }
            else if (e.Key == Key.Down && _search.IsKeyboardFocusWithin && _tree.Items.Count > 0)
            { ((TreeViewItem)_tree.Items[0]).Focus(); e.Handled = true; }
        };
        navigation.Changed += Refresh;
        IsVisibleChanged += (_, _) => { if (IsVisible) Refresh(); };
        Closed += (_, _) => { _closed = true; navigation.Changed -= Refresh; };
        Refresh();
    }

    private FrameworkElement BuildSurface(Action<Guid>? exportWorkflow)
    {
        var body = new Grid { Margin = new Thickness(16, 0, 16, 16) };
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new Grid { Margin = new Thickness(2, 0, 2, 10) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(_subtitle);
        var headerActions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _undo = TextButton("Undo", () => _replay?.Invoke(false), "Undo the last area move or resize on this desktop");
        _redo = TextButton("Redo", () => _replay?.Invoke(true), "Redo an undone area move or resize");
        _undo.Margin = new Thickness(0, 0, 6, 0);
        _redo.Margin = new Thickness(0, 0, 10, 0);
        headerActions.Children.Add(_undo);
        headerActions.Children.Add(_redo);
        var draw = new Button
        {
            Content = "Draw an area", Appearance = ControlAppearance.Primary,
            Padding = new Thickness(16, 6, 16, 6), FontSize = 12.5
        };
        draw.Style = (Style)FindResource("ProjectPrimaryButton");
        draw.Click += (_, _) => BeginDraw();
        headerActions.Children.Add(draw);
        Grid.SetColumn(headerActions, 1);
        header.Children.Add(headerActions);
        Grid.SetRow(header, 0);
        body.Children.Add(header);

        var main = new Grid();
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(340), MinWidth = 300 });
        Grid.SetRow(main, 1);
        body.Children.Add(main);

        main.Children.Add(BuildList());
        var details = BuildDetails(exportWorkflow);
        details.Margin = new Thickness(12, 0, 0, 0);
        Grid.SetColumn(details, 1);
        main.Children.Add(details);
        return body;
    }

    private FrameworkElement BuildList()
    {
        var card = new Border
        {
            Background = Brush("#151E2C"), BorderBrush = Brush("#243244"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 10, 10, 0)
        };
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        card.Child = grid;

        _search.Margin = new Thickness(0, 0, 0, 8);
        grid.Children.Add(_search);

        var host = new Grid();
        host.Children.Add(_tree);
        _empty.Foreground = Brush("#93A2B8");
        _empty.VerticalAlignment = VerticalAlignment.Top;
        host.Children.Add(_empty);
        Grid.SetRow(host, 1);
        grid.Children.Add(host);

        _hint.Foreground = Brush("#7D8CA3");
        var hint = new Border { Padding = new Thickness(2, 8, 2, 10), Child = _hint };
        Grid.SetRow(hint, 2);
        grid.Children.Add(hint);
        return card;
    }

    private FrameworkElement BuildDetails(Action<Guid>? exportWorkflow)
    {
        var card = new Border
        {
            Background = Brush("#151E2C"), BorderBrush = Brush("#243244"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10)
        };
        var stack = new StackPanel();
        card.Child = new ScrollViewer
        {
            Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(16, 14, 14, 16)
        };
        stack.Children.Add(new TextBlock
        {
            Text = "SELECTED PROJECT", FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = Brush("#7D8CA3")
        });
        stack.Children.Add(_detailTitle);
        _detailMeta.Foreground = Brush("#93A2B8");
        stack.Children.Add(_detailMeta);

        _memberCard.Background = Brush("#1B2637");
        _memberCard.BorderBrush = Brush("#2C3B51");
        _memberTitle.Foreground = Brush("#E8EEF8");
        _memberDetails.Foreground = Brush("#A7B3C9");
        _memberAction = new Button
        {
            Content = "Open member", Appearance = ControlAppearance.Secondary,
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0),
            Padding = new Thickness(12, 4, 12, 4), FontSize = 12
        };
        _memberAction.Style = (Style)FindResource("ProjectButton");
        _memberAction.Click += (_, _) => ActivateMember();
        var memberStack = new StackPanel();
        memberStack.Children.Add(_memberTitle);
        memberStack.Children.Add(_memberDetails);
        memberStack.Children.Add(_memberAction);
        _memberCard.Child = memberStack;
        stack.Children.Add(_memberCard);

        _emptyDetails.Text = "Select a project on the left to rename it, change its color, choose its members, or remove it.";
        _emptyDetails.Foreground = Brush("#93A2B8");
        _emptyDetails.Margin = new Thickness(0, 16, 0, 0);
        stack.Children.Add(_emptyDetails);

        _editor.Children.Add(Label("Name"));
        System.Windows.Automation.AutomationProperties.SetName(_name, "Area name");
        _editor.Children.Add(_name);

        _editor.Children.Add(Label("Color"));
        var palette = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        foreach (var hex in Palette)
        {
            var swatch = new System.Windows.Controls.Button
            {
                Style = (Style)FindResource("ProjectSwatch"), Background = Brush(hex), Tag = hex, ToolTip = hex
            };
            System.Windows.Automation.AutomationProperties.SetName(swatch, "Use color " + hex);
            swatch.Click += (_, _) => { _color.Text = hex; _color.Focus(); };
            void UpdateSwatch(object? sender, TextChangedEventArgs e) => swatch.Content =
                string.Equals(_color.Text, hex, StringComparison.OrdinalIgnoreCase) ? "\u2713" : null;
            _color.TextChanged += UpdateSwatch;
            palette.Children.Add(swatch);
        }
        _editor.Children.Add(palette);
        System.Windows.Automation.AutomationProperties.SetName(_color, "Area color in hexadecimal");
        _editor.Children.Add(_color);

        var pair = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        pair.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pair.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _jumpButton = DetailButton("Jump to area", () => Jump());
        _jumpButton.Margin = new Thickness(4, 0, 0, 0);
        var save = DetailButton("Save changes", () => Save(), ControlAppearance.Primary);
        save.Margin = new Thickness(0, 0, 4, 0);
        pair.Children.Add(save);
        Grid.SetColumn(_jumpButton, 1);
        pair.Children.Add(_jumpButton);
        _editor.Children.Add(pair);

        _membersButton = DetailButton("Choose members…", () => ChooseMembers());
        _membersButton.Margin = new Thickness(0, 8, 0, 0);
        _editor.Children.Add(_membersButton);

        _folderButton = DetailButton("Choose project folder…", () =>
        {
            if (SelectedArea is { } area) _chooseProjectFolder?.Invoke(area.Id);
        });
        _folderButton.Margin = new Thickness(0, 8, 0, 0);
        _folderButton.Visibility = _chooseProjectFolder == null ? Visibility.Collapsed : Visibility.Visible;
        _folderPath.Foreground = Brush("#7D8CA3");
        _editor.Children.Add(_folderButton);
        _editor.Children.Add(_folderPath);

        if (exportWorkflow != null)
        {
            var export = DetailButton("Export workflow (JSON)", () =>
            {
                if (SelectedArea is not { } area) return;
                exportWorkflow(area.Id);
                Report($"Exported '{area.Name}' workflow to clipboard.");
            });
            export.Margin = new Thickness(0, 8, 0, 0);
            _editor.Children.Add(export);
        }

        _editor.Children.Add(new Border { Height = 1, Background = Brush("#243244"), Margin = new Thickness(0, 16, 0, 0) });
        _removeButton = DetailButton("Remove area", () => Remove(), ControlAppearance.Danger);
        _removeButton.Margin = new Thickness(0, 12, 0, 0);
        _editor.Children.Add(_removeButton);
        _editor.IsEnabled = false;
        stack.Children.Add(_editor);

        _message.Foreground = Brush("#93A2B8");
        stack.Children.Add(_message);
        return card;
    }

    public void FocusSearch() { _search.Focus(); _search.SelectAll(); }

    /// <summary>Selects an area by stable ID, expanding a nested path when needed.</summary>
    public void Select(Guid id)
    {
        var row = FindAreaRow(_tree.Items, id);
        if (row != null)
        {
            for (var parent = ItemsControl.ItemsControlFromItemContainer(row);
                 parent is TreeViewItem parentItem;
                 parent = ItemsControl.ItemsControlFromItemContainer(parentItem))
                parentItem.IsExpanded = true;
            row.IsSelected = true;
            row.BringIntoView();
        }
        UpdateSelection();
        _name.Focus();
        _name.SelectAll();
    }

    public void Report(string message) => _message.Text = message;

    private static TreeViewItem? FindAreaRow(ItemCollection items, Guid id)
    {
        foreach (TreeViewItem item in items)
        {
            if (item.Tag is Selection { MemberId: null } selection && selection.AreaId == id) return item;
            if (FindAreaRow(item.Items, id) is { } nested) return nested;
        }
        return null;
    }

    private Selection? Current => (_tree.SelectedItem as TreeViewItem)?.Tag as Selection;
    private ZoneView? SelectedArea => Current is { } selected
        ? _zones.Items.FirstOrDefault(z => z.Id == selected.AreaId) : null;

    private void Refresh()
    {
        if (_closed || IsLoaded && !IsVisible) return;
        var entries = _navigation.Read(_search.Text);
        RefreshHistory();
        _hint.Text = _navigation.Status ?? "Enter jumps to an area or opens a member · Esc clears the search, then hides this window";
        int areas = _zones.Items.Count;
        _subtitle.Text = areas switch
        {
            0 => "No project areas on this virtual desktop",
            1 => "1 project area on this virtual desktop",
            _ => areas + " project areas on this virtual desktop"
        };
        // Workspace ticks need not reconstruct an unchanged tree or disrupt keyboard focus.
        bool same = _snapshot != null && _lastQuery == _search.Text && _snapshot.Count == entries.Count
            && _snapshot.Zip(entries).All(pair => pair.First.Area == pair.Second.Area
                && pair.First.Members.SequenceEqual(pair.Second.Members));
        if (same) { UpdateSelection(); return; }
        var selected = Current;
        bool restoreFocus = _tree.IsKeyboardFocusWithin;
        _refreshing = true;
        _tree.Items.Clear();
        _snapshot = entries; _lastQuery = _search.Text;
        var rows = new Dictionary<Guid, TreeViewItem>();
        foreach (var entry in entries)
        {
            var area = new TreeViewItem
            {
                Tag = new Selection(entry.Area.Id),
                Header = AreaHeader(entry),
                IsExpanded = _expanded.Contains(entry.Area.Id) || _search.Text.Trim().Length > 0
            };
            System.Windows.Automation.AutomationProperties.SetName(area, $"{entry.Area.Name}, {entry.Area.MemberCount} members");
            area.Expanded += (_, e) => { if (!_refreshing && e.OriginalSource == area) _expanded.Add(entry.Area.Id); };
            area.Collapsed += (_, e) => { if (!_refreshing && e.OriginalSource == area) _expanded.Remove(entry.Area.Id); };
            rows.Add(entry.Area.Id, area);
            foreach (var member in entry.Members)
            {
                var row = new TreeViewItem
                {
                    Tag = new Selection(entry.Area.Id, member.Id),
                    Header = MemberHeader(member),
                    ToolTip = member.Details
                };
                System.Windows.Automation.AutomationProperties.SetName(row, member.Title + ", " + member.State);
                area.Items.Add(row);
            }
        }
        foreach (var entry in entries)
        {
            if (entry.Area.ParentId is { } parent && rows.TryGetValue(parent, out var parentRow))
                parentRow.Items.Add(rows[entry.Area.Id]);
            else _tree.Items.Add(rows[entry.Area.Id]);
        }
        if (selected is { } previous)
        {
            if (rows.TryGetValue(previous.AreaId, out var area))
            {
                var selectedRow = previous.MemberId == null ? area
                    : area.Items.Cast<TreeViewItem>().FirstOrDefault(row => Equals(row.Tag, previous));
                if (selectedRow != null)
                {
                    if (previous.MemberId != null) area.IsExpanded = true;
                    selectedRow.IsSelected = true;
                    if (restoreFocus) selectedRow.Focus();
                }
            }
            var entry = entries.FirstOrDefault(e => e.Area.Id == previous.AreaId);
            while (entry?.Area.ParentId is { } parent && rows.TryGetValue(parent, out var row))
            {
                row.IsExpanded = true;
                entry = entries.FirstOrDefault(e => e.Area.Id == parent);
            }
        }
        if (_tree.SelectedItem == null && _tree.Items.Count > 0) ((TreeViewItem)_tree.Items[0]).IsSelected = true;
        _refreshing = false;
        _empty.Text = _search.Text.Trim().Length > 0 ? "No matching projects or members. Try another search."
            : "No project areas on this desktop yet. Draw one on the canvas to get started.";
        _empty.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        var selected = Current;
        var area = selected == null ? null : _zones.Items.FirstOrDefault(z => z.Id == selected.AreaId);
        var entry = selected == null ? null : _snapshot?.FirstOrDefault(e => e.Area.Id == selected.AreaId);
        var member = selected?.MemberId is { } memberId ? entry?.Members.FirstOrDefault(m => m.Id == memberId) : null;

        _editor.IsEnabled = area != null;
        _editor.Visibility = area == null ? Visibility.Collapsed : Visibility.Visible;
        _emptyDetails.Visibility = area == null ? Visibility.Visible : Visibility.Collapsed;
        _detailTitle.Text = area?.Name ?? "No project selected";
        _detailMeta.Text = area == null ? ""
            : $"{MemberCount(area.MemberCount)} · {area.Bounds.W:N0} × {area.Bounds.H:N0} canvas units"
              + (area.ParentId != null ? " · Sub-project" : "");

        bool editable = area is { IsTemporaryArea: false, IsMission: false };
        _jumpButton.IsEnabled = area != null;
        _membersButton.IsEnabled = editable;
        _removeButton.IsEnabled = editable;
        _folderButton.IsEnabled = _chooseProjectFolder != null && editable;
        _folderPath.Text = area?.ProjectFolderPath ?? "";
        _folderPath.ToolTip = area?.ProjectFolderPath;
        _folderPath.Visibility = _folderPath.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _membersButton.ToolTip = area?.IsTemporaryArea == true
            ? "Temporary windows join and leave this area automatically." : null;
        _removeButton.ToolTip = area?.IsTemporaryArea == true
            ? "Keep this area for incoming temporary windows. You can move, resize or rename it." : null;

        _memberCard.Visibility = member == null ? Visibility.Collapsed : Visibility.Visible;
        _memberTitle.Text = member?.Title ?? "";
        _memberDetails.Text = member == null ? ""
            : member.State + (member.Pinned ? " · Pinned" : "")
              + (member.Details.Length > 0 ? Environment.NewLine + member.Details : "");
        _memberAction.IsEnabled = member?.CanActivate == true;
        _memberAction.Content = member?.ActionLabel ?? "Open member";
        _memberAction.ToolTip = member?.State == "Needs linking" ? "Link this place in Saved Workspace first."
            : member?.Details;

        if (area != null && (_loadedZone != area.Id || _loadedName != area.Name || _loadedColor != area.Color)
            && !_name.IsKeyboardFocusWithin && !_color.IsKeyboardFocusWithin)
        {
            _loadedZone = area.Id; _loadedName = area.Name; _loadedColor = area.Color;
            _name.Text = area.Name;
            _color.Text = area.Color;
        }
    }

    private void RefreshHistory()
    {
        _undo.IsEnabled = _history?.CanUndoZoneLayout == true;
        _redo.IsEnabled = _history?.CanRedoZoneLayout == true;
    }

    private void ActivateSelection()
    {
        if (Current?.MemberId != null) ActivateMember();
        else Jump();
    }

    private void Jump()
    {
        if (Current is { } selected && !_navigation.Jump(selected.AreaId)) Refresh();
    }

    private void ActivateMember()
    {
        if (Current is { MemberId: { } id } selected)
        {
            bool accepted = _navigation.ActivateMember(selected.AreaId, id);
            Refresh();
            if (!accepted) Report("That member is no longer available to open. Select it again after refresh.");
        }
    }

    private void BeginDraw()
    {
        Hide();
        _draw();
    }

    private void Save()
    {
        if (SelectedArea is not { } area) return;
        if (!ZoneService.ValidName(_name.Text) || !ZoneService.ValidColor(_color.Text))
        { Report("Enter an area name and a color such as #60A5FA."); return; }
        var id = area.Id;
        var name = _name.Text;
        var color = _color.Text;
        if (!_zones.Update(id, name, color))
        {
            Report(_statusOf() ?? "Changes are in memory; saving failed. Try again.");
            return;
        }
        var saved = _zones.Items.FirstOrDefault(z => z.Id == id);
        if (saved != null)
        {
            _search.Text = saved.Name;
            Refresh();
            var row = FindAreaRow(_tree.Items, id);
            if (row != null) { row.IsSelected = true; row.BringIntoView(); }
            UpdateSelection();
        }
        Report("Changes saved.");
    }

    private void ChooseMembers()
    {
        if (SelectedArea is not { } area) return;
        var window = new ZoneMembersWindow(_zones, area, _statusOf) { Owner = this };
        window.ShowDialog();
        Refresh();
    }

    private void Remove()
    {
        if (SelectedArea is not { } area) return;
        if (MessageBox.Show(this, $"Remove the area ‘{area.Name}’? Its windows and files will stay in place.",
            "Remove area", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        bool saved = _zones.Remove(area.Id);
        if (!saved) Report(_statusOf() ?? "Removal is in memory; saving failed.");
    }

    private FrameworkElement AreaHeader(ZoneNavigationEntry entry)
    {
        var area = entry.Area;
        var stack = new StackPanel { Margin = new Thickness(0, 6, 0, 6) };
        var line = new DockPanel { LastChildFill = true };
        line.Children.Add(new Border
        {
            Width = 9, Height = 9, CornerRadius = new CornerRadius(5), Background = Brush(area.Color),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0)
        });
        if (area.IsMission || area.IsTemporaryArea)
        {
            var badge = Badge(area.IsMission ? "Mission" : "Temporary");
            DockPanel.SetDock(badge, Dock.Right);
            line.Children.Add(badge);
        }
        line.Children.Add(new TextBlock
        {
            Text = area.Name, FontSize = 13.5, FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center
        });
        stack.Children.Add(line);
        stack.Children.Add(new TextBlock
        {
            Text = MemberCount(area.MemberCount) + $" · {area.Bounds.W:N0} × {area.Bounds.H:N0}",
            FontSize = 11, Foreground = Brush("#8FA1B8"), Margin = new Thickness(17, 3, 0, 0)
        });
        return stack;
    }

    private static FrameworkElement MemberHeader(ZoneNavigationMember member)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 5, 0, 5) };
        var line = new DockPanel { LastChildFill = true };
        line.Children.Add(new Border
        {
            Width = 7, Height = 7, CornerRadius = new CornerRadius(4), Background = StateBrush(member.State),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0)
        });
        line.Children.Add(new TextBlock
        {
            Text = member.Title, FontSize = 12.5,
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center
        });
        stack.Children.Add(line);
        stack.Children.Add(new TextBlock
        {
            Text = member.State + (member.Pinned ? " · Pinned" : ""), FontSize = 10.5,
            Foreground = Brush("#7D8CA3"), Margin = new Thickness(15, 2, 0, 0)
        });
        return stack;
    }

    private static Border Badge(string text) => new()
    {
        Background = Brush("#243244"), CornerRadius = new CornerRadius(6), Padding = new Thickness(7, 1, 7, 2),
        Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock
        {
            Text = text, FontSize = 9.5, FontWeight = FontWeights.SemiBold, Foreground = Brush("#93A2B8")
        }
    };

    private static TextBlock Label(string text) => new()
    {
        Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold,
        Foreground = Brush("#8FA1B8"), Margin = new Thickness(0, 14, 0, 6)
    };

    private Button TextButton(string text, Action click, string tip)
    {
        var button = new Button
        {
            Content = text, Appearance = ControlAppearance.Secondary, Padding = new Thickness(12, 5, 12, 5),
            FontSize = 12, ToolTip = tip
        };
        button.Style = (Style)FindResource("ProjectButton");
        button.Click += (_, _) => click();
        return button;
    }

    private Button DetailButton(string text, Action click, ControlAppearance appearance = ControlAppearance.Secondary)
    {
        var button = new Button
        {
            Content = text, Appearance = appearance, HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(12, 6, 12, 6), FontSize = 12
        };
        button.Style = (Style)FindResource(appearance == ControlAppearance.Primary ? "ProjectPrimaryButton"
            : appearance == ControlAppearance.Danger ? "ProjectDangerButton" : "ProjectButton");
        button.Click += (_, _) => click();
        return button;
    }

    private static string MemberCount(int count) => count == 1 ? "1 member" : count + " members";

    private static SolidColorBrush StateBrush(string state) => Brush(state switch
    {
        "Open" => "#34D399",
        "Placeholder" => "#FBBF24",
        "Minimized" => "#94A3B8",
        "Sleeping" => "#64748B",
        "Needs linking" => "#FB7185",
        _ => "#64748B"
    });

    private static SolidColorBrush Brush(string hex)
    {
        try { return new((Color)ColorConverter.ConvertFromString(hex)); }
        catch (FormatException) { return new(Color.FromRgb(0x60, 0xA5, 0xFA)); }
    }

    internal Func<bool>? ClampBelowTaskbar { get; set; }

    internal void SetCanvasOwner(IntPtr owner)
    {
        var interop = new WindowInteropHelper(this);
        if (interop.Owner != owner) interop.Owner = owner;
        Topmost = owner != IntPtr.Zero;
    }

    internal void RaiseWithoutActivation()
    {
        if (!IsVisible || !Topmost) return;
        CanvasZOrder.PlaceAtCeiling(new WindowInteropHelper(this).Handle, ClampBelowTaskbar?.Invoke() ?? true);
    }

    // Fluent row chrome for the project tree. Wpf.Ui supplies window, button and text styling;
    // these local styles only replace the default TreeView/expander look.
    private const string SurfaceStyles = """
        <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                            xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                            xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml">
          <Style x:Key="ProjectButton" TargetType="{x:Type ui:Button}">
            <Setter Property="Background" Value="#30445E"/>
            <Setter Property="Foreground" Value="#FFFFFF"/>
            <Setter Property="BorderBrush" Value="#7187A3"/>
            <Setter Property="BorderThickness" Value="1"/>
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="{x:Type ui:Button}">
                  <Border x:Name="Chrome" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                          BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="6" Padding="{TemplateBinding Padding}">
                    <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center" RecognizesAccessKey="True"/>
                  </Border>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Chrome" Property="Background" Value="#49637F"/></Trigger>
                    <Trigger Property="IsPressed" Value="True"><Setter TargetName="Chrome" Property="Background" Value="#20344B"/></Trigger>
                    <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Chrome" Property="BorderBrush" Value="#FFFFFF"/><Setter TargetName="Chrome" Property="BorderThickness" Value="2"/></Trigger>
                    <Trigger Property="IsEnabled" Value="False"><Setter TargetName="Chrome" Property="Background" Value="#263140"/><Setter Property="Foreground" Value="#AAB8C9"/><Setter TargetName="Chrome" Property="BorderBrush" Value="#526175"/></Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
          </Style>
          <Style x:Key="ProjectPrimaryButton" TargetType="{x:Type ui:Button}" BasedOn="{StaticResource ProjectButton}">
            <Setter Property="Background" Value="#175B9E"/><Setter Property="BorderBrush" Value="#83BFFF"/>
          </Style>
          <Style x:Key="ProjectDangerButton" TargetType="{x:Type ui:Button}" BasedOn="{StaticResource ProjectButton}">
            <Setter Property="Background" Value="#71313B"/><Setter Property="BorderBrush" Value="#E69BA5"/>
          </Style>
          <Style TargetType="TreeView">
            <Setter Property="Background" Value="Transparent"/>
            <Setter Property="BorderThickness" Value="0"/>
            <Setter Property="Padding" Value="0"/>
            <Setter Property="Foreground" Value="#E6EDF7"/>
            <Setter Property="ScrollViewer.HorizontalScrollBarVisibility" Value="Disabled"/>
          </Style>
          <Style TargetType="TreeViewItem">
            <Setter Property="Foreground" Value="#E6EDF7"/>
            <Setter Property="HorizontalContentAlignment" Value="Stretch"/>
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="TreeViewItem">
                  <StackPanel>
                    <Border x:Name="Row" Margin="0,1" Padding="4,1,8,1" CornerRadius="9" Background="Transparent">
                      <Grid>
                        <Grid.ColumnDefinitions>
                          <ColumnDefinition Width="18"/>
                          <ColumnDefinition Width="*"/>
                        </Grid.ColumnDefinitions>
                        <ToggleButton x:Name="Expander" Width="16" Height="16" Focusable="False"
                                      Background="Transparent" BorderThickness="0" ClickMode="Press"
                                      HorizontalAlignment="Left" VerticalAlignment="Center"
                                      IsChecked="{Binding IsExpanded, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}">
                          <ToggleButton.Template>
                            <ControlTemplate TargetType="ToggleButton">
                              <Border Background="Transparent" Width="16" Height="16">
                                <Path x:Name="Chevron" Data="M 0 0 L 4 4 L 0 8" Stroke="#93A2B8" StrokeThickness="1.6"
                                      StrokeStartLineCap="Round" StrokeEndLineCap="Round"
                                      HorizontalAlignment="Center" VerticalAlignment="Center"
                                      RenderTransformOrigin="0.5,0.5">
                                  <Path.RenderTransform>
                                    <RotateTransform Angle="0"/>
                                  </Path.RenderTransform>
                                </Path>
                              </Border>
                              <ControlTemplate.Triggers>
                                <Trigger Property="IsChecked" Value="True">
                                  <Setter TargetName="Chevron" Property="RenderTransform">
                                    <Setter.Value>
                                      <RotateTransform Angle="90"/>
                                    </Setter.Value>
                                  </Setter>
                                </Trigger>
                                <Trigger Property="IsMouseOver" Value="True">
                                  <Setter TargetName="Chevron" Property="Stroke" Value="#E6EDF7"/>
                                </Trigger>
                              </ControlTemplate.Triggers>
                            </ControlTemplate>
                          </ToggleButton.Template>
                        </ToggleButton>
                        <ContentPresenter Grid.Column="1" ContentSource="Header"
                                          HorizontalAlignment="Stretch" VerticalAlignment="Center"/>
                      </Grid>
                    </Border>
                    <ItemsPresenter x:Name="ItemsHost" Margin="14,0,0,0"/>
                  </StackPanel>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsExpanded" Value="False">
                      <Setter TargetName="ItemsHost" Property="Visibility" Value="Collapsed"/>
                    </Trigger>
                    <Trigger Property="HasItems" Value="False">
                      <Setter TargetName="Expander" Property="Visibility" Value="Hidden"/>
                    </Trigger>
                    <Trigger Property="IsSelected" Value="True">
                      <Setter TargetName="Row" Property="Background" Value="#26354C"/>
                    </Trigger>
                    <MultiTrigger>
                      <MultiTrigger.Conditions>
                        <Condition Property="IsMouseOver" Value="True"/>
                        <Condition Property="IsSelected" Value="False"/>
                      </MultiTrigger.Conditions>
                      <Setter TargetName="Row" Property="Background" Value="#1B2637"/>
                    </MultiTrigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
          </Style>
          <Style x:Key="ProjectSwatch" TargetType="Button">
            <Setter Property="Width" Value="30"/>
            <Setter Property="Height" Value="26"/>
            <Setter Property="Margin" Value="0,0,8,4"/>
            <Setter Property="Cursor" Value="Hand"/>
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="Button">
                  <Grid>
                    <Border x:Name="Chip" CornerRadius="7" Background="{TemplateBinding Background}"
                            BorderThickness="2" BorderBrush="Transparent"/>
                    <Border x:Name="Ring" CornerRadius="7" BorderThickness="2" BorderBrush="#FFFFFF" Visibility="Collapsed"/>
                    <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center" TextElement.Foreground="#101820" TextElement.FontWeight="Bold"/>
                  </Grid>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True">
                      <Setter TargetName="Chip" Property="BorderBrush" Value="#CBD5E1"/>
                    </Trigger>
                    <Trigger Property="IsKeyboardFocused" Value="True">
                      <Setter TargetName="Ring" Property="Visibility" Value="Visible"/>
                    </Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
          </Style>
        </ResourceDictionary>
        """;
}
