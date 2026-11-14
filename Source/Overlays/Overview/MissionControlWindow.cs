using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Wpf.Ui.Markup;
using Button = Wpf.Ui.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace CanvasDesktop;

internal sealed class MissionControlWindow : FluentWindow
{
    private static SolidColorBrush Brush(string hex) => (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;

    private readonly StackPanel _body = new() { Margin = new(16, 12, 16, 16) };
    private readonly Grid _content = new();
    private readonly ScrollViewer _details = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBlock _feedback = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Border _terminalHost = new() { Margin = new(10, 0, 10, 10), CornerRadius = new(8), ClipToBounds = true };
    private readonly Border _feedbackBanner = new()
    {
        Background = Brush("#0B1120"),
        BorderBrush = Brush("#1E293B"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(10, 8, 10, 8),
        Margin = new Thickness(0, 4, 0, 8)
    };
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HerdrSessions _herdr;
    private TextBox? _initialInput;
    private AgentTerminalPane? _terminalPane;

    internal event Action? TerminalChanged;
    internal event Action? TerminalLayoutChanged;
    internal event Action? ExternalTerminalOpened;
    internal bool HasExpandedTerminal => _terminalPane != null;

    internal ComboBox? AgentSelector { get; private set; }
    internal Button? LaunchButton { get; private set; }

    public MissionControlWindow(IntPtr owner, HerdrSessions herdr)
    {
        _herdr = herdr;
        if (owner != IntPtr.Zero) new WindowInteropHelper(this).Owner = owner;
        Title = "Mission Control";
        Width = 490;
        Height = 580;
        MinWidth = 380;
        MinHeight = 440;
        ShowInTaskbar = false;
        Topmost = true;
        ExtendsContentIntoTitleBar = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowBackdropType = WindowBackdropType.Mica;
        Resources.MergedDictionaries.Add(new ThemesDictionary { Theme = ApplicationTheme.Dark });
        Resources.MergedDictionaries.Add(new ControlsDictionary());
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        Background = Brush("#0F172A");
        Foreground = Brush("#F8FAFC");

        var root = new DockPanel();
        var title = new TitleBar { Title = Title, ShowMaximize = false, ShowMinimize = false };
        DockPanel.SetDock(title, Dock.Top);
        root.Children.Add(title);

        var feedbackDock = new DockPanel();
        var feedbackIcon = new SymbolIcon
        {
            Symbol = SymbolRegular.Info16,
            FontSize = 14,
            Foreground = Brush("#60A5FA"),
            Margin = new Thickness(0, 0, 8, 0)
        };
        DockPanel.SetDock(feedbackIcon, Dock.Left);
        feedbackDock.Children.Add(feedbackIcon);
        _feedback.FontSize = 12;
        _feedback.Foreground = Brush("#CBD5E1");
        feedbackDock.Children.Add(_feedback);
        _feedbackBanner.Child = feedbackDock;

        _details.Content = _body;
        _content.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        _content.RowDefinitions.Add(new() { Height = new(0) });
        _content.Children.Add(_details);
        Grid.SetRow(_terminalHost, 1);
        _content.Children.Add(_terminalHost);
        root.Children.Add(_content);
        Content = root;
        System.Windows.Forms.Integration.ElementHost.EnableModelessKeyboardInterop(this);
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && !HasExpandedTerminal) Close(); };
        Closed += (_, _) => { _terminalPane?.Dispose(); _lifetime.Cancel(); _lifetime.Dispose(); };
    }

    public void Present()
    {
        Show();
        // Let the dock's context menu finish restoring focus before taking it.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
        {
            if (!IsVisible) return;
            Activate();
            _initialInput?.Focus();
        }));
    }

    public void ShowMissions(IReadOnlyList<ZoneView> missions, Func<string, bool> create, Action<ZoneView> jump,
        Action<ZoneView> inspect)
    {
        _body.Children.Clear();

        // Hero Card
        var hero = new StackPanel();
        hero.Children.Add(new TextBlock
        {
            Text = "Missions on this Desktop",
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            Foreground = Brush("#F8FAFC")
        });
        hero.Children.Add(new TextBlock
        {
            Text = missions.Count == 1 ? "1 active mission" : $"{missions.Count} active missions",
            FontSize = 11.5,
            Foreground = Brush("#94A3B8"),
            Margin = new Thickness(0, 2, 0, 0)
        });
        _body.Children.Add(CreateCard(hero));

        // Missions List Card
        var listStack = new StackPanel();
        listStack.Children.Add(SectionTitle("Active Missions", "Jump to a mission on the canvas or inspect its agents"));

        if (missions.Count == 0)
        {
            listStack.Children.Add(new TextBlock
            {
                Text = "No missions on this desktop. Create one below at the current camera position.",
                FontStyle = FontStyles.Italic,
                Foreground = Brush("#64748B"),
                Margin = new Thickness(0, 4, 0, 4)
            });
        }
        else
        {
            foreach (var mission in missions)
            {
                var row = new Border
                {
                    Background = Brush("#0F172A"),
                    BorderBrush = Brush("#334155"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(10, 8, 10, 8),
                    Margin = new Thickness(0, 2, 0, 4)
                };
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var icon = new SymbolIcon { Symbol = SymbolRegular.Sparkle20, FontSize = 16, Foreground = Brush("#A78BFA"), Margin = new Thickness(0, 0, 8, 0) };
                Grid.SetColumn(icon, 0);
                grid.Children.Add(icon);

                var nameBlock = new TextBlock
                {
                    Text = MissionName(mission),
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Brush("#F1F5F9"),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(nameBlock, 1);
                grid.Children.Add(nameBlock);

                var actions = new StackPanel { Orientation = Orientation.Horizontal };
                var jumpBtn = ActionButton("Jump", () => jump(mission), SymbolRegular.Location20);
                jumpBtn.Margin = new Thickness(0, 0, 4, 0);
                var inspectBtn = ActionButton("Inspect", () => inspect(mission), SymbolRegular.ArrowUpRight20);
                inspectBtn.Margin = new Thickness(0);
                actions.Children.Add(jumpBtn);
                actions.Children.Add(inspectBtn);

                Grid.SetColumn(actions, 2);
                grid.Children.Add(actions);

                row.Child = grid;
                listStack.Children.Add(row);
            }
        }
        _body.Children.Add(CreateCard(listStack));

        // Create Mission Card
        var createCard = CreateCard(BuildNameEntry("Mission name", "New mission", create, "Could not save the mission at this position.", SymbolRegular.Add20));
        _body.Children.Add(createCard);

        _body.Children.Add(_feedbackBanner);
    }

    public void ShowMission(ZoneView mission, IReadOnlyList<MissionAgentCard> cards, Action<MissionAgentCard> inspect,
        Func<bool, bool> setLocked, Func<string, bool> addAgent)
    {
        _body.Children.Clear();

        // Hero Card
        var hero = new StackPanel();
        var titleRow = new Grid();
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleText = new TextBlock
        {
            Text = MissionName(mission),
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            Foreground = Brush("#F8FAFC"),
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetColumn(titleText, 0);
        titleRow.Children.Add(titleText);

        var lockBtn = LockButton(mission.MissionPositionLocked, setLocked);
        Grid.SetColumn(lockBtn, 1);
        titleRow.Children.Add(lockBtn);
        hero.Children.Add(titleRow);

        var countText = new TextBlock
        {
            Text = cards.Count == 1 ? "1 agent assigned" : $"{cards.Count} agents assigned",
            FontSize = 11.5,
            Foreground = Brush("#94A3B8"),
            Margin = new Thickness(0, 4, 0, 0)
        };
        hero.Children.Add(countText);
        _body.Children.Add(CreateCard(hero));

        // Agents List Card
        var agentsStack = new StackPanel();
        agentsStack.Children.Add(SectionTitle("Mission Agents", "Select an agent to inspect its status and launch its terminal"));

        if (cards.Count == 0)
        {
            agentsStack.Children.Add(new TextBlock
            {
                Text = "No agents in this mission yet. Add an agent below.",
                FontStyle = FontStyles.Italic,
                Foreground = Brush("#64748B"),
                Margin = new Thickness(0, 4, 0, 4)
            });
        }
        else
        {
            foreach (var card in cards)
            {
                var row = new Border
                {
                    Background = Brush("#0F172A"),
                    BorderBrush = Brush("#334155"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(10, 8, 10, 8),
                    Margin = new Thickness(0, 2, 0, 4)
                };
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var icon = new SymbolIcon { Symbol = SymbolRegular.Bot20, FontSize = 16, Foreground = Brush("#60A5FA"), Margin = new Thickness(0, 0, 8, 0) };
                Grid.SetColumn(icon, 0);
                grid.Children.Add(icon);

                var nameBlock = new TextBlock
                {
                    Text = card.Name,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Brush("#F1F5F9"),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(nameBlock, 1);
                grid.Children.Add(nameBlock);

                var inspectBtn = ActionButton("Inspect", () => inspect(card), SymbolRegular.ArrowUpRight20);
                inspectBtn.Margin = new Thickness(0);
                Grid.SetColumn(inspectBtn, 2);
                grid.Children.Add(inspectBtn);

                row.Child = grid;
                agentsStack.Children.Add(row);
            }
        }
        _body.Children.Add(CreateCard(agentsStack));

        // Add Agent Card
        var addAgentCard = CreateCard(BuildNameEntry("Agent name", "Add agent", addAgent, "Could not add the agent to this mission.", SymbolRegular.Add20));
        _body.Children.Add(addAgentCard);

        _body.Children.Add(_feedbackBanner);
    }

    private FrameworkElement BuildNameEntry(string label, string action, Func<string, bool> submit, string error, SymbolRegular symbol)
    {
        var panel = new StackPanel();
        panel.Children.Add(SectionTitle(action, $"Enter a name to {action.ToLowerInvariant()}"));

        var grid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var input = new TextBox
        {
            PlaceholderText = label,
            MaxLength = 120,
            Margin = new Thickness(0, 0, 8, 0)
        };
        System.Windows.Automation.AutomationProperties.SetName(input, label);
        _initialInput = input;
        Grid.SetColumn(input, 0);
        grid.Children.Add(input);

        void Submit()
        {
            string name = input.Text.Trim();
            if (name.Length == 0) { _feedback.Text = $"Enter a {label.ToLowerInvariant()}."; return; }
            if (!submit(name)) _feedback.Text = error;
            else input.Clear();
        }

        input.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Submit(); } };

        var submitButton = ActionButton(action, Submit, symbol, ControlAppearance.Primary);
        submitButton.Margin = new Thickness(0);
        Grid.SetColumn(submitButton, 1);
        grid.Children.Add(submitButton);

        panel.Children.Add(grid);
        return panel;
    }

    public void ShowAgent(ZoneView? mission, MissionAgentCard card, AgentStatusReport? status, string? folder,
        Func<bool, bool> setLocked, Func<string, bool>? saveFolder, Action showMission)
    {
        _body.Children.Clear();

        // 1. Navigation / Back button if within a mission
        if (mission != null)
        {
            var back = new Button
            {
                Content = "Back to " + MissionName(mission),
                Icon = new SymbolIcon { Symbol = SymbolRegular.ArrowUndo20, FontSize = 14, Foreground = Brush("#94A3B8"), Margin = new Thickness(0, 0, 6, 0) },
                Appearance = ControlAppearance.Transparent,
                Margin = new Thickness(0, 0, 0, 8),
                Padding = new Thickness(8, 4, 10, 4),
                FontSize = 12,
                Foreground = Brush("#94A3B8")
            };
            back.Click += (_, _) => showMission();
            _body.Children.Add(back);
        }

        // 2. Hero Card: Agent Identity & Workflow Status
        var heroStack = new StackPanel();

        var headerGrid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var avatar = new Border
        {
            Width = 36,
            Height = 36,
            CornerRadius = new CornerRadius(18),
            Background = Brush("#1E3A8A"),
            Margin = new Thickness(0, 0, 10, 0),
            Child = new SymbolIcon { Symbol = SymbolRegular.Bot20, FontSize = 20, Foreground = Brush("#60A5FA") }
        };
        Grid.SetColumn(avatar, 0);
        headerGrid.Children.Add(avatar);

        var titleStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var titleText = new TextBlock
        {
            Text = card.Name,
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            Foreground = Brush("#F8FAFC"),
            TextWrapping = TextWrapping.Wrap
        };
        titleStack.Children.Add(titleText);

        var pillRow = new WrapPanel { Margin = new Thickness(0, 3, 0, 0) };
        var rolePill = new Border
        {
            Background = Brush("#0F172A"),
            BorderBrush = Brush("#334155"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(0, 0, 6, 0),
            Child = new TextBlock
            {
                Text = card.Role,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush("#94A3B8")
            }
        };
        pillRow.Children.Add(rolePill);
        if (!string.IsNullOrWhiteSpace(card.TaskId))
        {
            var taskPill = new Border
            {
                Background = Brush("#0F172A"),
                BorderBrush = Brush("#1E293B"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(6, 2, 6, 2),
                Child = new TextBlock
                {
                    Text = card.TaskId,
                    FontSize = 10,
                    Foreground = Brush("#64748B")
                }
            };
            pillRow.Children.Add(taskPill);
        }
        titleStack.Children.Add(pillRow);
        Grid.SetColumn(titleStack, 1);
        headerGrid.Children.Add(titleStack);
        heroStack.Children.Add(headerGrid);

        // Status row
        var statusColor = status != null ? TelemetryStatusPill.ColorFor(status.State) : Color.FromRgb(148, 163, 184);
        var statusRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        var statusDot = new Border
        {
            Width = 8,
            Height = 8,
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(statusColor),
            Margin = new Thickness(2, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        statusRow.Children.Add(statusDot);

        string statusText = status == null
            ? "No workflow status reported."
            : $"{status.State}" + (status.Detail.Length > 0 ? " · " + status.Detail : "");
        statusRow.Children.Add(new TextBlock
        {
            Text = statusText,
            FontSize = 12,
            Foreground = Brush("#CBD5E1"),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        });
        heroStack.Children.Add(statusRow);

        if (status?.Timestamp is { } timestamp && timestamp != default)
        {
            heroStack.Children.Add(new TextBlock
            {
                Text = "Last workflow update: " + timestamp.ToLocalTime().ToString("g"),
                FontSize = 11,
                Foreground = Brush("#64748B"),
                Margin = new Thickness(16, 2, 0, 0)
            });
        }
        if (!string.IsNullOrWhiteSpace(card.Prompt))
        {
            var promptPill = new Border
            {
                Background = Brush("#0F172A"),
                BorderBrush = Brush("#334155"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(0, 6, 0, 0),
                Child = new TextBlock
                {
                    Text = "Prompt: " + card.Prompt,
                    FontSize = 11.5,
                    Foreground = Brush("#A78BFA"),
                    TextWrapping = TextWrapping.Wrap
                }
            };
            heroStack.Children.Add(promptPill);
        }
        _body.Children.Add(CreateCard(heroStack));

        // 3. Environment & Workspace Settings Card
        var directory = new TextBox
        {
            Text = folder ?? "",
            PlaceholderText = "Local working folder",
            Icon = new SymbolIcon { Symbol = SymbolRegular.Folder20, FontSize = 16, Foreground = Brush("#94A3B8") },
            Margin = new Thickness(0, 6, 0, 8)
        };
        System.Windows.Automation.AutomationProperties.SetName(directory, "Local working folder");

        var settingsStack = new StackPanel();
        settingsStack.Children.Add(SectionTitle("Working Environment", "Folder where the agent creates and modifies files"));
        settingsStack.Children.Add(directory);

        var settingsButtons = new WrapPanel();
        if (saveFolder != null)
        {
            settingsButtons.Children.Add(ActionButton("Save working folder", () =>
            {
                _feedback.Text = saveFolder(directory.Text.Trim())
                    ? "Working folder saved." : "Choose an existing local working folder.";
            }, SymbolRegular.Save20));
        }
        if (mission != null)
        {
            settingsButtons.Children.Add(LockButton(mission.MissionPositionLocked, setLocked));
        }
        settingsStack.Children.Add(settingsButtons);

        var settingsCard = CreateCard(settingsStack);
        _body.Children.Add(settingsCard);

        // 4. Agent Selector & Launch Card
        var launchStack = new StackPanel();
        launchStack.Children.Add(SectionTitle("Autonomous Agent Runtime", "Select an AI agent and launch a Herdr background terminal"));

        // Agent selector combo box
        var selector = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 6, 0, 10),
            ItemsSource = HerdrSessions.SupportedAgents,
            SelectedIndex = 0
        };
        AgentSelector = selector;

        var itemTemplate = new DataTemplate(typeof(AgentProfile));
        var templateFactory = new FrameworkElementFactory(typeof(StackPanel));
        templateFactory.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        templateFactory.SetValue(StackPanel.MarginProperty, new Thickness(2, 2, 2, 2));

        var chipFactory = new FrameworkElementFactory(typeof(Border));
        chipFactory.SetValue(Border.WidthProperty, 10.0);
        chipFactory.SetValue(Border.HeightProperty, 10.0);
        chipFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        chipFactory.SetValue(Border.MarginProperty, new Thickness(0, 0, 8, 0));
        chipFactory.SetValue(Border.VerticalAlignmentProperty, VerticalAlignment.Center);
        chipFactory.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding(nameof(AgentProfile.AccentColorHex))
        {
            Converter = new HexToBrushConverter()
        });

        var nameFactory = new FrameworkElementFactory(typeof(TextBlock));
        nameFactory.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(AgentProfile.DisplayName)));
        nameFactory.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        nameFactory.SetValue(TextBlock.ForegroundProperty, Brush("#F8FAFC"));

        var subFactory = new FrameworkElementFactory(typeof(TextBlock));
        subFactory.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(AgentProfile.Subtitle))
        {
            StringFormat = " · {0}"
        });
        subFactory.SetValue(TextBlock.ForegroundProperty, Brush("#94A3B8"));
        subFactory.SetValue(TextBlock.FontSizeProperty, 11.5);
        subFactory.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);

        templateFactory.AppendChild(chipFactory);
        templateFactory.AppendChild(nameFactory);
        templateFactory.AppendChild(subFactory);
        itemTemplate.VisualTree = templateFactory;
        selector.ItemTemplate = itemTemplate;
        launchStack.Children.Add(selector);

        // Launch button with vibrant styling
        var launch = new Button
        {
            Appearance = ControlAppearance.Secondary,
            Margin = new Thickness(0, 0, 0, 10),
            Padding = new Thickness(18, 9, 18, 9),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Cursor = Cursors.Hand
        };
        LaunchButton = launch;

        void ApplyAgentStyling(AgentProfile agent)
        {
            launch.Content = $"Start {agent.DisplayName}";
            var accentBrush = Brush(agent.AccentColorHex);
            launch.Background = accentBrush;
            launch.BorderBrush = accentBrush;
            launch.Foreground = Brushes.White;
            launch.FontWeight = FontWeights.Bold;
            launch.FontSize = 13;
            launch.Icon = new SymbolIcon
            {
                Symbol = SymbolRegular.Play20,
                FontSize = 16,
                Foreground = Brushes.White,
                Margin = new Thickness(0, 0, 8, 0)
            };
        }

        ApplyAgentStyling(HerdrSessions.DefaultAgent);

        selector.SelectionChanged += (_, _) =>
        {
            if (selector.SelectedItem is AgentProfile selected)
            {
                ApplyAgentStyling(selected);
            }
        };

        launchStack.Children.Add(launch);

        // Terminal actions row
        var actions = new WrapPanel();
        var watch = ActionButton("Watch terminal", () => { }, SymbolRegular.Eye20);
        var control = ActionButton("Take control", () => { }, SymbolRegular.Keyboard20);
        var refresh = ActionButton("Refresh", () => { }, SymbolRegular.ArrowClockwise20);
        var external = ActionButton("Open externally", () => { }, SymbolRegular.ArrowUpRight20);
        var collapse = ActionButton("Detach terminal", () => { }, SymbolRegular.Dismiss20);
        collapse.Visibility = Visibility.Collapsed;

        actions.Children.Add(watch);
        actions.Children.Add(control);
        actions.Children.Add(refresh);
        actions.Children.Add(external);
        actions.Children.Add(collapse);
        launchStack.Children.Add(actions);

        var launchCard = CreateCard(launchStack);
        _body.Children.Add(launchCard);

        // 5. Diagnostics / Feedback banner
        _body.Children.Add(_feedbackBanner);

        var buttons = new[] { launch, watch, control, refresh, external };

        void Expand(bool observe)
        {
            if (_terminalPane == null)
            {
                _terminalPane = new(_herdr, card.Id);
                _terminalPane.StatusChanged += message => _feedback.Text = message;
                _terminalHost.Child = _terminalPane;
                settingsCard.Visibility = Visibility.Collapsed;
                _content.RowDefinitions[0].Height = GridLength.Auto;
                _details.MaxHeight = 220;
                _content.RowDefinitions[1].Height = new(1, GridUnitType.Star);
                collapse.Visibility = Visibility.Visible;
                TerminalLayoutChanged?.Invoke();
            }
            _ = _terminalPane.Attach(observe);
        }

        collapse.Click += (_, _) =>
        {
            _terminalPane?.Dispose();
            _terminalPane = null;
            _terminalHost.Child = null;
            settingsCard.Visibility = Visibility.Visible;
            _content.RowDefinitions[0].Height = new(1, GridUnitType.Star);
            _details.MaxHeight = double.PositiveInfinity;
            _content.RowDefinitions[1].Height = new(0);
            collapse.Visibility = Visibility.Collapsed;
            _feedback.Text = "Detached. The agent session is still available.";
            TerminalLayoutChanged?.Invoke();
        };

        HerdrSessions.Terminal? terminal = null;
        async Task Load(CancellationToken token)
        {
            terminal = await _herdr.Find(card.Id, token);
            _feedback.Text = terminal switch
            {
                { IsLive: true } live => $"{live.Address} · {live.AgentState ?? "No status reported"}",
                { } ended => $"{ended.Address} · Session ended",
                _ => "No Herdr terminal linked."
            };
            if (terminal is { IsLive: true, WorkingDirectory: { } cwd }) directory.Text = cwd;
            TerminalChanged?.Invoke();
        }

        async Task Run(Func<CancellationToken, Task> action)
        {
            var token = _lifetime.Token;
            foreach (var button in buttons) button.IsEnabled = false;
            selector.IsEnabled = false;
            _feedback.Text = "Checking Herdr…";
            bool checkedTerminal = false;
            try { await action(token); checkedTerminal = true; }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception
                or System.Text.Json.JsonException or System.IO.IOException or ArgumentException or KeyNotFoundException)
            {
                if (!token.IsCancellationRequested) _feedback.Text = ex.Message;
            }
            finally
            {
                if (!token.IsCancellationRequested)
                {
                    refresh.IsEnabled = true;
                    launch.IsEnabled = checkedTerminal && terminal?.IsLive != true;
                    selector.IsEnabled = checkedTerminal && terminal?.IsLive != true;
                    watch.IsEnabled = control.IsEnabled = external.IsEnabled = checkedTerminal && terminal?.IsLive == true;
                    directory.IsEnabled = saveFolder != null && terminal?.IsLive != true;
                }
            }
        }

        launch.Click += async (_, _) => await Run(async token =>
        {
            var selected = (AgentProfile)selector.SelectedItem ?? HerdrSessions.DefaultAgent;
            if (await _herdr.Find(card.Id, token) is not { IsLive: true })
                await _herdr.StartAgent(selected, directory.Text.Trim(), card.Id, token, card.Prompt);
            await Load(token);
        });
        watch.Click += (_, _) => Expand(observe: true);
        control.Click += (_, _) => Expand(observe: false);
        external.Click += async (_, _) => await Run(async token =>
        {
            await _herdr.Open(card.Id, terminal!.Address, observe: false, token);
            ExternalTerminalOpened?.Invoke();
        });
        refresh.Click += async (_, _) => await Run(Load);
        Loaded += async (_, _) => await Run(Load);
    }

    private Button LockButton(bool locked, Func<bool, bool> change)
    {
        var button = ActionButton(locked ? "Unlock position" : "Lock position", () => { },
            locked ? SymbolRegular.LockClosed20 : SymbolRegular.LockOpen20);
        button.Click += (_, _) =>
        {
            if (!change(!locked)) { _feedback.Text = "Could not save mission position lock."; return; }
            locked = !locked;
            button.Content = locked ? "Unlock position" : "Lock position";
            button.Icon = new SymbolIcon
            {
                Symbol = locked ? SymbolRegular.LockClosed20 : SymbolRegular.LockOpen20,
                FontSize = 14,
                Foreground = Brush("#94A3B8")
            };
        };
        return button;
    }

    internal static string MissionName(ZoneView mission) =>
        mission.Name.StartsWith("AI Mission Control: ", StringComparison.Ordinal) ? mission.Name[20..] : mission.Name;

    private static Button ActionButton(string text, Action action, SymbolRegular? symbol = null, ControlAppearance appearance = ControlAppearance.Secondary)
    {
        var button = new Button
        {
            Content = text,
            Appearance = appearance,
            Margin = new Thickness(0, 4, 6, 4),
            Padding = new Thickness(12, 6, 12, 6),
            FontSize = 12
        };
        if (symbol.HasValue)
        {
            button.Icon = new SymbolIcon { Symbol = symbol.Value, FontSize = 14, Margin = new Thickness(0, 0, 6, 0) };
        }
        button.Click += (_, _) => action();
        return button;
    }

    private static Border CreateCard(UIElement content, Thickness? margin = null, Thickness? padding = null) => new()
    {
        Background = Brush("#1E293B"),
        BorderBrush = Brush("#334155"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = padding ?? new Thickness(14, 12, 14, 12),
        Margin = margin ?? new Thickness(0, 0, 0, 10),
        Child = content
    };

    private static FrameworkElement SectionTitle(string title, string? subtitle = null)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("#E2E8F0")
        });
        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            panel.Children.Add(new TextBlock
            {
                Text = subtitle,
                FontSize = 11.5,
                Foreground = Brush("#94A3B8"),
                Margin = new Thickness(0, 2, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });
        }
        return panel;
    }

    private sealed class HexToBrushConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            => value is string hex ? Brush(hex) : Brushes.Transparent;
        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            => throw new NotSupportedException();
    }
}
