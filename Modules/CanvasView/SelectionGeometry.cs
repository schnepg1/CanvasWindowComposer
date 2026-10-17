namespace CanvasDesktop.CanvasView;

/// <summary>Shape-aware spatial queries. Empty arrow/ellipse bounding-box corners are not selectable content.</summary>
public static class SelectionGeometry
{
    public static bool Intersects(CanvasAnnotation item, CanvasRect rectangle)
    {
        double padding=item.Stroke/2;
        var rect=new CanvasRect(rectangle.X-padding,rectangle.Y-padding,rectangle.Width+padding*2,rectangle.Height+padding*2);
        if(item.Kind==AnnotationKind.Arrow)
            return ArrowSegments(item).Any(s=>SegmentIntersects(s.A,s.B,rect));
        if(item.Kind!=AnnotationKind.Ellipse) return CanvasSelection.Intersects(item.Bounds,rect);
        var center=CanvasSelection.Center(item.Bounds);
        double x=Math.Clamp(center.X,rect.X,rect.X+rect.Width)-center.X;
        double y=Math.Clamp(center.Y,rect.Y,rect.Y+rect.Height)-center.Y;
        return x*x/(item.Bounds.Width*item.Bounds.Width/4)+y*y/(item.Bounds.Height*item.Bounds.Height/4)<=1;
    }

    public static bool Within(CanvasAnnotation item, CanvasPoint center, double radius)
    {
        radius+=item.Stroke/2;
        if(item.Kind==AnnotationKind.Arrow)
            return ArrowSegments(item).Any(s=>SegmentDistance(center,s.A,s.B)<=radius);
        var bounds=item.Bounds;var middle=CanvasSelection.Center(bounds);
        if(item.Kind!=AnnotationKind.Ellipse)
            return AnnotationGeometry.Distance(center,new(Math.Clamp(center.X,bounds.X,bounds.X+bounds.Width),Math.Clamp(center.Y,bounds.Y,bounds.Y+bounds.Height)))<=radius;
        double x=Math.Abs(center.X-middle.X),y=Math.Abs(center.Y-middle.Y),rx=bounds.Width/2,ry=bounds.Height/2;
        if(x*x/(rx*rx)+y*y/(ry*ry)<=1) return true;
        // Closest point on an ellipse, solving its Lagrange multiplier by monotone bisection.
        double lo=0,hi=Math.Max(rx*x,ry*y);
        double Equation(double t)=>Math.Pow(rx*x/(t+rx*rx),2)+Math.Pow(ry*y/(t+ry*ry),2);
        while(Equation(hi)>1) hi*=2;
        for(int i=0;i<60;i++) { double mid=(lo+hi)/2;if(Equation(mid)>1)lo=mid;else hi=mid; }
        var nearest=new CanvasPoint(rx*rx*x/(hi+rx*rx),ry*ry*y/(hi+ry*ry));
        return AnnotationGeometry.Distance(new(x,y),nearest)<=radius;
    }

    private static IEnumerable<(CanvasPoint A,CanvasPoint B)> ArrowSegments(CanvasAnnotation item)
    {
        yield return (item.A,item.B);
        double length=AnnotationGeometry.Distance(item.A,item.B);
        if(length==0) yield break;
        double dx=(item.A.X-item.B.X)/length,dy=(item.A.Y-item.B.Y)/length;
        yield return (item.B,new(item.B.X+dx*12-dy*5.4,item.B.Y+dy*12+dx*5.4));
        yield return (item.B,new(item.B.X+dx*12+dy*5.4,item.B.Y+dy*12-dx*5.4));
    }
    private static double SegmentDistance(CanvasPoint point,CanvasPoint a,CanvasPoint b)
    {
        double dx=b.X-a.X,dy=b.Y-a.Y,den=dx*dx+dy*dy;
        double t=den==0?0:Math.Clamp(((point.X-a.X)*dx+(point.Y-a.Y)*dy)/den,0,1);
        return AnnotationGeometry.Distance(point,new(a.X+t*dx,a.Y+t*dy));
    }
    private static bool SegmentIntersects(CanvasPoint a,CanvasPoint b,CanvasRect r)
    {
        double enter=0,leave=1;
        bool Axis(double start,double delta,double min,double max)
        {
            if(delta==0) return start>=min && start<=max;
            double first=(min-start)/delta,last=(max-start)/delta;
            enter=Math.Max(enter,Math.Min(first,last));leave=Math.Min(leave,Math.Max(first,last));
            return enter<=leave;
        }
        return Axis(a.X,b.X-a.X,r.X,r.X+r.Width) && Axis(a.Y,b.Y-a.Y,r.Y,r.Y+r.Height);
    }
}
