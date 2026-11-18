using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Wpf.Ui.Markup;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace CanvasDesktop;

/// <summary>
/// A small Fluent WPF confirmation popup shown when dropping a text card onto an agent card.
/// The prompt preview is editable before sending. Pressing Enter confirms the prompt; Escape cancels.
/// </summary>
internal sealed class AgentPromptConfirmationDialog : FluentWindow
{
    private readonly TextBox _promptBox;
    private readonly Button _confirmButton;
    private readonly Button _cancelButton;
    private readonly string _initialPrompt;

    public string AgentName { get; }
    public string PromptText
    {
        get => _promptBox?.Text ?? _initialPrompt;
        set
        {
            if (_promptBox != null) _promptBox.Text = value ?? "";
        }
    }
    public bool Confirmed { get; private set; }

    internal TextBox PromptBox => _promptBox;
    internal Button ConfirmButton => _confirmButton;
    internal Button CancelButton => _cancelButton;

    public AgentPromptConfirmationDialog(IntPtr owner, string agentName, string promptText)
    {
        AgentName = string.IsNullOrWhiteSpace(agentName) ? "Agent" : agentName;
        _initialPrompt = promptText ?? "";

        if (owner != IntPtr.Zero)
        {
            try
            {
                new WindowInteropHelper(this).Owner = owner;
            }
            catch (Exception)
            {
                // Fallback for unmanaged or unshown owners per AGENTS.md
            }
        }

        Title = "Assign Prompt";
        Width = 468;
        Height = 324;
        MinWidth = 400;
        MinHeight = 300;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        ExtendsContentIntoTitleBar = true;
        WindowBackdropType = WindowBackdropType.Mica;
        WindowStartupLocation = owner == IntPtr.Zero ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;

        Resources.MergedDictionaries.Add(new ThemesDictionary { Theme = ApplicationTheme.Dark });
        Resources.MergedDictionaries.Add(new ControlsDictionary());
        Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse(DialogStyles));

        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        Background = MissionControlTheme.WindowBackground;
        Foreground = MissionControlTheme.TextPrimary;
        BorderBrush = MissionControlTheme.CardBorder;
        BorderThickness = new Thickness(1);

        var root = new DockPanel();
        var titleBar = new TitleBar
        {
            Title = Title,
            Height = 36,
            ShowMaximize = false,
            ShowMinimize = false,
            Foreground = MissionControlTheme.TextPrimary,
            ButtonsForeground = MissionControlTheme.TextSecondary
        };
        DockPanel.SetDock(titleBar, Dock.Top);
        root.Children.Add(titleBar);

        var body = new Grid
        {
            Margin = new Thickness(20, 6, 20, 18)
        };
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Header with agent badge, title, and concise subtitle
        var header = new DockPanel
        {
            Margin = new Thickness(0, 0, 0, 14)
        };

        var avatar = new Border
        {
            Width = 36,
            Height = 36,
            CornerRadius = new CornerRadius(10),
            Background = MissionControlTheme.AvatarBackground,
            BorderBrush = MissionControlTheme.Brush("#1D4ED8"),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new SymbolIcon
            {
                Symbol = SymbolRegular.Bot20,
                FontSize = 18,
                Foreground = MissionControlTheme.AccentBlue
            }
        };
        DockPanel.SetDock(avatar, Dock.Left);
        header.Children.Add(avatar);

        var headerText = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center
        };
        headerText.Children.Add(new TextBlock
        {
            Text = $"Send prompt to {AgentName}?",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = MissionControlTheme.TextPrimary,
            TextWrapping = TextWrapping.Wrap
        });
        headerText.Children.Add(new TextBlock
        {
            Text = "Review or edit the prompt before sending.",
            FontSize = 12,
            Foreground = MissionControlTheme.TextMuted,
            Margin = new Thickness(0, 2, 0, 0)
        });
        header.Children.Add(headerText);
        Grid.SetRow(header, 0);
        body.Children.Add(header);

        // Elevated dark prompt editor with explicit contrast colors
        _promptBox = new TextBox
        {
            Text = _initialPrompt,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = false,
            ClearButtonEnabled = false,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalContentAlignment = VerticalAlignment.Top,
            MinHeight = 88,
            Padding = new Thickness(12, 10, 12, 10),
            FontSize = 13,
            Background = MissionControlTheme.CardBackground,
            Foreground = MissionControlTheme.TextPrimary,
            CaretBrush = MissionControlTheme.AccentBlue,
            SelectionBrush = MissionControlTheme.Brush("#2563EB"),
            BorderBrush = MissionControlTheme.CardBorder,
            BorderThickness = new Thickness(1),
            PlaceholderText = "Enter prompt for agent..."
        };
        AutomationProperties.SetName(_promptBox, "Agent prompt");
        Grid.SetRow(_promptBox, 1);
        body.Children.Add(_promptBox);

        var shortcutHint = new TextBlock
        {
            Text = "Enter to send · Shift+Enter for new line · Esc to cancel",
            FontSize = 11.5,
            Foreground = MissionControlTheme.TextMuted,
            Margin = new Thickness(2, 8, 0, 12)
        };
        Grid.SetRow(shortcutHint, 2);
        body.Children.Add(shortcutHint);

        // Footer divider + right-aligned action bar
        var footer = new StackPanel();
        footer.Children.Add(new Border
        {
            Height = 1,
            Background = MissionControlTheme.Brush("#253349"),
            Margin = new Thickness(0, 0, 0, 12)
        });

        var buttonBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        _cancelButton = new Button
        {
            Content = "Cancel",
            Appearance = ControlAppearance.Secondary,
            Style = (Style)FindResource("PromptSecondaryButton"),
            MinWidth = 96,
            Padding = new Thickness(16, 8, 16, 8),
            FontSize = 13,
            Cursor = Cursors.Hand,
            IsCancel = true
        };
        AutomationProperties.SetName(_cancelButton, "Cancel");
        _cancelButton.Click += (_, _) => Dismiss();

        var confirmContent = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        confirmContent.Children.Add(new SymbolIcon
        {
            Symbol = SymbolRegular.Send16,
            FontSize = 14,
            Foreground = Brushes.White,
            Margin = new Thickness(0, 0, 7, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        confirmContent.Children.Add(new TextBlock
        {
            Text = "Send Prompt",
            Foreground = Brushes.White,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });

        _confirmButton = new Button
        {
            Content = confirmContent,
            Appearance = ControlAppearance.Primary,
            Style = (Style)FindResource("PromptPrimaryButton"),
            MinWidth = 136,
            Padding = new Thickness(16, 8, 16, 8),
            FontSize = 13,
            Margin = new Thickness(10, 0, 0, 0),
            Cursor = Cursors.Hand,
            IsDefault = true
        };
        AutomationProperties.SetName(_confirmButton, "Send Prompt");
        _confirmButton.Click += (_, _) => Confirm();

        buttonBar.Children.Add(_cancelButton);
        buttonBar.Children.Add(_confirmButton);
        footer.Children.Add(buttonBar);

        Grid.SetRow(footer, 3);
        body.Children.Add(footer);

        root.Children.Add(body);
        Content = root;

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
                {
                    if (_promptBox.IsKeyboardFocusWithin)
                    {
                        int caret = _promptBox.CaretIndex;
                        _promptBox.SelectedText = Environment.NewLine;
                        _promptBox.CaretIndex = caret + Environment.NewLine.Length;
                        e.Handled = true;
                        return;
                    }
                }
                e.Handled = true;
                Confirm();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Dismiss();
            }
        };

        Loaded += (_, _) =>
        {
            _promptBox.Focus();
            _promptBox.CaretIndex = _promptBox.Text.Length;
        };
    }

    private void Confirm()
    {
        Confirmed = true;
        try { DialogResult = true; }
        catch (InvalidOperationException) { }
        Close();
    }

    private void Dismiss()
    {
        Confirmed = false;
        try { DialogResult = false; }
        catch (InvalidOperationException) { }
        Close();
    }

    public static (bool Confirmed, string Prompt) ShowPromptConfirmation(IntPtr owner, string agentName, string promptText)
    {
        var dialog = new AgentPromptConfirmationDialog(owner, agentName, promptText);
        var result = dialog.ShowDialog();
        bool confirmed = result ?? dialog.Confirmed;
        return (confirmed, dialog.PromptText);
    }

    // Explicit dark-surface brush overrides and Fluent button templates prevent Windows light-theme
    // or uninitialized system-accent colors from bleeding into WinForms-hosted WPF modal dialogs.
    private const string DialogStyles = """
        <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                            xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                            xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml">
          <SolidColorBrush x:Key="TextControlBackground" Color="#1E293B"/>
          <SolidColorBrush x:Key="TextControlBackgroundPointerOver" Color="#243247"/>
          <SolidColorBrush x:Key="TextControlBackgroundFocused" Color="#162032"/>
          <SolidColorBrush x:Key="TextControlForeground" Color="#F8FAFC"/>
          <SolidColorBrush x:Key="TextControlForegroundPointerOver" Color="#F8FAFC"/>
          <SolidColorBrush x:Key="TextControlForegroundFocused" Color="#F8FAFC"/>
          <SolidColorBrush x:Key="TextControlPlaceholderForeground" Color="#64748B"/>
          <SolidColorBrush x:Key="TextControlPlaceholderForegroundPointerOver" Color="#94A3B8"/>
          <SolidColorBrush x:Key="TextControlPlaceholderForegroundFocused" Color="#64748B"/>
          <SolidColorBrush x:Key="TextControlBorderBrush" Color="#334155"/>
          <SolidColorBrush x:Key="TextControlBorderBrushPointerOver" Color="#475569"/>
          <SolidColorBrush x:Key="TextControlBorderBrushFocused" Color="#60A5FA"/>

          <Style x:Key="PromptSecondaryButton" TargetType="{x:Type ui:Button}">
            <Setter Property="Background" Value="#1E293B"/>
            <Setter Property="Foreground" Value="#E2E8F0"/>
            <Setter Property="BorderBrush" Value="#475569"/>
            <Setter Property="BorderThickness" Value="1"/>
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="{x:Type ui:Button}">
                  <Border x:Name="Chrome" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                          BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="6" Padding="{TemplateBinding Padding}">
                    <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center" RecognizesAccessKey="True"/>
                  </Border>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True">
                      <Setter TargetName="Chrome" Property="Background" Value="#334155"/>
                      <Setter TargetName="Chrome" Property="BorderBrush" Value="#64748B"/>
                      <Setter Property="Foreground" Value="#F8FAFC"/>
                    </Trigger>
                    <Trigger Property="IsPressed" Value="True">
                      <Setter TargetName="Chrome" Property="Background" Value="#0F172A"/>
                      <Setter TargetName="Chrome" Property="BorderBrush" Value="#475569"/>
                    </Trigger>
                    <Trigger Property="IsKeyboardFocused" Value="True">
                      <Setter TargetName="Chrome" Property="BorderBrush" Value="#93C5FD"/>
                      <Setter TargetName="Chrome" Property="BorderThickness" Value="2"/>
                    </Trigger>
                    <Trigger Property="IsEnabled" Value="False">
                      <Setter TargetName="Chrome" Property="Background" Value="#172031"/>
                      <Setter Property="Foreground" Value="#64748B"/>
                      <Setter TargetName="Chrome" Property="BorderBrush" Value="#334155"/>
                    </Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
          </Style>

          <Style x:Key="PromptPrimaryButton" TargetType="{x:Type ui:Button}" BasedOn="{StaticResource PromptSecondaryButton}">
            <Setter Property="Background" Value="#2563EB"/>
            <Setter Property="Foreground" Value="#FFFFFF"/>
            <Setter Property="BorderBrush" Value="#60A5FA"/>
            <Setter Property="FontWeight" Value="SemiBold"/>
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="{x:Type ui:Button}">
                  <Border x:Name="Chrome" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                          BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="6" Padding="{TemplateBinding Padding}">
                    <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center" RecognizesAccessKey="True"/>
                  </Border>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True">
                      <Setter TargetName="Chrome" Property="Background" Value="#3B82F6"/>
                      <Setter TargetName="Chrome" Property="BorderBrush" Value="#93C5FD"/>
                    </Trigger>
                    <Trigger Property="IsPressed" Value="True">
                      <Setter TargetName="Chrome" Property="Background" Value="#1D4ED8"/>
                      <Setter TargetName="Chrome" Property="BorderBrush" Value="#60A5FA"/>
                    </Trigger>
                    <Trigger Property="IsKeyboardFocused" Value="True">
                      <Setter TargetName="Chrome" Property="BorderBrush" Value="#FFFFFF"/>
                      <Setter TargetName="Chrome" Property="BorderThickness" Value="2"/>
                    </Trigger>
                    <Trigger Property="IsEnabled" Value="False">
                      <Setter TargetName="Chrome" Property="Background" Value="#1E3A5F"/>
                      <Setter Property="Foreground" Value="#94A3B8"/>
                      <Setter TargetName="Chrome" Property="BorderBrush" Value="#334155"/>
                    </Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
          </Style>
        </ResourceDictionary>
        """;
}
