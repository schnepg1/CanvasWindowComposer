namespace CanvasDesktop.CanvasView;

public enum CanvasItemKind { Annotation, Window, Project }
public readonly record struct CanvasItemId(CanvasItemKind Kind, Guid Id);
public sealed record CanvasSelectionItem(CanvasItemId Key, CanvasRect Bounds, string Title = "",
    IReadOnlyList<CanvasItemId>? Members = null, CanvasAnnotation? Annotation = null);
public sealed record CanvasSelectionMove(IReadOnlyList<CanvasSelectionItem> Before, double Dx, double Dy);

/// <summary>Desktop-scoped identities. Project membership combines explicit links and spatial contents.</summary>
public sealed class CanvasSelection
{
    private readonly HashSet<CanvasItemId> _ids = [];
    public IReadOnlySet<CanvasItemId> Ids => new HashSet<CanvasItemId>(_ids);
    public Guid Desktop { get; private set; }
    public event Action? Changed;
    public void SwitchDesktop(Guid desktop) { if (Desktop == desktop) return; Desktop = desktop; Clear(); }
    public void Clear() => Replace([]);
    public void Replace(IEnumerable<CanvasItemId> ids)
    {
        var next = ids.ToHashSet();
        if (_ids.SetEquals(next)) return;
        _ids.Clear(); _ids.UnionWith(next); Changed?.Invoke();
    }
    public void Pick(CanvasItemId key, IReadOnlyList<CanvasSelectionItem> items, bool toggle)
    {
        var group = Expand([key], items);
        if (!toggle) Replace(group);
        else if (_ids.Contains(key)) Replace(_ids.Except(group).ToArray());
        else Replace(_ids.Concat(group).ToArray());
    }
    public void Rectangle(CanvasRect rect, IReadOnlyList<CanvasSelectionItem> items, IEnumerable<CanvasItemId>? baseline = null)
        => Replace(Expand(items.Where(i => i.Key.Kind==CanvasItemKind.Project
            ? Contains(rect,new(i.Bounds.X,i.Bounds.Y)) && Contains(rect,new(i.Bounds.X+i.Bounds.Width,i.Bounds.Y+i.Bounds.Height))
            : i.Annotation is {} annotation ? SelectionGeometry.Intersects(annotation,rect) : Intersects(rect,i.Bounds))
            .Select(i => i.Key), items).Concat(baseline ?? []));
    public void Nearby(CanvasPoint center, double radius, IReadOnlyList<CanvasSelectionItem> items, bool add = false)
    {
        if (!double.IsFinite(radius) || radius < 1 || radius > 100000) throw new ArgumentOutOfRangeException(nameof(radius));
        // Rectangle/circle intersection includes a large window whose edge is close to the camera.
        bool Inside(CanvasRect r)
        {
            double x = Math.Clamp(center.X, r.X, r.X + r.Width), y = Math.Clamp(center.Y, r.Y, r.Y + r.Height);
            return (x-center.X)*(x-center.X) + (y-center.Y)*(y-center.Y) <= radius*radius;
        }
        // A selected project remains one group, including explicitly assigned members outside its tile.
        Replace(Expand(items.Where(i => i.Key.Kind==CanvasItemKind.Project
            ? Math.Pow(Math.Abs(Center(i.Bounds).X-center.X)+i.Bounds.Width/2,2)+Math.Pow(Math.Abs(Center(i.Bounds).Y-center.Y)+i.Bounds.Height/2,2)<=radius*radius
            : i.Annotation is {} annotation ? SelectionGeometry.Within(annotation,center,radius) : Inside(i.Bounds))
            .Select(i => i.Key),items).Concat(add ? _ids.ToArray() : []));
    }
    public static HashSet<CanvasItemId> Expand(IEnumerable<CanvasItemId> roots, IReadOnlyList<CanvasSelectionItem> items)
    {
        var byId = items.ToDictionary(i => i.Key); var result = new HashSet<CanvasItemId>();
        var queue = new Queue<CanvasItemId>(roots);
        while (queue.TryDequeue(out var key))
        {
            if (!byId.TryGetValue(key, out var item) || !result.Add(key) || key.Kind != CanvasItemKind.Project) continue;
            foreach (var member in item.Members ?? []) queue.Enqueue(member);
            // The center decides membership, preventing accidental capture of merely overlapping neighbours.
            foreach (var child in items.Where(c => c.Key.Kind != CanvasItemKind.Project && Contains(item.Bounds, Center(c.Bounds))))
                queue.Enqueue(child.Key);
        }
        return result;
    }
    public static CanvasPoint Center(CanvasRect r) => new(r.X + r.Width/2, r.Y + r.Height/2);
    public static bool Contains(CanvasRect r, CanvasPoint p) => p.X>=r.X && p.Y>=r.Y && p.X<=r.X+r.Width && p.Y<=r.Y+r.Height;
    public static bool Intersects(CanvasRect a, CanvasRect b) => a.X<=b.X+b.Width && a.X+a.Width>=b.X && a.Y<=b.Y+b.Height && a.Y+a.Height>=b.Y;
}

/// <summary>Transient marquee and group ghosts; the host validates and commits one move on release.</summary>
public sealed class CanvasSelectionTool(CanvasSelection selection, Func<IReadOnlyList<CanvasSelectionItem>> items,
    Func<CanvasPointer, CanvasItemId?> hit, Func<bool> marqueeEnabled) : ICanvasTool
{
    private CanvasSelectionItem[]? _snapshot;
    private CanvasItemId[] _baseline = [], _previous = [];
    private CanvasPoint _start;
    private Guid _desktop;
    private double _zoom;
    private bool _marquee;
    public bool HasPendingInteraction => _snapshot != null;
    public CanvasRect? Marquee { get; private set; }
    public CanvasPoint Offset { get; private set; }
    public IReadOnlyList<CanvasSelectionItem> Ghosts => _marquee ? [] : _snapshot ?? [];
    public event Action<CanvasSelectionMove>? MoveRequested;
    public bool PointerDown(CanvasPointer p)
    {
        if (p.Button != CanvasPointerButton.Left) return false;
        var all = items(); var key = hit(p); bool ctrl = p.Modifiers.HasFlag(CanvasModifiers.Control);
        if (key == null && !marqueeEnabled() && !ctrl) { selection.Clear(); return false; }
        Cancel(); _desktop = selection.Desktop; _start = p.World; _zoom = p.View.Zoom;
        _baseline = selection.Ids.ToArray(); _previous=_baseline; _marquee = key == null;
        if (key is {} id)
        {
            if (ctrl || !selection.Ids.Contains(id)) selection.Pick(id, all, ctrl);
            var selected=selection.Ids;
            _snapshot = all.Where(i => selected.Contains(i.Key)).ToArray();
            // A Ctrl-click toggles only; it must never start moving the remaining group.
            if (ctrl) _snapshot = [];
        }
        else
        {
            _snapshot = all.ToArray();
            if (!ctrl) _baseline = [];
            selection.Replace(_baseline);
        }
        return true;
    }
    public void PointerMove(CanvasPointer p)
    {
        if (_snapshot == null) return;
        if (selection.Desktop != _desktop) { Cancel(); return; }
        Offset = new(p.World.X-_start.X, p.World.Y-_start.Y);
        if (_marquee)
        {
            Marquee = new(Math.Min(_start.X,p.World.X),Math.Min(_start.Y,p.World.Y),Math.Abs(Offset.X),Math.Abs(Offset.Y));
            selection.Rectangle(Marquee.Value,_snapshot,_baseline);
        }
    }
    public void PointerUp(CanvasPointer p)
    {
        PointerMove(p);
        if (_snapshot == null) return;
        var move = !_marquee && AnnotationGeometry.Distance(new(0,0),Offset)*_zoom >= 4
            ? new CanvasSelectionMove(_snapshot,Offset.X,Offset.Y) : null;
        _snapshot=null; Marquee=null; Offset=default;
        if (move?.Before.Count > 0) MoveRequested?.Invoke(move);
    }
    public void Cancel()
    {
        if (_snapshot != null && _marquee && selection.Desktop == _desktop) selection.Replace(_previous);
        _snapshot=null; Marquee=null; Offset=default;
    }
}
