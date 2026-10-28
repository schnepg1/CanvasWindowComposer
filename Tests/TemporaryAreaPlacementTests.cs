using CanvasDesktop;
using CanvasDesktop.CanvasView;
using Windows.Win32.UI.WindowsAndMessaging;

namespace CanvasDesktop.Tests;

public class TemporaryAreaPlacementTests
{
    private static WorkspaceHarness Start()
    {
        var rig = new WorkspaceHarness(new WorkspaceDocument { PlacementPolicyInitialized = true });
        rig.Workspace.Start();
        return rig;
    }

    private static WorkspaceItem Add(WorkspaceHarness rig, int handle, bool minimized = false)
    {
        rig.Api.AddWindow((IntPtr)handle, 10, 20, 800, 600, title: "Temporary " + handle,
            style: minimized ? (int)WINDOW_STYLE.WS_MINIMIZE : 0);
        rig.Wm.TryRegisterWindow((IntPtr)handle); rig.Workspace.SaveNow();
        return rig.Workspace.Document.Items.Single(i => i.Title == "Temporary " + handle);
    }

    [Fact]
    public void MovingTheAreaCarriesLiveAndMinimizedWindowsWithoutSavingTheirPlaces()
    {
        using var rig = Start();
        var first = Add(rig, 42); var second = Add(rig, 43, minimized: true);
        var area = rig.Workspace.TemporaryArea!; var a = first.Bounds; var b = second.Bounds;
        Assert.True(rig.Workspace.MoveZone(area.Id, -2000, 1300));
        Assert.Equal(area.Bounds.X - 2000, rig.Workspace.TemporaryArea!.Bounds.X);
        Assert.Equal(a.X - 2000, first.Bounds.X); Assert.Equal(b.Y + 1300, second.Bounds.Y);
        Assert.Equal(first.Bounds.X, rig.Canvas.Windows[(IntPtr)42].X);
        Assert.Equal(second.Bounds.Y, rig.Canvas.Windows[rig.Cards.Created.Single(c => !c.Disposed).Handle].Y);
        Assert.Equal(WindowState.Minimized, second.Bounds.State);
        Assert.All(rig.Workspace.Items, i => Assert.True(i.Temporary));
        Assert.Empty(rig.Store.Document.Items);
        var saved = Assert.Single(rig.Store.Document.Zones);
        Assert.True(saved.IsTemporaryArea); Assert.Empty(saved.ItemIds);
        Assert.Equal(rig.Workspace.TemporaryArea.Bounds, saved.Bounds);
        Assert.True(rig.Workspace.UndoZoneLayout());
        Assert.Equal(area.Bounds, rig.Workspace.TemporaryArea.Bounds); Assert.Equal(a.X, first.Bounds.X);
        Assert.True(rig.Workspace.RedoZoneLayout());
        Assert.Equal(a.X - 2000, first.Bounds.X); Assert.Empty(rig.Store.Document.Items);
    }

    [Fact]
    public void EmptyAreaCanBeCreatedRenamedResizedAndMovedBeforeAnyWindowsArrive()
    {
        WorkspaceDocument saved;
        Guid id;
        WorldRect target;
        using (var rig = Start())
        {
            var area = rig.Workspace.EnsureTemporaryArea()!; id = area.Id;
            Assert.Equal(0, area.Count);
            Assert.True(rig.Workspace.Zones.Update(id, "Occasional apps", "#34D399"));
            target = area.Bounds; target.W = 5000; target.H = 2000;
            Assert.True(rig.Workspace.ResizeZone(id, target));
            Assert.True(rig.Workspace.MoveZone(id, -6000, 2500)); target.X -= 6000; target.Y += 2500;
            rig.Workspace.Stop(save: true); saved = rig.Store.Document;
            Assert.Empty(saved.Items); Assert.Empty(Assert.Single(saved.Zones).ItemIds);
        }
        using var restarted = new WorkspaceHarness(saved);
        restarted.Workspace.Start();
        var restored = restarted.Workspace.TemporaryArea!;
        Assert.Equal(id, restored.Id); Assert.Equal(target, restored.Bounds);
        Assert.Equal("Occasional apps", restored.Name); Assert.Equal("#34D399", restored.Color); Assert.Equal(0, restored.Count);
        Assert.Empty(restarted.Cards.Created);
        var next = Add(restarted, 45);
        Assert.Equal(target.X + TemporaryAreaLayout.Gap, next.Bounds.X);
        Assert.Equal(target.Y + TemporaryAreaLayout.Header, next.Bounds.Y);
        Assert.Equal(WorkspacePlacement.Temporary, next.Placement);
    }

    [Fact]
    public void MovingTemporaryAreaLeavesKeptWindowsAndOverlappingProjectAreasInPlace()
    {
        using var rig = Start();
        var kept = Add(rig, 42); var temporary = Add(rig, 43);
        rig.Workspace.KeepPlace(kept.Id);
        var area = rig.Workspace.TemporaryArea!; var keptBounds = kept.Bounds; var temporaryBounds = temporary.Bounds;
        var project = area.Bounds; project.X -= 100; project.Y -= 100; project.W += 200; project.H += 200;
        Assert.True(rig.Workspace.Zones.Add(project, out var projectId));
        Assert.True(rig.Workspace.Zones.SetMembership(projectId, [kept.Id]));
        Assert.Null(rig.Workspace.Zones.Items.Single(z => z.Id == area.Id).ParentId);
        Assert.True(rig.Workspace.MoveZone(area.Id, 0, 1500));
        Assert.Equal(keptBounds, kept.Bounds);
        Assert.Equal(project, rig.Workspace.Zones.Items.Single(z => z.Id == projectId).Bounds);
        Assert.Equal(temporaryBounds.Y + 1500, temporary.Bounds.Y);
        Assert.Equal(WorkspacePlacement.Temporary, temporary.Placement);
        Assert.Equal(kept.Id, Assert.Single(rig.Store.Document.Items).Id);
    }

    [Fact]
    public void CanvasHeaderDragAndCornerResizeUseTheSavedAreaAndKeepItsContentsTemporary()
    {
        using var rig = Start();
        var item = Add(rig, 42); var before = item.Bounds; var area = rig.Workspace.TemporaryArea!;
        var tool = new ZoneCanvasTool(() => true, () => rig.Workspace.Zones.Items, rig.Workspace.Zones.MoveMembers);
        tool.Completed += edit =>
        {
            if (edit.Kind == ZoneGestureKind.Move) Assert.True(rig.Workspace.MoveZone(edit.ZoneId, edit.Dx, edit.Dy));
            else Assert.True(rig.Workspace.ResizeZone(edit.ZoneId, edit.Bounds));
        };
        CanvasPointer Pointer(double x, double y) => new(0, new(x, y), CanvasPointerButton.Left, CanvasModifiers.None,
            new(area.Bounds.X, area.Bounds.Y, 1, new(0, 0, 1920, 1080)));
        var controller = new CanvasToolController(); controller.SetTool(tool);
        Assert.True(controller.TryBeginTool(Pointer(30, 75)));
        Assert.Equal(ZoneGestureKind.Move, tool.Gesture.Kind);
        controller.MoveTool(Pointer(230, 175)); controller.CompleteTool(Pointer(230, 175));
        Assert.Equal(before.X + 200, item.Bounds.X); Assert.Equal(before.Y + 100, item.Bounds.Y);
        area = rig.Workspace.TemporaryArea!;
        Assert.True(controller.TryBeginTool(Pointer(0, 0)));
        Assert.Equal(ZoneGestureKind.Resize, tool.Gesture.Kind);
        controller.CompleteTool(Pointer(-100, -100));
        Assert.Equal(area.Bounds.X - 100, rig.Workspace.TemporaryArea!.Bounds.X);
        Assert.True(Assert.Single(rig.Workspace.Items).Temporary); Assert.Empty(rig.Store.Document.Items);
    }

    [Fact]
    public void GroupMoveAndUndoMoveTheAreaBeforeEvaluatingTemporaryWindowPlacement()
    {
        using var rig = Start();
        var item = Add(rig, 42); var area = rig.Workspace.TemporaryArea!;
        var before = rig.Workspace.SelectionSnapshot([item.Id], [area.Id]);
        ZoneItemPlacement Move(ZoneItemPlacement p) { var b = p.Bounds; b.X -= 5000; return p with { Bounds = b }; }
        var after = before with { Items = before.Items.Select(Move).ToArray(), Areas = before.Areas.Select(Move).ToArray() };
        Assert.True(rig.Workspace.ApplySelectionPlacement(before, after));
        Assert.True(Assert.Single(rig.Workspace.Items).Temporary);
        Assert.True(rig.Workspace.ApplySelectionPlacement(after, before));
        Assert.True(Assert.Single(rig.Workspace.Items).Temporary); Assert.Empty(rig.Store.Document.Items);
    }

    [Fact]
    public void ClosingTheLastWindowLeavesAnEmptyMovableAreaAndNoStaleMembership()
    {
        using var rig = Start();
        Add(rig, 42); var area = rig.Workspace.TemporaryArea!;
        rig.Api.Windows.Remove((IntPtr)42); rig.Wm.RemoveWindow((IntPtr)42);
        Assert.True(rig.Workspace.MoveZone(area.Id, 200, 100));
        Assert.Equal(0, rig.Workspace.TemporaryArea!.Count);
        Assert.Empty(rig.Workspace.Zones.Members(area.Id)); Assert.Empty(rig.Store.Document.Items);
        Assert.Empty(rig.Workspace.Document.Zones.Single().ItemIds);
    }

    [Fact]
    public void FilePersistenceKeepsTheEditedAreaAndOmitsItsSessionMembers()
    {
        string directory = Path.Combine(Path.GetTempPath(), "canvas-area-object-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new WorkspaceStore(Path.Combine(directory, "workspace.json"));
            Guid areaId, windowId;
            using (var rig = new WorkspaceHarness(store: store))
            {
                rig.Workspace.Start();
                windowId = Add(rig, 42).Id; areaId = rig.Workspace.TemporaryArea!.Id;
                Assert.True(rig.Workspace.MoveZone(areaId, -3000, 1500));
                Assert.True(rig.Workspace.Zones.Update(areaId, "Occasional apps", "#34D399"));
                rig.Workspace.Stop(save: true);
            }
            string json = File.ReadAllText(store.FilePath);
            Assert.DoesNotContain(windowId.ToString(), json);
            var saved = Assert.Single(store.Load(out _).Zones);
            Assert.True(saved.IsTemporaryArea); Assert.Equal(areaId, saved.Id);
            Assert.Equal("Occasional apps", saved.Name); Assert.Equal("#34D399", saved.Color);
            Assert.Empty(saved.ItemIds);
            using var restarted = new WorkspaceHarness(store: store);
            restarted.Workspace.Start();
            Assert.Equal(saved.Bounds, restarted.Workspace.TemporaryArea!.Bounds);
            Assert.Empty(restarted.Workspace.Items); Assert.Empty(restarted.Cards.Created);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void AnchorOnlyWorkspaceUpgradesToAnEditableAreaWithoutMovingItsLocation()
    {
        var doc = new WorkspaceDocument { PlacementPolicyInitialized = true };
        doc.TemporaryAreas[Guid.Empty] = new() { X = -6000, Y = 2000 };
        using var rig = new WorkspaceHarness(doc);
        rig.Workspace.Start();
        Assert.Empty(rig.Workspace.Document.TemporaryAreas);
        var area = Assert.Single(rig.Workspace.Zones.Items);
        Assert.True(area.IsTemporaryArea); Assert.Equal(-6000, area.Bounds.X); Assert.Equal(2000, area.Bounds.Y);
        Assert.True(rig.Workspace.MoveZone(area.Id, 250, 150));
        Assert.Equal(-5750, rig.Store.Document.Zones.Single().Bounds.X);
    }
}
