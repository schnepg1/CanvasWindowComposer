namespace CanvasDesktop.CanvasView;

public sealed record ComposerZone(string Id, string Name, string Color = "#38BDF8");

public sealed record ComposerNode(
    string Id,
    string Kind,
    string Text,
    string Color,
    string Title = "",
    string Subtitle = "",
    string App = "",
    string Url = "",
    string ChromeProfile = "",
    string Zone = "");

public sealed record ComposerEdge(
    string From,
    string To,
    string Color,
    string Label = "",
    double Stroke = 16.0,
    string Routing = "curved");

public sealed record ComposerEdit(string Id, string Text);

public sealed record ComposerPlan(
    string Summary,
    ComposerNode[] Nodes,
    ComposerEdge[] Edges,
    ComposerEdit[] Edits,
    ComposerZone[]? Zones = null,
    string Direction = "LR")
{
    public bool HasWorkflowEntities() =>
        (Zones != null && Zones.Length > 0) ||
        Nodes.Any(n => n.Kind is "web" or "window" or "service" or "database" or "step" ||
                       !string.IsNullOrEmpty(n.Url) || !string.IsNullOrEmpty(n.App) || !string.IsNullOrEmpty(n.Zone));
}

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
    public ComposerPlan? SourcePlan { get; init; }
    public IReadOnlyDictionary<string, Guid>? NodeIdMap { get; init; }
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
        var normalizedNodes = new List<ComposerNode>();

        foreach (var originalNode in plan.Nodes)
        {
            if (originalNode is null || string.IsNullOrWhiteSpace(originalNode.Id) || originalNode.Id.Length > 64 || !nodeIds.Add(originalNode.Id))
                throw new ArgumentException("Invalid diagram node.");

            bool isSupportedKind = originalNode.Kind is "note" or "rectangle" or "ellipse" or "web" or "window" or "service" or "database" or "step";
            if (!isSupportedKind || !AnnotationDocument.ValidColor(originalNode.Color))
                throw new ArgumentException("Invalid diagram node.");

            string text = originalNode.Text ?? "";
            if (string.IsNullOrWhiteSpace(text))
            {
                text = !string.IsNullOrWhiteSpace(originalNode.Title) ? originalNode.Title : originalNode.Kind;
            }

            if (!(Guid.TryParse(originalNode.Id, out var sharedId) && shared.ContainsKey(sharedId)) && (string.IsNullOrWhiteSpace(text) || text.Length > 2000))
                throw new ArgumentException("Invalid diagram node.");

            normalizedNodes.Add(originalNode with { Text = text });
        }

        var nodesList = normalizedNodes.ToArray();

        var edges = new HashSet<(string, string)>();
        foreach (var edge in plan.Edges)
            if (edge is null || !nodeIds.Contains(edge.From) || !nodeIds.Contains(edge.To) || edge.From == edge.To
                || !edges.Add((edge.From, edge.To)) || !AnnotationDocument.ValidColor(edge.Color))
                throw new ArgumentException("Invalid diagram connection.");

        var reused = new Dictionary<string, CanvasAnnotation>();
        foreach (var node in nodesList)
        {
            if (!Guid.TryParse(node.Id, out var existingId)) continue;
            if (!shared.TryGetValue(existingId, out var existing) || existing.DesktopId != context.Desktop || existing.Kind == AnnotationKind.Arrow)
                throw new ArgumentException("The proposal tried to arrange an object outside the shared context.");
            reused.Add(node.Id, existing);
        }

        if (context.PreserveWording && (plan.Edits.Length > 0 || plan.Edges.Length > 0 || reused.Count != nodesList.Length
            || !reused.Values.Select(a => a.Id).ToHashSet().SetEquals(shared.Values.Where(a => a.Kind != AnnotationKind.Arrow).Select(a => a.Id))))
            throw new ArgumentException("Layout-only proposals must reuse every checked note or shape and cannot add or rewrite objects.");

        var edited = new HashSet<Guid>();
        var output = new List<CanvasAnnotation>();
        foreach (var edit in plan.Edits)
        {
            if (edit is null || !Guid.TryParse(edit.Id, out var id) || !edited.Add(id)
                || !shared.TryGetValue(id, out var item) || item.DesktopId != context.Desktop
                || item.Kind != AnnotationKind.Text || string.IsNullOrWhiteSpace(edit.Text)
                || reused.Values.Any(a => a.Id == id) || edit.Text.Length > AnnotationDocument.MaximumTextLength)
                throw new ArgumentException("The proposal tried to edit a note outside the shared context.");
            output.Add(item with { Text = edit.Text });
        }

        // Topological layers for DAGs. Cycles fall back to a stable three-column layout.
        var rank = nodesList.ToDictionary(n => n.Id, _ => 0);
        var degree = nodesList.ToDictionary(n => n.Id, n => plan.Edges.Count(e => e.To == n.Id));
        var queue = new Queue<string>(nodesList.Where(n => degree[n.Id] == 0).Select(n => n.Id));
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
        if (visited != nodesList.Length || plan.Edges.Length == 0)
            for (int i = 0; i < nodesList.Length; i++) rank[nodesList[i].Id] = i % 3;

        bool isVertical = string.Equals(plan.Direction, "TD", StringComparison.OrdinalIgnoreCase);
        var positions = new Dictionary<string, CanvasRect>();

        if (isVertical)
        {
            foreach (var node in nodesList)
            {
                int row = rank[node.Id];
                double nodeWidth = GetNodeWidth(node, reused);
                double nodeHeight = GetNodeHeight(node, reused);

                double x = positions.Where(p => rank[p.Key] == row).Select(p => p.Value.X + p.Value.Width + 60).DefaultIfEmpty(0).Max();
                double y = Enumerable.Range(0, row).Sum(r => nodesList.Where(n => rank[n.Id] == r)
                    .Select(n => GetNodeHeight(n, reused)).DefaultIfEmpty(180).Max() + 100);

                positions[node.Id] = new(x, y, nodeWidth, nodeHeight);
            }
        }
        else
        {
            foreach (var node in nodesList)
            {
                int column = rank[node.Id];
                double nodeWidth = GetNodeWidth(node, reused);
                double nodeHeight = GetNodeHeight(node, reused);

                double y = positions.Where(p => rank[p.Key] == column).Select(p => p.Value.Y + p.Value.Height + 60).DefaultIfEmpty(0).Max();
                double x = Enumerable.Range(0, column).Sum(c => nodesList.Where(n => rank[n.Id] == c)
                    .Select(n => GetNodeWidth(n, reused)).DefaultIfEmpty(300).Max() + 100);

                positions[node.Id] = new(x, y, nodeWidth, nodeHeight);
            }
        }

        double width = positions.Values.Select(r => r.X + r.Width).DefaultIfEmpty(0).Max();
        double heightTotal = positions.Values.Select(r => r.Y + r.Height).DefaultIfEmpty(0).Max();

        // Existing annotations may occupy their old footprint; unrelated objects remain obstacles.
        var obstacles = context.Obstacles.ToList();
        foreach (var existing in reused.Values)
        {
            int index = obstacles.IndexOf(existing.Bounds);
            if (index >= 0) obstacles.RemoveAt(index);
        }
        var origin = FindSpace(context.Anchor, width, heightTotal, obstacles);
        var nodes = new Dictionary<string, CanvasAnnotation>();
        var nodeIdMap = new Dictionary<string, Guid>();

        foreach (var node in nodesList)
        {
            var r = positions[node.Id];
            var annotId = Guid.NewGuid();
            nodeIdMap[node.Id] = annotId;

            string displayText = !string.IsNullOrWhiteSpace(node.Title)
                ? node.Title + (!string.IsNullOrWhiteSpace(node.Subtitle) ? "\n" + node.Subtitle : (!string.IsNullOrWhiteSpace(node.Url) ? "\n" + node.Url : ""))
                : node.Text;

            AnnotationKind kind = node.Kind switch
            {
                "rectangle" or "web" or "window" or "service" or "database" or "step" => AnnotationKind.Rectangle,
                "ellipse" => AnnotationKind.Ellipse,
                _ => AnnotationKind.Text
            };

            AnnotationCardFill fill = node.Kind switch
            {
                "web" or "window" or "service" or "database" or "step" => AnnotationCardFill.Solid,
                _ => (AnnotationCardFill)(-1)
            };

            var item = new CanvasAnnotation(
                Id: annotId,
                DesktopId: context.Desktop,
                Kind: kind,
                A: new(origin.X + r.X, origin.Y + r.Y),
                B: new(origin.X + r.X + r.Width, origin.Y + r.Y + r.Height),
                Color: node.Color,
                Stroke: 3,
                Text: displayText,
                FontSize: node.Kind is "web" or "window" or "service" or "database" or "step" ? 20 : 18,
                TextAlign: node.Kind is "web" or "window" or "service" or "database" or "step" ? AnnotationTextAlign.Center : AnnotationTextAlign.Left,
                CardFill: fill);

            if (reused.TryGetValue(node.Id, out var original))
                item = original.Translate(item.A.X - original.Bounds.X, item.A.Y - original.Bounds.Y);

            nodes.Add(node.Id, item);
            output.Add(item);
        }

        foreach (var edge in plan.Edges)
        {
            var from = nodes[edge.From];
            var to = nodes[edge.To];

            CanvasPoint a, b;
            if (from.Kind == AnnotationKind.Ellipse || to.Kind == AnnotationKind.Ellipse)
            {
                a = Boundary(from, Center(to.Bounds));
                b = Boundary(to, Center(from.Bounds));
            }
            else
            {
                (a, b) = AnnotationGeometry.ComputePortAnchors(from.Bounds, ConnectionPort.Auto, to.Bounds, ConnectionPort.Auto);
            }

            var routing = string.Equals(edge.Routing, "orthogonal", StringComparison.OrdinalIgnoreCase)
                ? ConnectorRouting.Orthogonal
                : string.Equals(edge.Routing, "straight", StringComparison.OrdinalIgnoreCase)
                    ? ConnectorRouting.Straight
                    : ConnectorRouting.Curved;

            double stroke = edge.Stroke > 0
                ? Math.Clamp(edge.Stroke, AnnotationDocument.MinimumStroke, AnnotationDocument.MaximumStroke)
                : 16.0;

            output.Add(new CanvasAnnotation(
                Id: Guid.NewGuid(),
                DesktopId: context.Desktop,
                Kind: AnnotationKind.Arrow,
                A: a,
                B: b,
                Color: edge.Color,
                Stroke: stroke,
                Text: edge.Label ?? "",
                FontSize: 20,
                Routing: routing,
                SourceId: from.Id,
                TargetId: to.Id));
        }

        if (plan.Zones != null)
        {
            foreach (var zone in plan.Zones)
            {
                var memberNodes = nodesList.Where(n => string.Equals(n.Zone, zone.Id, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(n.Zone, zone.Name, StringComparison.OrdinalIgnoreCase)).ToList();

                if (memberNodes.Count > 0)
                {
                    double minX = memberNodes.Min(n => positions[n.Id].X) + origin.X - 30;
                    double minY = memberNodes.Min(n => positions[n.Id].Y) + origin.Y - 50;
                    double maxX = memberNodes.Max(n => positions[n.Id].X + positions[n.Id].Width) + origin.X + 30;
                    double maxY = memberNodes.Max(n => positions[n.Id].Y + positions[n.Id].Height) + origin.Y + 30;

                    var zoneRect = new CanvasAnnotation(
                        Id: Guid.NewGuid(),
                        DesktopId: context.Desktop,
                        Kind: AnnotationKind.Rectangle,
                        A: new CanvasPoint(minX, minY),
                        B: new CanvasPoint(maxX, maxY),
                        Color: zone.Color,
                        Stroke: 2,
                        Text: zone.Name,
                        FontSize: 22,
                        CardFill: AnnotationCardFill.Tinted,
                        InBackground: true);

                    output.Insert(0, zoneRect);
                }
            }
        }

        foreach (var arrow in context.SharedAnnotations.Where(a => a.Kind == AnnotationKind.Arrow))
        {
            CanvasPoint Follow(CanvasPoint endpoint)
            {
                var match = reused.FirstOrDefault(pair => CanvasSelection.Contains(new(pair.Value.Bounds.X - 24, pair.Value.Bounds.Y - 24, pair.Value.Bounds.Width + 48, pair.Value.Bounds.Height + 48), endpoint));
                if (match.Value == null) return endpoint;
                var arranged = nodes[match.Key];
                return new(endpoint.X + arranged.Bounds.X - match.Value.Bounds.X, endpoint.Y + arranged.Bounds.Y - match.Value.Bounds.Y);
            }
            var linked = arrow with { A = Follow(arrow.A), B = Follow(arrow.B) };
            if (linked != arrow) output.Add(linked);
        }

        AnnotationDocument.Validate(output);
        return new(context.Desktop, context.Revision, plan.Summary, output.AsReadOnly())
        {
            References = context.SharedReferences.ToArray(),
            SourcePlan = plan,
            NodeIdMap = nodeIdMap
        };
    }

    private static double GetNodeWidth(ComposerNode node, Dictionary<string, CanvasAnnotation> reused)
    {
        if (reused.TryGetValue(node.Id, out var size)) return size.Bounds.Width;
        return node.Kind switch
        {
            "web" or "window" => 400,
            "service" or "database" or "step" => 320,
            _ => 300
        };
    }

    private static double GetNodeHeight(ComposerNode node, Dictionary<string, CanvasAnnotation> reused)
    {
        if (reused.TryGetValue(node.Id, out var original)) return original.Bounds.Height;
        return node.Kind switch
        {
            "web" or "window" => 300,
            "service" or "database" or "step" => 180,
            "rectangle" or "ellipse" => 160,
            _ => Math.Clamp(60 + Math.Ceiling(node.Text.Length / 28.0) * 24, 160, 1800)
        };
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
