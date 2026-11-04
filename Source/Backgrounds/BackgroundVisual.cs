using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CanvasDesktop;

/// <summary>One shared painter for desktop and overview. Canvas dimensions never allocate image-sized surfaces.</summary>
internal sealed class BackgroundVisual(BackgroundImageCache images)
{
    public void Draw(DrawingContext dc, IReadOnlyList<BackgroundSurface> items, CanvasViewTransform view,
        Size viewport, bool lowQuality, IReadOnlyList<WorldRect>? occluders = null)
    {
        var visible = new Rect(viewport);
        dc.PushClip(new RectangleGeometry(visible));
        if (occluders is { Count: >0 })
        {
            // The existing overview paints WPF above DWM. Exclude native content
            // in one geometry, so imagery never paints over a live application.
            var holes=new GeometryGroup { FillRule=FillRule.Nonzero };
            foreach(var b in occluders)
            {
                var p=view.WorldToDip(new(b.X,b.Y,b.W,b.H));
                var r=new Rect(p.X,p.Y,Math.Max(0,p.Width),Math.Max(0,p.Height));
                if(visible.IntersectsWith(r)) holes.Children.Add(new RectangleGeometry(Rect.Intersect(r,visible)));
            }
            dc.PushClip(new CombinedGeometry(GeometryCombineMode.Exclude,new RectangleGeometry(visible),holes));
        }
        foreach(var item in items)
        {
            var p=view.WorldToDip(new(item.Bounds.X,item.Bounds.Y,item.Bounds.W,item.Bounds.H));
            if(!double.IsFinite(p.X)||!double.IsFinite(p.Y)||!double.IsFinite(p.Width)||!double.IsFinite(p.Height)) continue;
            var rect=new Rect(p.X,p.Y,Math.Max(0,p.Width),Math.Max(0,p.Height));
            if(!visible.IntersectsWith(rect) || item.Opacity<=0) continue;
            var image=images.TryGet(item.AssetId,lowQuality || Math.Max(rect.Width,rect.Height)*Math.Max(view.DpiX,view.DpiY)<=1024)
                ?? images.GetResident(item.AssetId);
            dc.PushClip(new RectangleGeometry(Rect.Intersect(rect,visible)));
            dc.PushOpacity(item.Opacity);
            if(image is BitmapSource bitmap)
            {
                if(item.Fit==BackgroundFit.Tile)
                {
                    // Tile dimensions use original pixels, so changing cache detail does not change placement.
                    var natural=images.GetDimensions(item.AssetId);
                    double width=(natural?.Width ?? bitmap.PixelWidth)*view.Zoom/view.DpiX;
                    double height=(natural?.Height ?? bitmap.PixelHeight)*view.Zoom/view.DpiY;
                    var brush=new ImageBrush(image) { TileMode=TileMode.Tile,Stretch=Stretch.Fill,
                        ViewportUnits=BrushMappingMode.Absolute, Viewport=new Rect(rect.X,rect.Y,width,height) };
                    dc.DrawRectangle(brush,null,Rect.Intersect(rect,visible));
                }
                else
                {
                    var target=ImageBounds(rect,bitmap.PixelWidth,bitmap.PixelHeight,item.Fit,item.CropX,item.CropY);
                    if(!target.IsEmpty)dc.DrawImage(image,target);
                }
                if(item.Dim>0) { dc.PushOpacity(item.Dim);dc.DrawRectangle(Brushes.Black,null,rect);dc.Pop(); }
            }
            else
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(150,30,35,44)),null,Rect.Intersect(rect,visible));
                if(images.GetError(item.AssetId)!=null && rect.Width>100 && rect.Height>50)
                {
                    var text=new FormattedText("Image unavailable · Locate or replace in Backgrounds",
                        System.Globalization.CultureInfo.CurrentUICulture,FlowDirection.LeftToRight,new Typeface("Segoe UI"),13,Brushes.LightGray,1)
                        { MaxTextWidth=Math.Min(360,rect.Width-24) };
                    dc.DrawText(text,new Point(Math.Max(visible.Left,rect.Left)+12,Math.Max(visible.Top,rect.Top)+12));
                }
            }
            dc.Pop();dc.Pop();
        }
        if(occluders is { Count:>0 }) dc.Pop();
        dc.Pop();
    }

    internal static Rect ImageBounds(Rect frame,double width,double height,BackgroundFit fit,double cropX,double cropY)
    {
        double scale=fit==BackgroundFit.Fit?Math.Min(frame.Width/width,frame.Height/height):Math.Max(frame.Width/width,frame.Height/height);
        double w=width*scale,h=height*scale;
        double x=frame.X+(frame.Width-w)*cropX,y=frame.Y+(frame.Height-h)*cropY;
        return double.IsFinite(x)&&double.IsFinite(y)&&double.IsFinite(w)&&double.IsFinite(h)
            ?new(x,y,w,h):Rect.Empty;
    }
}

internal sealed class BackgroundAnnotationLayer(BackgroundService service,BackgroundVisual visual,
    Func<bool> lowQuality,BackgroundCanvasTool tool,Func<bool>? enabled=null) : IOverviewAnnotationLayer
{
    public void Draw(DrawingContext dc,OverviewAnnotationContext context)
    {
        if(enabled?.Invoke()==false)return;
        var zones=context.Frame.Zones.ToDictionary(zone=>zone.Id);
        var items=service.Items.Select(b=>tool.Preview?.Id==b.Id?tool.Preview
            :b.ZoneId is Guid id && zones.TryGetValue(id,out var zone)?b with { Bounds=zone.Bounds }:b).ToArray();
        visual.Draw(dc,items,context.View,context.Size,lowQuality(),context.Frame.BackgroundOccluders);
    }
}

internal sealed class BackgroundEditLayer(BackgroundService service,BackgroundCanvasTool tool) : IOverviewAnnotationLayer
{
    public void Draw(DrawingContext dc,OverviewAnnotationContext context)
    {
        var item=tool.Preview??service.Items.FirstOrDefault(b=>b.Id==tool.SelectedId);
        if(item==null) return;
        var rect=context.Project(item.Bounds);
        if(!double.IsFinite(rect.X)||!double.IsFinite(rect.Y)||!double.IsFinite(rect.Width)||!double.IsFinite(rect.Height)
            || !double.IsFinite(rect.Right)||!double.IsFinite(rect.Bottom))return;
        dc.DrawRectangle(null,new Pen(Brushes.DeepSkyBlue,2),rect);
        Point[] points=[rect.TopLeft,rect.TopRight,rect.BottomLeft,rect.BottomRight];
        foreach(var p in points) dc.DrawRectangle(Brushes.White,new Pen(Brushes.DodgerBlue,2),new Rect(p.X-5,p.Y-5,10,10));
    }
}
