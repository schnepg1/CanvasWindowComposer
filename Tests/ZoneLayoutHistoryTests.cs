using CanvasDesktop;

namespace CanvasDesktop.Tests;

// All window/process/desktop calls use WorkspaceHarness fakes. No native surfaces or hooks.
public class ZoneLayoutHistoryTests
{
    private static (WorkspaceDocument Document, WorkspaceZone Zone, WorkspaceItem Item) Setup()
    {
        var item = new WorkspaceItem { Title = "Editor", ExecutablePath = @"C:\Apps\Editor.exe",
            Bounds = new WorldRect { X = 100, Y = 200, W = 800, H = 600 } };
        var zone = new WorkspaceZone { Bounds = new WorldRect { X = -100, Y = -200, W = 2000, H = 1500 },
            ItemIds = new() { item.Id } };
        return (new WorkspaceDocument { Items = new() { item }, Zones = new() { zone } }, zone, item);
    }

    [Fact]
    public void ResizeThenMoveUndoAndRedoKeepMembersInPlaceDuringResizeAndSaveOncePerEdit()
    {
        var (document, zone, item) = Setup();
        using var rig = new WorkspaceHarness(document);
        rig.Api.AddWindow((IntPtr)42, 10, 20, 800, 600, title: item.Title);
        rig.Workspace.Start();
        var initial = zone.Bounds;
        var resized = initial; resized.X -= 300; resized.W += 300; resized.H += 400;
        rig.Api.SetPositionCalls.Clear(); rig.Api.NativeStateCalls.Clear();
        int saves = rig.Store.Saves;
        Assert.True(rig.Workspace.ResizeZone(zone.Id, resized));
        Assert.Equal(100, item.Bounds.X); Assert.Equal(100, rig.Canvas.Windows[(IntPtr)42].X);
        Assert.Empty(rig.Api.SetPositionCalls); Assert.Empty(rig.Api.NativeStateCalls);
        Assert.True(rig.Workspace.MoveZone(zone.Id, 250, 75));
        Assert.Equal(350, item.Bounds.X);
        Assert.True(rig.Workspace.UndoZoneLayout());
        Assert.Equal(resized, zone.Bounds); Assert.Equal(100, item.Bounds.X);
        Assert.True(rig.Workspace.UndoZoneLayout());
        Assert.Equal(initial, zone.Bounds); Assert.False(rig.Workspace.CanUndoZoneLayout);
        Assert.True(rig.Workspace.RedoZoneLayout());
        Assert.Equal(resized, zone.Bounds); Assert.Equal(100, item.Bounds.X);
        Assert.True(rig.Workspace.RedoZoneLayout());
        Assert.Equal(350, item.Bounds.X); Assert.False(rig.Workspace.CanRedoZoneLayout);
        Assert.Equal(6, rig.Store.Saves - saves);
        rig.Workspace.SaveNow(); Assert.Equal(350, item.Bounds.X);
    }

    [Fact]
    public void SharedMembersPinnedPlacesAndClosedPlaceholdersReplayCorrectly()
    {
        var (document, zone, item) = Setup();
        var pinned = new WorkspaceItem { Title = "Pinned", ExecutablePath = item.ExecutablePath,
            Bounds = new WorldRect { X = 600, Y = 200, W = 500, H = 300, PinnedToScreen = true } };
        document.Items.Add(pinned); zone.ItemIds.Add(pinned.Id);
        var second = new WorkspaceZone { Bounds = zone.Bounds, ItemIds = new() { item.Id } }; document.Zones.Add(second);
        using var rig = new WorkspaceHarness(document); rig.Workspace.Start();
        Assert.True(rig.Workspace.MoveZone(zone.Id, 200, 0));
        Assert.True(rig.Workspace.MoveZone(second.Id, 300, 0));
        Assert.True(rig.Workspace.UndoZoneLayout()); Assert.Equal(300, item.Bounds.X);
        Assert.True(rig.Workspace.UndoZoneLayout()); Assert.Equal(100, item.Bounds.X);
        Assert.True(rig.Workspace.RedoZoneLayout()); Assert.Equal(300, item.Bounds.X);
        Assert.Equal(600, pinned.Bounds.X);
        var card = rig.Cards.Created.Single(c => c.Item.Id == item.Id);
        Assert.Equal(300, card.Item.Bounds.X); Assert.Equal(300, rig.Canvas.Windows[card.Handle].X);
    }

    [Theory]
    [InlineData("geometry")]
    [InlineData("membership")]
    [InlineData("deleted-area")]
    [InlineData("forgotten-member")]
    [InlineData("pin")]
    public void ConflictingChangesRejectReplayWithoutPartialLayoutOrSave(string conflict)
    {
        var (document, zone, item) = Setup();
        using var rig = new WorkspaceHarness(document); rig.Workspace.Start();
        Assert.True(rig.Workspace.MoveZone(zone.Id, 200, 0));
        var card = rig.Cards.Created.Single(c => c.Item.Id == item.Id);
        switch (conflict)
        {
            case "geometry": rig.Canvas.SetWindow(card.Handle, 350, 200, 800, 600); break;
            case "membership": rig.Workspace.Zones.SetMembership(zone.Id, Array.Empty<Guid>()); break;
            case "deleted-area": rig.Workspace.Zones.Remove(zone.Id); break;
            case "forgotten-member": rig.Workspace.Forget(item.Id); break;
            case "pin": var b = item.Bounds; b.PinnedToScreen = true; item.Bounds = b; break;
        }
        int saves = rig.Store.Saves;
        Assert.False(rig.Workspace.UndoZoneLayout());
        Assert.Equal(100, zone.Bounds.X); // No partial rollback of the area itself.
        Assert.False(rig.Workspace.CanUndoZoneLayout); Assert.False(rig.Workspace.CanRedoZoneLayout);
        Assert.Equal(saves, rig.Store.Saves);
        Assert.Contains("history was cleared", rig.Workspace.Status);
        if (conflict == "geometry") Assert.Equal(350, item.Bounds.X);
    }

    [Fact]
    public void UndoPreservesNewerWindowStateAndAreaMetadata()
    {
        var (document, zone, item) = Setup();
        using var rig = new WorkspaceHarness(document); rig.Workspace.Start();
        Assert.True(rig.Workspace.MoveZone(zone.Id, 200, 0));
        var b = item.Bounds; b.State = WindowState.Maximized; item.Bounds = b;
        rig.Workspace.Zones.Update(zone.Id, "Renamed", "#34D399");
        Assert.True(rig.Workspace.UndoZoneLayout());
        Assert.Equal(100, item.Bounds.X); Assert.Equal(WindowState.Maximized, item.Bounds.State);
        Assert.Equal("Renamed", zone.Name); Assert.Equal("#34D399", zone.Color);
    }

    [Fact]
    public void FailedSaveStillRecordsAppliedEditAndReplayForPersistenceRetry()
    {
        var (document, zone, item) = Setup();
        using var rig = new WorkspaceHarness(document); rig.Workspace.Start();
        rig.Store.SaveError = new IOException("Disk full");
        Assert.False(rig.Workspace.MoveZone(zone.Id, 200, 0));
        Assert.True(rig.Workspace.CanUndoZoneLayout);
        // Returning to the last successfully persisted document needs no disk write.
        Assert.True(rig.Workspace.UndoZoneLayout()); Assert.Equal(100, item.Bounds.X);
        Assert.True(rig.Workspace.CanRedoZoneLayout);
        Assert.False(rig.Workspace.RedoZoneLayout()); Assert.Equal(300, item.Bounds.X);
        Assert.True(rig.Workspace.CanUndoZoneLayout);
        rig.Store.SaveError = null;
        Assert.True(rig.Workspace.SaveNow()); Assert.Equal(300, item.Bounds.X);
    }

    [Fact]
    public void InvalidOrUnchangedEditsDoNotClearRedoOrWriteWorkspace()
    {
        var (document, zone, _) = Setup();
        using var rig = new WorkspaceHarness(document); rig.Workspace.Start();
        rig.Workspace.MoveZone(zone.Id, 200, 0); rig.Workspace.UndoZoneLayout();
        int saves = rig.Store.Saves;
        Assert.False(rig.Workspace.ResizeZone(zone.Id, new WorldRect { W = double.NaN, H = 100 }));
        Assert.True(rig.Workspace.ResizeZone(zone.Id, zone.Bounds));
        Assert.True(rig.Workspace.MoveZone(zone.Id, 0, 0));
        Assert.Equal(saves, rig.Store.Saves); Assert.True(rig.Workspace.CanRedoZoneLayout);
        var b = zone.Bounds; b.W += 200;
        Assert.True(rig.Workspace.ResizeZone(zone.Id, b)); Assert.False(rig.Workspace.CanRedoZoneLayout);
    }

    [Fact]
    public void BoundedHistoryIsIndependentForEachDesktop()
    {
        var history = new ZoneLayoutHistory(); var a = Guid.NewGuid(); var b = Guid.NewGuid();
        var plan = new ZoneLayoutPlan(Guid.NewGuid(), new WorldRect { W = 100, H = 100 }, Array.Empty<ZoneItemPlacement>());
        var edit = new ZoneLayoutEdit(plan, plan, null);
        for (int i = 0; i < 80; i++) history.Record(a, edit);
        history.Record(b, edit);
        int count = 0;
        while (history.Peek(a, false) != null) { history.Accept(a, false); count++; }
        Assert.Equal(ZoneLayoutHistory.Capacity, count); Assert.NotNull(history.Peek(b, false));
        Assert.Null(history.Peek(b, true)); Assert.NotNull(history.Peek(a, true));
        history.Record(a, edit); Assert.Null(history.Peek(a, true));
        history.Clear(a); Assert.NotNull(history.Peek(b, false));
    }

    [Fact]
    public void WorkspaceSwitchesUndoStacksWithVirtualDesktop()
    {
        var first = Guid.NewGuid(); var second = Guid.NewGuid();
        var a = new WorkspaceZone { DesktopId = first, Bounds = new WorldRect { W = 1000, H = 800 } };
        var b = new WorkspaceZone { DesktopId = second, Bounds = a.Bounds };
        using var rig = new WorkspaceHarness(new WorkspaceDocument { Zones = new() { a, b } });
        rig.Workspace.Start(); rig.Desktops.SwitchTo(first);
        Assert.True(rig.Workspace.MoveZone(a.Id, 100, 0));
        rig.Desktops.SwitchTo(second);
        Assert.False(rig.Workspace.CanUndoZoneLayout);
        Assert.True(rig.Workspace.MoveZone(b.Id, 200, 0));
        Assert.True(rig.Workspace.UndoZoneLayout()); Assert.Equal(0, b.Bounds.X); Assert.Equal(100, a.Bounds.X);
        rig.Desktops.SwitchTo(first);
        Assert.True(rig.Workspace.CanUndoZoneLayout); Assert.False(rig.Workspace.CanRedoZoneLayout);
        Assert.True(rig.Workspace.UndoZoneLayout()); Assert.Equal(0, a.Bounds.X);
        rig.Desktops.SwitchTo(second);
        Assert.True(rig.Workspace.RedoZoneLayout()); Assert.Equal(200, b.Bounds.X); Assert.Equal(0, a.Bounds.X);
    }

    [Fact]
    public void ReplayNeverMovesARecycledHandleAndPreservesLogicalHistoryAfterNativeFailure()
    {
        var (document, zone, item) = Setup();
        using var rig = new WorkspaceHarness(document);
        var handle = (IntPtr)42;
        var bounds = item.Bounds; bounds.State = WindowState.Minimized; item.Bounds = bounds;
        rig.Api.AddWindow(handle, 10, 20, 800, 600,
            style: (int)Windows.Win32.UI.WindowsAndMessaging.WINDOW_STYLE.WS_MINIMIZE, title: item.Title);
        rig.Workspace.Start();
        rig.Api.FailedMoves.Add(handle);
        Assert.False(rig.Workspace.MoveZone(zone.Id, 200, 0));
        Assert.True(rig.Workspace.CanUndoZoneLayout);
        rig.Api.FailedMoves.Clear();
        rig.Api.Windows[handle].ProcessId = 99;
        rig.Identity.Executables[99] = @"C:\Other\Other.exe";
        rig.Api.Windows[handle].Title = "Other app"; rig.Api.Windows[handle].X = 4000;
        Assert.True(rig.Workspace.UndoZoneLayout());
        Assert.Equal(100, item.Bounds.X);
        Assert.True(rig.Workspace.Items.Single(i => i.Title == "Other app").Temporary);
        Assert.NotEqual(item.Bounds.X, rig.Canvas.Windows[handle].X);
    }
}
