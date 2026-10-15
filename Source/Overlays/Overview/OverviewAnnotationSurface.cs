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
    IReadOnlyList<PlaceholderAnnotation> Placeholders, bool ShowHints, StartupIntroFrame? Intro, bool ShowIntroTitle)
{
    public static OverviewAnnotationSnapshot Create(IReadOnlyList<ZoneView> zones,
        double x, double y, double zoom, System.Drawing.Rectangle screen, WorldRect? preview, bool drawing,
        IReadOnlyList<WorldRect>? ghosts = null, IReadOnlyList<PlaceholderAnnotation>? placeholders = null,
        bool showHints = true, StartupIntroFrame? intro = null, bool showIntroTitle = false)
        => new(zones.ToArray(), x, y, zoom, new(screen.X, screen.Y, screen.Width, screen.Height),
            preview, drawing, ghosts?.ToArray() ?? Array.Empty<WorldRect>(),
            placeholders?.ToArray() ?? Array.Empty<PlaceholderAnnotation>(), showHints, intro, showIntroTitle);
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
        var point = View(_frame, dpi).LocalToDip(new CanvasPoint(x, y));
        foreach (var label in _placeholders.Labels)
            if (label.Rect.Contains(new Point(point.X, point.Y))) return label.Card.Handle;
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
    public void Draw(DrawingContext dc, OverviewAnnotationContext context)
    {
        if (context.Frame.Intro is { } intro && context.Frame.ShowIntroTitle)
            StartupIntroTitle.Draw(dc, context.Size, context.Dpi, intro);
        if (!context.Frame.ShowHints) return;
        string hint = context.Frame.Drawing ? "Drag to mark a project area  ·  Esc to cancel"
            : "Drag name: move group   Corners: resize   Ctrl+Z/Y: undo/redo   Z: areas   P: projects";
        var text = context.Text(hint, 13, Brushes.White);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(240, 23, 29, 41)), null,
            new Rect(20, 20, text.Width + 28, 36), 10, 10);
        dc.DrawText(text, new Point(34, 28));
    }
}
