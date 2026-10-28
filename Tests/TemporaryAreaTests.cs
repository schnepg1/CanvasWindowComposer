using CanvasDesktop;
using Windows.Win32.UI.WindowsAndMessaging;

namespace CanvasDesktop.Tests;

public class TemporaryAreaTests
{
    private static WorkspaceDocument Fresh() => new() { PlacementPolicyInitialized = true };
    private static WorkspaceItem Arrival(WorkspaceHarness rig, int handle, int style = 0)
    {
        rig.Api.AddWindow((IntPtr)handle, 10, 20, 800, 600, style: style, title: "Window " + handle);
        rig.Wm.TryRegisterWindow((IntPtr)handle);
        rig.Workspace.SaveNow();
        return rig.Workspace.Document.Items.Single(i => i.Title == "Window " + handle);
    }

    [Fact]
    public void UpgradeAdoptsExistingWindowsOnceAndOnlyNewArrivalsAreTemporary()
    {
        using var rig = new WorkspaceHarness(); // Missing policy marker is a legacy document.
        rig.Api.AddWindow((IntPtr)42, 10, 20, 800, 600, title: "Existing");
        rig.Workspace.Start();
        var existing = Assert.Single(rig.Workspace.Document.Items);
        Assert.Equal(WorkspacePlacement.Deliberate, existing.Placement);
        Assert.Equal(10, existing.Bounds.X);
        Assert.True(rig.Workspace.Document.PlacementPolicyInitialized);
        var arrival = Arrival(rig, 43);
        Assert.Equal(WorkspacePlacement.Temporary, arrival.Placement);
        Assert.Equal(10, existing.Bounds.X);
        Assert.Single(rig.Store.Document.Items);
        Assert.Null(rig.Workspace.Items.Single(i => i.Id == existing.Id).LaunchMessage);
    }

    [Fact]
    public void FreshStartupBatchesManyWindowsWithoutOverlapOrWakingMinimizedWindows()
    {
        using var rig = new WorkspaceHarness(Fresh());
        for (int i = 1; i <= 24; i++)
            rig.Api.AddWindow((IntPtr)i, 10, 20, 800 + i * 5, 600,
                style: i % 2 == 0 ? (int)WINDOW_STYLE.WS_MINIMIZE : 0, title: "Window " + i);
        var notices = new List<TemporaryAreaView>();
        rig.Workspace.TemporaryWindowsArrived += notices.Add;
        rig.Workspace.Start();
        var items = rig.Workspace.Document.Items;
        Assert.Equal(24, items.Count);
        Assert.All(items, i => Assert.Equal(WorkspacePlacement.Temporary, i.Placement));
        for (int i = 0; i < items.Count; i++)
            for (int j = i + 1; j < items.Count; j++)
                Assert.False(TemporaryAreaLayout.Overlaps(items[i].Bounds, items[j].Bounds));
        Assert.Equal(12, items.Count(i => i.Bounds.State == WindowState.Minimized));
        Assert.All(rig.Api.NativeStateCalls.Where(c => c.hWnd.ToInt64() % 2 == 0), c => Assert.Equal(WindowState.Minimized, c.state));
        Assert.Equal(24, Assert.Single(notices).Count);
        Assert.Empty(rig.Store.Document.Items);
        Assert.Empty(rig.Actions.Focused);
        Assert.Equal(0, rig.Canvas.CamX); Assert.Equal(0, rig.Canvas.CamY);
    }

    [Fact]
    public void AnchorAndExistingSlotsStayStableWhileNewContentAndNotesAreProtected()
    {
        using var rig = new WorkspaceHarness(Fresh());
        rig.Workspace.Start();
        var first = Arrival(rig, 42);
        var firstBounds = first.Bounds;
        var anchor = rig.Workspace.Document.TemporaryAreas[Guid.Empty];
        var note = new WorldRect { X = firstBounds.X + firstBounds.W + 64, Y = firstBounds.Y, W = 900, H = 700 };
        rig.Workspace.AdditionalPlacementObstacles = () => [note];
        var zone = new WorkspaceZone { Bounds = new() { X = firstBounds.X, Y = firstBounds.Y + 800, W = 3500, H = 2000 } };
        rig.Workspace.Document.Zones.Add(zone);
        var second = Arrival(rig, 43);
        Assert.Equal(firstBounds, first.Bounds);
        Assert.Same(anchor, rig.Workspace.Document.TemporaryAreas[Guid.Empty]);
        Assert.False(TemporaryAreaLayout.Overlaps(note, second.Bounds));
        Assert.False(TemporaryAreaLayout.Overlaps(zone.Bounds, second.Bounds));
        Assert.False(TemporaryAreaLayout.Overlaps(firstBounds, second.Bounds));
    }

    [Fact]
    public void ClosingTemporaryWindowLeavesNoSavedEntryOrPlaceholderAndDoesNotReflowOthers()
    {
        using var rig = new WorkspaceHarness(Fresh());
        rig.Workspace.Start();
        var first = Arrival(rig, 42, (int)WINDOW_STYLE.WS_MINIMIZE);
        var second = Arrival(rig, 43);
        var bounds = second.Bounds;
        var card = Assert.Single(rig.Cards.Created);
        rig.Api.Windows.Remove((IntPtr)42);
        rig.Wm.RemoveWindow((IntPtr)42);
        rig.Workspace.SaveNow();
        Assert.DoesNotContain(rig.Workspace.Document.Items, i => i.Id == first.Id);
        Assert.True(card.Disposed);
        Assert.Equal(bounds, second.Bounds);
        Assert.Empty(rig.Store.Document.Items);
        Assert.Equal(1, rig.Workspace.TemporaryArea!.Count);
        rig.Api.Windows.Remove((IntPtr)43); rig.Wm.RemoveWindow((IntPtr)43); rig.Workspace.SaveNow();
        Assert.Null(rig.Workspace.TemporaryArea);
        Assert.All(rig.Cards.Created, c => Assert.True(c.Disposed));
    }

    [Fact]
    public void UsingMinimizingOrMovingInsideTheAreaDoesNotSaveButMovingOutsideDoes()
    {
        using var rig = new WorkspaceHarness(Fresh());
        rig.Workspace.Start();
        var item = Arrival(rig, 42);
        rig.Workspace.Open(item.Id);
        var bounds = item.Bounds;
        rig.Canvas.SetWindow((IntPtr)42, bounds.X + 10, bounds.Y + 10, bounds.W, bounds.H);
        rig.Workspace.CommitWindowPlacement((IntPtr)42);
        rig.Workspace.SaveNow();
        Assert.Equal(WorkspacePlacement.Temporary, item.Placement);
        Assert.Empty(rig.Store.Document.Items);
        rig.Canvas.SetWindow((IntPtr)42, -4000, -1000, bounds.W, bounds.H);
        rig.Workspace.CommitWindowPlacement((IntPtr)42);
        Assert.Equal(WorkspacePlacement.Deliberate, item.Placement);
        Assert.Equal(-4000, Assert.Single(rig.Store.Document.Items).Bounds.X);
        rig.Api.Windows.Remove((IntPtr)42); rig.Wm.RemoveWindow((IntPtr)42); rig.Workspace.SaveNow();
        Assert.Single(rig.Cards.Created);
    }

    [Fact]
    public void ExplicitKeepAndProjectMembershipBothPromoteTemporaryWindows()
    {
        using var rig = new WorkspaceHarness(Fresh());
        rig.Workspace.Start();
        var first = Arrival(rig, 42);
        rig.Workspace.KeepPlace(first.Id);
        var second = Arrival(rig, 43);
        Assert.True(rig.Workspace.Zones.Add(new() { X = -2000, Y = 0, W = 2000, H = 1000 }, out var zone));
        Assert.True(rig.Workspace.Zones.SetMembership(zone, [second.Id]));
        Assert.Equal(WorkspacePlacement.Deliberate, second.Placement);
        Assert.Equal(2, rig.Store.Document.Items.Count);
        Assert.Null(rig.Workspace.TemporaryArea);
    }

    [Fact]
    public void RestartRetainsTheAreaAnchorButNoTemporarySlotsOrPlaceholders()
    {
        WorkspaceDocument saved;
        using (var rig = new WorkspaceHarness(Fresh()))
        {
            rig.Workspace.Start(); Arrival(rig, 42); Arrival(rig, 43);
            rig.Workspace.Stop(save: true);
            saved = rig.Store.Document;
            Assert.Empty(saved.Items);
        }
        double x = saved.TemporaryAreas[Guid.Empty].X;
        using var restarted = new WorkspaceHarness(saved);
        restarted.Workspace.Start();
        Assert.Empty(restarted.Cards.Created);
        Assert.Null(restarted.Workspace.TemporaryArea);
        var next = Arrival(restarted, 45);
        Assert.Equal(x + TemporaryAreaLayout.Gap, next.Bounds.X);
        Assert.Equal(WorkspacePlacement.Temporary, next.Placement);
    }

    [Fact]
    public void DelayedProjectIdentityDoesNotExtendTheMigrationBoundary()
    {
        using var rig = new WorkspaceHarness();
        rig.Identity.Exe = @"C:\Apps\Code.exe";
        rig.Identity.VsCodeTargets[(IntPtr)42] = null;
        rig.Api.AddWindow((IntPtr)42, 10, 20, 800, 600, title: "Code");
        rig.Workspace.Start();
        Assert.Empty(rig.Workspace.Items);
        rig.Identity.VsCodeTargets[(IntPtr)42] = @"C:\OldProject";
        rig.Identity.VsCodeTargets[(IntPtr)43] = @"C:\NewProject";
        rig.Api.AddWindow((IntPtr)43, 10, 20, 800, 600, title: "New project - Code");
        rig.Workspace.SaveNow();
        Assert.Equal(WorkspacePlacement.Deliberate, rig.Workspace.Document.Items.Single(i => i.Target == @"C:\OldProject").Placement);
        Assert.Equal(WorkspacePlacement.Temporary, rig.Workspace.Document.Items.Single(i => i.Target == @"C:\NewProject").Placement);
    }

    [Fact]
    public void NewWindowsAwaitingProjectIdentityAreStagedThenRestoreTheMatchingSavedPlace()
    {
        var doc = Fresh();
        var saved = new WorkspaceItem { Title = "Project", ExecutablePath = @"C:\Apps\Code.exe", Target = @"C:\Project",
            Bounds = new() { X = -5000, Y = -1000, W = 900, H = 700 } };
        doc.Items.Add(saved);
        using var rig = new WorkspaceHarness(doc);
        rig.Identity.Exe = saved.ExecutablePath;
        rig.Identity.VsCodeTargets[(IntPtr)42] = null;
        rig.Api.AddWindow((IntPtr)42, 10, 20, 800, 600, title: "Code");
        rig.Workspace.Start();
        var pending = rig.Workspace.Document.Items.Single(i => i.Placement == WorkspacePlacement.Temporary);
        var stagedBounds = pending.Bounds;
        Assert.False(rig.Workspace.IsOpen(saved));
        rig.Workspace.SaveNow();
        Assert.Equal(stagedBounds, pending.Bounds);
        rig.Identity.VsCodeTargets[(IntPtr)42] = saved.Target;
        rig.Workspace.SaveNow();
        Assert.True(rig.Workspace.IsOpen(saved));
        Assert.Equal(saved.Id, Assert.Single(rig.Workspace.Document.Items).Id);
        Assert.Equal(saved.Bounds, rig.Canvas.Windows[(IntPtr)42]);
        Assert.Null(rig.Workspace.TemporaryArea);
    }

    [Fact]
    public void AmbiguousNewWindowsRemainTemporaryAndCanBeLinkedWithoutChangingSavedPlaces()
    {
        var doc = Fresh();
        var a = new WorkspaceItem { Title = "Editor", ExecutablePath = @"C:\Apps\Editor.exe", Bounds = new() { X = -5000, W = 800, H = 600 } };
        var b = new WorkspaceItem { Title = a.Title, ExecutablePath = a.ExecutablePath, Bounds = new() { X = -3000, W = 800, H = 600 } };
        doc.Items.AddRange([a, b]);
        using var rig = new WorkspaceHarness(doc);
        rig.Workspace.Start();
        Arrival(rig, 42);
        Assert.Single(rig.Workspace.Document.Items, i => i.Placement == WorkspacePlacement.Temporary);
        Assert.Equal(2, rig.Store.Document.Items.Count);
        Assert.Equal(-5000, a.Bounds.X); Assert.Equal(-3000, b.Bounds.X);
        var candidate = Assert.Single(rig.Workspace.LinkCandidates(a.Id));
        Assert.True(rig.Workspace.LinkWindow(a.Id, candidate.Token));
        Assert.True(rig.Workspace.IsOpen(a));
        Assert.Equal(2, rig.Workspace.Document.Items.Count);
        Assert.Null(rig.Workspace.TemporaryArea);
    }

    [Fact]
    public void ProgrammaticWindowMovesDoNotPromoteButAnExplicitNativeMoveDoes()
    {
        using var rig = new WorkspaceHarness(Fresh(), config: new FakeAppConfig { DisableGreedyDraw = true });
        rig.Workspace.Start();
        var item = Arrival(rig, 42);
        rig.Api.Windows[(IntPtr)42].X = -4000;
        rig.Input.RaiseWindowMoved((IntPtr)42);
        rig.Workspace.SaveNow();
        Assert.Equal(WorkspacePlacement.Temporary, item.Placement);
        Assert.Empty(rig.Store.Document.Items);
        rig.Input.RaiseWindowMoveEnded((IntPtr)42);
        Assert.Equal(WorkspacePlacement.Deliberate, item.Placement);
        Assert.Equal(-4000, Assert.Single(rig.Store.Document.Items).Bounds.X);
    }

    [Fact]
    public void ExplicitScreenPinningKeepsThePlace()
    {
        using var rig = new WorkspaceHarness(Fresh());
        rig.Workspace.Start();
        var item = Arrival(rig, 42);
        rig.Wm.SetWindowPinnedToScreen((IntPtr)42, true);
        Assert.Equal(WorkspacePlacement.Deliberate, item.Placement);
        Assert.True(Assert.Single(rig.Store.Document.Items).Bounds.PinnedToScreen);
    }

    [Fact]
    public void DiskRoundTripOmitsTemporaryWindowsAndProtectsLegacyPositions()
    {
        string directory = Path.Combine(Path.GetTempPath(), "canvas-arrivals-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new WorkspaceStore(Path.Combine(directory, "workspace.json"));
            Assert.True(store.Load(out _).PlacementPolicyInitialized);
            var doc = new WorkspaceDocument();
            var old = new WorkspaceItem { Title = "Saved", Bounds = new() { X = -1000, W = 800, H = 600 } };
            doc.Items.Add(old);
            doc.Items.Add(new() { Title = "Never save this", Placement = WorkspacePlacement.Temporary, Bounds = new() { W = 800, H = 600 } });
            store.Save(doc);
            string json = File.ReadAllText(store.FilePath);
            Assert.DoesNotContain("Never save this", json);
            var loaded = store.Load(out _);
            Assert.Equal(old.Id, Assert.Single(loaded.Items).Id);
            Assert.Equal(WorkspacePlacement.Deliberate, loaded.Items[0].Placement);
            Assert.False(loaded.PlacementPolicyInitialized);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
