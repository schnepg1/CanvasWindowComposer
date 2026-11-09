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
        Assert.StartsWith("AI Mission Control", zone.Name);
        Assert.Equal(16, annotations.Items.Count);
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
        Assert.Equal(16, annotations.Items.Count(a => a.AgentTaskId == "a"));
        Assert.Equal(16, annotations.Items.Count(a => a.AgentTaskId == "b"));
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
                new TeamPodService(rig.Workspace, overview.Annotations!).Reconcile(snapshot, rig.Desktops.CurrentDesktopId);
                var zone = Assert.Single(rig.Workspace.Document.Zones);
                var before = overview.Annotations!.Items.ToArray(); var bounds = zone.Bounds;
                var all = overview.SelectionItems();
                var selected = CanvasSelection.Expand([new(CanvasItemKind.Project, zone.Id)], all);
                overview.MoveSelection(new(all.Where(i => selected.Contains(i.Key)).ToArray(), 200, 300));
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
