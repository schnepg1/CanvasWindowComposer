using CanvasDesktop.CanvasView;

namespace CanvasDesktop.Tests;

public class TeamTelemetryTests
{
    private const string TwoTeams = """
        {"roles":{"programmer":{"state":"Error","detail":"legacy"}},"teams":{
          "task-a":{"name":"Feature A","taskId":"task-a","activeRole":"architect","roles":{
            "programmer":{"state":"Working","detail":"Editing WindowManager.cs"}}},
          "task-b":{"name":"Feature B","taskId":"task-b","roles":{
            "programmer":{"state":"Testing","detail":"Running mutex tests"}}}}}
        """;
    private static CanvasAnnotation Card(string task, string role = "programmer") => new(
        Guid.NewGuid(), Guid.Empty, AnnotationKind.Rectangle, new(0, 0), new(400, 180), Text: "User renamed this card")
        { AgentTaskId = task, AgentRole = role };

    [Fact]
    public void TasksResolveByIdentityEvenWhenCardTextChanges()
    {
        var snapshot = TeamTelemetryJson.Parse(TwoTeams);
        Assert.Equal(2, snapshot.Teams.Count);
        Assert.Equal("Editing WindowManager.cs", TeamTelemetryJson.ReportForCard(snapshot, Card("task-a"))!.Detail);
        Assert.Equal(AgentExecutionState.Testing, TeamTelemetryJson.ReportForCard(snapshot, Card("task-b"))!.State);
        Assert.Null(TeamTelemetryJson.ReportForCard(snapshot, Card("unregistered")));
        Assert.Equal(AgentExecutionState.Idle, TeamTelemetryJson.ReportForCard(snapshot, Card("task-a", "reviewer"))!.State);
        Assert.Equal(AgentExecutionState.Working, TeamTelemetryJson.ReportForCard(snapshot, Card("task-a", "architect"))!.State);
    }

    [Theory]
    [InlineData("{\"teams\":{\"a\":{\"taskId\":\"b\"}}}")]
    [InlineData("{\"teams\":[]}")]
    [InlineData("{\"roles\":{\"programmer\":{\"state\":\"999\"}}}")]
    [InlineData("{\"teams\":{\"a\":null}}")]
    [InlineData("[]")]
    [InlineData("{\"teams\":{\"a\":{},\"a\":{}}}")]
    [InlineData("{\"roles\":{\"dev\":{},\"DEV\":{}}}")]
    public void MalformedRegistrationsAreRejected(string json)
        => Assert.Throws<System.Text.Json.JsonException>(() => TeamTelemetryJson.Parse(json));

    [Fact]
    public void BomAndLegacyRolesRemainCompatible()
    {
        var snapshot = TeamTelemetryJson.Parse("\uFEFF" + TwoTeams);
        Assert.Equal(AgentExecutionState.Error, TeamTelemetryJson.LegacyReport(snapshot, "Worktree Programmer")!.State);
    }

    [Fact]
    public void TemplateInstancesHaveStableDisjointIdsAndBoundConnectors()
    {
        var desktop = Guid.NewGuid();
        var first = TeamPodLayout.Create(desktop, "task-a", new(100, 100));
        var repeated = TeamPodLayout.Create(desktop, "task-a", new(100, 100));
        var second = TeamPodLayout.Create(desktop, "task-b", new(100, 100));
        var otherDesktop = TeamPodLayout.Create(Guid.NewGuid(), "task-a", new(100, 100));
        Assert.Equal(first.Items, repeated.Items);
        Assert.Equal(first.ZoneId, repeated.ZoneId);
        Assert.NotEqual(first.ZoneId, second.ZoneId);
        Assert.Empty(first.Items.Select(a => a.Id).Intersect(second.Items.Select(a => a.Id)));
        Assert.Empty(first.Items.Select(a => a.Id).Intersect(otherDesktop.Items.Select(a => a.Id)));
        Assert.Equal(7, first.Items.Count(a => a.AgentRole.Length > 0));
        Assert.Equal(9, first.Items.Count(a => a.Kind == AnnotationKind.Arrow));
        Assert.All(first.Items, a =>
        {
            Assert.Equal("task-a", a.AgentTaskId);
            Assert.True(CanvasSelection.Contains(first.Bounds, a.A));
            Assert.True(CanvasSelection.Contains(first.Bounds, a.B));
        });
        var cards = first.Items.Where(a => a.AgentRole.Length > 0).Select(a => a.Id).ToHashSet();
        Assert.All(first.Items.Where(a => a.Kind == AnnotationKind.Arrow), arrow =>
        {
            Assert.Contains(arrow.SourceId!.Value, cards); Assert.Contains(arrow.TargetId!.Value, cards);
        });
        // Persisted identity metadata survives annotation JSON round trips.
        var json = System.Text.Json.JsonSerializer.Serialize(first.Items);
        Assert.Equal(first.Items, System.Text.Json.JsonSerializer.Deserialize<CanvasAnnotation[]>(json));
    }

    [Fact]
    public void ProviderRecoversAfterPartialWriteAndAtomicReplacementWithoutLosingTeams()
    {
        var folder = Path.Combine(Path.GetTempPath(), "teams-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "state.json");
            File.WriteAllText(path, TwoTeams);
            using var provider = new WindowsNativeTelemetryProvider(path, () => false);
            File.WriteAllText(path, "{\"teams\":{"); provider.ReloadState();
            Assert.Equal(2, provider.Current.Teams.Count);
            File.WriteAllText(path + ".tmp", TwoTeams.Replace("Editing WindowManager.cs", "Phase complete"));
            File.Move(path + ".tmp", path, true); provider.ReloadState();
            Assert.Equal("Phase complete", provider.GetReportForCard(Card("task-a"))!.Detail);
            provider.SetState("reviewer", AgentExecutionState.Done, "Reviewed");
            provider.ReloadState();
            Assert.Equal(2, TeamTelemetryJson.Parse(File.ReadAllText(path)).Teams.Count);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void PollDiscoversLateFileAndMutexReleaseRestoresReportedStateWithoutAffectingTeams()
    {
        var folder = Path.Combine(Path.GetTempPath(), "teams-late-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, "state.json");
        bool held = false;
        try
        {
            using var provider = new WindowsNativeTelemetryProvider(path, () => held);
            Directory.CreateDirectory(folder); File.WriteAllText(path, TwoTeams);
            provider.CheckHardwareAndState();
            held = true; provider.CheckHardwareAndState();
            Assert.Equal(AgentExecutionState.Testing, provider.Current.Roles["programmer"].State);
            Assert.Equal(AgentExecutionState.Working, provider.GetReportForCard(Card("task-a"))!.State);
            held = false; provider.CheckHardwareAndState();
            Assert.Equal(AgentExecutionState.Error, provider.Current.Roles["programmer"].State);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
}
