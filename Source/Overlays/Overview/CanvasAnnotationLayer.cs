using System;
using System.Linq;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace CanvasDesktop;

internal sealed class CanvasAnnotationLayer(AnnotationDocument document, AnnotationCanvasTool tool, Func<Guid> desktop, Func<bool> visible, Func<Guid, bool>? hidden = null) : IOverviewAnnotationLayer
{
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
        if (_revision != document.Revision) { _artwork.Clear(); _revision=document.Revision; }
        var preview = tool.Preview;
        var items = document.Items.Where(a => a.DesktopId == desktop() && a.Id != preview?.Id && hidden?.Invoke(a.Id) != true).ToList();
        if (preview != null && preview.DesktopId == desktop()) items.Add(preview);
        foreach (var item in items)
        {
            var projected = context.View.WorldToDip(item.Bounds);
            var rect = new Rect(projected.X,projected.Y,projected.Width,projected.Height);
            if (!new Rect(-32,-32,context.Size.Width+64,context.Size.Height+64).IntersectsWith(rect)) continue;
            // Scale the whole artwork together, including type, inset, stroke and arrowheads.
            dc.PushTransform(new TranslateTransform(rect.X,rect.Y));
            dc.PushTransform(new ScaleTransform(context.View.Zoom/context.Dpi.DpiScaleX,context.View.Zoom/context.Dpi.DpiScaleY));
            dc.DrawDrawing(Artwork(item,context.Dpi.PixelsPerDip,item.Id==preview?.Id));
            dc.Pop(); dc.Pop();
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
}
