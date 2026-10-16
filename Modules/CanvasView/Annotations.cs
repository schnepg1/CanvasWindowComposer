namespace CanvasDesktop.CanvasView;

public enum AnnotationKind { Arrow, Rectangle, Ellipse, Text }
public enum AnnotationMode { Navigate, Select, Arrow, Rectangle, Ellipse, Text }
public sealed record CanvasAnnotation(Guid Id, Guid DesktopId, AnnotationKind Kind,
    CanvasPoint A, CanvasPoint B, string Color = "#FBBF24", double Stroke = 3, string Text = "")
{
    public CanvasRect Bounds => new(Math.Min(A.X, B.X), Math.Min(A.Y, B.Y), Math.Abs(B.X - A.X), Math.Abs(B.Y - A.Y));
    public CanvasAnnotation Translate(double x, double y) => this with { A = new(A.X + x, A.Y + y), B = new(B.X + x, B.Y + y) };
}

/// <summary>Immutable edits, desktop-local bounded history; previews never enter the document.</summary>
public sealed class AnnotationDocument
{
    public const int MaximumItems = 2000;
    public const int MaximumTextLength = 10000;
    private readonly List<CanvasAnnotation> _items = [];
    private sealed record HistoryState(CanvasAnnotation[] Items, Func<bool, bool>? Replay = null);
    private readonly Dictionary<Guid, (List<HistoryState> Undo, List<HistoryState> Redo)> _history = [];
    public IReadOnlyList<CanvasAnnotation> Items => _items.AsReadOnly();
    public long Revision { get; private set; }
    public event Action? Changed;
    public AnnotationDocument(IEnumerable<CanvasAnnotation>? items = null)
    {
        var list = items?.ToArray() ?? [];
        Validate(list); _items.AddRange(list);
    }
    public static void Validate(IEnumerable<CanvasAnnotation> items)
    {
        var ids = new HashSet<Guid>(); int count = 0;
        foreach (var item in items)
        {
            if (++count > MaximumItems || item is null || item.Id == Guid.Empty || !ids.Add(item.Id)
                || !Enum.IsDefined(item.Kind) || !ValidPoint(item.A) || !ValidPoint(item.B)
                || !double.IsFinite(item.Stroke) || item.Stroke < 1 || item.Stroke > 12
                || !ValidColor(item.Color) || item.Text is null || item.Text.Length > MaximumTextLength
                || (item.Kind == AnnotationKind.Arrow ? AnnotationGeometry.Distance(item.A, item.B) < 1
                    : item.Bounds.Width < 1 || item.Bounds.Height < 1)
                || (item.Kind == AnnotationKind.Text && string.IsNullOrWhiteSpace(item.Text)))
                throw new ArgumentException("Invalid canvas annotation.");
        }
    }
    private static bool ValidPoint(CanvasPoint p) => double.IsFinite(p.X) && double.IsFinite(p.Y) && Math.Abs(p.X) <= 10000000 && Math.Abs(p.Y) <= 10000000;
    public static bool ValidColor(string? s) => s is { Length: 7 } && s[0] == '#' && s.AsSpan(1).IndexOfAnyExcept("0123456789ABCDEFabcdef") < 0;
    private (List<HistoryState> Undo, List<HistoryState> Redo) History(Guid desktop)
    {
        if (!_history.TryGetValue(desktop, out var h)) _history[desktop] = h = ([], []);
        return h;
    }
    private void NotifyChanged() { Revision++; Changed?.Invoke(); }

    /// <summary>Validate the entire transaction before changing anything. One history step and notification.</summary>
    public bool ApplyBatch(long expectedRevision, Guid desktop, IReadOnlyList<CanvasAnnotation> upserts, Func<bool, bool>? replay = null)
    {
        if (expectedRevision != Revision) throw new InvalidOperationException("The canvas changed. Generate a fresh preview.");
        var batch = upserts.ToArray();
        Validate(batch);
        if (batch.Any(a => a.DesktopId != desktop)) throw new ArgumentException("The edit belongs to another desktop.");
        var ids = batch.Select(a => a.Id).ToHashSet();
        if (_items.Any(a => ids.Contains(a.Id) && a.DesktopId != desktop)) throw new ArgumentException("Cannot change another desktop.");
        var next = _items.Select(a => batch.FirstOrDefault(b => b.Id == a.Id) ?? a).ToList();
        next.AddRange(batch.Where(a => !_items.Any(b => b.Id == a.Id)));
        Validate(next);
        if (_items.SequenceEqual(next) && replay == null) return false;
        if (replay?.Invoke(true)==false) throw new InvalidOperationException("The selected group changed. Select it again.");
        Remember(desktop, replay); _items.Clear(); _items.AddRange(next); NotifyChanged(); return true;
    }

    public void Put(CanvasAnnotation item)
    {
        var index = _items.FindIndex(a => a.Id == item.Id);
        if (index >= 0 && _items[index].DesktopId != item.DesktopId) throw new ArgumentException("Cannot move an annotation between desktops.");
        if (index >= 0 && _items[index] == item) return;
        var next = _items.Where(a => a.Id != item.Id).Append(item).ToArray(); Validate(next);
        Remember(item.DesktopId);
        if (index < 0) _items.Add(item); else _items[index] = item;
        NotifyChanged();
    }
    public void Delete(Guid desktop, Guid id)
    {
        if (!_items.Any(a => a.Id == id && a.DesktopId == desktop)) return;
        Remember(desktop); _items.RemoveAll(a => a.Id == id); NotifyChanged();
    }
    private CanvasAnnotation[] Snapshot(Guid desktop) => _items.Where(a => a.DesktopId == desktop).ToArray();
    private void Remember(Guid desktop, Func<bool, bool>? replay = null)
    {
        var h = History(desktop); h.Undo.Add(new(Snapshot(desktop), replay)); h.Redo.Clear();
        if (h.Undo.Count > 100) h.Undo.RemoveAt(0);
    }
    public bool Undo(Guid desktop, bool redo = false)
    {
        var h = History(desktop); var from = redo ? h.Redo : h.Undo; var to = redo ? h.Undo : h.Redo;
        if (from.Count == 0) return false;
        var snapshot = from[^1];
        if (_items.Count(a => a.DesktopId != desktop) + snapshot.Items.Length > MaximumItems) return false;
        // A companion validates all saved identities and positions before either half replays.
        if (snapshot.Replay?.Invoke(redo) == false) return false;
        to.Add(new(Snapshot(desktop), snapshot.Replay)); from.RemoveAt(from.Count - 1);
        _items.RemoveAll(a => a.DesktopId == desktop); _items.AddRange(snapshot.Items); NotifyChanged(); return true;
    }
}

public static class AnnotationGeometry
{
    public static double Distance(CanvasPoint a, CanvasPoint b) => Math.Sqrt((a.X-b.X)*(a.X-b.X)+(a.Y-b.Y)*(a.Y-b.Y));
    public static CanvasPoint Constrain(CanvasPoint start, CanvasPoint end, bool arrow)
    {
        double dx = end.X-start.X, dy = end.Y-start.Y;
        if (arrow)
        {
            double angle = Math.Round(Math.Atan2(dy,dx)/(Math.PI/4))*(Math.PI/4), length = Distance(start,end);
            return new(start.X+Math.Cos(angle)*length,start.Y+Math.Sin(angle)*length);
        }
        double size = Math.Max(Math.Abs(dx),Math.Abs(dy));
        return new(start.X+(dx<0?-size:size),start.Y+(dy<0?-size:size));
    }
    public static bool Hit(CanvasAnnotation item, CanvasPoint p, CanvasViewTransform view)
    {
        // Screen-space distance keeps picking usable at every zoom/DPI, including nonuniform DPI.
        CanvasPoint Project(CanvasPoint q) => view.LocalToDip(view.WorldToLocal(q));
        p = Project(p); var a=Project(item.A); var b=Project(item.B);
        if (item.Kind == AnnotationKind.Arrow)
        {
            double dx=b.X-a.X,dy=b.Y-a.Y,den=dx*dx+dy*dy;
            double t=den==0?0:Math.Clamp(((p.X-a.X)*dx+(p.Y-a.Y)*dy)/den,0,1);
            return Distance(p,new(a.X+t*dx,a.Y+t*dy)) <= 8+item.Stroke*view.Zoom/2;
        }
        double x=Math.Min(a.X,b.X), y=Math.Min(a.Y,b.Y), w=Math.Abs(b.X-a.X),h=Math.Abs(b.Y-a.Y);
        if (item.Kind == AnnotationKind.Ellipse)
            return Math.Pow((p.X-x-w/2)/(w/2+8),2)+Math.Pow((p.Y-y-h/2)/(h/2+8),2)<=1;
        return p.X>=x-8 && p.Y>=y-8 && p.X<=x+w+8 && p.Y<=y+h+8;
    }
    public static CanvasPoint[] Handles(CanvasAnnotation item)
    {
        if (item.Kind==AnnotationKind.Arrow) return [item.A,item.B];
        var r=item.Bounds;
        return [new(r.X,r.Y),new(r.X+r.Width,r.Y),new(r.X+r.Width,r.Y+r.Height),new(r.X,r.Y+r.Height)];
    }
}
