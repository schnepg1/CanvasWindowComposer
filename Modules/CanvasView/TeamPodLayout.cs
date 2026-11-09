using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CanvasDesktop.CanvasView;

public sealed record TeamPodLayout(Guid ZoneId, CanvasRect Bounds, IReadOnlyList<CanvasAnnotation> Items)
{
    public static Guid StableId(Guid desktop, string taskId, string key)
        => new(SHA256.HashData(Encoding.UTF8.GetBytes($"{desktop:D}\n{taskId}\n{key}"))[..16]);

    /// <summary>Use the shipped CVL template; give every instance its own persisted identities.</summary>
    public static TeamPodLayout Create(Guid desktop, string taskId, CanvasPoint anchor)
    {
        using var stream = typeof(TeamPodLayout).Assembly.GetManifestResourceStream("CanvasDesktop.TeamPod.json")!;
        var plan = JsonSerializer.Deserialize<ComposerPlan>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var preview = CanvasComposer.Prepare(new(desktop, 0, anchor, [], []), plan);
        var idMap = preview.Items.Select((a, i) => (a.Id, Stable: StableId(desktop, taskId, "item-" + i)))
            .ToDictionary(p => p.Id, p => p.Stable);
        var roles = new Dictionary<string, string>
        {
            ["orch"] = "orchestrator", ["arch"] = "architect", ["dev"] = "programmer", ["rev"] = "reviewer",
            ["mrg"] = "merger", ["res"] = "conflict-resolver", ["cln"] = "branch-cleaner"
        };
        var roleIds = preview.NodeIdMap!.ToDictionary(p => p.Value, p => roles[p.Key]);
        var items = preview.Items.Select(a => a with
        {
            Id = idMap[a.Id], AgentTaskId = taskId, AgentRole = roleIds.GetValueOrDefault(a.Id, ""),
            SourceId = a.SourceId is { } source ? idMap[source] : null,
            TargetId = a.TargetId is { } target ? idMap[target] : null
        }).ToArray();
        double x = items.Min(a => a.Bounds.X) - 120, y = items.Min(a => a.Bounds.Y) - 120;
        var bounds = new CanvasRect(x, y, items.Max(a => a.Bounds.X + a.Bounds.Width) + 120 - x,
            items.Max(a => a.Bounds.Y + a.Bounds.Height) + 120 - y);
        return new(StableId(desktop, taskId, "container"), bounds, items);
    }
}
