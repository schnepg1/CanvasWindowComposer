using System;
using System.Linq;

namespace CanvasDesktop;

/// <summary>Explicit editing of one free background. Normal navigation never hits backgrounds.</summary>
internal sealed class BackgroundCanvasTool(BackgroundService service) : ICanvasTool
{
    public Guid? SelectedId { get; private set; }
    public BackgroundSurface? Preview { get; private set; }
    private BackgroundSurface? _original;
    private CanvasPoint _start;
    private int _corner = -1;
    public bool HasPendingInteraction => _original != null;
    public event Action? Changed;
    public event Action<BackgroundSurface>? Completed;

    public bool Select(Guid id)
    {
        Cancel();
        var item = service.Items.FirstOrDefault(b => b.Id == id);
        if (item == null || item.ZoneId != null || item.Locked) return false;
        SelectedId = id; Changed?.Invoke(); return true;
    }

    public void Deselect() { Cancel(); SelectedId = null; Changed?.Invoke(); }

    public bool PointerDown(CanvasPointer p)
    {
        if (p.Button != CanvasPointerButton.Left || SelectedId == null) return false;
        var item = service.Items.FirstOrDefault(b => b.Id == SelectedId);
        if (item == null || item.ZoneId != null || item.Locked) return false;
        _corner = HitCorner(item, p);
        var w = p.World;
        var b = item.Bounds;
        // Only a thin frame and its handles intercept input; apps inside remain usable.
        double edge = 10 / p.View.Zoom;
        bool frame = w.X >= b.X-edge && w.X <= b.X+b.W+edge && w.Y >= b.Y-edge && w.Y <= b.Y+b.H+edge
            && (Math.Abs(w.X-b.X)<edge || Math.Abs(w.X-b.X-b.W)<edge || Math.Abs(w.Y-b.Y)<edge || Math.Abs(w.Y-b.Y-b.H)<edge);
        if (_corner < 0 && !frame) return false;
        _start = w; _original = item; Preview = item; return true;
    }

    public void PointerMove(CanvasPointer p)
    {
        if (_original == null) return;
        double dx=p.World.X-_start.X, dy=p.World.Y-_start.Y;
        var b=_original.Bounds;
        if (_corner < 0) { b.X+=dx; b.Y+=dy; }
        else
        {
            bool left=_corner is 0 or 2, top=_corner is 0 or 1;
            double anchorX=left?b.X+b.W:b.X, anchorY=top?b.Y+b.H:b.Y;
            double width=Math.Max(1,left?b.W-dx:b.W+dx), height=Math.Max(1,top?b.H-dy:b.H+dy);
            if (p.Modifiers.HasFlag(CanvasModifiers.Shift))
            {
                double ratio=b.W/b.H;
                if (width/height>ratio) height=width/ratio; else width=height*ratio;
            }
            b.X=left?anchorX-width:anchorX; b.Y=top?anchorY-height:anchorY; b.W=width; b.H=height;
        }
        if (ZoneGeometry.ValidBounds(b)) Preview=_original with { Bounds=b };
        Changed?.Invoke();
    }

    public void PointerUp(CanvasPointer p)
    {
        PointerMove(p);
        var edit=Preview;
        var before=_original;
        _original=null; Preview=null;
        if (edit != null && before != null && edit != before) Completed?.Invoke(edit);
        Changed?.Invoke();
    }

    public void Cancel() { _original=null; Preview=null; Changed?.Invoke(); }
    public CanvasCursor CursorAt(CanvasPointer p)
    {
        var item=service.Items.FirstOrDefault(b=>b.Id==SelectedId);
        if (item==null) return CanvasCursor.Default;
        return HitCorner(item,p) switch
        { 0 or 3=>CanvasCursor.ResizeNorthWestSouthEast, 1 or 2=>CanvasCursor.ResizeNorthEastSouthWest, _=>CanvasCursor.Default };
    }

    private static int HitCorner(BackgroundSurface item,CanvasPointer p)
    {
        var b=item.Bounds;
        CanvasPoint[] points=[new(b.X,b.Y),new(b.X+b.W,b.Y),new(b.X,b.Y+b.H),new(b.X+b.W,b.Y+b.H)];
        for(int i=0;i<points.Length;i++)
        {
            var local=p.View.WorldToLocal(points[i]);
            if(Math.Abs(local.X-p.Position.X)<=10 && Math.Abs(local.Y-p.Position.Y)<=10) return i;
        }
        return -1;
    }
}
