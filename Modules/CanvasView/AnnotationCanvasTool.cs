namespace CanvasDesktop.CanvasView;

public sealed class AnnotationCanvasTool(AnnotationDocument document, Func<Guid> desktop) : ICanvasTool
{
    public AnnotationMode Mode { get; private set; }
    public Guid? SelectedId { get; private set; }
    public string Color { get; private set; } = "#FBBF24";
    public double Stroke { get; private set; } = 3;
    public CanvasAnnotation? Preview { get; private set; }
    private CanvasAnnotation? _original;
    private CanvasPoint _start;
    private Guid _gestureDesktop;
    private int _handle = -1;
    private bool _active, _creating;
    public bool HasPendingInteraction => _active;
    public CanvasAnnotation? Selected => document.Items.FirstOrDefault(a=>a.Id==SelectedId && a.DesktopId==desktop());
    public event Action<CanvasAnnotation>? EditTextRequested;
    public void SetMode(AnnotationMode mode) { Cancel(); Mode=mode; if (mode==AnnotationMode.Navigate) SelectedId=null; }
    public void ClearSelection() { Cancel(); SelectedId=null; }
    public void SetStyle(string color, double stroke)
    {
        if (!AnnotationDocument.ValidColor(color) || !double.IsFinite(stroke) || stroke<1 || stroke>12) throw new ArgumentException("Invalid style.");
        Cancel(); Color=color; Stroke=stroke;
        if (Selected is {} item) document.Put(item with { Color=color,Stroke=stroke });
    }
    public CanvasAnnotation? Hit(CanvasPointer pointer) => document.Items.LastOrDefault(a=>a.DesktopId==desktop() && AnnotationGeometry.Hit(a,pointer.World,pointer.View));
    public bool PointerDown(CanvasPointer p)
    {
        if (p.Button!=CanvasPointerButton.Left || Mode==AnnotationMode.Navigate) return false;
        Cancel(); _gestureDesktop=desktop(); _start=p.World; _active=true;
        if (Mode==AnnotationMode.Select)
        {
            var selected=Selected;
            _handle=selected is null?-1:AnnotationGeometry.HandleAt(selected,p);
            _original=_handle>=0?selected:Hit(p);
            SelectedId=_original?.Id; Preview=_original; return true;
        }
        _creating=true; SelectedId=null;
        var kind=Mode switch { AnnotationMode.Arrow=>AnnotationKind.Arrow,AnnotationMode.Rectangle=>AnnotationKind.Rectangle,AnnotationMode.Ellipse=>AnnotationKind.Ellipse,_=>AnnotationKind.Text };
        Preview=new(Guid.NewGuid(),_gestureDesktop,kind,_start,_start,Color,Stroke);
        return true;
    }
    public void PointerMove(CanvasPointer p)
    {
        if (!_active) return;
        if (desktop()!=_gestureDesktop) { ClearSelection(); return; }
        var end=p.World;
        if (_creating && Preview is {} drawing)
        {
            if (p.Modifiers.HasFlag(CanvasModifiers.Shift)) end=AnnotationGeometry.Constrain(_start,end,drawing.Kind==AnnotationKind.Arrow);
            Preview=drawing with { B=end }; return;
        }
        if (_original is not {} original) return;
        if (_handle<0) { Preview=original.Translate(end.X-_start.X,end.Y-_start.Y); return; }
        Preview=AnnotationGeometry.Resize(original,_handle,new(end.X-_start.X,end.Y-_start.Y),p.Modifiers.HasFlag(CanvasModifiers.Shift));
    }
    public void PointerUp(CanvasPointer p)
    {
        if (!_active) return;
        PointerMove(p); if (!_active) return;
        var result=Preview; bool creating=_creating;
        Cancel();
        if (result is null) return;
        if (creating && result.Kind==AnnotationKind.Text)
        {
            result=result with { B=new(result.A.X+240,result.A.Y+120) };
            EditTextRequested?.Invoke(result); return;
        }
        // Reject accidental clicks or invalid/out-of-range previews without changing history.
        if (creating && AnnotationGeometry.Distance(result.A,result.B)*p.View.Zoom < 4) return;
        try { AnnotationDocument.Validate([result]); } catch (ArgumentException) { return; }
        if (creating && document.Items.Count >= AnnotationDocument.MaximumItems) return;
        document.Put(result); SelectedId=result.Id;
    }
    public void Select(Guid id) { Cancel(); SelectedId=document.Items.Any(a=>a.Id==id && a.DesktopId==desktop())?id:null; }
    public void Delete() { if (Selected is {} item) document.Delete(desktop(),item.Id); ClearSelection(); }
    public void Cancel() { _active=false; _creating=false; Preview=null; _original=null; _handle=-1; }
    public CanvasCursor CursorAt(CanvasPointer p)
    {
        if (Mode is not (AnnotationMode.Navigate or AnnotationMode.Select)) return CanvasCursor.Crosshair;
        if (Mode == AnnotationMode.Navigate) return CanvasCursor.Default;
        if (Selected is {} item)
        {
            int handle = _active ? _handle : AnnotationGeometry.HandleAt(item,p);
            if (handle >= 0)
                return item.Kind == AnnotationKind.Arrow ? CanvasCursor.Crosshair : handle switch
                {
                    0 or 2 => CanvasCursor.ResizeNorthWestSouthEast,
                    1 or 3 => CanvasCursor.ResizeNorthEastSouthWest,
                    4 or 6 => CanvasCursor.ResizeNorthSouth,
                    _ => CanvasCursor.ResizeEastWest
                };
        }
        return Hit(p) != null ? CanvasCursor.Move : CanvasCursor.Default;
    }
    public bool HasHandleAt(CanvasPointer p) => Mode == AnnotationMode.Select && Selected is {} item && AnnotationGeometry.HandleAt(item, p) >= 0;
}
