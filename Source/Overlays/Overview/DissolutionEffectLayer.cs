using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using CanvasDesktop.CanvasView;

namespace CanvasDesktop;

/// <summary>Paints the particle "dissolution" of removed canvas items above the cards.</summary>
internal sealed class DissolutionEffectLayer(Func<bool> reduceMotion) : IOverviewAnnotationLayer
{
    private readonly DissolutionEffect _effect = new();
    private readonly Dictionary<uint, SolidColorBrush> _brushes = [];

    public bool HasAnimatedContent => _effect.HasParticles(Environment.TickCount64);

    public bool Spawn(CanvasRect bounds, string? color, bool ghost = true)
    {
        if (reduceMotion()) return false;
        return _effect.Spawn(bounds, ParseArgb(color), Environment.TickCount64, ghost: ghost);
    }

    public void Clear() => _effect.Clear();

    public void Draw(DrawingContext dc, OverviewAnnotationContext context)
    {
        if (reduceMotion()) return;
        foreach (var frame in _effect.Sample(context.Timestamp))
        {
            if (frame.GhostOpacity > 0.005)
            {
                var ghost = context.View.WorldToDip(frame.Bounds);
                double width = Math.Max(2, ghost.Width);
                double height = Math.Max(2, ghost.Height);
                double grownWidth = width * frame.Scale;
                double grownHeight = height * frame.Scale;
                var rect = new Rect(
                    ghost.X - (grownWidth - width) / 2,
                    ghost.Y - (grownHeight - height) / 2,
                    grownWidth, grownHeight);
                dc.DrawRoundedRectangle(Brush(frame.Argb, frame.GhostOpacity), null, rect, 6, 6);
            }

            foreach (var particle in frame.Particles)
            {
                var point = context.View.LocalToDip(context.View.WorldToLocal(new CanvasPoint(particle.X, particle.Y)));
                double radius = Math.Max(0.9, particle.Size * context.View.Zoom / context.Dpi.DpiScaleX * 0.5);
                dc.DrawEllipse(Brush(frame.Argb, particle.Alpha), null, new Point(point.X, point.Y), radius, radius);
            }
        }
    }

    private SolidColorBrush? Brush(uint argb, double opacity)
    {
        int alpha = (int)Math.Round((argb >> 24) * Math.Clamp(opacity, 0, 1));
        if (alpha <= 0) return null;
        // Quantised alpha keeps the brush cache tiny across a 60 fps burst.
        alpha = Math.Min(255, Math.Clamp((int)Math.Round(alpha / 32.0) * 32, 32, 256));
        uint key = (argb & 0x00FFFFFFu) | ((uint)alpha << 24);
        if (_brushes.TryGetValue(key, out var brush)) return brush;
        brush = new SolidColorBrush(Color.FromArgb((byte)alpha, (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        brush.Freeze();
        if (_brushes.Count > 256) _brushes.Clear();
        _brushes[key] = brush;
        return brush;
    }

    private static uint ParseArgb(string? color)
    {
        try
        {
            if (ColorConverter.ConvertFromString(color) is Color parsed)
                return (uint)(parsed.A << 24 | parsed.R << 16 | parsed.G << 8 | parsed.B);
        }
        catch (FormatException) { }
        catch (NotSupportedException) { }
        return 0xFFFBBF24;
    }
}
