using System;
using System.Linq;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace CanvasDesktop;

using CanvasDesktop.CanvasView;

internal sealed class CanvasAnnotationLayer(AnnotationDocument document, AnnotationCanvasTool tool, Func<Guid> desktop, Func<bool> visible, Func<Guid, bool>? hidden = null, Func<IReadOnlyList<CanvasRect>>? windowOccluders = null, Func<CanvasAnnotation?>? connectionPreview = null, Func<CanvasAnnotation, AgentStatusReport?>? telemetryState = null) : IOverviewAnnotationLayer
{
    public bool HasAnimatedContent => visible() && document.Items.Any(item => item.DesktopId == desktop()
        && item.Kind != AnnotationKind.Arrow && !item.InBackground
        && telemetryState?.Invoke(item) is { State: not AgentExecutionState.Idle });
    private readonly Dictionary<Guid, (AgentExecutionState State, string Detail, double Width, TelemetryStatusPill Pill)> _pills = [];
    private readonly Dictionary<Guid,(CanvasAnnotation Item,double Dpi,DrawingGroup Drawing)> _artwork = [];
    private long _revision = -1;
    private DrawingGroup Artwork(CanvasAnnotation item,double dpi,bool transient)
    {
        if (!transient && _artwork.TryGetValue(item.Id,out var cached) && cached.Item==item && cached.Dpi==dpi) return cached.Drawing;
        var drawing=new DrawingGroup();
        using (var dc=drawing.Open()) AnnotationVisual.Draw(dc,item,dpi);
        drawing.Freeze();
        if (!transient) _artwork[item.Id]=(item,dpi,drawing);
        return drawing;
    }

    public void Draw(DrawingContext dc, OverviewAnnotationContext context)
    {
        if (!visible()) return;
        // Camera movement only changes transforms; keep expensive text layout out of that path.
        if (_revision != document.Revision) { _artwork.Clear(); _pills.Clear(); _revision=document.Revision; }
        var preview = tool.Preview;
        var conn = connectionPreview?.Invoke();
        var items = document.Items.Where(a => a.DesktopId == desktop() && a.Id != preview?.Id && hidden?.Invoke(a.Id) != true).ToList();
        if (preview != null && preview.DesktopId == desktop()) items.Add(preview);
        if (conn != null && conn.DesktopId == desktop()) items.Add(conn);
        foreach (var item in items)
        {
            var projected = context.View.WorldToDip(item.Bounds);
            var rect = new Rect(projected.X,projected.Y,projected.Width,projected.Height);
            if (!new Rect(-32,-32,context.Size.Width+64,context.Size.Height+64).IntersectsWith(rect)) continue;
            bool pushedClip = false;
            if (item.InBackground && windowOccluders?.Invoke() is { } occluders)
            {
                List<Rect>? intersecting = null;
                foreach (var w in occluders)
                {
                    if (CanvasSelection.Intersects(item.Bounds, w))
                    {
                        var wDip = context.View.WorldToDip(w);
                        var wRect = new Rect(wDip.X, wDip.Y, Math.Max(0, wDip.Width), Math.Max(0, wDip.Height));
                        if (wRect.IntersectsWith(rect))
                        {
                            intersecting ??= new();
                            intersecting.Add(wRect);
                        }
                    }
                }
                if (intersecting != null && intersecting.Count > 0)
                {
                    Geometry clip = new RectangleGeometry(rect);
                    foreach (var wRect in intersecting)
                    {
                        clip = new CombinedGeometry(GeometryCombineMode.Exclude, clip, new RectangleGeometry(wRect));
                    }
                    dc.PushClip(clip);
                    pushedClip = true;
                }
            }

            // Scale the whole artwork together, including type, inset, stroke and arrowheads.
            dc.PushTransform(new TranslateTransform(rect.X,rect.Y));
            dc.PushTransform(new ScaleTransform(context.View.Zoom/context.Dpi.DpiScaleX,context.View.Zoom/context.Dpi.DpiScaleY));
            dc.DrawDrawing(Artwork(item,context.Dpi.PixelsPerDip,item.Id==preview?.Id));
            var report = item.Kind != AnnotationKind.Arrow && !item.InBackground ? telemetryState?.Invoke(item) : null;
            if (report != null && (report.State != AgentExecutionState.Idle || item.AgentRole.Length > 0) && item.Bounds.Width >= 80)
            {
                double width = item.Bounds.Width - 16;
                if (!_pills.TryGetValue(item.Id, out var cached) || cached.State != report.State || cached.Detail != report.Detail || cached.Width != width)
                    _pills[item.Id] = cached = (report.State, report.Detail, width, new TelemetryStatusPill(report, width));
                dc.DrawRectangle(cached.Pill.Brush, null, new Rect(8, item.AgentRole.Length > 0 ? 8 : -32, width, 28));
            }
            dc.Pop(); dc.Pop();
            if (pushedClip) dc.Pop();

            // Telemetry Glow HUD
            if (report is { State: not AgentExecutionState.Idle })
            {
                DrawTelemetryGlow(dc, rect, report.State, context);
            }

            if (tool.Mode == AnnotationMode.Select && item.Id == tool.SelectedId)
            {
                dc.DrawRectangle(null,new Pen(Brushes.DeepSkyBlue,2),rect);
                double half = AnnotationGeometry.HandleSize/2;
                foreach (var handle in AnnotationGeometry.Handles(item))
                {
                    var p = context.View.LocalToDip(context.View.WorldToLocal(handle));
                    dc.DrawRoundedRectangle(Brushes.White,new Pen(Brushes.DodgerBlue,2),new Rect(p.X-half,p.Y-half,half*2,half*2),2,2);
                }
            }
        }
    }

    private static void DrawTelemetryGlow(DrawingContext dc, Rect rect, AgentExecutionState state, OverviewAnnotationContext context)
    {
        Color glowColor = state switch
        {
            AgentExecutionState.Working => Color.FromRgb(56, 189, 248),   // Cyan / Blue (#38BDF8)
            AgentExecutionState.Testing => Color.FromRgb(245, 158, 11),   // Amber (#F59E0B)
            AgentExecutionState.Blocked => Color.FromRgb(251, 146, 60),   // Orange (#FB923C)
            AgentExecutionState.Done => Color.FromRgb(16, 185, 129),      // Emerald Green (#10B981)
            AgentExecutionState.Error => Color.FromRgb(239, 68, 68),      // Red (#EF4444)
            _ => Colors.Transparent
        };

        if (glowColor == Colors.Transparent) return;

        double pulse = context.ReduceMotion || context.MotionActive ? 0.7 : 0.7 + 0.3 * Math.Sin(context.Timestamp / 220.0);

        byte outerAlpha = (byte)(35 * pulse);
        byte midAlpha = (byte)(80 * pulse);
        byte innerAlpha = (byte)(180 * pulse);

        var outerPen = new Pen(new SolidColorBrush(Color.FromArgb(outerAlpha, glowColor.R, glowColor.G, glowColor.B)), 8) { LineJoin = PenLineJoin.Round };
        var midPen = new Pen(new SolidColorBrush(Color.FromArgb(midAlpha, glowColor.R, glowColor.G, glowColor.B)), 4) { LineJoin = PenLineJoin.Round };
        var innerPen = new Pen(new SolidColorBrush(Color.FromArgb(innerAlpha, glowColor.R, glowColor.G, glowColor.B)), 1.5) { LineJoin = PenLineJoin.Round };
        outerPen.Freeze(); midPen.Freeze(); innerPen.Freeze();

        dc.DrawRoundedRectangle(null, outerPen, new Rect(rect.X - 3, rect.Y - 3, rect.Width + 6, rect.Height + 6), 8, 8);
        dc.DrawRoundedRectangle(null, midPen, new Rect(rect.X - 1.5, rect.Y - 1.5, rect.Width + 3, rect.Height + 3), 7, 7);
        dc.DrawRoundedRectangle(null, innerPen, rect, 6, 6);

    }
}
