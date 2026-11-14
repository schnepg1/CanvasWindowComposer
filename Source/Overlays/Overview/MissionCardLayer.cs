using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace CanvasDesktop;

internal sealed record MissionAgentCard(CanvasAnnotation Layout)
{
    public Guid Id => Layout.Id;
    public string TaskId => Layout.AgentTaskId;
    public string Role => Layout.AgentRole;
    public string Name => string.IsNullOrWhiteSpace(Layout.Text) ? Role : Layout.Text.Split('\n')[0].Trim();
    public CanvasRect Bounds => Layout.Bounds;

    public static bool IsCard(CanvasAnnotation item) =>
        item.AgentTaskId.Length != 0 && item.AgentRole.Length != 0 && item.Kind != AnnotationKind.Arrow && !item.InBackground;
}

internal sealed class MissionCardLayer(Func<IEnumerable<MissionAgentCard>> cards,
    Func<MissionAgentCard, AgentStatusReport?> status, Func<Guid, string?> stateOverride) : IOverviewAnnotationLayer
{
    private static readonly Brush Surface = Frozen(Color.FromRgb(25, 32, 46));
    private static readonly Brush Secondary = Frozen(Color.FromRgb(180, 193, 216));
    private static readonly Pen Outline = new(Frozen(Color.FromRgb(98, 84, 143)), 1);
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
            double radius = Math.Min(8, Math.Min(rect.Width / 2, rect.Height / 2));
            dc.DrawRoundedRectangle(Surface, Outline, rect, radius, radius);
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
            var title = context.Text(card.Name, 14, Brushes.White);
            title.MaxTextWidth = rect.Width - 22;
            title.MaxTextHeight = 19;
            title.Trimming = TextTrimming.CharacterEllipsis;
            dc.DrawText(title, new Point(rect.X + 11, rect.Y + 8));

            var reported = status(card);
            var report = reported ?? new(card.Role, AgentExecutionState.Idle);
            var state = stateOverride(card.Id) ?? (reported == null ? "No status reported" : null);
            if (rect.Height >= 70)
            {
                // Bucket widths so camera zoom doesn't rebuild controls every frame.
                double width = Math.Min(248, Math.Floor((rect.Width - 22) / 16) * 16);
                if (!_pills.TryGetValue(card.Id, out var cached) || cached.Report.State != report.State
                    || cached.Report.Detail != report.Detail || cached.Width != width || cached.State != state)
                    _pills[card.Id] = cached = (report, width, state, new(report, width, state));
                dc.DrawRectangle(cached.Pill.Brush, null, new Rect(rect.X + 11, rect.Y + 33, width, 28));
            }
            else
                dc.DrawEllipse(Frozen(TelemetryStatusPill.ColorFor(report.State)), null, new Point(rect.Right - 10, rect.Bottom - 10), 3, 3);
            if (rect.Height >= 96)
                dc.DrawText(context.Text("Open agent", 12, Secondary), new Point(rect.X + 11, rect.Y + 71));
            dc.Pop();
        }
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
