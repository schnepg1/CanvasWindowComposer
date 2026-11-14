using CanvasDesktop.CanvasView;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CanvasDesktop.Tests;

[Collection("ShellViews")]
public class TeamPodTests
{
    [Fact]
    public void RepeatedTelemetryAndWorkspaceReloadKeepOnePodAtItsSavedPosition()
    {
        using var rig = new WorkspaceHarness();
        var annotations = new AnnotationDocument();
        var service = new TeamPodService(rig.Workspace, annotations);
        var desktop = rig.Desktops.CurrentDesktopId;
        var snapshot = TeamTelemetryJson.Parse("""{"teams":{"feature":{"taskId":"feature","name":"Window linking","activeRole":"architect"}}}""");
        service.Reconcile(snapshot, desktop);
        var zone = Assert.Single(rig.Workspace.Document.Zones);
        Assert.True(zone.IsMission);
        Assert.Equal("Window linking", zone.Name);
        Assert.Equal(7, annotations.Items.Count(a => a.AgentRole.Length > 0));
        Assert.Equal(9, annotations.Items.Count(a => a.Kind == AnnotationKind.Arrow));
        Assert.Equal(3, annotations.Items.Count(a => a.InBackground));
        var original = zone.Bounds; var moved = original; moved.X += 400; moved.Y += 800;
        zone.Bounds = moved;
        var offset = annotations.Items.Select(a => a.Translate(400, 800)).ToArray();
        annotations.ApplyBatch(annotations.Revision, desktop, offset);
        service.Reconcile(snapshot, desktop);
        Assert.Equal(moved, zone.Bounds); Assert.Equal(offset, annotations.Items);

        // Undoing creation removes its frame as well as the cards.
        Assert.True(annotations.Undo(desktop));
        Assert.True(annotations.Undo(desktop));
        Assert.Empty(rig.Workspace.Document.Zones); Assert.Empty(annotations.Items);
        Assert.True(annotations.Undo(desktop, true));
        Assert.True(annotations.Undo(desktop, true));

        var saved = WorkspacePersistence.SavedState(rig.Workspace.Document);
        using var restarted = new WorkspaceHarness(saved);
        var reloaded = new AnnotationDocument(annotations.Items);
        new TeamPodService(restarted.Workspace, reloaded).Reconcile(snapshot, desktop);
        Assert.Equal("feature", Assert.Single(restarted.Workspace.Document.Zones).AgentTaskId);
        Assert.Equal(moved, Assert.Single(restarted.Workspace.Document.Zones).Bounds);
        Assert.Equal(offset, reloaded.Items);
    }

    [Fact]
    public void TwoRegistrationsCreateSeparateFramesWithoutDuplicatingExistingPods()
    {
        using var rig = new WorkspaceHarness();
        var annotations = new AnnotationDocument();
        var pods = new TeamPodService(rig.Workspace, annotations);
        var snapshot = TeamTelemetryJson.Parse("""{"teams":{"a":{"name":"A"},"b":{"name":"B"}}}""");
        pods.Reconcile(snapshot, Guid.Empty); pods.Reconcile(snapshot, Guid.Empty);
        Assert.Equal(2, rig.Workspace.Document.Zones.Count);
        var first = rig.Workspace.Document.Zones[0].Bounds; var second = rig.Workspace.Document.Zones[1].Bounds;
        Assert.True(second.X > first.X + first.W);
        Assert.Equal(19, annotations.Items.Count(a => a.AgentTaskId == "a"));
        Assert.Equal(19, annotations.Items.Count(a => a.AgentTaskId == "b"));
    }

    [Fact]
    public void MissionsPlacedInDifferentProjectAreasKeepIndependentSavedPositionsAndLocks()
    {
        using var rig = new WorkspaceHarness();
        var annotations = new AnnotationDocument();
        var pods = new TeamPodService(rig.Workspace, annotations);
        var desktop = rig.Desktops.CurrentDesktopId;
        Assert.True(rig.Workspace.Zones.Add(ZoneGeometry.FromPoints(-4500, -2500, 4500, 2500), out var firstProject));
        Assert.True(rig.Workspace.Zones.Add(ZoneGeometry.FromPoints(5500, -2500, 14500, 2500), out var secondProject));
        var first = Assert.IsType<Guid>(pods.CreateMission(desktop, "  First mission  ", new(0, 0)));
        var second = Assert.IsType<Guid>(pods.CreateMission(desktop, "Second mission", new(10000, 0)));
        Assert.True(pods.SetPositionLocked(first, true));
        var workingFolder = System.IO.Path.GetTempPath();
        Assert.True(pods.SetWorkingFolder(first, workingFolder));
        Assert.False(pods.SetWorkingFolder(secondProject, workingFolder));
        Assert.False(pods.SetPositionLocked(secondProject, true));
        Assert.Null(pods.CreateMission(Guid.NewGuid(), "Wrong desktop", new(0, 0)));
        Assert.Null(pods.CreateMission(desktop, "Invalid location", new(double.NaN, 0)));
        Assert.Null(pods.CreateMission(desktop, new string('x', 121), new(0, 0)));

        var saved = WorkspacePersistence.SavedState(rig.Workspace.Document);
        using var restarted = new WorkspaceHarness(saved);
        var zones = restarted.Workspace.Zones.Items.ToDictionary(z => z.Id);
        Assert.True(zones[first].IsMission);
        Assert.Equal("First mission", zones[first].Name);
        Assert.True(zones[first].MissionPositionLocked);
        Assert.Equal(workingFolder, restarted.Workspace.Document.Zones.Single(zone => zone.Id == first).MissionWorkingFolder);
        Assert.False(zones[second].MissionPositionLocked);
        Assert.Null(zones[first].ParentId);
        Assert.Null(zones[second].ParentId);
        Assert.True(ZoneHierarchy.Contains(zones[firstProject].Bounds, zones[first].Bounds));
        Assert.True(ZoneHierarchy.Contains(zones[secondProject].Bounds, zones[second].Bounds));
    }

    [Fact]
    public void SavedMissionRecoversMissingCardsWithoutCreatingAnotherFrame()
    {
        using var rig = new WorkspaceHarness();
        var annotations = new AnnotationDocument();
        var desktop = rig.Desktops.CurrentDesktopId;
        var snapshot = TeamTelemetryJson.Parse("""{"teams":{"legacy":{"name":"Restore cards"}}}""");
        new TeamPodService(rig.Workspace, annotations).Reconcile(snapshot, desktop);
        var id = Assert.Single(rig.Workspace.Document.Zones).Id;
        using var restarted = new WorkspaceHarness(WorkspacePersistence.SavedState(rig.Workspace.Document));
        var restored = new AnnotationDocument();
        var pods = new TeamPodService(restarted.Workspace, restored);
        pods.Reconcile(TeamTelemetrySnapshot.Empty, desktop);
        pods.Reconcile(TeamTelemetrySnapshot.Empty, desktop);
        Assert.Equal(id, Assert.Single(restarted.Workspace.Document.Zones).Id);
        Assert.Equal(annotations.Items, restored.Items);
    }

    [Fact]
    public void ManualMissionStartsEmptyAndAddsOnlyIndividuallyRequestedCardsAfterReload()
    {
        using var rig = new WorkspaceHarness();
        var annotations = new AnnotationDocument();
        var original = new TeamPodService(rig.Workspace, annotations);
        var desktop = rig.Desktops.CurrentDesktopId;
        var id = Assert.IsType<Guid>(original.CreateMission(desktop, "Draft Friday's script", new(150, 200)));
        Assert.Empty(annotations.Items);

        using var restarted = new WorkspaceHarness(WorkspacePersistence.SavedState(rig.Workspace.Document));
        var pods = new TeamPodService(restarted.Workspace, annotations);
        pods.Reconcile(TeamTelemetrySnapshot.Empty, desktop);
        Assert.Empty(annotations.Items);
        Assert.True(pods.AddAgent(id, "Writer"));
        var writer = Assert.Single(annotations.Items);
        Assert.Equal("Writer", writer.Text);
        Assert.True(pods.AddAgent(id, "Reviewer"));
        Assert.True(pods.AddAgent(id, "Editor"));
        Assert.Equal(new[] { "Writer", "Reviewer", "Editor" }, annotations.Items.Select(a => a.Text));
        Assert.False(CanvasSelection.Intersects(writer.Bounds, annotations.Items[1].Bounds));
        Assert.False(CanvasSelection.Intersects(writer.Bounds, annotations.Items[2].Bounds));
        Assert.True(annotations.Undo(desktop));
        Assert.Equal(2, annotations.Items.Count);
        Assert.True(annotations.Undo(desktop, true));
        Assert.Equal(3, annotations.Items.Count);
        new TeamPodService(restarted.Workspace, annotations).Reconcile(TeamTelemetrySnapshot.Empty, desktop);
        Assert.Equal(writer, annotations.Items[0]);
        Assert.Equal(3, annotations.Items.Count);
    }

    [Fact]
    public void MissionExpansionUsesOwnedCardsAndProjectExpansionLeavesMissionsAlone()
    {
        var project = new CanvasItemId(CanvasItemKind.Project, Guid.NewGuid());
        var mission = new CanvasItemId(CanvasItemKind.Project, Guid.NewGuid());
        var windows = new CanvasItemId(CanvasItemKind.Window, Guid.NewGuid());
        var card = new CanvasItemId(CanvasItemKind.Annotation, Guid.NewGuid());
        var layout = new CanvasRect(0, 0, 400, 400);
        CanvasSelectionItem[] items =
        [
            new(project, layout),
            new(mission, layout, Members: [card], ExplicitMembersOnly: true),
            new(windows, new(120, 180, 40, 40)),
            new(card, new(40, 40, 200, 100), ExcludeFromSpatialGroups: true)
        ];
        Assert.True(CanvasSelection.Expand([project], items).SetEquals([project, windows]));
        Assert.True(CanvasSelection.Expand([mission], items).SetEquals([mission, card]));
    }

    [Fact]
    [Trait("Category", "NativeRendering")]
    public void PodMoveIncludesConnectorsAndUndoRestoresFrameAndCardsTogether()
        => WorkspaceViewTests.OnSta(() =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "pod-move-" + Guid.NewGuid().ToString("N"));
            try
            {
                var config = new FakeAppConfig { EnableStartupIntro = false };
                using var rig = new WorkspaceHarness(config: config); rig.Workspace.Start();
                using var overview = new OverviewManager(rig.Canvas, rig.Wm, rig.Api, rig.Input, config);
                overview.AttachZones(rig.Workspace.Zones); overview.AttachPlaceholderWorkspace(rig.Workspace);
                overview.AttachAnnotations(rig.Desktops, Path.Combine(folder, "annotations.json"));
                var snapshot = TeamTelemetryJson.Parse("""{"teams":{"move-test":{"name":"Move test"}}}""");
                var service = new TeamPodService(rig.Workspace, overview.Annotations!);
                service.Reconcile(snapshot, rig.Desktops.CurrentDesktopId);
                var zone = Assert.Single(rig.Workspace.Document.Zones);
                var before = overview.Annotations!.Items.ToArray(); var bounds = zone.Bounds;
                var all = overview.SelectionItems();
                var selected = CanvasSelection.Expand([new(CanvasItemKind.Project, zone.Id)], all);
                var move = new CanvasSelectionMove(all.Where(i => selected.Contains(i.Key)).ToArray(), 200, 300);
                Assert.True(service.SetPositionLocked(zone.Id, true));
                overview.MoveSelection(move);
                Assert.Equal(bounds, zone.Bounds);
                Assert.Equal(before, overview.Annotations.Items);
                Assert.True(service.SetPositionLocked(zone.Id, false));
                overview.MoveSelection(move);
                Assert.Equal(bounds.X + 200, zone.Bounds.X);
                Assert.Equal(before.Select(a => a.Translate(200, 300)), overview.Annotations.Items);
                Assert.True(overview.Annotations.Undo(rig.Desktops.CurrentDesktopId));
                Assert.Equal(bounds, zone.Bounds); Assert.Equal(before, overview.Annotations.Items);
                Assert.True(overview.Annotations.Undo(rig.Desktops.CurrentDesktopId, true));
                Assert.Equal(bounds.X + 200, zone.Bounds.X);
                Assert.Equal(before.Select(a => a.Translate(200, 300)), overview.Annotations.Items);
                var persisted = new AnnotationPersistence(Path.Combine(folder, "annotations.json"));
                Assert.Equal(overview.Annotations.Items, persisted.Document.Items);
            }
            finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        });

    [Theory]
    [InlineData(AgentExecutionState.Working)]
    [InlineData(AgentExecutionState.Testing)]
    [InlineData(AgentExecutionState.Done)]
    [InlineData(AgentExecutionState.Blocked)]
    [InlineData(AgentExecutionState.Error)]
    public void FluentBadgeContainsStateAndDetailAndRendersAtNarrowWidths(AgentExecutionState state)
        => WorkspaceViewTests.OnSta(() =>
        {
            var pill = new TelemetryStatusPill(new("programmer", state, "Editing WindowManager.cs " + new string('x', 200)), 160);
            Assert.IsType<Wpf.Ui.Controls.Badge>(pill.Badge);
            Assert.Contains(state.ToString().ToUpperInvariant(), pill.Label);
            Assert.Contains("Editing WindowManager.cs", pill.Label);
            Assert.Equal(TextTrimming.CharacterEllipsis, Assert.IsType<TextBlock>(pill.Badge.Content).TextTrimming);
            var bitmap = new RenderTargetBitmap(160, 28, 96, 96, PixelFormats.Pbgra32); bitmap.Render(pill.Badge);
            var pixels = new byte[160 * 28 * 4]; bitmap.CopyPixels(pixels, 160 * 4, 0);
            Assert.Contains(pixels, p => p > 0);
        });
}
