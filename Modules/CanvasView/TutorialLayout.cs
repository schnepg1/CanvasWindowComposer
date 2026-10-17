namespace CanvasDesktop.CanvasView;

/// <summary>A stable neighbourhood of ordinary annotation artwork, independent of native windows.</summary>
public sealed record TutorialLayout(CanvasAnnotation[] Items, CanvasRect Welcome, double Scale, Guid? LandmarkConnectorId = null, bool SampleLandmark = false)
{
    public static TutorialLayout Create(Guid desktop, CanvasRect viewport, IReadOnlyList<CanvasRect> windows,
        IReadOnlyList<CanvasRect> otherContent, double scale = 1, bool overviewHotkey = true, bool altPan = true)
    {
        scale = double.IsFinite(scale) ? Math.Clamp(scale, 1, 4) : 1;
        bool Valid(CanvasRect r) => double.IsFinite(r.X) && double.IsFinite(r.Y) && double.IsFinite(r.Width)
            && double.IsFinite(r.Height) && r.Width > 0 && r.Height > 0
            && Math.Abs(r.X) + r.Width < 9000000 && Math.Abs(r.Y) + r.Height < 9000000;
        if (!Valid(viewport)) throw new ArgumentException("Invalid tutorial viewport.", nameof(viewport));
        double Distance(CanvasRect r) => Math.Pow(r.X+r.Width/2-viewport.X-viewport.Width/2,2)
            + Math.Pow(r.Y+r.Height/2-viewport.Y-viewport.Height/2,2);
        var nearby = windows.Where(Valid).Where(r => Intersects(r, viewport)).OrderBy(Distance).ToArray();
        bool sample = nearby.Length == 0;
        var anchor = sample ? new CanvasRect(viewport.X+viewport.Width/2-160*scale, viewport.Y+viewport.Height/2-100*scale,320*scale,200*scale) : nearby[0];
        var obstacles = windows.Concat(otherContent).Where(Valid).Append(anchor).ToArray();
        double w = 820*scale, h = 730*scale, gap = 100*scale;
        var candidates = new List<CanvasRect>();
        // Try both sides and above/below before extending the search. Never move the user's content.
        for (int ring=0; ring<16; ring++)
        {
            double dx=gap+ring*(w+gap), dy=gap+ring*(h+gap);
            candidates.Add(new(anchor.X+anchor.Width+dx,anchor.Y,w,h));
            candidates.Add(new(anchor.X-w-dx,anchor.Y,w,h));
            candidates.Add(new(anchor.X,anchor.Y-h-dy,w,h));
            candidates.Add(new(anchor.X,anchor.Y+anchor.Height+dy,w,h));
        }
        // A crowded workspace still has space beyond its rightmost object.
        candidates.Add(new(obstacles.Max(r=>r.X+r.Width)+gap,anchor.Y,w,h));
        var place = candidates.Where(Valid).FirstOrDefault(r => obstacles.All(o => !Intersects(Inflate(r,24*scale),o)));
        if (place.Width == 0) throw new InvalidOperationException("No safe space is available for tutorial notes.");
        var items = new List<CanvasAnnotation>();
        CanvasRect Note(double x,double y,double width,double height,string text,string color)
        {
            var r=new CanvasRect(place.X+x*scale,place.Y+y*scale,width*scale,height*scale);
            items.Add(new(Guid.NewGuid(),desktop,AnnotationKind.Text,new(r.X,r.Y),new(r.X+r.Width,r.Y+r.Height),color,3,text));
            return r;
        }
        Guid Arrow(CanvasPoint a, CanvasPoint b)
        {
            var id=Guid.NewGuid();items.Add(new(id,desktop,AnnotationKind.Arrow,a,b,"#60A5FA",3));return id;
        }
        var welcome=Note(0,0,390,340,
            "1  •  Move around\n\nYour desktop has more space than you think.\n\nMiddle-drag to pan. On a trackpad, scroll with two fingers.\n\nFollow the arrows to explore.\n\nTray → Tutorial: return or hide notes.","#FBBF24");
        var zoom=Note(430,0,390,340,
            "2  •  Change your view\n\nPinch or Ctrl+scroll to zoom around the pointer.\n\nYou can also press + / −.\n\nZoom out for the big picture; zoom in to read the details.","#60A5FA");
        Note(430,390,390,340,
            "3  •  Your windows live here\n\nChoose Windows in the toolbar. Drag a window to arrange it.\n\nDouble-click a window or card to return to it.\n\n"+(overviewHotkey?"Alt+Q opens the overview again.":"Alt+scroll on the desktop opens overview."),"#34D399");
        Note(0,390,390,340,
            "4  •  Leave yourself a thought\n\nChoose Text, click empty canvas, and write a note. Ctrl+Enter saves it.\n\nChoose Arrow to connect ideas. Select lets you move and resize them.\n\n"+(altPan?"Outside overview: Alt+middle-drag pans over windows.":"Esc leaves overview."),"#C084FC");
        Arrow(new(welcome.X+welcome.Width+8*scale,welcome.Y+150*scale),new(zoom.X-8*scale,zoom.Y+150*scale));
        Arrow(new(zoom.X+195*scale,zoom.Y+350*scale),new(zoom.X+195*scale,zoom.Y+380*scale));
        Arrow(new(place.X+420*scale,place.Y+520*scale),new(place.X+400*scale,place.Y+520*scale));
        // Connect the neighbourhood to its landmark without drawing through the instruction cards.
        var from=new CanvasPoint(Math.Clamp(anchor.X+anchor.Width/2,place.X,place.X+w), Math.Clamp(anchor.Y+anchor.Height/2,place.Y,place.Y+h));
        var to=new CanvasPoint(Math.Clamp(place.X+w/2,anchor.X,anchor.X+anchor.Width),Math.Clamp(place.Y+h/2,anchor.Y,anchor.Y+anchor.Height));
        var landmarkConnector=Arrow(from,to);
        if(sample) items.Add(new(Guid.NewGuid(),desktop,AnnotationKind.Rectangle,new(anchor.X,anchor.Y),new(anchor.X+anchor.Width,anchor.Y+anchor.Height),"#60A5FA",3,"Your canvas\n\nOpen an app, then return here to see it on the canvas."));
        AnnotationDocument.Validate(items);
        return new(items.ToArray(),welcome,scale,landmarkConnector,sample);
    }

    /// <summary>Keep the neighbourhood origin, identities and real-window arrow endpoint when DPI changes.</summary>
    public TutorialLayout WithScale(double scale)
    {
        if(!double.IsFinite(scale) || scale is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(scale));
        if(Math.Abs(scale-Scale)<.001) return this;
        double ratio=scale/Scale;
        CanvasPoint Project(CanvasPoint p)=>new(Welcome.X+(p.X-Welcome.X)*ratio,Welcome.Y+(p.Y-Welcome.Y)*ratio);
        var items=Items.Select(a=>a with { A=Project(a.A),
            B=a.Id==LandmarkConnectorId && !SampleLandmark?a.B:Project(a.B) }).ToArray();
        AnnotationDocument.Validate(items);
        return this with { Items=items,Welcome=items[0].Bounds,Scale=scale };
    }

    public static bool Intersects(CanvasRect a, CanvasRect b) => a.X < b.X+b.Width && a.X+a.Width > b.X && a.Y < b.Y+b.Height && a.Y+a.Height > b.Y;
    private static CanvasRect Inflate(CanvasRect r,double p) => new(r.X-p,r.Y-p,r.Width+2*p,r.Height+2*p);
}
