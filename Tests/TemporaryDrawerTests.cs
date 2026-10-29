using CanvasDesktop;
using Windows.Win32.UI.WindowsAndMessaging;

namespace CanvasDesktop.Tests;

public class TemporaryDrawerTests
{
    internal static WorkspaceItemView Row(string title) => new(Guid.NewGuid(), title, @"C:\Apps\Editor.exe", "", false, new() { W = 400, H = 300 }, "Open", Temporary: true);

    [Fact]
    public void NewBatchesArePrependedWithoutReorderingExistingEntriesOrAnnouncingTitleChanges()
    {
        var state = new TemporaryDrawerState(); var area = Guid.NewGuid();
        var first = Row("First"); var second = Row("Second"); var third = Row("Third");
        Assert.Equal(new[] { first.Id }, state.Update(area, [first], 100));
        Assert.Equal(new[] { third.Id, second.Id }, state.Update(area, [first, second, third], 200));
        Assert.Equal(new[] { third.Id, second.Id, first.Id }, state.Items.Select(i => i.Id));
        Assert.Empty(state.Update(area, [first with { Title = "Renamed" }, second, third], 300));
        Assert.Equal("Renamed", state.Items.Last().Title);
        state.Update(area, [first, third], 400);
        Assert.Equal(new[] { third.Id, first.Id }, state.Items.Select(i => i.Id));
    }

    [Fact]
    public void ArrivalAndManualPeeksExpireAfterFiveSecondsAndNewArrivalsRestartTheClock()
    {
        var state = new TemporaryDrawerState(); var area = Guid.NewGuid(); var a = Row("A");
        state.Update(area, [a], 100); state.Tick(5099); Assert.True(state.IsOpen);
        state.Update(area, [a, Row("B")], 5100); state.Tick(10099); Assert.True(state.IsOpen);
        state.Tick(10100); Assert.False(state.IsOpen);
        state.Open(20000); state.Tick(24999); Assert.True(state.IsOpen);
        state.Tick(25000); Assert.False(state.IsOpen);
    }

    [Fact]
    public void HoverAndCapturedDragHoldThePeekAndGiveFiveSecondsAfterLeaving()
    {
        var state = new TemporaryDrawerState(); state.Open(0); state.SetHovered(true, 1000);
        state.Tick(90000); Assert.True(state.IsOpen);
        state.SetHovered(false, 90000); state.SetDragging(true, 91000); state.Tick(100000); Assert.True(state.IsOpen);
        state.SetDragging(false, 100000); state.Tick(104999); Assert.True(state.IsOpen);
        state.Tick(105000); Assert.False(state.IsOpen);
    }

    [Fact]
    public void EmptyListAndDesktopChangesDoNotRetainOldRowsOrReopenAClosedPeek()
    {
        var state = new TemporaryDrawerState(); var area = Guid.NewGuid(); var a = Row("A");
        state.Update(area, [a], 0); state.Close(); state.Update(area, [a], 100); Assert.False(state.IsOpen);
        state.Update(Guid.NewGuid(), [], 200); Assert.Empty(state.Items); Assert.False(state.IsOpen);
        state.Open(300); state.Update(null, [], 400); Assert.Empty(state.Items); Assert.False(state.IsOpen);
    }

    private static WorkspaceHarness Start(bool minimized = false)
    {
        var rig = new WorkspaceHarness(new WorkspaceDocument { PlacementPolicyInitialized = true });
        rig.Api.AddWindow((IntPtr)42, 10, 20, 400, 300, title: "Temporary", style: minimized ? (int)WINDOW_STYLE.WS_MINIMIZE : 0);
        rig.Workspace.Start(); return rig;
    }
    private static ArrivalViewport Landing() => new(new(-8000, 5000, .5), [new(0, 0, 1920, 1080)]);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DrawerDropMovesLiveAndMinimizedWindowsToTheCameraAndKeepsTheirPlaces(bool minimized)
    {
        using var rig = Start(minimized); var item = Assert.Single(rig.Workspace.TemporaryAreaItems);
        Assert.True(rig.Workspace.PlaceFromTemporaryArea(item.Id, Landing(), -7600, 5500));
        var placed = Assert.Single(rig.Workspace.Items);
        Assert.Equal(-7600, placed.Bounds.X); Assert.Equal(5500, placed.Bounds.Y); Assert.False(placed.Temporary);
        Assert.Equal(minimized ? WindowState.Minimized : WindowState.Normal, placed.Bounds.State);
        Assert.Empty(rig.Workspace.TemporaryAreaItems); Assert.Equal(0, rig.Workspace.TemporaryArea!.Count);
        Assert.Single(rig.Store.Document.Items); Assert.Empty(Assert.Single(rig.Store.Document.Zones).ItemIds);
        rig.Workspace.SaveNow(); // A stale sleeping/overview proxy must not overwrite the dropped geometry.
        Assert.Equal(-7600, Assert.Single(rig.Workspace.Items).Bounds.X);
        Assert.Equal(-7600, rig.Canvas.Windows[(IntPtr)42].X);
        Assert.Empty(rig.Actions.Focused);
    }

    [Fact]
    public void NoSpaceAndNativeFailureKeepEntriesInTheZoneWithoutChangingOtherContent()
    {
        using var rig = Start(); var item = Assert.Single(rig.Workspace.TemporaryAreaItems); var original = item.Bounds;
        var obstacle = new WorkspaceItem { Title = "Organized", Bounds = new() { X = -8000, Y = 5000, W = 3840, H = 2160 } };
        rig.Workspace.Document.Items.Add(obstacle);
        Assert.False(rig.Workspace.PlaceFromTemporaryArea(item.Id, Landing()));
        Assert.Equal(original, rig.Workspace.Document.Items.Single(i => i.Id == item.Id).Bounds);
        Assert.Equal(WorkspacePlacement.Deliberate, obstacle.Placement);
        rig.Workspace.Document.Items.Remove(obstacle); rig.Api.FailedMoves.Add((IntPtr)42);
        Assert.False(rig.Workspace.PlaceFromTemporaryArea(item.Id, Landing()));
        Assert.Equal(original, Assert.Single(rig.Workspace.Items).Bounds); Assert.True(Assert.Single(rig.Workspace.Items).Temporary);
    }

    [Fact]
    public void ClosedAndReusedHandlesCannotBePlacedFromAStaleDrawerEntry()
    {
        using var rig = Start(); var id = Assert.Single(rig.Workspace.TemporaryAreaItems).Id;
        rig.Api.Windows[(IntPtr)42].ProcessId = 99;
        int before = rig.Api.NativeStateCalls.Count;
        Assert.False(rig.Workspace.PlaceFromTemporaryArea(id, Landing()));
        Assert.Equal(before, rig.Api.NativeStateCalls.Count); Assert.Empty(rig.Workspace.TemporaryAreaItems);
    }

    [Fact]
    public void DrawerOnlyListsTemporaryZoneMembersOnTheActiveDesktop()
    {
        using var rig = Start(); var first = Assert.Single(rig.Workspace.TemporaryAreaItems);
        var nearby = new WorkspaceItem { Placement = WorkspacePlacement.Temporary, Title = "Nearby", Bounds = new() { W = 400, H = 300 } };
        rig.Workspace.Document.Items.Add(nearby);
        Assert.Equal(first.Id, Assert.Single(rig.Workspace.TemporaryAreaItems).Id);
        Assert.False(rig.Workspace.PlaceFromTemporaryArea(nearby.Id, Landing()));
        rig.Workspace.CollectInInventory(first.Id); Assert.Empty(rig.Workspace.TemporaryAreaItems);
    }
}
