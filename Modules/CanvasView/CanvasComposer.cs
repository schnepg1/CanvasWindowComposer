namespace CanvasDesktop.CanvasView;

public sealed record ComposerNode(string Id, string Kind, string Text, string Color);
public sealed record ComposerEdge(string From, string To, string Color);
public sealed record ComposerEdit(string Id, string Text);
public sealed record ComposerPlan(string Summary, ComposerNode[] Nodes, ComposerEdge[] Edges, ComposerEdit[] Edits);
public sealed record ComposerReference(Guid Id, string Kind, string Title, CanvasRect Bounds);
public sealed record ComposerContext(Guid Desktop, long Revision, CanvasPoint Anchor,
    IReadOnlyList<CanvasAnnotation> SharedAnnotations, IReadOnlyList<CanvasRect> Obstacles)
{
    public IReadOnlyList<ComposerReference> SharedReferences { get; init; } = [];
    public bool PreserveWording { get; init; }
}
public sealed record ComposerPreview(Guid Desktop, long Revision, string Summary, IReadOnlyList<CanvasAnnotation> Items)
{
    public IReadOnlyList<ComposerReference> References { get; init; } = [];
}

/// <summary>Provider-independent validation and deterministic world-space layout. No model-generated coordinates.</summary>
public static class CanvasComposer
{
    public const int MaximumNodes = 40;
    public const int MaximumEdges = 60;
    public const int MaximumContextItems = 20;
    public const int MaximumContextCharacters = 32000;

    public static ComposerPreview Prepare(ComposerContext context, ComposerPlan plan)
    {
        if (plan is null || plan.Summary is null || plan.Summary.Length > 1000
            || plan.Nodes is null || plan.Edges is null || plan.Edits is null
            || plan.Nodes.Length > MaximumNodes || plan.Edges.Length > MaximumEdges || plan.Edits.Length > MaximumContextItems
            || plan.Nodes.Length + plan.Edits.Length == 0)
            throw new ArgumentException("The response does not contain a supported canvas proposal.");
        var shared = context.SharedAnnotations.ToDictionary(a => a.Id);
        var nodeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in plan.Nodes)
            if (node is null || string.IsNullOrWhiteSpace(node.Id) || node.Id.Length > 64 || !nodeIds.Add(node.Id)
                || node.Kind is not ("note" or "rectangle" or "ellipse") || !AnnotationDocument.ValidColor(node.Color)
                || node.Text is null || (!(Guid.TryParse(node.Id,out var sharedId) && shared.ContainsKey(sharedId)) && (string.IsNullOrWhiteSpace(node.Text) || node.Text.Length > 2000)))
                throw new ArgumentException("Invalid diagram node.");
        var edges = new HashSet<(string, string)>();
        foreach (var edge in plan.Edges)
            if (edge is null || !nodeIds.Contains(edge.From) || !nodeIds.Contains(edge.To) || edge.From == edge.To
                || !edges.Add((edge.From, edge.To)) || !AnnotationDocument.ValidColor(edge.Color))
                throw new ArgumentException("Invalid diagram connection.");
        var reused = new Dictionary<string,CanvasAnnotation>();
        foreach (var node in plan.Nodes)
        {
            if (!Guid.TryParse(node.Id,out var existingId)) continue;
            if (!shared.TryGetValue(existingId,out var existing) || existing.DesktopId!=context.Desktop || existing.Kind==AnnotationKind.Arrow)
                throw new ArgumentException("The proposal tried to arrange an object outside the shared context.");
            reused.Add(node.Id,existing);
        }
        if (context.PreserveWording && (plan.Edits.Length>0 || plan.Edges.Length>0 || reused.Count!=plan.Nodes.Length
            || !reused.Values.Select(a=>a.Id).ToHashSet().SetEquals(shared.Values.Where(a=>a.Kind!=AnnotationKind.Arrow).Select(a=>a.Id))))
            throw new ArgumentException("Layout-only proposals must reuse every checked note or shape and cannot add or rewrite objects.");
        var edited = new HashSet<Guid>();
        var output = new List<CanvasAnnotation>();
        foreach (var edit in plan.Edits)
        {
            if (edit is null || !Guid.TryParse(edit.Id, out var id) || !edited.Add(id)
                || !shared.TryGetValue(id, out var item) || item.DesktopId != context.Desktop
                || item.Kind != AnnotationKind.Text || string.IsNullOrWhiteSpace(edit.Text)
                || reused.Values.Any(a => a.Id==id) || edit.Text.Length > AnnotationDocument.MaximumTextLength)
                throw new ArgumentException("The proposal tried to edit a note outside the shared context.");
            output.Add(item with { Text = edit.Text });
        }
        // Topological layers for DAGs. Cycles fall back to a stable three-column layout.
        var rank = plan.Nodes.ToDictionary(n => n.Id, _ => 0);
        var degree = plan.Nodes.ToDictionary(n => n.Id, n => plan.Edges.Count(e => e.To == n.Id));
        var queue = new Queue<string>(plan.Nodes.Where(n => degree[n.Id] == 0).Select(n => n.Id));
        int visited = 0;
        while (queue.TryDequeue(out var id))
        {
            visited++;
            foreach (var edge in plan.Edges.Where(e => e.From == id))
            {
                rank[edge.To] = Math.Max(rank[edge.To], rank[id] + 1);
                if (--degree[edge.To] == 0) queue.Enqueue(edge.To);
            }
        }
        if (visited != plan.Nodes.Length || plan.Edges.Length == 0)
            for (int i = 0; i < plan.Nodes.Length; i++) rank[plan.Nodes[i].Id] = i % 3;
        var positions = new Dictionary<string, CanvasRect>();
        foreach (var node in plan.Nodes)
        {
            int column = rank[node.Id];
            // Height follows text length; wrapping is estimated locally, never taken from model coordinates.
            double height = reused.TryGetValue(node.Id,out var original) ? original.Bounds.Height : Math.Clamp(60 + Math.Ceiling(node.Text.Length / 28.0) * 24, 160, 1800);
            double y = positions.Where(p => rank[p.Key] == column).Select(p => p.Value.Y + p.Value.Height + 60).DefaultIfEmpty(0).Max();
            double x=Enumerable.Range(0,column).Sum(c => plan.Nodes.Where(n => rank[n.Id]==c)
                .Select(n => reused.TryGetValue(n.Id,out var size)?size.Bounds.Width:300).DefaultIfEmpty(300).Max()+100);
            positions[node.Id] = new(x, y, reused.TryGetValue(node.Id,out var sized)?sized.Bounds.Width:300, height);
        }
        double width = positions.Values.Select(r => r.X + r.Width).DefaultIfEmpty(0).Max();
        double heightTotal = positions.Values.Select(r => r.Y + r.Height).DefaultIfEmpty(0).Max();
        // Existing annotations may occupy their old footprint; unrelated objects remain obstacles.
        var obstacles=context.Obstacles.Where(r => !reused.Values.Any(a => a.Bounds==r)).ToArray();
        var origin = FindSpace(context.Anchor, width, heightTotal, obstacles);
        var nodes = new Dictionary<string, CanvasAnnotation>();
        foreach (var node in plan.Nodes)
        {
            var r = positions[node.Id];
            var item = new CanvasAnnotation(Guid.NewGuid(), context.Desktop,
                node.Kind == "rectangle" ? AnnotationKind.Rectangle : node.Kind == "ellipse" ? AnnotationKind.Ellipse : AnnotationKind.Text,
                new(origin.X + r.X, origin.Y + r.Y), new(origin.X + r.X + r.Width, origin.Y + r.Y + r.Height), node.Color, 3, node.Text);
            if (reused.TryGetValue(node.Id,out var original))
                item=original.Translate(item.A.X-original.Bounds.X,item.A.Y-original.Bounds.Y);
            nodes.Add(node.Id, item); output.Add(item);
        }
        foreach (var edge in plan.Edges)
        {
            var from = nodes[edge.From]; var to = nodes[edge.To];
            output.Add(new(Guid.NewGuid(), context.Desktop, AnnotationKind.Arrow,
                Boundary(from, Center(to.Bounds)), Boundary(to, Center(from.Bounds)), edge.Color));
        }
        foreach (var arrow in context.SharedAnnotations.Where(a => a.Kind==AnnotationKind.Arrow))
        {
            CanvasPoint Follow(CanvasPoint endpoint)
            {
                var match=reused.FirstOrDefault(pair => CanvasSelection.Contains(new(pair.Value.Bounds.X-24,pair.Value.Bounds.Y-24,pair.Value.Bounds.Width+48,pair.Value.Bounds.Height+48),endpoint));
                if (match.Value==null) return endpoint;
                var arranged=nodes[match.Key];
                return new(endpoint.X+arranged.Bounds.X-match.Value.Bounds.X,endpoint.Y+arranged.Bounds.Y-match.Value.Bounds.Y);
            }
            var linked=arrow with { A=Follow(arrow.A),B=Follow(arrow.B) };
            if (linked!=arrow) output.Add(linked);
        }
        AnnotationDocument.Validate(output);
        return new(context.Desktop, context.Revision, plan.Summary, output.AsReadOnly()) { References=context.SharedReferences.ToArray() };
    }

    private static CanvasPoint Center(CanvasRect r) => new(r.X + r.Width / 2, r.Y + r.Height / 2);
    private static CanvasPoint Boundary(CanvasAnnotation node, CanvasPoint toward)
    {
        var r = node.Bounds; var center = Center(r); double dx = toward.X - center.X, dy = toward.Y - center.Y;
        double scale = node.Kind == AnnotationKind.Ellipse
            ? 1 / Math.Sqrt(dx * dx / (r.Width * r.Width / 4) + dy * dy / (r.Height * r.Height / 4))
            : 1 / Math.Max(Math.Abs(dx) / (r.Width / 2), Math.Abs(dy) / (r.Height / 2));
        return new(center.X + dx * scale, center.Y + dy * scale);
    }
    private static CanvasPoint FindSpace(CanvasPoint anchor, double width, double height, IReadOnlyList<CanvasRect> obstacles)
    {
        if (width == 0) return anchor;
        for (int attempt = 0; attempt < 100; attempt++)
        {
            double x = anchor.X + (attempt % 10) * (width + 80), y = anchor.Y + (attempt / 10) * (height + 80);
            if (obstacles.All(r => x + width + 40 < r.X || x > r.X + r.Width + 40 || y + height + 40 < r.Y || y > r.Y + r.Height + 40))
                return new(x, y);
        }
        throw new ArgumentException("No clear space found nearby. Pan to an empty area and try again.");
    }
}
