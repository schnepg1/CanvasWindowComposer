using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace CanvasDesktop;

/// <summary>Cache a real WPF-UI Badge as passive canvas artwork between telemetry updates.</summary>
internal sealed class TelemetryStatusPill
{
    internal Badge Badge { get; }
    internal string Label { get; }
    internal VisualBrush Brush { get; }
    public TelemetryStatusPill(AgentStatusReport report, double width)
    {
        Label = "● " + report.State.ToString().ToUpperInvariant()
            + (string.IsNullOrWhiteSpace(report.Detail) ? "" : " · " + report.Detail.Replace('\r', ' ').Replace('\n', ' '));
        var color = ColorFor(report.State);
        var styles = (ResourceDictionary)Application.LoadComponent(
            new Uri("/Wpf.Ui;component/Controls/Badge/Badge.xaml", UriKind.Relative));
        Badge = new Badge
        {
            Style = (Style)styles[typeof(Badge)], Appearance = ControlAppearance.Primary,
            Foreground = new SolidColorBrush(color), Background = new SolidColorBrush(Color.FromRgb(19, 29, 43)),
            BorderBrush = new SolidColorBrush(color), Padding = new Thickness(9, 4, 9, 4),
            Width = width, Height = 28, IsHitTestVisible = false,
            Content = new TextBlock { Text = Label, FontSize = 12, FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Segoe UI"), TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap, MaxWidth = Math.Max(1, width - 20) }
        };
        AutomationProperties.SetName(Badge, Label);
        Badge.ApplyTemplate();
        Badge.Measure(new Size(width, 28));
        Badge.Arrange(new Rect(0, 0, width, 28));
        Badge.UpdateLayout();
        // WPF requires brushes that reference a visual to stay unfrozen.
        Brush = new VisualBrush(Badge) { Stretch = Stretch.None };
    }

    public static Color ColorFor(AgentExecutionState state) => state switch
    {
        AgentExecutionState.Working => Color.FromRgb(56, 189, 248),
        AgentExecutionState.Testing => Color.FromRgb(245, 158, 11),
        AgentExecutionState.Blocked => Color.FromRgb(251, 146, 60),
        AgentExecutionState.Done => Color.FromRgb(16, 185, 129),
        AgentExecutionState.Error => Color.FromRgb(239, 68, 68),
        _ => Color.FromRgb(148, 163, 184)
    };
}
