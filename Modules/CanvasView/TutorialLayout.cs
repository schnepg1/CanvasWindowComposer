namespace CanvasDesktop.CanvasView;

/// <summary>A stable neighbourhood of ordinary annotation artwork, independent of native windows.</summary>
public sealed record TutorialLayout(CanvasAnnotation[] Items, CanvasRect Welcome, double Scale, Guid? LandmarkConnectorId = null, bool SampleLandmark = false)
{
    public static TutorialLayout Create(Guid desktop, CanvasRect viewport, IReadOnlyList<CanvasRect> windows,
        IReadOnlyList<CanvasRect> otherContent, double scale = 1)
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
        var welcome=Note(0,0,390,340,Instructions[0],"#FBBF24");
        var zoom=Note(430,0,390,340,Instructions[1],"#60A5FA");
        Note(430,390,390,340,Instructions[2],"#34D399");
        Note(0,390,390,340,Instructions[3],"#C084FC");
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

    private static readonly string[] Instructions =
    [
        "1  •  Welcome to your canvas\n\nYour apps and notes share one large workspace.\n\nHold Space and drag with the left mouse button to look around. On a touchpad, scroll with two fingers.\n\nNeed help? Controls is in the top-right corner.",
        "2  •  Zoom in and out\n\nHold Ctrl and scroll to zoom around your pointer.\n\nTry zooming out to see more of your workspace, then back in to read the details.\n\nYou can also press + / −.",
        "3  •  Move a window\n\nChoose Arrange at the top right. Drag a window or app card to a new spot.\n\nDouble-click it to open the app at desktop size.\n\nAfter the tour, zoom in and choose Interact to use apps on the canvas.",
        "4  •  Write your first note\n\nChoose Text in the bottom toolbar. Click empty canvas and type a thought.\n\nPress Ctrl+Enter to save it.\n\nWant to change it? Double-click the note to edit it."
    ];

    /// <summary>Refresh saved help without relocating artwork or changing its identities.</summary>
    public TutorialLayout WithInstructions()
    {
        int index=0;
        CanvasAnnotation[]? updated=null;
        for(int i=0;i<Items.Length && index<Instructions.Length;i++)
        {
            if(Items[i].Kind!=AnnotationKind.Text)continue;
            string instruction=Instructions[index++];
            if(Items[i].Text==instruction)continue;
            updated??=(CanvasAnnotation[])Items.Clone();
            updated[i]=Items[i] with { Text=instruction };
        }
        return updated==null?this:this with { Items=updated };
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
