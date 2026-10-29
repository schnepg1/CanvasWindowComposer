using CanvasDesktop;

namespace CanvasDesktop.Tests;

public class InventoryTests
{
    private static WorkspaceDocument Fresh() => new() { PlacementPolicyInitialized = true };
    private static WorkspaceItem Add(WorkspaceHarness rig, int handle = 42)
    {
        rig.Api.AddWindow((IntPtr)handle, 10, 20, 400, 300, title: "Window " + handle);
        rig.Wm.TryRegisterWindow((IntPtr)handle); rig.Workspace.SaveNow();
        return rig.Workspace.Document.Items.Single(i => i.Title == "Window " + handle);
    }
    private static void EnableNearby(WorkspaceHarness rig, double x = 5000, double y = -2000)
        => rig.Workspace.ArrivalViewportProvider = () => new(new(x, y, 1), [new(0, 0, 1920, 1080)]);

    [Fact]
    public void LiveArrivalUsesTheCurrentCameraAndCanOverlapExistingContent()
    {
        using var rig = new WorkspaceHarness(Fresh()); rig.Workspace.Start(); EnableNearby(rig);
        var obstacle = new WorkspaceItem { Title = "Kept", Bounds = new() { X = 5000, Y = -2000, W = 1920, H = 1080 } };
        rig.Workspace.Document.Items.Add(obstacle); var original = obstacle.Bounds;
        var item = Add(rig);
        Assert.Equal(5010, item.Bounds.X); Assert.Equal(-1980, item.Bounds.Y);
        Assert.Equal(400, item.Bounds.W); Assert.Equal(300, item.Bounds.H);
        Assert.False(item.Bounds.InInventory); Assert.True(TemporaryAreaLayout.Overlaps(item.Bounds, original, 0));
        Assert.Equal(original, obstacle.Bounds); Assert.Null(rig.Workspace.TemporaryArea);
    }

    [Fact]
    public void AFullViewKeepsLiveArrivalsVisibleWithoutCollectingOrSavingThem()
    {
        using var rig = new WorkspaceHarness(Fresh()); rig.Workspace.Start(); EnableNearby(rig);
        rig.Canvas.SetCamera(5000, -2000);
        rig.Workspace.AdditionalPlacementObstacles = () => [new() { X = 5000, Y = -2000, W = 1920, H = 1080 }];
        var effects = new List<InventoryCollection>(); rig.Workspace.InventoryCollected += effects.Add;
        var item = Add(rig);
        Assert.False(item.Bounds.InInventory); Assert.Equal(WorkspacePlacement.Temporary, item.Placement);
        Assert.Empty(effects); Assert.Empty(rig.Workspace.InventoryItems);
        Assert.Empty(rig.Store.Document.Items); Assert.Null(rig.Workspace.TemporaryArea);
        Assert.DoesNotContain((IntPtr)42, rig.Api.ClippedWindows);
        Assert.Empty(rig.Cards.Created);
        var placed = item.Bounds;
        rig.Workspace.SaveNow(); rig.Clock.Advance(3000); rig.Workspace.Tick();
        Assert.Equal(placed, item.Bounds); Assert.Empty(effects);
    }

    [Fact]
    public void StartupWindowsStillUseTheStartupAreaWhenNearbyRoutingIsAttached()
    {
        using var rig = new WorkspaceHarness(Fresh()); EnableNearby(rig);
        rig.Api.AddWindow((IntPtr)42, 10, 20, 400, 300, title: "At startup"); rig.Workspace.Start();
        Assert.Equal(1, rig.Workspace.TemporaryArea!.Count);
        Assert.False(Assert.Single(rig.Workspace.Items).Bounds.InInventory);
    }

    [Fact]
    public void LiveExplorerDialogStaysVisibleWhileFolderDiscoveryIsPending()
    {
        using var rig = new WorkspaceHarness(Fresh());
        rig.Identity.Exe = @"C:\Windows\explorer.exe";
        rig.Workspace.Start(); EnableNearby(rig);
        rig.Canvas.SetCamera(5000, -2000);
        rig.Workspace.AdditionalPlacementObstacles = () => [new() { X = 5000, Y = -2000, W = 1920, H = 1080 }];
        var item = Add(rig);
        var placed = item.Bounds;
        Assert.False(placed.InInventory); Assert.Equal(5010, placed.X); Assert.Equal(-1980, placed.Y);
        rig.Clock.Advance(9000); rig.Workspace.Tick();
        Assert.Equal(placed, item.Bounds); Assert.Empty(rig.Workspace.InventoryItems);
        Assert.Null(rig.Workspace.TemporaryArea); Assert.Empty(rig.Store.Document.Items);
        Assert.DoesNotContain((IntPtr)42, rig.Api.ClippedWindows);
    }

    [Fact]
    public void OversizedLiveWindowStaysOnCanvasWithoutBeingResizedOrCollected()
    {
        using var rig = new WorkspaceHarness(Fresh()); rig.Workspace.Start(); EnableNearby(rig);
        rig.Canvas.SetCamera(5000, -2000);
        rig.Api.AddWindow((IntPtr)42, 10, 20, 2560, 1440, title: "Large popup");
        rig.Workspace.SaveNow();
        var item = Assert.Single(rig.Workspace.Items);
        Assert.Equal(5000, item.Bounds.X); Assert.Equal(-2000, item.Bounds.Y);
        Assert.Equal(2560, item.Bounds.W); Assert.Equal(1440, item.Bounds.H);
        Assert.False(item.Bounds.InInventory); Assert.Empty(rig.Workspace.InventoryItems);
        Assert.DoesNotContain((IntPtr)42, rig.Api.ClippedWindows);
    }

    [Fact]
    public void KeepingAnOverlappingLiveWindowSavesItsCurrentPlace()
    {
        using var rig = new WorkspaceHarness(Fresh()); rig.Workspace.Start(); EnableNearby(rig);
        var item = Add(rig); var placed = item.Bounds;
        Assert.Empty(rig.Store.Document.Items);
        rig.Workspace.KeepPlace(item.Id);
        var saved = Assert.Single(rig.Store.Document.Items);
        Assert.Equal(placed, saved.Bounds); Assert.Equal(WorkspacePlacement.Deliberate, saved.Placement);
        Assert.False(saved.OpenOnStartup);
    }

    [Fact]
    public void DeliberatelyCollectingKeepsTheItemAndRemovesItFromCanvasSelectionAndProjects()
    {
        using var rig = new WorkspaceHarness(Fresh()); rig.Workspace.Start(); var item = Add(rig);
        rig.Workspace.Zones.Add(new() { X = -5000, W = 2000, H = 1500 }, out var project);
        rig.Workspace.Zones.SetMembership(project, [item.Id]);
        Assert.True(rig.Workspace.CollectInInventory(item.Id));
        Assert.True(item.Bounds.InInventory); Assert.Equal(WorkspacePlacement.Deliberate, item.Placement);
        Assert.All(rig.Workspace.Document.Zones, z => Assert.DoesNotContain(item.Id, z.ItemIds));
        Assert.True(Assert.Single(rig.Store.Document.Items).Bounds.InInventory);
        var windows = new OverviewWindowList(rig.Canvas, rig.Api); windows.Refresh(); Assert.Empty(windows.Windows);
        Assert.Null(rig.Canvas.GetWorldExtents());
    }

    [Fact]
    public void MovingTheCameraAndPreviewUnclippingNeverRevealCarriedWindows()
    {
        using var rig = new WorkspaceHarness(Fresh()); rig.Workspace.Start(); var item = Add(rig);
        Assert.True(rig.Workspace.CollectInInventory(item.Id));
        rig.Api.UnclipCalls.Clear();
        rig.Canvas.SetCamera(10000, 3000); rig.Wm.UnclipAll(); rig.Wm.SetLivePreviewWindows([(IntPtr)42]); rig.Wm.ReprojectSync();
        Assert.Contains((IntPtr)42, rig.Api.ClippedWindows);
        Assert.DoesNotContain((IntPtr)42, rig.Api.UnclipCalls);
    }

    [Fact]
    public void DroppingElsewhereUsesFreeSpaceNearTheNewCameraAndKeepsThePlace()
    {
        using var rig = new WorkspaceHarness(Fresh()); rig.Workspace.Start(); var item = Add(rig);
        Assert.True(rig.Workspace.CollectInInventory(item.Id)); EnableNearby(rig, -9000, 5000);
        Assert.True(rig.Workspace.DropFromInventory(item.Id, -8800, 5200));
        Assert.False(item.Bounds.InInventory); Assert.Equal(-8800, item.Bounds.X); Assert.Equal(5200, item.Bounds.Y);
        Assert.Equal(WorkspacePlacement.Deliberate, item.Placement); Assert.Empty(rig.Workspace.InventoryItems);
    }

    [Fact]
    public void FailedDropKeepsTheWindowSafelyInTheBackpack()
    {
        using var rig = new WorkspaceHarness(Fresh()); rig.Workspace.Start(); var item = Add(rig);
        rig.Workspace.CollectInInventory(item.Id); EnableNearby(rig);
        rig.Workspace.AdditionalPlacementObstacles = () => [new() { X = 5000, Y = -2000, W = 1920, H = 1080 }];
        Assert.False(rig.Workspace.DropFromInventory(item.Id)); Assert.True(item.Bounds.InInventory);
        Assert.Contains((IntPtr)42, rig.Api.ClippedWindows); Assert.Contains("landing", rig.Workspace.Status);
    }

    [Fact]
    public void NativeCollectionFailureLeavesTheSourceOnTheCanvas()
    {
        using var rig = new WorkspaceHarness(Fresh()); rig.Workspace.Start(); var item = Add(rig);
        var original = item.Bounds; rig.Api.FailedClips.Add((IntPtr)42);
        Assert.False(rig.Workspace.CollectInInventory(item.Id));
        Assert.False(item.Bounds.InInventory); Assert.Equal(original, item.Bounds);
    }

    [Fact]
    public void OverlappingLiveArrivalDisappearsOnCloseWithoutAPlaceholder()
    {
        using var rig = new WorkspaceHarness(Fresh()); rig.Workspace.Start(); EnableNearby(rig);
        rig.Workspace.AdditionalPlacementObstacles = () => [new() { X = 5000, Y = -2000, W = 1920, H = 1080 }];
        var item = Add(rig); Assert.False(item.Bounds.InInventory);
        rig.Api.Windows.Remove((IntPtr)42); rig.Wm.RemoveWindow((IntPtr)42); rig.Workspace.SaveNow();
        Assert.Empty(rig.Workspace.InventoryItems); Assert.Empty(rig.Store.Document.Items);
        Assert.All(rig.Cards.Created, c => Assert.True(c.Disposed));
    }

    [Fact]
    public void AClosedKeptItemStaysInInventoryAndCanBeDroppedAsAPlaceholder()
    {
        using var rig = new WorkspaceHarness(Fresh()); rig.Workspace.Start(); var item = Add(rig);
        rig.Workspace.CollectInInventory(item.Id);
        rig.Api.Windows.Remove((IntPtr)42); rig.Wm.RemoveWindow((IntPtr)42); rig.Workspace.SaveNow();
        Assert.Single(rig.Workspace.InventoryItems); Assert.All(rig.Cards.Created, c => Assert.True(c.Disposed));
        EnableNearby(rig); Assert.True(rig.Workspace.DropFromInventory(item.Id));
        Assert.Contains(rig.Cards.Created, c => !c.Disposed && c.Item.Id == item.Id);
    }

    [Fact]
    public void KeptInventoryMembershipSurvivesAFileRoundTripButLiveArrivalsDoNot()
    {
        string directory = Path.Combine(Path.GetTempPath(), "canvas-inventory-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new WorkspaceStore(Path.Combine(directory, "workspace.json"));
            Guid kept;
            using (var rig = new WorkspaceHarness(store: store))
            {
                rig.Workspace.Start(); var item = Add(rig); kept = item.Id; rig.Workspace.CollectInInventory(kept);
                EnableNearby(rig); rig.Workspace.AdditionalPlacementObstacles = () => [new() { X = 5000, Y = -2000, W = 1920, H = 1080 }];
                Add(rig, 43); rig.Workspace.SetInventoryOpen(true); rig.Workspace.Stop(save: true);
            }
            var saved = store.Load(out _); Assert.Equal(kept, Assert.Single(saved.Items).Id);
            Assert.True(saved.Items[0].Bounds.InInventory); Assert.True(saved.InventoryOpen);
            using var restarted = new WorkspaceHarness(store: store); restarted.Workspace.Start();
            Assert.Equal(kept, Assert.Single(restarted.Workspace.InventoryItems).Id);
            Assert.Empty(restarted.Cards.Created);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void NearbyPlacementHonorsMultipleMonitorsAndReservedHudSpace()
    {
        var window = new WorldRect { W = 800, H = 600 };
        var view = new ArrivalViewport(new(-4000, 2000, .5), [new(-1920, 0, 1920, 1080), new(0, 0, 1920, 1080)], [new(1000, 100, 900, 900)]);
        Assert.True(NearbyWindowPlacement.TryPlace(window, view, [], out var placed));
        Assert.False(placed.InInventory);
        var screen = view.Camera.Project(placed);
        Assert.True(screen.X >= -1920 && screen.X + screen.W <= 1920);
        var reserved = new WorldRect { X = -2000, Y = 2200, W = 1800, H = 1800 };
        Assert.False(TemporaryAreaLayout.Overlaps(placed, reserved, 48));
    }

    [Fact]
    public void RejectedNativeUnclipRollsTheItemBackIntoInventory()
    {
        using var rig = new WorkspaceHarness(Fresh()); rig.Workspace.Start(); var item = Add(rig);
        rig.Workspace.CollectInInventory(item.Id); EnableNearby(rig, 0, 0);
        rig.Api.FailedUnclips.Add((IntPtr)42);
        Assert.False(rig.Workspace.DropFromInventory(item.Id));
        Assert.True(item.Bounds.InInventory); Assert.True(rig.Canvas.Windows[(IntPtr)42].InInventory);
        Assert.Contains((IntPtr)42, rig.Api.ClippedWindows); Assert.Single(rig.Workspace.InventoryItems);
    }

    [Fact]
    public void CollectedWindowInvalidatesOldCanvasMoveHistory()
    {
        using var rig = new WorkspaceHarness(Fresh()); rig.Workspace.Start(); var item = Add(rig);
        var before = rig.Workspace.SelectionSnapshot([item.Id], []);
        rig.Workspace.CollectInInventory(item.Id);
        var b = before.Items[0].Bounds; b.X += 500;
        var after = before with { Items = [new(item.Id, b)] };
        Assert.False(rig.Workspace.ApplySelectionPlacement(before, after));
        Assert.True(item.Bounds.InInventory);
    }

    [Fact]
    public void CanvasSearchAndTaskbarNavigationOpenTheBackpackWithoutMovingTheCamera()
    {
        using var rig = new WorkspaceHarness(Fresh()); rig.Workspace.Start(); var item = Add(rig);
        rig.Workspace.CollectInInventory(item.Id);
        var camera = rig.Canvas.Camera;
        Guid? requested = null; rig.Workspace.InventoryRequested += id => requested = id;
        Assert.True(rig.Wm.NavigateInventoryWindow((IntPtr)42)); Assert.Equal(item.Id, requested);
        Assert.Equal(camera, rig.Canvas.Camera);
        var result = Assert.Single(new WindowSearchService(rig.Canvas, rig.Api).Search("Window 42"));
        Assert.Contains("Backpack", result.Display);
    }

    [Fact]
    public void InventoryItemsStayOnTheirWindowsVirtualDesktopAndRestoreWhenReturning()
    {
        using var rig = new WorkspaceHarness(Fresh()); rig.Workspace.Start();
        var first = Guid.NewGuid(); var second = Guid.NewGuid(); rig.Desktops.SwitchTo(first);
        rig.Desktops.WindowDesktops[(IntPtr)42] = first;
        var item = Add(rig); rig.Workspace.CollectInInventory(item.Id);
        rig.Desktops.SwitchTo(second);
        Assert.Empty(rig.Workspace.InventoryItems); Assert.Contains((IntPtr)42, rig.Api.ClippedWindows);
        rig.Desktops.SwitchTo(first);
        Assert.Equal(item.Id, Assert.Single(rig.Workspace.InventoryItems).Id);
        Assert.True(rig.Canvas.Windows[(IntPtr)42].InInventory);
    }

    [Fact]
    public void SavingFailureRetainsTheCarriedWindowAndReportsThePersistenceProblem()
    {
        using var rig = new WorkspaceHarness(Fresh()); rig.Workspace.Start(); var item = Add(rig);
        rig.Store.SaveError = new IOException("Disk full");
        Assert.True(rig.Workspace.CollectInInventory(item.Id));
        Assert.True(item.Bounds.InInventory); Assert.Contains("Disk full", rig.Workspace.InventoryStatus);
        rig.Store.SaveError = null; Assert.True(rig.Workspace.SaveNow());
        Assert.True(Assert.Single(rig.Store.Document.Items).Bounds.InInventory);
    }

    [Fact]
    public void InventoryFlightStartsAndEndsAtItsEndpoints()
    {
        Assert.Equal((10d, 20d, 1d), InventoryFlight.Sample(0, 10, 20, 900, 200));
        var end = InventoryFlight.Sample(1, 10, 20, 900, 200);
        Assert.Equal(900, end.X); Assert.Equal(200, end.Y, 8); Assert.Equal(.1, end.Scale, 8);
    }
}
