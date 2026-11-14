using CanvasDesktop.CanvasView;

namespace CanvasDesktop.Tests;

public class AgentCardTests
{
    private static CanvasPointer Pointer(double x, double y) => new(0, new(x, y), CanvasPointerButton.Left,
        CanvasModifiers.None, new CanvasViewTransform(0, 0, 1, new(0, 0, 1920, 1080)));

    [Fact]
    public void ClickPlacesIndependentAgentWithUndoRedoAndPersistence()
    {
        var desktop = Guid.NewGuid();
        var document = new AnnotationDocument();
        var tool = new AnnotationCanvasTool(document, () => desktop);
        tool.EditTextRequested += _ => throw new Exception("Agents must not open the note editor.");
        tool.SetMode(AnnotationMode.Agent);
        Assert.True(tool.PointerDown(Pointer(120, 180)));
        Assert.Empty(document.Items);
        tool.PointerUp(Pointer(120, 180));
        var card = Assert.Single(document.Items);
        Assert.True(MissionAgentCard.IsCard(card));
        Assert.Equal(new CanvasRect(120, 180, 240, 112), card.Bounds);
        Assert.Equal(AnnotationMode.Select, tool.Mode);
        Assert.True(document.Undo(desktop));
        Assert.Empty(document.Items);
        Assert.True(document.Undo(desktop, true));
        Assert.Equal(card, Assert.Single(document.Items));
        tool.SetMode(AnnotationMode.Agent);
        tool.PointerDown(Pointer(500, 180)); tool.PointerUp(Pointer(500, 180));
        Assert.Equal(2, document.Items.Select(item => item.AgentTaskId).Distinct().Count());
        var saved = card with { AgentWorkingFolder = Path.GetTempPath() };
        Assert.Equal(saved, System.Text.Json.JsonSerializer.Deserialize<CanvasAnnotation>(System.Text.Json.JsonSerializer.Serialize(saved)));
    }

    [Fact]
    public void CancelAndDesktopSwitchDoNotPlaceAgents()
    {
        var desktop = Guid.NewGuid();
        var document = new AnnotationDocument();
        var tool = new AnnotationCanvasTool(document, () => desktop);
        tool.SetMode(AnnotationMode.Agent);
        tool.PointerDown(Pointer(1, 2)); tool.Cancel(); tool.PointerUp(Pointer(1, 2));
        Assert.Empty(document.Items);
        tool.PointerDown(Pointer(1, 2)); desktop = Guid.NewGuid(); tool.PointerUp(Pointer(1, 2));
        Assert.Empty(document.Items);
    }

    [Fact]
    public void ContextUsesSmallestProjectOnSameDesktopAndExcludesMissionsAndTemporaryAreas()
    {
        var desktop = Guid.NewGuid();
        var card = new CanvasAnnotation(Guid.NewGuid(), desktop, AnnotationKind.Text, new(50, 50), new(290, 162));
        var outer = new WorkspaceZone { DesktopId = desktop, Bounds = new() { W = 1000, H = 1000 }, ProjectFolderPath = "outer" };
        var inner = new WorkspaceZone { DesktopId = desktop, Bounds = new() { W = 400, H = 400 }, ProjectFolderPath = "inner" };
        var mission = new WorkspaceZone { DesktopId = desktop, AgentTaskId = "mission", Bounds = new() { W = 100, H = 100 } };
        var temporary = new WorkspaceZone { DesktopId = desktop, IsTemporaryArea = true, Bounds = mission.Bounds };
        var otherDesktop = new WorkspaceZone { DesktopId = Guid.NewGuid(), Bounds = mission.Bounds };
        Assert.Same(inner, AgentCardContext.ProjectAt([outer, mission, temporary, otherDesktop, inner], card));
        Assert.Null(AgentCardContext.ProjectAt([outer, inner], card.Translate(2000, 0)));
    }
}
