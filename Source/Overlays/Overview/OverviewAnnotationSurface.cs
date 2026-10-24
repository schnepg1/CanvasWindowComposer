using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace CanvasDesktop;

/// <summary>Detached scene data. Images are borrowed immutable snapshots, never owned by painters.</summary>
internal sealed record OverviewAnnotationSnapshot(
    IReadOnlyList<ZoneView> Zones, double CameraX, double CameraY, double Zoom, ScreenRect Screen,
    WorldRect? Preview, bool Drawing, IReadOnlyList<WorldRect> Ghosts,
    IReadOnlyList<PlaceholderAnnotation> Placeholders, bool ShowHints, StartupIntroFrame? Intro, bool ShowIntroTitle, string? ToolHint = null)
{
    public string WindowStats { get; init; } = "Active: 0  ·  Minimized: 0 (auto: 0)";
    public string MinimizationStatus { get; init; } = "Auto-minimize: off";

    public static OverviewAnnotationSnapshot Create(IReadOnlyList<ZoneView> zones,
        double x, double y, double zoom, System.Drawing.Rectangle screen, WorldRect? preview, bool drawing,
        IReadOnlyList<WorldRect>? ghosts = null, IReadOnlyList<PlaceholderAnnotation>? placeholders = null,
        bool showHints = true, StartupIntroFrame? intro = null, bool showIntroTitle = false, string? toolHint = null)
        => new(zones.ToArray(), x, y, zoom, new(screen.X, screen.Y, screen.Width, screen.Height),
            preview, drawing, ghosts?.ToArray() ?? Array.Empty<WorldRect>(),
            placeholders?.ToArray() ?? Array.Empty<PlaceholderAnnotation>(), showHints, intro, showIntroTitle, toolHint);
}

internal readonly record struct OverviewAnnotationContext(
    OverviewAnnotationSnapshot Frame, CanvasViewTransform View, DpiScale Dpi, Size Size)
{
    public Rect Project(WorldRect b)
    {
        var rect = View.WorldToDip(new CanvasRect(b.X, b.Y, b.W, b.H));
        return new(rect.X, rect.Y, Math.Max(0, rect.Width), Math.Max(0, rect.Height));
    }

    public FormattedText Text(string value, double size, Brush brush)
        => new(value, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), size, brush, Dpi.PixelsPerDip);
}

internal interface IOverviewAnnotationLayer
{
    void Draw(DrawingContext dc, OverviewAnnotationContext context);
}

/// <summary>
/// One passive WPF surface above DWM: placeholders, areas, optional drawing layers,
/// then screen-space HUD. Native ownership and input stay outside the painters.
/// </summary>
internal sealed class OverviewAnnotationSurface : FrameworkElement
{
    private readonly PlaceholderAnnotationLayer _placeholders = new();
    private readonly ZoneAnnotationLayer _zones = new();
    private readonly OverviewHudLayer _hud = new();
    private IReadOnlyList<IOverviewAnnotationLayer> _drawingLayers = Array.Empty<IOverviewAnnotationLayer>();
    private OverviewAnnotationSnapshot? _frame;

    public void SetDrawingLayers(IReadOnlyList<IOverviewAnnotationLayer> layers)
    {
        _drawingLayers = layers.ToArray();
        InvalidateVisual();
    }

    public void Update(OverviewAnnotationSnapshot frame)
    {
        _frame = frame;
        Opacity = frame.Intro?.RevealOpacity ?? 1;
        _placeholders.Clear();
        InvalidateVisual();
    }

    public IntPtr HitPlaceholderLabel(int x, int y)
    {
        if (_frame == null) return IntPtr.Zero;
        var dpi = VisualTreeHelper.GetDpi(this);
        var view = View(_frame, dpi);
        var point = view.LocalToDip(new CanvasPoint(x, y));
        var world = view.LocalToWorld(new CanvasPoint(x, y));
        foreach (var label in _placeholders.Labels)
            if (label.Rect.Contains(new Point(point.X, point.Y))
                && (label.Card.Occluders == null || !label.Card.Occluders.Any(b =>
                    world.X >= b.X && world.X < b.X + b.W && world.Y >= b.Y && world.Y < b.Y + b.H)))
                return label.Card.Handle;
        return IntPtr.Zero;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (_frame == null) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        var context = new OverviewAnnotationContext(_frame, View(_frame, dpi), dpi, RenderSize);
        dc.PushClip(new RectangleGeometry(new Rect(RenderSize)));
        _placeholders.Draw(dc, context);
        _zones.Draw(dc, context);
        foreach (var layer in _drawingLayers) layer.Draw(dc, context);
        _hud.Draw(dc, context);
        dc.Pop();
    }

    private static CanvasViewTransform View(OverviewAnnotationSnapshot frame, DpiScale dpi)
        => new(frame.CameraX, frame.CameraY, frame.Zoom,
            new(frame.Screen.X, frame.Screen.Y, frame.Screen.Width, frame.Screen.Height), dpi.DpiScaleX, dpi.DpiScaleY);
}

internal sealed class OverviewHudLayer : IOverviewAnnotationLayer
{
    private static readonly Brush HudFill = FrozenBrush(Color.FromArgb(240, 23, 29, 41));
    private static readonly Brush ZoomFill = FrozenBrush(Color.FromArgb(240, 10, 16, 25));

    private static Brush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public void Draw(DrawingContext dc, OverviewAnnotationContext context)
    {
        if (context.Frame.Intro is { } intro && context.Frame.ShowIntroTitle)
            StartupIntroTitle.Draw(dc, context.Size, context.Dpi, intro);
        if (!context.Frame.ShowHints) return;
        var zoomText = context.Text($"{Math.Round(context.View.Zoom * 100):0}%", 13, Brushes.White);
        dc.DrawRoundedRectangle(ZoomFill, null, new Rect(20, 20, zoomText.Width + 22, 32), 8, 8);
        dc.DrawText(zoomText, new Point(31, 27));
        var text = context.Text(context.Frame.WindowStats + "\n" + context.Frame.MinimizationStatus, 13, Brushes.White);
        dc.DrawRoundedRectangle(HudFill, null, new Rect(20, 62, text.Width + 28, text.Height + 16), 10, 10);
        dc.DrawText(text, new Point(34, 70));
        if (!string.IsNullOrWhiteSpace(context.Frame.ToolHint))
        {
            var status = context.Text(context.Frame.ToolHint, 13, Brushes.White);
            double y = 62 + text.Height + 24;
            dc.DrawRoundedRectangle(HudFill, null, new Rect(20, y, status.Width + 28, status.Height + 16), 10, 10);
            dc.DrawText(status, new Point(34, y + 8));
        }
    }
}
