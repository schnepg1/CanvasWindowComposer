using System;
using System.Collections.Generic;
using System.IO;
using CanvasDesktop;
using CanvasDesktop.CanvasView;
using Xunit;

namespace CanvasDesktop.Tests;

public class AgentEnvironmentTests
{
    private sealed class MockTelemetryProvider : ITeamTelemetryProvider
    {
        public TeamTelemetrySnapshot Current { get; set; } = TeamTelemetrySnapshot.Empty;
        public Dictionary<Guid, AgentStatusReport> Reports { get; } = new();

        public event Action<TeamTelemetrySnapshot>? TelemetryUpdated { add { } remove { } }

        public AgentStatusReport? GetReportForCard(CanvasAnnotation card) =>
            Reports.GetValueOrDefault(card.Id);

        public AgentExecutionState GetStateForCard(string cardText) => AgentExecutionState.Idle;
        public AgentExecutionState GetStateForCard(string cardText, string? taskId) => AgentExecutionState.Idle;
        public AgentExecutionState GetStateForAnnotation(CanvasAnnotation item) => AgentExecutionState.Idle;
        public void SetState(string role, AgentExecutionState state, string detail = "") { }
        public void SetTeamState(string taskId, string role, AgentExecutionState state, string detail = "") { }
        public void RegisterTeam(string taskId, string name, string conversationId = "", string worktree = "") { }
        public void CompleteTeam(string taskId, bool success = true) { }
        public void UnregisterTeam(string taskId) { }
        public void Dispose() { }
    }

    private static CanvasPointer Pointer(double x, double y) => new(0, new(x, y), CanvasPointerButton.Left,
        CanvasModifiers.None, new CanvasViewTransform(0, 0, 1, new(0, 0, 1920, 1080)));

    [Fact]
    public void ThreeTierResolution_LiveTelemetryBeatsCardExplicitFolder()
    {
        var desktop = Guid.NewGuid();
        var card = new CanvasAnnotation(Guid.NewGuid(), desktop, AnnotationKind.Text,
            new(50, 50), new(290, 162), AgentTaskId: "task-01", AgentRole: "Coder")
        {
            AgentWorkingFolder = @"C:\Explicit\CardFolder"
        };

        var telemetry = new MockTelemetryProvider();
        telemetry.Reports[card.Id] = new AgentStatusReport(
            "Coder", AgentExecutionState.Working, "Working on task", DateTimeOffset.UtcNow,
            Worktree: @"C:\Live\TelemetryWorktree");

        var project = new WorkspaceZone
        {
            DesktopId = desktop,
            Bounds = new() { X = 0, Y = 0, W = 500, H = 500 },
            ProjectFolderPath = @"C:\Project\ZoneFolder"
        };

        var resolved = AgentCardContext.ResolveWorkingFolder(card, [project], telemetry);
        Assert.Equal(@"C:\Live\TelemetryWorktree", resolved);
    }

    [Fact]
    public void ThreeTierResolution_LiveWorkflowTeamWorktreeBeatsCardExplicitFolder()
    {
        var desktop = Guid.NewGuid();
        var card = new CanvasAnnotation(Guid.NewGuid(), desktop, AnnotationKind.Text,
            new(50, 50), new(290, 162), AgentTaskId: "task-02", AgentRole: "Coder")
        {
            AgentWorkingFolder = @"C:\Explicit\CardFolder"
        };

        var telemetry = new MockTelemetryProvider();
        var teamStatus = new TeamStatusReport("task-02", "Team Alpha", "Active", "Coder",
            new Dictionary<string, AgentStatusReport>(), Worktree: @"C:\Live\WorkflowWorktree");
        telemetry.Current = new TeamTelemetrySnapshot(
            new Dictionary<string, AgentStatusReport>(),
            DateTimeOffset.UtcNow,
            new Dictionary<string, TeamStatusReport> { ["task-02"] = teamStatus });

        var project = new WorkspaceZone
        {
            DesktopId = desktop,
            Bounds = new() { X = 0, Y = 0, W = 500, H = 500 },
            ProjectFolderPath = @"C:\Project\ZoneFolder"
        };

        var resolved = AgentCardContext.ResolveWorkingFolder(card, [project], telemetry);
        Assert.Equal(@"C:\Live\WorkflowWorktree", resolved);
    }

    [Fact]
    public void ThreeTierResolution_CardExplicitBeatsProjectZoneAndMission()
    {
        var desktop = Guid.NewGuid();
        var card = new CanvasAnnotation(Guid.NewGuid(), desktop, AnnotationKind.Text,
            new(50, 50), new(290, 162), AgentTaskId: "task-03", AgentRole: "Coder")
        {
            AgentWorkingFolder = @"C:\Explicit\CardFolder"
        };

        var mission = new WorkspaceZone
        {
            DesktopId = desktop,
            AgentTaskId = "task-03",
            Bounds = new() { X = 0, Y = 0, W = 500, H = 500 },
            MissionWorkingFolder = @"C:\Mission\WorkingFolder"
        };

        var project = new WorkspaceZone
        {
            DesktopId = desktop,
            Bounds = new() { X = 0, Y = 0, W = 500, H = 500 },
            ProjectFolderPath = @"C:\Project\ZoneFolder"
        };

        var resolved = AgentCardContext.ResolveWorkingFolder(card, [project], mission: mission);
        Assert.Equal(@"C:\Explicit\CardFolder", resolved);
    }

    [Fact]
    public void ThreeTierResolution_MissionWorkingFolderBeatsProjectZone()
    {
        var desktop = Guid.NewGuid();
        var card = new CanvasAnnotation(Guid.NewGuid(), desktop, AnnotationKind.Text,
            new(50, 50), new(290, 162), AgentTaskId: "task-04", AgentRole: "Coder");

        var mission = new WorkspaceZone
        {
            DesktopId = desktop,
            AgentTaskId = "task-04",
            Bounds = new() { X = 0, Y = 0, W = 500, H = 500 },
            MissionWorkingFolder = @"C:\Mission\WorkingFolder"
        };

        var project = new WorkspaceZone
        {
            DesktopId = desktop,
            Bounds = new() { X = 0, Y = 0, W = 500, H = 500 },
            ProjectFolderPath = @"C:\Project\ZoneFolder"
        };

        var resolved = AgentCardContext.ResolveWorkingFolder(card, [project], mission: mission);
        Assert.Equal(@"C:\Mission\WorkingFolder", resolved);
    }

    [Fact]
    public void ThreeTierResolution_ProjectZoneBeatsFallback()
    {
        var desktop = Guid.NewGuid();
        var card = new CanvasAnnotation(Guid.NewGuid(), desktop, AnnotationKind.Text,
            new(50, 50), new(290, 162), AgentTaskId: "task-05", AgentRole: "Coder");

        var project = new WorkspaceZone
        {
            DesktopId = desktop,
            Bounds = new() { X = 0, Y = 0, W = 500, H = 500 },
            ProjectFolderPath = @"C:\Project\ZoneFolder"
        };

        var resolved = AgentCardContext.ResolveWorkingFolder(card, [project], fallback: @"C:\Fallback\Dir");
        Assert.Equal(@"C:\Project\ZoneFolder", resolved);
    }

    [Fact]
    public void ThreeTierResolution_FallbackDirectoryUsedWhenNoUpperTierPresent()
    {
        var desktop = Guid.NewGuid();
        var card = new CanvasAnnotation(Guid.NewGuid(), desktop, AnnotationKind.Text,
            new(50, 50), new(290, 162), AgentTaskId: "task-06", AgentRole: "Coder");

        var resolved = AgentCardContext.ResolveWorkingFolder(card, [], fallback: @"C:\CustomFallback");
        Assert.Equal(@"C:\CustomFallback", resolved);

        var userProfileFallback = AgentCardContext.ResolveWorkingFolder(card, []);
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), userProfileFallback);
    }

    [Fact]
    public void ExplicitAgentWorkingFolder_RetainedInsideProjectZone()
    {
        var desktop = Guid.NewGuid();
        var project = new WorkspaceZone
        {
            DesktopId = desktop,
            Bounds = new() { X = 0, Y = 0, W = 1000, H = 1000 },
            ProjectFolderPath = @"C:\Project\BaselineFolder"
        };

        var cardWithExplicitFolder = new CanvasAnnotation(Guid.NewGuid(), desktop, AnnotationKind.Text,
            new(100, 100), new(340, 212), AgentTaskId: "task-07", AgentRole: "Lead")
        {
            AgentWorkingFolder = @"C:\Card\ExplicitOverride"
        };

        var resolved = AgentCardContext.ResolveWorkingFolder(cardWithExplicitFolder, [project]);
        Assert.Equal(@"C:\Card\ExplicitOverride", resolved);
    }

    [Fact]
    public void AnnotationCanvasTool_AutoInheritsProjectFolder_WhenPlacedInsideProjectZone()
    {
        var desktop = Guid.NewGuid();
        var document = new AnnotationDocument();
        var projectZone = new WorkspaceZone
        {
            DesktopId = desktop,
            Bounds = new() { X = 0, Y = 0, W = 800, H = 800 },
            ProjectFolderPath = @"C:\Project\InheritedWorkspace"
        };

        var tool = new AnnotationCanvasTool(document, () => desktop,
            resolveProjectFolder: card => AgentCardContext.ProjectAt([projectZone], card)?.ProjectFolderPath);

        tool.SetMode(AnnotationMode.Agent);
        tool.PointerDown(Pointer(150, 150));
        tool.PointerUp(Pointer(150, 150));

        var placedCard = Assert.Single(document.Items);
        Assert.Equal(@"C:\Project\InheritedWorkspace", placedCard.AgentWorkingFolder);
    }

    [Fact]
    public void AnnotationCanvasTool_LeavesFolderNull_WhenPlacedOutsideProjectZone()
    {
        var desktop = Guid.NewGuid();
        var document = new AnnotationDocument();
        var projectZone = new WorkspaceZone
        {
            DesktopId = desktop,
            Bounds = new() { X = 0, Y = 0, W = 500, H = 500 },
            ProjectFolderPath = @"C:\Project\InheritedWorkspace"
        };

        var tool = new AnnotationCanvasTool(document, () => desktop,
            resolveProjectFolder: card => AgentCardContext.ProjectAt([projectZone], card)?.ProjectFolderPath);

        tool.SetMode(AnnotationMode.Agent);
        tool.PointerDown(Pointer(1500, 1500));
        tool.PointerUp(Pointer(1500, 1500));

        var placedCard = Assert.Single(document.Items);
        Assert.Null(placedCard.AgentWorkingFolder);
    }

    [Fact]
    public void GitWorktreeHelper_DetectsStandardRepository()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "git_test_standard_" + Guid.NewGuid().ToString("N"));
        try
        {
            var dotGit = Path.Combine(tempDir, ".git");
            Directory.CreateDirectory(dotGit);
            File.WriteAllText(Path.Combine(dotGit, "HEAD"), "ref: refs/heads/feature/standard-repo\n");

            var info = GitWorktreeHelper.GetWorktreeInfo(tempDir);
            Assert.True(info.IsGitRepo);
            Assert.False(info.IsWorktree);
            Assert.Equal(Path.GetFullPath(tempDir), info.RepoRoot);
            Assert.Equal("feature/standard-repo", info.BranchName);
            Assert.Null(info.WorktreeName);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void GitWorktreeHelper_DetectsLinkedWorktree()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "git_test_wt_" + Guid.NewGuid().ToString("N"));
        try
        {
            var repoDir = Path.Combine(baseDir, "main_repo");
            var worktreeDir = Path.Combine(baseDir, "linked_wt");
            var gitdirWorktree = Path.Combine(repoDir, ".git", "worktrees", "wt_feature_branch");

            Directory.CreateDirectory(gitdirWorktree);
            Directory.CreateDirectory(worktreeDir);

            File.WriteAllText(Path.Combine(gitdirWorktree, "HEAD"), "ref: refs/heads/feature/worktree-branch\n");
            File.WriteAllText(Path.Combine(gitdirWorktree, "commondir"), "../..\n");
            File.WriteAllText(Path.Combine(worktreeDir, ".git"), $"gitdir: {gitdirWorktree}\n");

            var info = GitWorktreeHelper.GetWorktreeInfo(worktreeDir);
            Assert.True(info.IsGitRepo);
            Assert.True(info.IsWorktree);
            Assert.Equal(Path.GetFullPath(repoDir), info.RepoRoot);
            Assert.Equal("feature/worktree-branch", info.BranchName);
            Assert.Equal("wt_feature_branch", info.WorktreeName);
        }
        finally
        {
            if (Directory.Exists(baseDir)) Directory.Delete(baseDir, true);
        }
    }

    [Fact]
    public void GitWorktreeHelper_HandlesDetachedHeadAndNonGitFolders()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "git_test_detached_" + Guid.NewGuid().ToString("N"));
        try
        {
            var dotGit = Path.Combine(tempDir, ".git");
            Directory.CreateDirectory(dotGit);
            const string commitSha = "0123456789abcdef0123456789abcdef01234567";
            File.WriteAllText(Path.Combine(dotGit, "HEAD"), commitSha + "\r\n");

            var info = GitWorktreeHelper.GetWorktreeInfo(tempDir);
            Assert.True(info.IsGitRepo);
            Assert.False(info.IsWorktree);
            Assert.Equal(commitSha, info.BranchName);

            var nonGitDir = Path.Combine(tempDir, "plain_folder");
            Directory.CreateDirectory(nonGitDir);
            var nonGitInfo = GitWorktreeHelper.GetWorktreeInfo(nonGitDir);
            Assert.False(nonGitInfo.IsGitRepo);
            Assert.False(nonGitInfo.IsWorktree);

            var nullInfo = GitWorktreeHelper.GetWorktreeInfo(null);
            Assert.False(nullInfo.IsGitRepo);
            Assert.False(nullInfo.IsWorktree);

            var missingInfo = GitWorktreeHelper.GetWorktreeInfo(@"Z:\NonExistent\Folder\Path");
            Assert.False(missingInfo.IsGitRepo);
            Assert.False(missingInfo.IsWorktree);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void GitWorktreeHelper_DetectsLiveWorktree()
    {
        var worktreeFolder = @"C:\Users\ESCEHA\.gemini\antigravity\worktrees\CanvasWindowComposer\phase_1_worktree_environment";
        if (Directory.Exists(worktreeFolder))
        {
            var info = GitWorktreeHelper.GetWorktreeInfo(worktreeFolder);
            Assert.True(info.IsGitRepo);
            Assert.True(info.IsWorktree);
            Assert.Equal("feature/phase-1-worktree-environment", info.BranchName);
            Assert.Equal("phase_1_worktree_environment", info.WorktreeName);
            Assert.NotNull(info.RepoRoot);
        }
    }
}
