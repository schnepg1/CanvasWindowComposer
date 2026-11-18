using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using CanvasDesktop.CanvasView;

namespace CanvasDesktop;

internal sealed record MissionAgentCard(CanvasAnnotation Layout)
{
    public Guid Id => Layout.Id;
    public string TaskId => Layout.AgentTaskId;
    public string Role => Layout.AgentRole;
    public string Name => string.IsNullOrWhiteSpace(Layout.Text) ? Role : Layout.Text.Split('\n')[0].Trim();
    public CanvasRect Bounds => Layout.Bounds;
    public string? Prompt => Layout.AgentPrompt;
    public string? WorkingFolder => Layout.AgentWorkingFolder;

    public static bool IsCard(CanvasAnnotation item) =>
        item.AgentTaskId.Length != 0 && item.AgentRole.Length != 0 && item.Kind != AnnotationKind.Arrow && !item.InBackground;
}

internal static class MissionCardLayout
{
    public static Rect AvatarRect(Rect card) =>
        new(card.X + 10, card.Y + 10, 26, 26);

    public static Rect PlayStopRect(Rect card) =>
        new(card.Right - 106, card.Y + 11, 44, 22);

    public static Rect ConsoleToggleRect(Rect card) =>
        new(card.Right - 56, card.Y + 11, 22, 22);

    public static Rect CloseRect(Rect card) =>
        new(card.Right - 30, card.Y + 11, 22, 22);

    public static Rect TelemetryRect(Rect card) =>
        new(card.X + 10, card.Y + 42, Math.Max(20, card.Width - 20), 26);

    public static Rect EnvironmentRect(Rect card) =>
        new(card.X + 10, card.Y + 74, Math.Max(20, card.Width - 20), 24);

    public static Rect TerminalDrawerRect(Rect card) =>
        new(card.X + 10, card.Y + 108, Math.Max(20, card.Width - 20), Math.Max(10, card.Height - 118));

    public static CanvasRect TerminalDrawerRect(CanvasRect card) =>
        new(card.X + 10, card.Y + 108, Math.Max(20, card.Width - 20), Math.Max(10, card.Height - 118));
}

internal sealed class MissionCardLayer(
    Func<IEnumerable<MissionAgentCard>> cards,
    Func<MissionAgentCard, AgentStatusReport?> status,
    Func<Guid, string?> stateOverride,
    Func<MissionAgentCard, string?>? resolveFolder = null,
    Func<Guid, bool>? isLive = null,
    Func<Guid, HerdrSessions.Terminal?>? getTerminal = null) : IOverviewAnnotationLayer
{
    private static readonly Brush Surface = Frozen(Color.FromRgb(15, 23, 42)); // #0F172A
    private static readonly Brush CardRowBg = Frozen(Color.FromRgb(30, 41, 59)); // #1E293B
    private static readonly Brush TerminalBg = Frozen(Color.FromRgb(10, 15, 29)); // #0A0F1D
    private static readonly Brush TextMuted = Frozen(Color.FromRgb(148, 163, 184)); // #94A3B8
    private static readonly Brush TextFeedback = Frozen(Color.FromRgb(203, 213, 225)); // #CBD5E1
    private static readonly Pen BorderSubtle = FrozenPen(Color.FromRgb(51, 65, 85), 1); // #334155
    private static readonly Brush CloseButtonBg = Frozen(Color.FromRgb(42, 18, 21)); // #2A1215
    private static readonly Pen CloseButtonBorder = FrozenPen(Color.FromRgb(127, 29, 29), 1); // #7F1D1D
    private static readonly Pen CloseButtonGlyph = FrozenPen(Color.FromRgb(252, 165, 165), 1.4); // #FCA5A5

    private readonly Dictionary<Guid, (AgentStatusReport Report, double Width, string? State, TelemetryStatusPill Pill)> _pills = [];
    public bool HasAnimatedContent => false;

    public static bool HasCardControls(double width, double height) => width >= 104 && height >= 44;

    public void Draw(DrawingContext dc, OverviewAnnotationContext context)
    {
        var items = cards().ToArray();
        if (_pills.Count > items.Length)
        {
            var ids = items.Select(card => card.Id).ToHashSet();
            foreach (var id in _pills.Keys.Where(id => !ids.Contains(id)).ToArray()) _pills.Remove(id);
        }

        foreach (var card in items)
        {
            var bounds = context.View.WorldToDip(card.Bounds);
            var rect = new Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height);
            if (!rect.IntersectsWith(new Rect(context.Size)) || rect.Width < 6 || rect.Height < 4) continue;

            var agent = HerdrSessions.FindAgent(card.Role);
            var accentColor = (Color)ColorConverter.ConvertFromString(agent.AccentColorHex);
            var borderPen = FrozenPen(accentColor, 1.5);

            // Satellite Pill Rendering (Compact subagent card)
            if (card.Bounds.Height <= 60)
            {
                DrawSatellitePill(dc, context, card, rect, agent, accentColor, borderPen);
                continue;
            }

            double radius = Math.Min(8, Math.Min(rect.Width / 2, rect.Height / 2));
            dc.DrawRoundedRectangle(Surface, borderPen, rect, radius, radius);

            if (!HasCardControls(rect.Width, rect.Height))
            {
                if (rect.Width >= 28 && rect.Height >= 14)
                {
                    var mini = context.Text(card.Name, Math.Max(8, Math.Min(12, rect.Height - 6)), Brushes.White);
                    mini.MaxTextWidth = Math.Max(1, rect.Width - 8);
                    mini.MaxTextHeight = Math.Max(1, rect.Height - 4);
                    mini.Trimming = TextTrimming.CharacterEllipsis;
                    dc.DrawText(mini, new Point(rect.X + 4, rect.Y + 2));
                }
                continue;
            }

            dc.PushClip(new RectangleGeometry(rect, 8, 8));

            // 1. Profile Avatar Badge (Circle)
            var avatarRect = MissionCardLayout.AvatarRect(rect);
            var avatarBg = Frozen(Color.FromArgb(45, accentColor.R, accentColor.G, accentColor.B));
            var avatarPen = FrozenPen(accentColor, 1.2);
            dc.DrawRoundedRectangle(avatarBg, avatarPen, avatarRect, 13, 13);

            string initial = agent.Id switch
            {
                "codex" => "C",
                "claude" => "CC",
                "gemini" => "G",
                "aider" => "A",
                "opencode" => "O",
                _ => agent.DisplayName.Length > 0 ? agent.DisplayName.Substring(0, 1) : "A"
            };
            var initText = context.Text(initial, 10, Brushes.White);
            initText.Trimming = TextTrimming.None;
            double initX = avatarRect.X + (avatarRect.Width - initText.Width) / 2.0;
            double initY = avatarRect.Y + (avatarRect.Height - initText.Height) / 2.0;
            dc.DrawText(initText, new Point(initX, initY));

            // 2. Title / Name
            var title = context.Text(card.Name, 13, Brushes.White);
            title.MaxTextWidth = Math.Max(10, rect.Width - 160);
            title.MaxTextHeight = 20;
            title.Trimming = TextTrimming.CharacterEllipsis;
            dc.DrawText(title, new Point(rect.X + 40, rect.Y + 12));

            // 3. Play / Stop Button
            bool live = isLive?.Invoke(card.Id) ?? false;
            var playRect = MissionCardLayout.PlayStopRect(rect);
            var playBg = live ? Frozen(Color.FromRgb(127, 29, 29)) : Frozen(Color.FromRgb(6, 78, 59));
            var playBorder = live ? FrozenPen(Color.FromRgb(239, 68, 68), 1) : FrozenPen(Color.FromRgb(16, 185, 129), 1);
            dc.DrawRoundedRectangle(playBg, playBorder, playRect, 4, 4);
            var playText = context.Text(live ? "■ Stop" : "▶ Start", 10, Brushes.White);
            playText.MaxTextWidth = playRect.Width;
            playText.Trimming = TextTrimming.None;
            double playTextX = playRect.X + (playRect.Width - playText.Width) / 2.0;
            double playTextY = playRect.Y + (playRect.Height - playText.Height) / 2.0;
            dc.DrawText(playText, new Point(playTextX, playTextY));

            // 4. Console Toggle Button
            var consoleRect = MissionCardLayout.ConsoleToggleRect(rect);
            dc.DrawRoundedRectangle(CardRowBg, BorderSubtle, consoleRect, 4, 4);
            var consoleText = context.Text(">_", 10, Brushes.White);
            double consoleTextX = consoleRect.X + (consoleRect.Width - consoleText.Width) / 2.0;
            double consoleTextY = consoleRect.Y + (consoleRect.Height - consoleText.Height) / 2.0;
            dc.DrawText(consoleText, new Point(consoleTextX, consoleTextY));

            // 5. Close Button (top-right)
            var closeRect = MissionCardLayout.CloseRect(rect);
            dc.DrawRoundedRectangle(CloseButtonBg, CloseButtonBorder, closeRect, 4, 4);
            double closeInset = Math.Min(6.0, closeRect.Width / 4);
            dc.DrawLine(CloseButtonGlyph,
                new Point(closeRect.X + closeInset, closeRect.Y + closeInset),
                new Point(closeRect.Right - closeInset, closeRect.Bottom - closeInset));
            dc.DrawLine(CloseButtonGlyph,
                new Point(closeRect.Right - closeInset, closeRect.Y + closeInset),
                new Point(closeRect.X + closeInset, closeRect.Bottom - closeInset));

            // 6. Telemetry Status Pill
            var reported = status(card);
            var report = reported ?? new(card.Role, AgentExecutionState.Idle);
            var state = stateOverride(card.Id) ?? (reported == null ? "No status reported" : null);
            if (rect.Height >= 65)
            {
                double width = Math.Min(248, Math.Floor((rect.Width - 20) / 16) * 16);
                var pillRect = MissionCardLayout.TelemetryRect(rect);
                try
                {
                    if (!_pills.TryGetValue(card.Id, out var cached) || cached.Report.State != report.State
                        || cached.Report.Detail != report.Detail || cached.Width != width || cached.State != state)
                        _pills[card.Id] = cached = (report, width, state, new(report, width, state));
                    dc.DrawRectangle(cached.Pill.Brush, null, new Rect(pillRect.X, pillRect.Y, width, 24));
                }
                catch
                {
                    dc.DrawRoundedRectangle(CardRowBg, BorderSubtle, new Rect(pillRect.X, pillRect.Y, width, 24), 4, 4);
                    var fallbackText = context.Text("● " + (state ?? report.State.ToString()), 10, Brushes.White);
                    dc.DrawText(fallbackText, new Point(pillRect.X + 6, pillRect.Y + 4));
                }
            }
            else
            {
                dc.DrawEllipse(Frozen(TelemetryStatusPill.ColorFor(report.State)), null, new Point(rect.Right - 10, rect.Bottom - 10), 3, 3);
            }

            // 7. Environment Pill (Git worktree / repo info)
            if (rect.Height >= 95)
            {
                string? folder = resolveFolder?.Invoke(card) ?? card.WorkingFolder;
                string envLabel = GitWorktreeHelper.FormatEnvironmentLabel(folder);
                var envRect = MissionCardLayout.EnvironmentRect(rect);
                double envWidth = Math.Min(rect.Width - 20, 256);
                dc.DrawRoundedRectangle(CardRowBg, BorderSubtle, new Rect(envRect.X, envRect.Y, envWidth, 22), 4, 4);
                var envText = context.Text(envLabel, 10, TextFeedback);
                envText.MaxTextWidth = Math.Max(10, envWidth - 10);
                envText.MaxTextHeight = 18;
                envText.Trimming = TextTrimming.CharacterEllipsis;
                dc.DrawText(envText, new Point(envRect.X + 6, envRect.Y + 3));
            }

            // 8. Expanded Live Console Drawer
            if (rect.Height >= 180)
            {
                var drawerRect = MissionCardLayout.TerminalDrawerRect(rect);
                dc.DrawRoundedRectangle(TerminalBg, BorderSubtle, drawerRect, 4, 4);

                var terminalHeader = context.Text(">_ Live Terminal Console", 10, TextMuted);
                dc.DrawText(terminalHeader, new Point(drawerRect.X + 8, drawerRect.Y + 6));

                var term = getTerminal?.Invoke(card.Id);
                string sessionLine = term is { IsLive: true }
                    ? $"Address: {term.Address}  State: {term.AgentState ?? term.State}"
                    : "[Console offline. Click ▶ Start or use right-click menu to run]";
                var sessionText = context.Text(sessionLine, 9, live ? Frozen(Color.FromRgb(167, 243, 208)) : TextMuted);
                sessionText.MaxTextWidth = Math.Max(10, drawerRect.Width - 16);
                sessionText.Trimming = TextTrimming.CharacterEllipsis;
                dc.DrawText(sessionText, new Point(drawerRect.X + 8, drawerRect.Y + 24));

                if (!string.IsNullOrWhiteSpace(card.Prompt))
                {
                    var promptText = context.Text($"Prompt: {card.Prompt}", 9, TextMuted);
                    promptText.MaxTextWidth = Math.Max(10, drawerRect.Width - 16);
                    promptText.Trimming = TextTrimming.CharacterEllipsis;
                    dc.DrawText(promptText, new Point(drawerRect.X + 8, drawerRect.Y + 40));
                }
            }

            dc.Pop();
        }
    }

    private void DrawSatellitePill(
        DrawingContext dc,
        OverviewAnnotationContext context,
        MissionAgentCard card,
        Rect rect,
        AgentProfile agent,
        Color accentColor,
        Pen borderPen)
    {
        double pillRadius = Math.Min(rect.Height / 2, 12);
        dc.DrawRoundedRectangle(Surface, borderPen, rect, pillRadius, pillRadius);

        if (rect.Width < 20 || rect.Height < 10) return;

        // 1. Small avatar circle with initials
        double avatarSize = Math.Min(22, Math.Max(12, rect.Height - 8));
        var avatarRect = new Rect(rect.X + 6, rect.Y + (rect.Height - avatarSize) / 2, avatarSize, avatarSize);
        var avatarBg = Frozen(Color.FromArgb(45, accentColor.R, accentColor.G, accentColor.B));
        var avatarPen = FrozenPen(accentColor, 1.0);
        dc.DrawRoundedRectangle(avatarBg, avatarPen, avatarRect, avatarSize / 2, avatarSize / 2);

        string initial = agent.Id switch
        {
            "codex" => "C",
            "claude" => "CC",
            "gemini" => "G",
            "aider" => "A",
            "opencode" => "O",
            _ => agent.DisplayName.Length > 0 ? agent.DisplayName.Substring(0, 1) : "A"
        };
        var initText = context.Text(initial, Math.Max(7, Math.Min(9, avatarSize - 4)), Brushes.White);
        initText.Trimming = TextTrimming.None;
        double initX = avatarRect.X + (avatarRect.Width - initText.Width) / 2.0;
        double initY = avatarRect.Y + (avatarRect.Height - initText.Height) / 2.0;
        dc.DrawText(initText, new Point(initX, initY));

        // 2. Telemetry status dot
        var reported = status(card);
        var report = reported ?? new(card.Role, AgentExecutionState.Idle);
        double dotRadius = Math.Min(4, Math.Max(2.5, (rect.Height - 16) / 4));
        var dotBrush = Frozen(TelemetryStatusPill.ColorFor(report.State));
        double dotCenterX = rect.Right - 10;
        double dotCenterY = rect.Y + rect.Height / 2;
        dc.DrawEllipse(dotBrush, null, new Point(dotCenterX, dotCenterY), dotRadius, dotRadius);

        // 3. Name text
        double textLeft = avatarRect.Right + 6;
        double textRight = dotCenterX - dotRadius - 6;
        if (textRight > textLeft + 8)
        {
            var title = context.Text(card.Name, Math.Max(8, Math.Min(11, rect.Height - 14)), Brushes.White);
            title.MaxTextWidth = textRight - textLeft;
            title.MaxTextHeight = Math.Max(8, rect.Height - 4);
            title.Trimming = TextTrimming.CharacterEllipsis;
            double textY = rect.Y + (rect.Height - title.Height) / 2;
            dc.DrawText(title, new Point(textLeft, textY));
        }
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Color color, double thickness)
    {
        var pen = new Pen(Frozen(color), thickness);
        pen.Freeze();
        return pen;
    }
}
