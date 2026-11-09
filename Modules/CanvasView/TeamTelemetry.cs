using System;
using System.Collections.Generic;

namespace CanvasDesktop.CanvasView;

public enum AgentExecutionState
{
    Idle,
    Working,    // Thinking, coding, planning (Cyan glow #38BDF8)
    Testing,    // Running tests under mutex (Amber glow #F59E0B)
    Blocked,    // Waiting for peer review or user confirmation (Orange glow #FB923C)
    Done,       // Task completed successfully (Emerald green glow #10B981)
    Error       // Failed or merge conflict (Red glow #EF4444)
}

public sealed record AgentStatusReport(
    string Role,
    AgentExecutionState State,
    string Detail = "",
    DateTimeOffset Timestamp = default,
    string TaskId = "",
    string Worktree = "");

public sealed record TeamStatusReport(
    string TaskId,
    string Name,
    string Status,
    string ActiveRole,
    IReadOnlyDictionary<string, AgentStatusReport> Roles,
    string ConversationId = "",
    string Worktree = "",
    DateTimeOffset UpdatedAt = default);

public sealed record TeamInstanceTelemetry(
    string TaskId,
    string Name,
    string ConversationId,
    string Worktree,
    string ActiveRole,
    string Status, // "Active" | "Completed" | "Failed"
    IReadOnlyDictionary<string, AgentStatusReport> Roles,
    DateTimeOffset UpdatedAt)
{
    public static implicit operator TeamStatusReport(TeamInstanceTelemetry t) =>
        new(t.TaskId, t.Name, t.Status, t.ActiveRole, t.Roles, t.ConversationId, t.Worktree, t.UpdatedAt);

    public static implicit operator TeamInstanceTelemetry(TeamStatusReport s) =>
        new(s.TaskId, s.Name, s.ConversationId, s.Worktree, s.ActiveRole, s.Status, s.Roles, s.UpdatedAt);
}

public sealed record TeamTelemetrySnapshot(
    IReadOnlyDictionary<string, AgentStatusReport> Roles,
    DateTimeOffset UpdatedAt,
    IReadOnlyDictionary<string, TeamStatusReport>? Teams = null,
    string? ActiveTaskId = null)
{
    public IReadOnlyDictionary<string, TeamStatusReport> Teams { get; init; } = Teams ?? new Dictionary<string, TeamStatusReport>();
    public string? ActiveTaskId { get; init; } = ActiveTaskId;
    public static TeamTelemetrySnapshot Empty => new(
        new Dictionary<string, AgentStatusReport>(),
        DateTimeOffset.UtcNow,
        new Dictionary<string, TeamStatusReport>());
}

public interface ITeamTelemetryProvider : IDisposable
{
    TeamTelemetrySnapshot Current { get; }
    event Action<TeamTelemetrySnapshot>? TelemetryUpdated;
    AgentExecutionState GetStateForCard(string cardText);
    AgentExecutionState GetStateForCard(string cardText, string? taskId);
    AgentExecutionState GetStateForAnnotation(CanvasAnnotation item);
    AgentStatusReport? GetReportForCard(CanvasAnnotation card);
    void SetState(string role, AgentExecutionState state, string detail = "");
    void SetTeamState(string taskId, string role, AgentExecutionState state, string detail = "");
    void RegisterTeam(string taskId, string name, string conversationId = "", string worktree = "");
    void CompleteTeam(string taskId, bool success = true);
    void UnregisterTeam(string taskId);
}
