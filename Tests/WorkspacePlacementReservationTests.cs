using CanvasDesktop;

namespace CanvasDesktop.Tests;

public class WorkspacePlacementReservationTests
{
    private static readonly IntPtr Handle = (IntPtr)42;

    [Fact]
    public void SaveNowDoesNotMatchOrRestoreWhilePlacementIsReserved()
    {
        var item = Item();
        using var rig = new WorkspaceHarness(new() { Items = new() { item } }, useAsyncProjection: true);
        rig.Workspace.Start();
        AddWindow(rig);
        using var lease = Reserve(rig);
        rig.Input.RaiseWindowShown(Handle);
        item.Name = "Edited workspace label";
        var saved = item.Bounds;
        var savesBefore = rig.Store.Saves;

        Assert.False(rig.Workspace.SaveNow());
        Assert.Equal(WorkspaceService.PlacementRetryMessage, rig.Workspace.Status);
        Assert.False(rig.Workspace.IsOpen(item));
        Assert.Equal(savesBefore, rig.Store.Saves);
        Assert.Empty(rig.Api.NativeStateCalls);

        lease.Dispose();
        rig.Workspace.Tick();
        Assert.True(rig.Workspace.IsOpen(item));
        Assert.Equal(saved, rig.Canvas.Windows[Handle]);
        Assert.Single(rig.Api.NativeStateCalls);
        Assert.True(rig.Store.Saves > savesBefore);
    }

    [Fact]
    public void InventoryCollectReportsRetryWithoutChangingPlacementOrAcknowledgingSuccess()
    {
        var item = Item();
        using var rig = new WorkspaceHarness(new() { Items = new() { item } }, useAsyncProjection: true);
        AddWindow(rig);
        rig.Workspace.Start();
        using var lease = Reserve(rig);
        var saved = item.Bounds;
        var writes = rig.Api.NativeStateCalls.Count;
        var collections = new List<InventoryCollection>();
        rig.Workspace.InventoryCollected += collections.Add;

        Assert.False(rig.Workspace.CollectInInventory(item.Id));
        Assert.Equal(WorkspaceService.PlacementRetryMessage, rig.Workspace.InventoryStatus);
        Assert.Equal(saved, item.Bounds);
        Assert.Equal(saved, rig.Canvas.Windows[Handle]);
        Assert.Equal(writes, rig.Api.NativeStateCalls.Count);
        Assert.Empty(collections);

        lease.Dispose();
        Assert.True(rig.Workspace.CollectInInventory(item.Id));
        Assert.True(item.Bounds.InInventory);
        Assert.Single(collections);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PendingFocusRunsOnlyForTheOriginalLiveBinding(bool replaced)
    {
        var item = Item();
        using var rig = new WorkspaceHarness(new() { Items = new() { item } }, useAsyncProjection: true);
        AddWindow(rig);
        rig.Workspace.Start();
        rig.Workspace.Tick();
        Assert.True(rig.Workspace.IsOpen(item));
        using var lease = Reserve(rig);

        rig.Workspace.FocusItem(item.Id);
        Assert.Contains("will resume", rig.Workspace.Status);
        rig.Workspace.Tick();
        Assert.Empty(rig.Actions.Focused);
        Assert.Empty(rig.Launcher.Opened);
        if (replaced) rig.Identity.StartedAt++;

        lease.Dispose();
        rig.Workspace.Tick(); // No clock advance and the next maintenance deadline is in the future.
        rig.Workspace.Tick();
        if (replaced)
        {
            Assert.Empty(rig.Actions.Focused);
            Assert.Contains("canceled", rig.Workspace.Status);
        }
        else
        {
            Assert.Equal(Handle, Assert.Single(rig.Actions.Focused).Handle);
        }
        Assert.Empty(rig.Launcher.Opened);
    }

    private static ProjectionWorker.PlacementLease Reserve(WorkspaceHarness rig)
        => Assert.IsType<ProjectionWorker.PlacementLease>(rig.Wm.TryAcquireDesktopPlacementLease([Handle]));

    private static void AddWindow(WorkspaceHarness rig)
        => rig.Api.AddWindow(Handle, 40, 50, 700, 500, title: "Editor");

    private static WorkspaceItem Item() => new()
    {
        ExecutablePath = @"C:\Apps\Editor.exe",
        Title = "Editor",
        Bounds = new() { X = 70, Y = 80, W = 400, H = 250 }
    };
}
