using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace CanvasDesktop;

internal sealed class SelectionAnnotationLayer(CanvasSelection selection, Func<IReadOnlyList<CanvasSelectionItem>> items,
    CanvasSelectionTool tool, Func<(CanvasPoint Center,double Radius)?> circle, Func<bool> visible) : IOverviewAnnotationLayer
{
    public void Draw(DrawingContext dc, OverviewAnnotationContext context)
    {
        if (!visible()) return;
        Rect Project(CanvasRect r) { var p=context.View.WorldToDip(r); return new(p.X,p.Y,Math.Max(1,p.Width),Math.Max(1,p.Height)); }
        var pen=new Pen(Brushes.DeepSkyBlue,2);
        var selected=selection.Ids;
        foreach (var item in items()) if (selected.Contains(item.Key)) dc.DrawRectangle(null,pen,Project(item.Bounds));
        if (tool.Marquee is {} marquee)
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(35,0,191,255)),new Pen(Brushes.DeepSkyBlue,1),Project(marquee));
        if (tool.Offset != default)
            foreach (var item in tool.Ghosts)
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(45,0,191,255)),new Pen(Brushes.White,1) { DashStyle=DashStyles.Dash },
                    Project(item.Bounds with { X=item.Bounds.X+tool.Offset.X,Y=item.Bounds.Y+tool.Offset.Y }));
        if (circle() is {} proximity)
        {
            var p=context.View.LocalToDip(context.View.WorldToLocal(proximity.Center));
            dc.DrawEllipse(null,new Pen(Brushes.DeepSkyBlue,1) { DashStyle=DashStyles.Dash },new(p.X,p.Y),
                proximity.Radius*context.View.Zoom/context.Dpi.DpiScaleX,proximity.Radius*context.View.Zoom/context.Dpi.DpiScaleY);
        }
    }
}
