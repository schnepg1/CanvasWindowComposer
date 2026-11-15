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

/// <summary>
/// State snapshot for agent terminal observation and action controls in Mission Control.
/// </summary>
internal sealed record AgentTerminalState(
    bool IsBusy,
    bool IsLive,
    string? Address,
    string? AgentState,
    string? WorkingDirectory,
    string FeedbackMessage,
    bool CanEditDirectory
);

internal sealed class MissionControlWindow : FluentWindow
{
    private readonly StackPanel _body = new() { Margin = new(16, 12, 16, 16) };
    private readonly Grid _content = new();
    private readonly ScrollViewer _details = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBlock _feedback = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Border _terminalHost = new() { Margin = new(10, 0, 10, 10), CornerRadius = new(8), ClipToBounds = true };
    private readonly Border _feedbackBanner = new()
    {
        Background = MissionControlTheme.BannerBackground,
        BorderBrush = MissionControlTheme.BannerBorder,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(10, 8, 10, 8),
        Margin = new Thickness(0, 4, 0, 8)
    };
    private readonly CancellationTokenSource _lifetime = new();
    private readonly IAgentSessionManager _herdr;
    private TextBox? _initialInput;
    private AgentTerminalPane? _terminalPane;

    private Button? _launchButton;
    private Button? _watchButton;
    private Button? _controlButton;
    private Button? _refreshButton;
    private Button? _externalButton;
    private ComboBox? _agentSelector;
    private TextBox? _directoryInput;
    private AgentTerminalState _currentState = new(false, false, null, null, null, "Ready", true);

    internal event Action? TerminalChanged;
    internal event Action? TerminalLayoutChanged;
    internal event Action? ExternalTerminalOpened;
    internal bool HasExpandedTerminal => _terminalPane != null;

    internal ComboBox? AgentSelector => _agentSelector;
    internal Button? LaunchButton => _launchButton;

    public MissionControlWindow(IntPtr owner, IAgentSessionManager herdr)
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
        Background = MissionControlTheme.WindowBackground;
        Foreground = MissionControlTheme.ForegroundBright;

        var root = new DockPanel();
        var title = new TitleBar { Title = Title, ShowMaximize = false, ShowMinimize = false };
        DockPanel.SetDock(title, Dock.Top);
        root.Children.Add(title);

        var feedbackDock = new DockPanel();
        var feedbackIcon = new SymbolIcon
        {
            Symbol = SymbolRegular.Info16,
            FontSize = 14,
            Foreground = MissionControlTheme.AccentBlue,
            Margin = new Thickness(0, 0, 8, 0)
        };
        DockPanel.SetDock(feedbackIcon, Dock.Left);
        feedbackDock.Children.Add(feedbackIcon);
        _feedback.FontSize = 12;
        _feedback.Foreground = MissionControlTheme.TextFeedback;
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

        var hero = new StackPanel();
        hero.Children.Add(new TextBlock
        {
            Text = "Missions on this Desktop",
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            Foreground = MissionControlTheme.TextPrimary
        });
        hero.Children.Add(new TextBlock
        {
            Text = missions.Count == 1 ? "1 active mission" : $"{missions.Count} active missions",
            FontSize = 11.5,
            Foreground = MissionControlTheme.TextMuted,
            Margin = new Thickness(0, 2, 0, 0)
        });
        _body.Children.Add(CreateCard(hero));

        var listStack = new StackPanel();
        listStack.Children.Add(SectionTitle("Active Missions", "Jump to a mission on the canvas or inspect its agents"));

        if (missions.Count == 0)
        {
            listStack.Children.Add(new TextBlock
            {
                Text = "No missions on this desktop. Create one below at the current camera position.",
                FontStyle = FontStyles.Italic,
                Foreground = MissionControlTheme.TextSubtle,
                Margin = new Thickness(0, 4, 0, 4)
            });
        }
        else
        {
            foreach (var mission in missions)
            {
                var row = new Border
                {
                    Background = MissionControlTheme.CardRowBackground,
                    BorderBrush = MissionControlTheme.CardRowBorder,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(10, 8, 10, 8),
                    Margin = new Thickness(0, 2, 0, 4)
                };
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var icon = new SymbolIcon { Symbol = SymbolRegular.Sparkle20, FontSize = 16, Foreground = MissionControlTheme.AccentPurple, Margin = new Thickness(0, 0, 8, 0) };
                Grid.SetColumn(icon, 0);
                grid.Children.Add(icon);

                var nameBlock = new TextBlock
                {
                    Text = MissionName(mission),
                    FontWeight = FontWeights.SemiBold,
                    Foreground = MissionControlTheme.TextPrimary,
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

        var createCard = CreateCard(BuildNameEntry("Mission name", "New mission", create, "Could not save the mission at this position.", SymbolRegular.Add20));
        _body.Children.Add(createCard);
        _body.Children.Add(_feedbackBanner);
    }

    public void ShowMission(ZoneView mission, IReadOnlyList<MissionAgentCard> cards, Action<MissionAgentCard> inspect,
        Func<bool, bool> setLocked, Func<string, bool> addAgent)
    {
        _body.Children.Clear();

        var hero = new StackPanel();
        var titleRow = new Grid();
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleText = new TextBlock
        {
            Text = MissionName(mission),
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            Foreground = MissionControlTheme.TextPrimary,
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
            Foreground = MissionControlTheme.TextMuted,
            Margin = new Thickness(0, 4, 0, 0)
        };
        hero.Children.Add(countText);
        _body.Children.Add(CreateCard(hero));

        var agentsStack = new StackPanel();
        agentsStack.Children.Add(SectionTitle("Mission Agents", "Select an agent to inspect its status and launch its terminal"));

        if (cards.Count == 0)
        {
            agentsStack.Children.Add(new TextBlock
            {
                Text = "No agents in this mission yet. Add an agent below.",
                FontStyle = FontStyles.Italic,
                Foreground = MissionControlTheme.TextSubtle,
                Margin = new Thickness(0, 4, 0, 4)
            });
        }
        else
        {
            foreach (var card in cards)
            {
                var row = new Border
                {
                    Background = MissionControlTheme.CardRowBackground,
                    BorderBrush = MissionControlTheme.CardRowBorder,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(10, 8, 10, 8),
                    Margin = new Thickness(0, 2, 0, 4)
                };
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var icon = new SymbolIcon { Symbol = SymbolRegular.Bot20, FontSize = 16, Foreground = MissionControlTheme.AccentBlue, Margin = new Thickness(0, 0, 8, 0) };
                Grid.SetColumn(icon, 0);
                grid.Children.Add(icon);

                var nameBlock = new TextBlock
                {
                    Text = card.Name,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = MissionControlTheme.TextPrimary,
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

        if (mission != null)
        {
            var back = new Button
            {
                Content = "Back to " + MissionName(mission),
                Icon = new SymbolIcon { Symbol = SymbolRegular.ArrowUndo20, FontSize = 14, Foreground = MissionControlTheme.TextMuted, Margin = new Thickness(0, 0, 6, 0) },
                Appearance = ControlAppearance.Transparent,
                Margin = new Thickness(0, 0, 0, 8),
                Padding = new Thickness(8, 4, 10, 4),
                FontSize = 12,
                Foreground = MissionControlTheme.TextMuted
            };
            back.Click += (_, _) => showMission();
            _body.Children.Add(back);
        }

        var heroStack = new StackPanel();
        var headerGrid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var avatar = new Border
        {
            Width = 36,
            Height = 36,
            CornerRadius = new CornerRadius(18),
            Background = MissionControlTheme.AvatarBackground,
            Margin = new Thickness(0, 0, 10, 0),
            Child = new SymbolIcon { Symbol = SymbolRegular.Bot20, FontSize = 20, Foreground = MissionControlTheme.AccentBlue }
        };
        Grid.SetColumn(avatar, 0);
        headerGrid.Children.Add(avatar);

        var titleStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var titleText = new TextBlock
        {
            Text = card.Name,
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            Foreground = MissionControlTheme.TextPrimary,
            TextWrapping = TextWrapping.Wrap
        };
        titleStack.Children.Add(titleText);

        var pillRow = new WrapPanel { Margin = new Thickness(0, 3, 0, 0) };
        var rolePill = new Border
        {
            Background = MissionControlTheme.CardRowBackground,
            BorderBrush = MissionControlTheme.CardBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(0, 0, 6, 0),
            Child = new TextBlock
            {
                Text = card.Role,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = MissionControlTheme.TextMuted
            }
        };
        pillRow.Children.Add(rolePill);
        if (!string.IsNullOrWhiteSpace(card.TaskId))
        {
            var taskPill = new Border
            {
                Background = MissionControlTheme.CardRowBackground,
                BorderBrush = MissionControlTheme.SubtleBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(6, 2, 6, 2),
                Child = new TextBlock
                {
                    Text = card.TaskId,
                    FontSize = 10,
                    Foreground = MissionControlTheme.TextSubtle
                }
            };
            pillRow.Children.Add(taskPill);
        }
        titleStack.Children.Add(pillRow);
        Grid.SetColumn(titleStack, 1);
        headerGrid.Children.Add(titleStack);
        heroStack.Children.Add(headerGrid);

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
            Foreground = MissionControlTheme.TextFeedback,
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
                Foreground = MissionControlTheme.TextSubtle,
                Margin = new Thickness(16, 2, 0, 0)
            });
        }
        if (!string.IsNullOrWhiteSpace(card.Prompt))
        {
            var promptPill = new Border
            {
                Background = MissionControlTheme.CardRowBackground,
                BorderBrush = MissionControlTheme.CardBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(0, 6, 0, 0),
                Child = new TextBlock
                {
                    Text = "Prompt: " + card.Prompt,
                    FontSize = 11.5,
                    Foreground = MissionControlTheme.AccentPurple,
                    TextWrapping = TextWrapping.Wrap
                }
            };
            heroStack.Children.Add(promptPill);
        }
        _body.Children.Add(CreateCard(heroStack));

        var directory = new TextBox
        {
            Text = folder ?? "",
            PlaceholderText = "Local working folder",
            Icon = new SymbolIcon { Symbol = SymbolRegular.Folder20, FontSize = 16, Foreground = MissionControlTheme.TextMuted },
            Margin = new Thickness(0, 6, 0, 8)
        };
        System.Windows.Automation.AutomationProperties.SetName(directory, "Local working folder");
        _directoryInput = directory;

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

        var launchStack = new StackPanel();
        launchStack.Children.Add(SectionTitle("Autonomous Agent Runtime", "Select an AI agent and launch a Herdr background terminal"));

        var selector = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 6, 0, 10)
        };
        foreach (var agent in _herdr.SupportedAgents)
        {
            selector.Items.Add(new ComboBoxItem
            {
                Tag = agent,
                Content = BuildAgentItemContent(agent)
            });
        }
        selector.SelectedIndex = 0;
        _agentSelector = selector;

        var launch = new Button
        {
            Appearance = ControlAppearance.Secondary,
            Margin = new Thickness(0, 0, 0, 10),
            Padding = new Thickness(18, 9, 18, 9),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Cursor = Cursors.Hand
        };
        _launchButton = launch;

        AgentProfile GetSelectedAgent()
        {
            if (selector.SelectedItem is ComboBoxItem item && item.Tag is AgentProfile profile) return profile;
            if (selector.SelectedItem is AgentProfile directProfile) return directProfile;
            return _herdr.DefaultAgent;
        }

        selector.SelectionChanged += (_, _) =>
        {
            ApplyTerminalState(_currentState, GetSelectedAgent());
        };

        launchStack.Children.Add(selector);
        launchStack.Children.Add(launch);

        var actions = new WrapPanel();
        _watchButton = ActionButton("Watch terminal", () => { }, SymbolRegular.Eye20);
        _controlButton = ActionButton("Take control", () => { }, SymbolRegular.Keyboard20);
        _refreshButton = ActionButton("Refresh", () => { }, SymbolRegular.ArrowClockwise20);
        _externalButton = ActionButton("Open externally", () => { }, SymbolRegular.ArrowUpRight20);
        var collapse = ActionButton("Detach terminal", () => { }, SymbolRegular.Dismiss20);
        collapse.Visibility = Visibility.Collapsed;

        actions.Children.Add(_watchButton);
        actions.Children.Add(_controlButton);
        actions.Children.Add(_refreshButton);
        actions.Children.Add(_externalButton);
        actions.Children.Add(collapse);
        launchStack.Children.Add(actions);

        var launchCard = CreateCard(launchStack);
        _body.Children.Add(launchCard);
        _body.Children.Add(_feedbackBanner);

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
            string feedback = terminal switch
            {
                { IsLive: true } live => $"{live.Address} · {live.AgentState ?? "No status reported"}",
                { } ended => $"{ended.Address} · Session ended",
                _ => "No Herdr terminal linked."
            };
            if (terminal is { IsLive: true, WorkingDirectory: { } cwd }) directory.Text = cwd;
            _currentState = new(false, terminal?.IsLive == true, terminal?.Address, terminal?.AgentState, terminal?.WorkingDirectory, feedback, saveFolder != null);
            ApplyTerminalState(_currentState, GetSelectedAgent());
            TerminalChanged?.Invoke();
        }

        async Task Run(Func<CancellationToken, Task> action)
        {
            var token = _lifetime.Token;
            _currentState = _currentState with { IsBusy = true, FeedbackMessage = "Checking Herdr…" };
            ApplyTerminalState(_currentState, GetSelectedAgent());
            try
            {
                await action(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex) when (ex is HerdrException or InvalidOperationException or System.ComponentModel.Win32Exception
                or System.Text.Json.JsonException or System.IO.IOException or ArgumentException or KeyNotFoundException)
            {
                if (!token.IsCancellationRequested)
                {
                    _currentState = _currentState with { IsBusy = false, FeedbackMessage = ex.Message };
                    ApplyTerminalState(_currentState, GetSelectedAgent());
                }
            }
            finally
            {
                if (!token.IsCancellationRequested)
                {
                    _currentState = _currentState with { IsBusy = false };
                    ApplyTerminalState(_currentState, GetSelectedAgent());
                }
            }
        }

        launch.Click += async (_, _) => await Run(async token =>
        {
            var selected = GetSelectedAgent();
            if (await _herdr.Find(card.Id, token) is not { IsLive: true })
                await _herdr.StartAgent(selected, directory.Text.Trim(), card.Id, token, card.Prompt);
            await Load(token);
        });
        _watchButton.Click += (_, _) => Expand(observe: true);
        _controlButton.Click += (_, _) => Expand(observe: false);
        _externalButton.Click += async (_, _) => await Run(async token =>
        {
            await _herdr.Open(card.Id, terminal!.Address, observe: false, token);
            ExternalTerminalOpened?.Invoke();
        });
        _refreshButton.Click += async (_, _) => await Run(Load);
        Loaded += async (_, _) => await Run(Load);

        // Initial visual state
        ApplyTerminalState(_currentState, GetSelectedAgent());
    }

    private void ApplyTerminalState(AgentTerminalState state, AgentProfile selected)
    {
        _feedback.Text = state.FeedbackMessage;
        if (_refreshButton != null) _refreshButton.IsEnabled = !state.IsBusy;
        if (_launchButton != null) _launchButton.IsEnabled = !state.IsBusy && !state.IsLive;
        if (_agentSelector != null) _agentSelector.IsEnabled = !state.IsBusy && !state.IsLive;
        if (_directoryInput != null) _directoryInput.IsEnabled = !state.IsBusy && !state.IsLive && state.CanEditDirectory;
        if (_watchButton != null) _watchButton.IsEnabled = !state.IsBusy && state.IsLive;
        if (_controlButton != null) _controlButton.IsEnabled = !state.IsBusy && state.IsLive;
        if (_externalButton != null) _externalButton.IsEnabled = !state.IsBusy && state.IsLive;

        UpdateLaunchButton(selected);
    }

    private void UpdateLaunchButton(AgentProfile agent)
    {
        if (_launchButton == null) return;
        _launchButton.Content = $"Start {agent.DisplayName}";
        var accentBrush = MissionControlTheme.AgentAccent(agent.AccentColorHex);
        _launchButton.Background = accentBrush;
        _launchButton.BorderBrush = accentBrush;
        _launchButton.Foreground = Brushes.White;
        _launchButton.FontWeight = FontWeights.Bold;
        _launchButton.FontSize = 13;
        _launchButton.Icon = new SymbolIcon
        {
            Symbol = SymbolRegular.Play20,
            FontSize = 16,
            Foreground = Brushes.White,
            Margin = new Thickness(0, 0, 8, 0)
        };
    }

    private static UIElement BuildAgentItemContent(AgentProfile agent)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2) };
        var dot = new Border
        {
            Width = 10,
            Height = 10,
            CornerRadius = new CornerRadius(5),
            Background = MissionControlTheme.AgentAccent(agent.AccentColorHex),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        var name = new TextBlock
        {
            Text = agent.DisplayName,
            FontWeight = FontWeights.SemiBold,
            Foreground = MissionControlTheme.TextPrimary,
            VerticalAlignment = VerticalAlignment.Center
        };
        var sub = new TextBlock
        {
            Text = " · " + agent.Subtitle,
            FontSize = 11.5,
            Foreground = MissionControlTheme.TextMuted,
            VerticalAlignment = VerticalAlignment.Center
        };
        panel.Children.Add(dot);
        panel.Children.Add(name);
        panel.Children.Add(sub);
        return panel;
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
                Foreground = MissionControlTheme.TextMuted
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
        Background = MissionControlTheme.CardBackground,
        BorderBrush = MissionControlTheme.CardBorder,
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
            Foreground = MissionControlTheme.TextSecondary
        });
        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            panel.Children.Add(new TextBlock
            {
                Text = subtitle,
                FontSize = 11.5,
                Foreground = MissionControlTheme.TextMuted,
                Margin = new Thickness(0, 2, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });
        }
        return panel;
    }
}
