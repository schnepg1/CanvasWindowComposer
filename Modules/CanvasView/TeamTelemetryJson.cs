using System.Text.Json;

namespace CanvasDesktop.CanvasView;

/// <summary>The registration file is telemetry, never executable canvas commands.</summary>
public static class TeamTelemetryJson
{
    public const int MaximumBytes = 1024 * 1024;
    public static TeamTelemetrySnapshot Parse(string json)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaximumBytes) throw new JsonException("Telemetry file is too large.");
        using var document = JsonDocument.Parse(json.TrimStart('\uFEFF'));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("Expected telemetry object.");
        var now = DateTimeOffset.UtcNow;
        var teams = new Dictionary<string, TeamStatusReport>(StringComparer.Ordinal);
        if (root.TryGetProperty("teams", out var entries))
        {
            if (entries.ValueKind != JsonValueKind.Object) throw new JsonException("Expected teams object.");
            foreach (var entry in entries.EnumerateObject())
            {
                if (teams.Count >= 64 || string.IsNullOrWhiteSpace(entry.Name) || entry.Name.Length > 200)
                    throw new JsonException("Invalid task registration.");
                string taskId = Read(entry.Value, "taskId", entry.Name, 200);
                if (taskId != entry.Name) throw new JsonException("Task key and taskId must match.");
                var name = Read(entry.Value, "name", taskId[..Math.Min(taskId.Length, 100)], 100);
                var status = Read(entry.Value, "status", "Active", 40);
                var activeRole = Read(entry.Value, "activeRole", "", 80);
                if (!teams.TryAdd(taskId, new(taskId, name, status, activeRole, Roles(entry.Value, taskId, now))))
                    throw new JsonException("Duplicate task registration.");
            }
        }
        return new(Roles(root, "", now), now) { Teams = teams };
    }

    private static Dictionary<string, AgentStatusReport> Roles(JsonElement parent, string taskId, DateTimeOffset now)
    {
        var roles = new Dictionary<string, AgentStatusReport>(StringComparer.OrdinalIgnoreCase);
        if (!parent.TryGetProperty("roles", out var entries)) return roles;
        if (entries.ValueKind != JsonValueKind.Object) throw new JsonException("Expected roles object.");
        foreach (var entry in entries.EnumerateObject())
        {
            if (roles.Count >= 64 || entry.Name.Length is 0 or > 80) throw new JsonException("Invalid role.");
            var stateText = Read(entry.Value, "state", "Idle", 40);
            if (!Enum.TryParse<AgentExecutionState>(stateText, true, out var state) || !Enum.IsDefined(state))
                throw new JsonException("Unknown agent state.");
            if (!roles.TryAdd(entry.Name, new(entry.Name, state, Read(entry.Value, "detail", "", 1000), now, taskId,
                Read(entry.Value, "worktree", "", 1000)))) throw new JsonException("Duplicate role.");
        }
        return roles;
    }

    private static string Read(JsonElement parent, string key, string fallback, int maximum)
    {
        if (parent.ValueKind != JsonValueKind.Object) throw new JsonException("Expected telemetry object.");
        if (!parent.TryGetProperty(key, out var value)) return fallback;
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } text || text.Length > maximum)
            throw new JsonException("Invalid telemetry field: " + key);
        return text;
    }

    public static AgentStatusReport? ReportForCard(TeamTelemetrySnapshot snapshot, CanvasAnnotation card)
    {
        // Bound cards never fall back to global roles or another team's telemetry.
        if (card.AgentTaskId.Length > 0)
        {
            if (!snapshot.Teams.TryGetValue(card.AgentTaskId, out var team)) return null;
            if (team.Roles.TryGetValue(card.AgentRole, out var report)) return report;
            return new(card.AgentRole, string.Equals(team.ActiveRole, card.AgentRole, StringComparison.OrdinalIgnoreCase)
                && string.Equals(team.Status, "Active", StringComparison.OrdinalIgnoreCase) ? AgentExecutionState.Working : AgentExecutionState.Idle,
                Timestamp: snapshot.UpdatedAt, TaskId: team.TaskId);
        }
        return LegacyReport(snapshot, card.Text);
    }

    public static AgentStatusReport? LegacyReport(TeamTelemetrySnapshot snapshot, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        foreach (var report in snapshot.Roles.Values)
        {
            var aliases = report.Role.ToLowerInvariant() switch
            {
                "programmer" or "dev" => new[] { "Programmer", "Developer", "dev" },
                "architect" or "arch" => ["Architect", "Planner"],
                "reviewer" or "rev" => ["Reviewer", "Quality"],
                "merger" or "mrg" => ["Merger"],
                "conflict-resolver" or "res" => ["Conflict", "Resolver"],
                "branch-cleaner" or "cln" => ["Cleaner", "Hygiene"],
                "orchestrator" or "orch" => ["Orchestrator", "Mission"],
                _ => [report.Role]
            };
            if (report.State != AgentExecutionState.Idle && aliases.Any(a => text.Contains(a, StringComparison.OrdinalIgnoreCase))) return report;
        }
        return null;
    }
}
