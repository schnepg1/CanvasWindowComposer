using System;
using System.IO;
using System.Text.Json;
using CanvasDesktop;
using CanvasDesktop.CanvasView;
using Xunit;

namespace CanvasDesktop.Tests;

public class TelemetryTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "canvas-telemetry-" + Guid.NewGuid().ToString("N"));
    private string StatePath => Path.Combine(_tempDir, "state.json");

    public TelemetryTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, true);
        }
        catch { }
    }

    [Fact]
    public void EmptySnapshot_HasEmptyRoles()
    {
        var snapshot = TeamTelemetrySnapshot.Empty;
        Assert.NotNull(snapshot);
        Assert.Empty(snapshot.Roles);
        Assert.True(snapshot.UpdatedAt <= DateTimeOffset.UtcNow);
    }

    [Fact]
    public void Provider_LoadsInitialJsonState()
    {
        var json = @"
{
    ""roles"": {
        ""programmer"": { ""state"": ""Working"", ""detail"": ""Coding feature"" },
        ""architect"": { ""state"": ""Done"", ""detail"": ""Plan finalized"" },
        ""reviewer"": { ""state"": ""Blocked"", ""detail"": ""Awaiting PR"" }
    }
}";
        File.WriteAllText(StatePath, json);

        using var provider = new WindowsNativeTelemetryProvider(StatePath, mutexProbe: () => false);

        var current = provider.Current;
        Assert.Equal(3, current.Roles.Count);

        Assert.True(current.Roles.TryGetValue("programmer", out var prog));
        Assert.Equal(AgentExecutionState.Working, prog.State);
        Assert.Equal("Coding feature", prog.Detail);

        Assert.True(current.Roles.TryGetValue("architect", out var arch));
        Assert.Equal(AgentExecutionState.Done, arch.State);

        Assert.True(current.Roles.TryGetValue("reviewer", out var rev));
        Assert.Equal(AgentExecutionState.Blocked, rev.State);
    }

    [Fact]
    public void Provider_GetStateForCard_FuzzyMatching()
    {
        var json = @"
{
    ""roles"": {
        ""programmer"": { ""state"": ""Working"", ""detail"": ""Writing code"" },
        ""architect"": { ""state"": ""Done"", ""detail"": ""Spec ready"" },
        ""reviewer"": { ""state"": ""Testing"", ""detail"": ""Running suite"" }
    }
}";
        File.WriteAllText(StatePath, json);

        using var provider = new WindowsNativeTelemetryProvider(StatePath, mutexProbe: () => false);

        Assert.Equal(AgentExecutionState.Working, provider.GetStateForCard("Worktree Developer"));
        Assert.Equal(AgentExecutionState.Working, provider.GetStateForCard("Lead Programmer"));
        Assert.Equal(AgentExecutionState.Working, provider.GetStateForCard("dev task"));

        Assert.Equal(AgentExecutionState.Done, provider.GetStateForCard("System Architect"));
        Assert.Equal(AgentExecutionState.Done, provider.GetStateForCard("Feature Planner"));

        Assert.Equal(AgentExecutionState.Testing, provider.GetStateForCard("Code Reviewer"));
        Assert.Equal(AgentExecutionState.Testing, provider.GetStateForCard("Quality Gate"));

        Assert.Equal(AgentExecutionState.Idle, provider.GetStateForCard("Unrelated Card Note"));
        Assert.Equal(AgentExecutionState.Idle, provider.GetStateForCard(""));
    }

    [Fact]
    public void Provider_SetState_PersistsToFileAndFiresEvent()
    {
        using var provider = new WindowsNativeTelemetryProvider(StatePath, mutexProbe: () => false);

        TeamTelemetrySnapshot? received = null;
        provider.TelemetryUpdated += s => received = s;

        provider.SetState("merger", AgentExecutionState.Working, "Merging branch feature/telemetry");

        Assert.NotNull(received);
        Assert.True(received.Roles.TryGetValue("merger", out var report));
        Assert.Equal(AgentExecutionState.Working, report.State);
        Assert.Equal("Merging branch feature/telemetry", report.Detail);

        // Verify persisted to file on disk
        Assert.True(File.Exists(StatePath));
        var content = File.ReadAllText(StatePath);
        Assert.Contains("merger", content);
        Assert.Contains("Working", content);
    }

    [Fact]
    public void Provider_HardwareMutex_OverridesProgrammerAndReviewerToTesting()
    {
        bool isMutexHeld = false;
        using var provider = new WindowsNativeTelemetryProvider(StatePath, mutexProbe: () => isMutexHeld);

        provider.SetState("programmer", AgentExecutionState.Working, "Starting tests");
        Assert.Equal(AgentExecutionState.Working, provider.Current.Roles["programmer"].State);

        // Simulate named mutex acquisition by test runner
        isMutexHeld = true;
        provider.CheckHardwareAndState();

        Assert.Equal(AgentExecutionState.Testing, provider.Current.Roles["programmer"].State);
        Assert.Equal(AgentExecutionState.Testing, provider.Current.Roles["reviewer"].State);
    }
}
