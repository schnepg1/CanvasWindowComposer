using CanvasDesktop;

namespace CanvasDesktop.Tests;

public class WorkspaceSelectionTests
{
    private static WorkspaceItem Item(double x) => new() { Title="Editor",ExecutablePath=@"C:\Apps\Editor.exe",Bounds=new WorldRect { X=x,Y=50,W=400,H=300 } };
    private static WorkspaceSelectionPlacement Translate(WorkspaceSelectionPlacement p,double dx)
    {
        ZoneItemPlacement Move(ZoneItemPlacement item) { var b=item.Bounds;b.X+=dx;return item with { Bounds=b }; }
        return p with { Items=p.Items.Select(Move).ToArray(),Areas=p.Areas.Select(Move).ToArray() };
    }
    [Fact]
    public void SavedItemsAndTilesMoveTogetherPersistOnceAndReplayWithoutChangingMembership()
    {
        var a=Item(100);var b=Item(800);var tile=new WorkspaceZone { Bounds=new WorldRect { W=1800,H=1000 },ItemIds=[a.Id,b.Id] };
        using var rig=new WorkspaceHarness(new WorkspaceDocument { Items=[a,b],Zones=[tile] });rig.Workspace.Start();
        var before=rig.Workspace.SelectionSnapshot([a.Id,b.Id],[tile.Id]);var after=Translate(before,300);int saves=rig.Store.Saves;
        Assert.True(rig.Workspace.ApplySelectionPlacement(before,after));Assert.Equal(saves+1,rig.Store.Saves);
        Assert.Equal(400,a.Bounds.X);Assert.Equal(1100,b.Bounds.X);Assert.Equal(300,tile.Bounds.X);Assert.Equal(new[]{a.Id,b.Id},tile.ItemIds);
        Assert.True(rig.Workspace.ApplySelectionPlacement(after,before));Assert.Equal(100,a.Bounds.X);Assert.Equal(800,b.Bounds.X);Assert.Equal(0,tile.Bounds.X);
    }
    [Fact]
    public void MissingPinnedOrStaleMemberRejectsEntireMoveWithoutSaving()
    {
        var a=Item(100);var b=Item(800);using var rig=new WorkspaceHarness(new WorkspaceDocument { Items=[a,b] });rig.Workspace.Start();
        var before=rig.Workspace.SelectionSnapshot([a.Id,b.Id],[]);var after=Translate(before,300);int saves=rig.Store.Saves;
        var bounds=b.Bounds;bounds.PinnedToScreen=true;b.Bounds=bounds;
        Assert.False(rig.Workspace.ApplySelectionPlacement(before,after));Assert.Equal(100,a.Bounds.X);Assert.Equal(800,b.Bounds.X);Assert.Equal(saves,rig.Store.Saves);
        bounds.PinnedToScreen=false;bounds.X++;b.Bounds=bounds;
        Assert.False(rig.Workspace.ApplySelectionPlacement(before,after));Assert.Equal(100,a.Bounds.X);
        rig.Workspace.Document.Items.Remove(b);Assert.False(rig.Workspace.ApplySelectionPlacement(before,after));Assert.Equal(100,a.Bounds.X);
    }
    [Fact]
    public void SaveFailureRetainsLogicalMoveAndAllowsAtomicUndo()
    {
        var a=Item(100);using var rig=new WorkspaceHarness(new WorkspaceDocument { Items=[a] });rig.Workspace.Start();
        var before=rig.Workspace.SelectionSnapshot([a.Id],[]);var after=Translate(before,300);rig.Store.SaveError=new IOException("disk full");
        Assert.True(rig.Workspace.ApplySelectionPlacement(before,after));Assert.Equal(400,a.Bounds.X);Assert.NotNull(rig.Workspace.Status);
        rig.Store.SaveError=null;Assert.True(rig.Workspace.ApplySelectionPlacement(after,before));Assert.Equal(100,a.Bounds.X);
    }
    [Fact]
    public void DesktopChangeAndOutOfRangeDestinationRejectTheBatch()
    {
        var a=Item(100);using var rig=new WorkspaceHarness(new WorkspaceDocument { Items=[a] });rig.Workspace.Start();
        var before=rig.Workspace.SelectionSnapshot([a.Id],[]);Assert.False(rig.Workspace.ApplySelectionPlacement(before,Translate(before,double.NaN)));
        rig.Desktops.SwitchTo(Guid.NewGuid());Assert.False(rig.Workspace.ApplySelectionPlacement(before,Translate(before,300)));Assert.Equal(100,a.Bounds.X);
    }
}
