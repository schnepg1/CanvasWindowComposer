using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace CanvasDesktop;

/// <summary>Passive world-space notes. Uses the same artwork as user text/arrows at readable DPI scale.</summary>
internal sealed class TutorialAnnotationLayer(Func<TutorialPersistence.DesktopNotes?> notes, Func<bool> visible) : IOverviewAnnotationLayer
{
    private readonly Dictionary<Guid,DrawingGroup> _artwork=[];
    private TutorialLayout? _layout;
    private double _dpi;
    public void Draw(DrawingContext dc,OverviewAnnotationContext context)
    {
        var current=notes();
        if(!visible() || current is not { Visible:true }) return;
        var layout=current.Layout;
        if(!ReferenceEquals(layout,_layout) || _dpi!=context.Dpi.PixelsPerDip) { _artwork.Clear();_layout=layout;_dpi=context.Dpi.PixelsPerDip; }
        foreach(var item in layout.Items)
        {
            var r=context.View.WorldToDip(item.Bounds);
            var rect=new Rect(r.X,r.Y,r.Width,r.Height);
            if(!rect.IntersectsWith(new Rect(-32,-32,context.Size.Width+64,context.Size.Height+64))) continue;
            if(!_artwork.TryGetValue(item.Id,out var drawing))
            {
                double s=layout.Scale;
                var local=item with { A=new(item.A.X/s,item.A.Y/s),B=new(item.B.X/s,item.B.Y/s) };
                drawing=new DrawingGroup();using(var painter=drawing.Open())AnnotationVisual.Draw(painter,local,_dpi);
                drawing.Freeze();_artwork[item.Id]=drawing;
            }
            dc.PushTransform(new TranslateTransform(rect.X,rect.Y));
            dc.PushTransform(new ScaleTransform(context.View.Zoom*layout.Scale/context.Dpi.DpiScaleX,context.View.Zoom*layout.Scale/context.Dpi.DpiScaleY));
            dc.DrawDrawing(drawing);dc.Pop();dc.Pop();
        }
    }
}
