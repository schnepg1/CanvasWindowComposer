using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace CanvasDesktop;

internal sealed partial class OverviewManager
{
    /// <summary>Screen-sized drag affordances share the existing transparent annotation surface.</summary>
    private sealed class WindowInteractionAnnotationLayer(OverviewManager owner) : IOverviewAnnotationLayer
    {
        public void Draw(DrawingContext dc, OverviewAnnotationContext context)
        {
            if (owner.CurrentMode != OverviewMode.Zooming || owner.IsStartupIntroActive
                || (!owner._interactMode && !owner._windowInteraction.Active)) return;
            var screen = context.Frame.Screen;
            var pass = owner._passes.FirstOrDefault(p => p.OriginX == screen.X && p.OriginY == screen.Y);
            if (pass == null) return;
            Rect Dip(System.Drawing.Rectangle r) => new((r.X - screen.X) / context.Dpi.DpiScaleX,
                (r.Y - screen.Y) / context.Dpi.DpiScaleY, r.Width / context.Dpi.DpiScaleX, r.Height / context.Dpi.DpiScaleY);
            var windows = owner._windows.Windows;
            for (int i = windows.Count - 1; i >= 0; i--)
            {
                var entry = windows[i];
                if (!owner.CanInteract(pass, entry.HWnd)
                    || !owner._thumbnails.TryGetInteractionGeometry(pass, entry.HWnd, out var geometry)) continue;
                var rect = Dip(geometry.Destination);
                if (!new Rect(0, 0, context.Size.Width, context.Size.Height).IntersectsWith(rect)) continue;
                Geometry clip = new RectangleGeometry(rect);
                for (int front = 0; front < i; front++)
                    clip = new CombinedGeometry(GeometryCombineMode.Exclude, clip,
                        new RectangleGeometry(context.Project(windows[front].World)));
                dc.PushClip(clip);
                bool active = owner._windowInteraction.Window == entry.HWnd;
                var brush = active ? Brushes.MediumSpringGreen : Brushes.SlateGray;
                if (active) dc.DrawRectangle(null, new Pen(brush, 3), rect);
                if (owner._interactMode)
                {
                    var header = Dip(InteractionHeader(geometry.Destination, pass.DeviceDpi));
                    dc.DrawRectangle(active ? Brushes.DarkGreen : Brushes.DarkSlateGray, null, header);
                    dc.PushClip(new RectangleGeometry(header));
                    var text = context.Text(active ? "↕ Drag window   •   🖱 Interacting" : "↕ Drag window   •   Click content to interact", 11, Brushes.White);
                    dc.DrawText(text, new Point(header.X + 6, header.Y + 3));
                    dc.Pop();
                }
                dc.Pop();
            }
        }
    }
}
