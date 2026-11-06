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
            if (owner.CurrentMode != OverviewMode.Zooming || owner.IsStartupIntroActive) return;
            bool hasInteraction = owner._interactMode || owner._windowInteraction.Active || owner._peekWindow != null;
            bool hasResize = owner._hoveredResizeWindow != IntPtr.Zero || owner._tools.Kind == CanvasInteractionKind.WindowResize;
            if (!hasInteraction && !hasResize) return;

            var screen = context.Frame.Screen;
            var pass = owner._passes.FirstOrDefault(p => p.OriginX == screen.X && p.OriginY == screen.Y);
            if (pass == null) return;
            Rect Dip(System.Drawing.Rectangle r) => new((r.X - screen.X) / context.Dpi.DpiScaleX,
                (r.Y - screen.Y) / context.Dpi.DpiScaleY, r.Width / context.Dpi.DpiScaleX, r.Height / context.Dpi.DpiScaleY);

            if (hasInteraction)
            {
                if (owner._peekWindow != null && owner._peekPass == pass)
                    dc.DrawRectangle(null, new Pen(Brushes.MediumSpringGreen, 2), Dip(owner._peekOrigin));
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

            if (hasResize)
            {
                IntPtr resizingHwnd = owner._tools.Kind == CanvasInteractionKind.WindowResize && owner._resizeIndex >= 0 && owner._resizeIndex < owner._windows.Count
                    ? owner._windows.Windows[owner._resizeIndex].HWnd
                    : owner._hoveredResizeWindow;

                if (resizingHwnd != IntPtr.Zero)
                {
                    var targetEntry = owner._windows.Windows.FirstOrDefault(w => w.HWnd == resizingHwnd);
                    if (targetEntry.HWnd != IntPtr.Zero)
                    {
                        var projRect = context.Project(targetEntry.World);
                        if (new Rect(0, 0, context.Size.Width, context.Size.Height).IntersectsWith(projRect))
                        {
                            var highlightPen = new Pen(new SolidColorBrush(Color.FromArgb(220, 59, 130, 246)), 2);
                            dc.DrawRoundedRectangle(null, highlightPen, projRect, 3, 3);

                            const double GripSize = 6;
                            var gripBrush = Brushes.White;
                            var gripPen = new Pen(new SolidColorBrush(Color.FromRgb(37, 99, 235)), 1.5);
                            void DrawGrip(double cx, double cy) =>
                                dc.DrawRoundedRectangle(gripBrush, gripPen, new Rect(cx - GripSize / 2, cy - GripSize / 2, GripSize, GripSize), 1, 1);

                            DrawGrip(projRect.Left, projRect.Top);
                            DrawGrip(projRect.Right, projRect.Top);
                            DrawGrip(projRect.Left, projRect.Bottom);
                            DrawGrip(projRect.Right, projRect.Bottom);
                        }
                    }
                }
            }
        }
    }
}
