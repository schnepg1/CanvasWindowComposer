using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Wpf.Ui.Markup;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace CanvasDesktop;

/// <summary>
/// A small Fluent WPF confirmation popup shown when dropping a text card onto an agent card.
/// Pressing Enter confirms the prompt; Escape cancels.
/// </summary>
internal sealed class AgentPromptConfirmationDialog : FluentWindow
{
    private readonly Button _confirmButton;
    private readonly Button _cancelButton;

    public string AgentName { get; }
    public string PromptText { get; }
    public bool Confirmed { get; private set; }

    public AgentPromptConfirmationDialog(IntPtr owner, string agentName, string promptText)
    {
        AgentName = string.IsNullOrWhiteSpace(agentName) ? "Agent" : agentName;
        PromptText = promptText ?? "";

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
        Width = 420;
        Height = 250;
        MinWidth = 360;
        MinHeight = 220;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        ExtendsContentIntoTitleBar = true;
        WindowStartupLocation = owner == IntPtr.Zero ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;

        Resources.MergedDictionaries.Add(new ThemesDictionary { Theme = ApplicationTheme.Dark });
        Resources.MergedDictionaries.Add(new ControlsDictionary());
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        Background = new SolidColorBrush(Color.FromRgb(24, 30, 42));
        Foreground = Brushes.White;

        var root = new DockPanel();
        var titleBar = new TitleBar
        {
            Title = Title,
            ShowMaximize = false,
            ShowMinimize = false
        };
        DockPanel.SetDock(titleBar, Dock.Top);
        root.Children.Add(titleBar);

        var body = new StackPanel
        {
            Margin = new Thickness(20, 8, 20, 16)
        };

        var heading = new TextBlock
        {
            Text = $"Send prompt to {AgentName}?",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        };
        body.Children.Add(heading);

        var previewBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(26, 37, 56)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(71, 85, 105)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8, 10, 8),
            MaxHeight = 85
        };

        var scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new TextBlock
            {
                Text = PromptText,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(226, 232, 240)),
                FontSize = 13
            }
        };
        previewBorder.Child = scroller;
        body.Children.Add(previewBorder);

        var buttonGrid = new System.Windows.Controls.Primitives.UniformGrid
        {
            Columns = 2,
            Margin = new Thickness(0, 14, 0, 0)
        };

        _confirmButton = new Button
        {
            Content = "Send Prompt",
            Appearance = ControlAppearance.Primary,
            Margin = new Thickness(0, 0, 6, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsDefault = true
        };
        _confirmButton.Click += (_, _) => Confirm();

        _cancelButton = new Button
        {
            Content = "Cancel",
            Margin = new Thickness(6, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsCancel = true
        };
        _cancelButton.Click += (_, _) => Dismiss();

        buttonGrid.Children.Add(_confirmButton);
        buttonGrid.Children.Add(_cancelButton);
        body.Children.Add(buttonGrid);

        root.Children.Add(body);
        Content = root;

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
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
            _confirmButton.Focus();
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

    public static bool? ShowPromptConfirmation(IntPtr owner, string agentName, string promptText)
    {
        var dialog = new AgentPromptConfirmationDialog(owner, agentName, promptText);
        var result = dialog.ShowDialog();
        return result ?? (dialog.Confirmed ? true : false);
    }
}
