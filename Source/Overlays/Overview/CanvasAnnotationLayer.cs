using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace CanvasDesktop;

internal sealed class CanvasAnnotationLayer(AnnotationDocument document, AnnotationCanvasTool tool, Func<Guid> desktop, Func<bool> visible, Func<Guid, bool>? hidden = null) : IOverviewAnnotationLayer
{
    public void Draw(DrawingContext dc, OverviewAnnotationContext context)
    {
        if(!visible()) return;
        var preview=tool.Preview;
        var items=document.Items.Where(a=>a.DesktopId==desktop() && a.Id!=preview?.Id && hidden?.Invoke(a.Id) != true).ToList();
        if(preview!=null && preview.DesktopId==desktop()) items.Add(preview);
        foreach(var item in items)
        {
            var projected=context.View.WorldToDip(item.Bounds);
            var rect=new Rect(projected.X,projected.Y,projected.Width,projected.Height);
            if(!new Rect(-32,-32,context.Size.Width+64,context.Size.Height+64).IntersectsWith(rect)) continue;
            var brush=new SolidColorBrush((Color)ColorConverter.ConvertFromString(item.Color)); brush.Freeze();
            var pen=new Pen(brush,Math.Max(.75,item.Stroke*context.View.Zoom/context.Dpi.DpiScaleX));
            Point Project(CanvasPoint p) { var q=context.View.LocalToDip(context.View.WorldToLocal(p)); return new(q.X,q.Y); }
            if(item.Kind==AnnotationKind.Arrow)
            {
                var a=Project(item.A); var b=Project(item.B); dc.DrawLine(pen,a,b);
                var delta=a-b;
                if(delta.Length>0)
                {
                    delta.Normalize(); double size=Math.Max(6,12*context.View.Zoom/context.Dpi.DpiScaleX);
                    var wing=new Vector(-delta.Y,delta.X)*size*.45;
                    dc.DrawLine(pen,b,b+delta*size+wing); dc.DrawLine(pen,b,b+delta*size-wing);
                }
            }
            else
            {
                if(item.Kind==AnnotationKind.Rectangle) dc.DrawRectangle(null,pen,rect);
                else if(item.Kind==AnnotationKind.Ellipse) dc.DrawEllipse(null,pen,new(rect.X+rect.Width/2,rect.Y+rect.Height/2),rect.Width/2,rect.Height/2);
                else dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(245,28,34,46)),pen,rect,6,6);
                if(!string.IsNullOrEmpty(item.Text) && rect.Width>16 && rect.Height>16)
                {
                    var textRect = item.Kind == AnnotationKind.Ellipse
                        ? new Rect(rect.X + rect.Width * .15, rect.Y + rect.Height * .15, rect.Width * .7, rect.Height * .7)
                        : rect;
                    var text=context.Text(item.Text,Math.Max(.5,18*context.View.Zoom/context.Dpi.DpiScaleX),brush);
                    text.MaxTextWidth=Math.Max(1,textRect.Width-16); text.MaxTextHeight=Math.Max(1,textRect.Height-16);
                    text.Trimming=TextTrimming.CharacterEllipsis;
                    dc.PushClip(new RectangleGeometry(textRect)); dc.DrawText(text,new Point(textRect.X+8,textRect.Y+8)); dc.Pop();
                }
            }
            if(tool.Mode!=AnnotationMode.Navigate && item.Id==tool.SelectedId)
            {
                var selection=new Pen(Brushes.White,1) { DashStyle=DashStyles.Dash };
                dc.DrawRectangle(null,selection,rect);
                foreach(var handle in AnnotationGeometry.Handles(item))
                { var p=Project(handle); dc.DrawRectangle(Brushes.White,new Pen(Brushes.DodgerBlue,1),new Rect(p.X-4,p.Y-4,8,8)); }
            }
        }
    }
}
