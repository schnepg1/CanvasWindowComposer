using CanvasDesktop;

namespace CanvasDesktop.Tests;

public class ProjectFolderAndPlaceholderCopyTests
{
    [Fact]
    public void ProjectFolderDocksOnceAndRemainsMovable()
    {
        var zone = new WorkspaceZone { Bounds = new WorldRect { X = 100, Y = 200, W = 900, H = 600 } };
        using var rig = new WorkspaceHarness(new WorkspaceDocument { Zones = [zone] });
        rig.Workspace.Start();
        string path = Path.GetTempPath();

        Assert.True(rig.Workspace.SetProjectFolder(zone.Id, path));
        var item = Assert.Single(rig.Workspace.Document.Items);
        Assert.Equal(Path.TrimEndingDirectorySeparator(path), zone.ProjectFolderPath);
        Assert.Equal(item.Id, Assert.Single(zone.ItemIds));
        Assert.Equal((124d, 224d), (item.Bounds.X, item.Bounds.Y));
        Assert.False(item.Bounds.PinnedToScreen);

        var handle = rig.Cards.Created.Single(card => card.Item.Id == item.Id).Handle;
        rig.Canvas.SetWindow(handle, 300, 350, item.Bounds.W, item.Bounds.H);
        Assert.True(rig.Workspace.SetProjectFolder(zone.Id, path));
        Assert.Same(item, Assert.Single(rig.Workspace.Document.Items));
        Assert.Equal((300d, 350d), (item.Bounds.X, item.Bounds.Y));
    }

    [Fact]
    public void CopiesResolveToThePromotedCardAndSurviveRemovalOfTheMain()
    {
        var item = new WorkspaceItem { Title = "Editor", ExecutablePath = @"C:\Apps\Editor.exe",
            Bounds = new WorldRect { W = 400, H = 300 } };
        using var rig = new WorkspaceHarness(new WorkspaceDocument { Items = [item] });
        rig.Workspace.Start();
        rig.Workspace.Duplicate(item.Id);
        var copy = Assert.Single(rig.Workspace.Document.Items, candidate => candidate.Id != item.Id);
        var originalHandle = rig.Cards.Created.Single(card => card.Item.Id == item.Id).Handle;
        var copyHandle = rig.Cards.Created.Single(card => card.Item.Id == copy.Id).Handle;

        Assert.NotEqual(item.Bounds.X, copy.Bounds.X);
        Assert.Equal(originalHandle, rig.Workspace.ResolveMainNavigationWindow(copyHandle));
        rig.Workspace.SetAsMain(copy.Id);
        Assert.Equal(copyHandle, rig.Workspace.ResolveMainNavigationWindow(originalHandle));
        rig.Workspace.Forget(copy.Id);
        Assert.True(Assert.Single(rig.Workspace.Document.Items).IsMainCopy);
    }
}
