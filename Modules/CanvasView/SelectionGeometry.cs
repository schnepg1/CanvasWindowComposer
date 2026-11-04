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
        double headLen = Math.Max(20.0, item.Stroke * 3.2);
        double headWid = Math.Max(10.0, item.Stroke * 1.8);

        if (item.Routing == ConnectorRouting.Curved)
        {
            var (c1, c2) = AnnotationGeometry.BezierControlPoints(item.A, item.SourcePort, item.B, item.TargetPort);
            CanvasPoint prev = item.A;
            for (int i = 1; i <= 10; i++)
            {
                double t = i / 10.0;
                double omt = 1.0 - t;
                double x = omt * omt * omt * item.A.X + 3 * omt * omt * t * c1.X + 3 * omt * t * t * c2.X + t * t * t * item.B.X;
                double y = omt * omt * omt * item.A.Y + 3 * omt * omt * t * c1.Y + 3 * omt * t * t * c2.Y + t * t * t * item.B.Y;
                var curr = new CanvasPoint(x, y);
                yield return (prev, curr);
                prev = curr;
            }

            double arrDx = item.B.X - c2.X, arrDy = item.B.Y - c2.Y;
            double arrLen = Math.Sqrt(arrDx * arrDx + arrDy * arrDy);
            if (arrLen > 0.0001)
            {
                double ux = arrDx / arrLen, uy = arrDy / arrLen;
                double nx = -uy, ny = ux;
                var basePt = new CanvasPoint(item.B.X - ux * headLen, item.B.Y - uy * headLen);
                yield return (item.B, new(basePt.X + nx * headWid, basePt.Y + ny * headWid));
                yield return (item.B, new(basePt.X - nx * headWid, basePt.Y - ny * headWid));
            }
        }
        else
        {
            yield return (item.A, item.B);
            double length = AnnotationGeometry.Distance(item.A, item.B);
            if (length > 0)
            {
                double dx = (item.A.X - item.B.X) / length, dy = (item.A.Y - item.B.Y) / length;
                yield return (item.B, new(item.B.X + dx * headLen - dy * headWid, item.B.Y + dy * headLen + dx * headWid));
                yield return (item.B, new(item.B.X + dx * headLen + dy * headWid, item.B.Y + dy * headLen - dx * headWid));
            }
        }

        if (!string.IsNullOrWhiteSpace(item.Text))
        {
            var (mid, tan) = item.Routing == ConnectorRouting.Curved
                ? AnnotationGeometry.BezierMidpointAndTangent(item.A, AnnotationGeometry.BezierControlPoints(item.A, item.SourcePort, item.B, item.TargetPort).C1, AnnotationGeometry.BezierControlPoints(item.A, item.SourcePort, item.B, item.TargetPort).C2, item.B)
                : (new CanvasPoint((item.A.X + item.B.X) / 2.0, (item.A.Y + item.B.Y) / 2.0), new CanvasPoint(item.B.X - item.A.X, item.B.Y - item.A.Y));

            double approxWidth = Math.Max(40, item.Text.Length * item.FontSize * 0.6 + 20);
            double approxHeight = item.FontSize + 12;
            var r = AnnotationGeometry.ArrowLabelBounds(mid, tan, item.Stroke, approxWidth, approxHeight);
            yield return (new(r.X, r.Y), new(r.X + r.Width, r.Y));
            yield return (new(r.X + r.Width, r.Y), new(r.X + r.Width, r.Y + r.Height));
            yield return (new(r.X + r.Width, r.Y + r.Height), new(r.X, r.Y + r.Height));
            yield return (new(r.X, r.Y + r.Height), new(r.X, r.Y));
        }
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
