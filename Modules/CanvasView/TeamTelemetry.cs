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

public sealed record TeamTelemetrySnapshot(
    IReadOnlyDictionary<string, AgentStatusReport> Roles,
    DateTimeOffset UpdatedAt)
{
    public static TeamTelemetrySnapshot Empty => new(new Dictionary<string, AgentStatusReport>(), DateTimeOffset.UtcNow);
}

public interface ITeamTelemetryProvider : IDisposable
{
    TeamTelemetrySnapshot Current { get; }
    event Action<TeamTelemetrySnapshot>? TelemetryUpdated;
    AgentExecutionState GetStateForCard(string cardText);
    void SetState(string role, AgentExecutionState state, string detail = "");
}
