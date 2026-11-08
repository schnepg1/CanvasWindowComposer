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

    [Fact]
    public void Provider_ResolveStateFilePath_HonorsExplicitAndRelativePaths()
    {
        // Explicit path is always respected
        var custom = Path.Combine(_tempDir, "custom.json");
        Assert.Equal(custom, WindowsNativeTelemetryProvider.ResolveStateFilePath(custom));

        // Default resolution returns a vendor-neutral path ending in state.json
        var resolved = WindowsNativeTelemetryProvider.ResolveStateFilePath();
        Assert.False(string.IsNullOrWhiteSpace(resolved));
        Assert.EndsWith("state.json", resolved, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Provider_LoadsMultiTenantTeamsJsonState()
    {
        var json = @"
{
    ""activeTaskId"": ""task-alpha"",
    ""updatedAt"": ""2026-11-08T19:00:00Z"",
    ""roles"": {
        ""programmer"": { ""state"": ""Working"", ""detail"": ""Coding task-alpha"" }
    },
    ""teams"": {
        ""task-alpha"": {
            ""name"": ""Alpha Team"",
            ""conversationId"": ""conv-1"",
            ""worktree"": ""C:\\worktrees\\task-alpha"",
            ""activeRole"": ""programmer"",
            ""status"": ""Active"",
            ""roles"": {
                ""programmer"": { ""state"": ""Working"", ""detail"": ""Coding task-alpha"" },
                ""reviewer"": { ""state"": ""Idle"", ""detail"": """" }
            }
        },
        ""task-beta"": {
            ""name"": ""Beta Team"",
            ""conversationId"": ""conv-2"",
            ""worktree"": ""C:\\worktrees\\task-beta"",
            ""activeRole"": ""architect"",
            ""status"": ""Active"",
            ""roles"": {
                ""architect"": { ""state"": ""Done"", ""detail"": ""Spec finalized"" }
            }
        }
    }
}";
        File.WriteAllText(StatePath, json);

        using var provider = new WindowsNativeTelemetryProvider(StatePath, mutexProbe: () => false);

        var snapshot = provider.Current;
        Assert.NotNull(snapshot.Teams);
        Assert.Equal(2, snapshot.Teams.Count);
        Assert.Equal("task-alpha", snapshot.ActiveTaskId);

        Assert.True(snapshot.Teams.TryGetValue("task-alpha", out var alpha));
        Assert.Equal("Alpha Team", alpha.Name);
        Assert.Equal("Active", alpha.Status);
        Assert.Equal(AgentExecutionState.Working, alpha.Roles["programmer"].State);

        Assert.True(snapshot.Teams.TryGetValue("task-beta", out var beta));
        Assert.Equal("Beta Team", beta.Name);
        Assert.Equal(AgentExecutionState.Done, beta.Roles["architect"].State);
    }

    [Fact]
    public void Provider_SetTeamState_PersistsAndSyncsRootRoles()
    {
        using var provider = new WindowsNativeTelemetryProvider(StatePath, mutexProbe: () => false);

        TeamTelemetrySnapshot? received = null;
        provider.TelemetryUpdated += s => received = s;

        provider.SetTeamState("team-1", "programmer", AgentExecutionState.Working, "Coding team-1");

        Assert.NotNull(received);
        Assert.NotNull(received.Teams);
        Assert.True(received.Teams.TryGetValue("team-1", out var team));
        Assert.Equal(AgentExecutionState.Working, team.Roles["programmer"].State);
        Assert.Equal("Coding team-1", team.Roles["programmer"].Detail);

        // Root roles mirrored
        Assert.True(received.Roles.TryGetValue("programmer", out var rootProg));
        Assert.Equal(AgentExecutionState.Working, rootProg.State);

        // Check on-disk persistence
        Assert.True(File.Exists(StatePath));
        string content = File.ReadAllText(StatePath);
        Assert.Contains("team-1", content);
        Assert.Contains("Coding team-1", content);
    }

    [Fact]
    public void Provider_GetStateForCard_ScopedToTeam()
    {
        using var provider = new WindowsNativeTelemetryProvider(StatePath, mutexProbe: () => false);

        provider.SetTeamState("team-1", "programmer", AgentExecutionState.Working, "Working on 1");
        provider.SetTeamState("team-2", "programmer", AgentExecutionState.Done, "Finished 2");

        // Scoped by taskId parameter
        Assert.Equal(AgentExecutionState.Working, provider.GetStateForCard("Worktree Programmer", "team-1"));
        Assert.Equal(AgentExecutionState.Done, provider.GetStateForCard("Worktree Programmer", "team-2"));

        // Scoped by annotation text with [taskId]
        var annot1 = new CanvasAnnotation(
            Id: Guid.NewGuid(),
            DesktopId: Guid.NewGuid(),
            Kind: AnnotationKind.Rectangle,
            A: new CanvasPoint(0, 0),
            B: new CanvasPoint(200, 100),
            Color: "#10B981",
            Stroke: 1,
            Text: "[team-1] Worktree Programmer\nTask 1");

        var annot2 = new CanvasAnnotation(
            Id: Guid.NewGuid(),
            DesktopId: Guid.NewGuid(),
            Kind: AnnotationKind.Rectangle,
            A: new CanvasPoint(0, 0),
            B: new CanvasPoint(200, 100),
            Color: "#10B981",
            Stroke: 1,
            Text: "[team-2] Worktree Programmer\nTask 2");

        Assert.Equal(AgentExecutionState.Working, provider.GetStateForAnnotation(annot1));
        Assert.Equal(AgentExecutionState.Done, provider.GetStateForAnnotation(annot2));
    }

    [Fact]
    public void Provider_HardwareMutex_OverridesActiveTeamProgrammerAndReviewer()
    {
        bool isMutexHeld = false;
        using var provider = new WindowsNativeTelemetryProvider(StatePath, mutexProbe: () => isMutexHeld);

        provider.RegisterTeam("active-task", "Active Team");
        provider.SetTeamState("active-task", "programmer", AgentExecutionState.Working, "Coding");
        provider.SetTeamState("active-task", "reviewer", AgentExecutionState.Idle, "Waiting");

        Assert.Equal(AgentExecutionState.Working, provider.Current.Teams!["active-task"].Roles["programmer"].State);
        Assert.Equal(AgentExecutionState.Idle, provider.Current.Teams!["active-task"].Roles["reviewer"].State);

        // Hardware named mutex becomes held
        isMutexHeld = true;
        provider.CheckHardwareAndState();

        Assert.Equal(AgentExecutionState.Testing, provider.Current.Teams!["active-task"].Roles["programmer"].State);
        Assert.Equal(AgentExecutionState.Testing, provider.Current.Teams!["active-task"].Roles["reviewer"].State);
        Assert.Equal(AgentExecutionState.Testing, provider.Current.Roles["programmer"].State);
        Assert.Equal(AgentExecutionState.Testing, provider.Current.Roles["reviewer"].State);
    }

    [Fact]
    public void Provider_RegistersAndCompletesTeam()
    {
        using var provider = new WindowsNativeTelemetryProvider(StatePath, mutexProbe: () => false);

        provider.RegisterTeam("task-xyz", "Task XYZ Team", "conv-999", @"C:\worktree\xyz");
        Assert.True(provider.Current.Teams!.ContainsKey("task-xyz"));
        var team = provider.Current.Teams["task-xyz"];
        Assert.Equal("Task XYZ Team", team.Name);
        Assert.Equal("Active", team.Status);
        Assert.Equal(@"C:\worktree\xyz", team.Worktree);

        provider.CompleteTeam("task-xyz", true);
        Assert.Equal("Completed", provider.Current.Teams["task-xyz"].Status);

        provider.UnregisterTeam("task-xyz");
        Assert.False(provider.Current.Teams.ContainsKey("task-xyz"));
    }

    [Fact]
    public void Provider_ResolveWatchedPaths_IncludesAppDataAndLocalAgents()
    {
        var paths = WindowsNativeTelemetryProvider.ResolveWatchedPaths(null);
        Assert.NotEmpty(paths);

        string appDataExpected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CanvasWindowComposer", "Teams", "state.json");

        Assert.Contains(paths, p => string.Equals(Path.GetFullPath(p), Path.GetFullPath(appDataExpected), StringComparison.OrdinalIgnoreCase));

        // When explicit path is specified, it should only return that explicit path
        var explicitPaths = WindowsNativeTelemetryProvider.ResolveWatchedPaths(StatePath);
        Assert.Single(explicitPaths);
        Assert.Equal(Path.GetFullPath(StatePath), explicitPaths[0]);
    }

    [Fact]
    public void AnnotationToolbar_AiTeamsToggle_ControlsState()
    {
        using var toolbar = new AnnotationToolbar();
        Assert.False(toolbar.IsAiTeamsActive);


        bool toggled = false;
        toolbar.AiTeamsToggled += () => toggled = true;

        toolbar.SetAiTeamsActive(true);
        Assert.True(toolbar.IsAiTeamsActive);

        toolbar.SetAiTeamsActive(false);
        Assert.False(toggled);
        Assert.False(toolbar.IsAiTeamsActive);

    }
}
