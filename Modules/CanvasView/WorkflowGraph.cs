using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CanvasDesktop.CanvasView;

public sealed record WorkflowNode(
    Guid Id,
    string Kind,
    string Title,
    string CustomName,
    string AppExecutable,
    string? TargetUrl,
    string? ChromeProfile,
    string? Zone,
    CanvasRect Bounds);

public sealed record WorkflowEdge(
    Guid Id,
    Guid SourceId,
    Guid TargetId,
    string Description,
    string Color,
    double Stroke,
    string Routing);

public sealed record WorkflowGraph(
    string Name,
    Guid DesktopId,
    IReadOnlyList<WorkflowNode> Nodes,
    IReadOnlyList<WorkflowEdge> Edges,
    int Version = 1)
{
    [JsonPropertyName("$schema")]
    public string Schema { get; init; } = "https://canvaswindowcomposer.dev/schemas/workflow.v1.json";
}

public sealed record WorkflowPlanWindow(
    string Key,
    string CustomName,
    string App,
    string Url,
    string ChromeProfile = "");

public sealed record WorkflowPlanLink(
    string From,
    string To,
    string Label,
    double Stroke = 16.0,
    string Color = "#60A5FA");

public sealed record WorkflowPlan(
    string ProjectName,
    IReadOnlyList<WorkflowPlanWindow> Windows,
    IReadOnlyList<WorkflowPlanLink> Links,
    string LayoutDirection = "LR");

public static class WorkflowExport
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string ToJson(WorkflowGraph graph) => JsonSerializer.Serialize(graph, Options);

    public static WorkflowGraph? FromJson(string json) => JsonSerializer.Deserialize<WorkflowGraph>(json, Options);
}
