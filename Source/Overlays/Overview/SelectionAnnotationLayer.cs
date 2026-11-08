using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace CanvasDesktop;

internal sealed class SelectionAnnotationLayer(CanvasSelection selection, Func<IReadOnlyList<CanvasSelectionItem>> items,
    CanvasSelectionTool tool, Func<(CanvasPoint Center,double Radius)?> circle, Func<bool> visible, ConnectionCanvasTool? connectionTool = null) : IOverviewAnnotationLayer
{
    public void Draw(DrawingContext dc, OverviewAnnotationContext context)
    {
        if (!visible()) return;
        Rect Project(CanvasRect r) { var p=context.View.WorldToDip(r); return new(p.X,p.Y,Math.Max(1,p.Width),Math.Max(1,p.Height)); }
        var pen=new Pen(Brushes.DeepSkyBlue,2);
        var handleFill = new SolidColorBrush(Color.FromArgb(240, 30, 41, 59));
        handleFill.Freeze();
        var handleBorder = new Pen(Brushes.DeepSkyBlue, 1.5);
        handleBorder.Freeze();
        var hoveredBorder = new Pen(Brushes.White, 2.0);
        hoveredBorder.Freeze();
        var dotBrush = Brushes.DeepSkyBlue;
        var hoveredDotBrush = Brushes.White;
        var selected=selection.Ids;
        var hoveredTarget = connectionTool?.HoveredHandle;
        ConnectionPort[] ports = [ConnectionPort.Top, ConnectionPort.Right, ConnectionPort.Bottom, ConnectionPort.Left];

        foreach (var item in items())
        {
            bool isSelected = selected.Contains(item.Key);
            bool isHovered = hoveredTarget?.Item.Key == item.Key;

            if (isSelected) dc.DrawRectangle(null, pen, Project(item.Bounds));

            if (item.Key.Kind != CanvasItemKind.Annotation && (isSelected || isHovered))
            {
                foreach (var port in ports)
                {
                    var portWorld = AnnotationGeometry.PortPosition(item.Bounds, port);
                    var dip = context.View.LocalToDip(context.View.WorldToLocal(portWorld));
                    bool isPortHovered = hoveredTarget != null && hoveredTarget.Item.Key == item.Key && hoveredTarget.Port == port;

                    if (isPortHovered)
                    {
                        dc.DrawEllipse(handleFill, hoveredBorder, new Point(dip.X, dip.Y), 8, 8);
                        dc.DrawEllipse(hoveredDotBrush, null, new Point(dip.X, dip.Y), 3.5, 3.5);
                    }
                    else
                    {
                        dc.DrawEllipse(handleFill, handleBorder, new Point(dip.X, dip.Y), 6, 6);
                        dc.DrawEllipse(dotBrush, null, new Point(dip.X, dip.Y), 2.5, 2.5);
                    }
                }
            }
        }

        if (connectionTool?.SnapTarget is {} snap)
        {
            var dip = context.View.LocalToDip(context.View.WorldToLocal(snap.Point));
            var snapHalo = new SolidColorBrush(Color.FromArgb(80, 56, 189, 248));
            var snapBorder = new Pen(Brushes.DeepSkyBlue, 2.0);
            dc.DrawEllipse(snapHalo, snapBorder, new Point(dip.X, dip.Y), 13, 13);
            dc.DrawEllipse(Brushes.White, null, new Point(dip.X, dip.Y), 4, 4);
        }
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
