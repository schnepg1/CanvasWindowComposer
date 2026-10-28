using CanvasDesktop;

namespace CanvasDesktop.Tests;

public class TemporaryAreaReviewRegressionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SettledTemporaryWindowDoesNotClaimASavedPlaceWhenItsWindowCloses(bool sameTitle)
    {
        var saved = new WorkspaceItem { Title = "Editor", ExecutablePath = @"C:\Apps\Editor.exe",
            Bounds = new() { X = -5000, Y = 1000, W = 800, H = 600 } };
        using var rig = new WorkspaceHarness(new WorkspaceDocument { PlacementPolicyInitialized = true, Items = [saved] });
        rig.Api.AddWindow((IntPtr)42, 10, 20, 800, 600, title: saved.Title);
        rig.Workspace.Start();
        rig.Api.AddWindow((IntPtr)43, 10, 20, 800, 600, title: sameTitle ? saved.Title : "Quick scratchpad");
        rig.Wm.TryRegisterWindow((IntPtr)43); rig.Workspace.SaveNow();
        var temporary = rig.Workspace.Document.Items.Single(i => i.Placement == WorkspacePlacement.Temporary);
        var bounds = temporary.Bounds;
        rig.Api.Windows.Remove((IntPtr)42); rig.Wm.RemoveWindow((IntPtr)42);
        rig.Workspace.SaveNow(); rig.Workspace.SaveNow();
        Assert.False(rig.Workspace.IsOpen(saved)); Assert.True(rig.Workspace.IsOpen(temporary));
        Assert.Equal(WorkspacePlacement.Temporary, temporary.Placement);
        Assert.Equal(bounds, temporary.Bounds); Assert.Equal(bounds, rig.Canvas.Windows[(IntPtr)43]);
        Assert.Equal(saved.Id, Assert.Single(rig.Store.Document.Items).Id);
        Assert.Contains(rig.Cards.Created, c => !c.Disposed && c.Item.Id == saved.Id);
    }

    [Fact]
    public void AmbiguousTemporaryWindowStaysTemporaryAfterOtherCandidatesAreForgotten()
    {
        var a = new WorkspaceItem { Title = "Editor", ExecutablePath = @"C:\Apps\Editor.exe", Bounds = new() { W = 800, H = 600 } };
        var b = new WorkspaceItem { Title = a.Title, ExecutablePath = a.ExecutablePath, Bounds = a.Bounds };
        using var rig = new WorkspaceHarness(new WorkspaceDocument { PlacementPolicyInitialized = true, Items = [a, b] });
        rig.Api.AddWindow((IntPtr)42, 10, 20, 800, 600, title: "Welcome"); rig.Workspace.Start();
        var temporary = rig.Workspace.Document.Items.Single(i => i.Placement == WorkspacePlacement.Temporary);
        rig.Workspace.Forget(b.Id); rig.Workspace.SaveNow();
        Assert.True(rig.Workspace.IsOpen(temporary)); Assert.False(rig.Workspace.IsOpen(a));
        Assert.Equal(WorkspacePlacement.Temporary, temporary.Placement);
        Assert.True(rig.Workspace.LinkWindow(a.Id, Assert.Single(rig.Workspace.LinkCandidates(a.Id)).Token));
        Assert.True(rig.Workspace.IsOpen(a));
    }

    [Fact]
    public void OutstandingUpgradeWindowsSurviveRestartWithoutAdoptingLaterArrivals()
    {
        WorkspaceDocument checkpoint;
        var api = new FakeWindowApi(); api.AddWindow((IntPtr)42, 10, 20, 800, 600, title: "Original desktop window");
        using (var first = new WorkspaceHarness(api: api))
        {
            first.Desktops.DefaultIsOnCurrent = false;
            first.Workspace.Start(); first.Workspace.Stop(save: true); checkpoint = first.Store.Document;
            Assert.Empty(checkpoint.Items); Assert.Single(checkpoint.PendingPlacementAdoptions);
        }
        api.AddWindow((IntPtr)43, 10, 20, 800, 600, title: "Opened after upgrade");
        using var restarted = new WorkspaceHarness(checkpoint, api: api);
        restarted.Workspace.Start();
        var original = restarted.Workspace.Document.Items.Single(i => i.Title == "Original desktop window");
        var arrival = restarted.Workspace.Document.Items.Single(i => i.Title == "Opened after upgrade");
        Assert.Equal(WorkspacePlacement.Deliberate, original.Placement); Assert.Equal(10, original.Bounds.X);
        Assert.Equal(WorkspacePlacement.Temporary, arrival.Placement);
        Assert.Empty(restarted.Store.Document.PendingPlacementAdoptions);
        Assert.Equal(original.Id, Assert.Single(restarted.Store.Document.Items).Id);
    }

    [Fact]
    public void OutstandingProjectLookupSurvivesRestartWithoutChangingTheUpgradeBoundary()
    {
        WorkspaceDocument checkpoint;
        var api = new FakeWindowApi(); api.AddWindow((IntPtr)42, 10, 20, 800, 600, title: "Code");
        using (var first = new WorkspaceHarness(api: api))
        {
            first.Identity.Exe = @"C:\Apps\Code.exe"; first.Identity.VsCodeTargets[(IntPtr)42] = null;
            first.Workspace.Start(); first.Workspace.Stop(save: true); checkpoint = first.Store.Document;
            Assert.Single(checkpoint.PendingPlacementAdoptions);
        }
        api.AddWindow((IntPtr)43, 10, 20, 800, 600, title: "New Code window");
        using var restarted = new WorkspaceHarness(checkpoint, api: api);
        restarted.Identity.Exe = @"C:\Apps\Code.exe";
        restarted.Identity.VsCodeTargets[(IntPtr)42] = null; restarted.Identity.VsCodeTargets[(IntPtr)43] = null;
        restarted.Workspace.Start();
        Assert.Single(restarted.Workspace.Document.PendingPlacementAdoptions);
        restarted.Identity.VsCodeTargets[(IntPtr)42] = @"C:\OriginalProject";
        restarted.Identity.VsCodeTargets[(IntPtr)43] = @"C:\NewProject";
        restarted.Workspace.SaveNow();
        Assert.Equal(WorkspacePlacement.Deliberate, restarted.Workspace.Document.Items.Single(i => i.Target == @"C:\OriginalProject").Placement);
        Assert.Equal(WorkspacePlacement.Temporary, restarted.Workspace.Document.Items.Single(i => i.Target == @"C:\NewProject").Placement);
        Assert.Empty(restarted.Store.Document.PendingPlacementAdoptions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UpgradeCheckpointDoesNotAdoptAReplacedProcess(bool samePid)
    {
        var api = new FakeWindowApi(); api.AddWindow((IntPtr)42, 10, 20, 800, 600, title: "Original");
        WorkspaceDocument checkpoint;
        using (var first = new WorkspaceHarness(api: api))
        {
            first.Desktops.DefaultIsOnCurrent = false; first.Workspace.Start(); first.Workspace.Stop(save: true);
            checkpoint = first.Store.Document;
        }
        api.Windows[(IntPtr)42].Title = "Replacement";
        if (!samePid) api.Windows[(IntPtr)42].ProcessId = 2;
        using var restarted = new WorkspaceHarness(checkpoint, api: api);
        if (samePid) restarted.Identity.StartedAt = 2;
        restarted.Workspace.Start();
        Assert.True(Assert.Single(restarted.Workspace.Items).Temporary);
        Assert.Empty(restarted.Store.Document.PendingPlacementAdoptions); Assert.Empty(restarted.Store.Document.Items);
    }

    [Fact]
    public void UpgradeCheckpointSurvivesAFileRoundTripAndIsRemovedAfterAdoption()
    {
        string directory = Path.Combine(Path.GetTempPath(), "canvas-adoption-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new WorkspaceStore(Path.Combine(directory, "workspace.json"));
            store.Save(new WorkspaceDocument()); // Existing, pre-policy workspace.
            var api = new FakeWindowApi(); api.AddWindow((IntPtr)42, 10, 20, 800, 600, title: "Original");
            using (var first = new WorkspaceHarness(store: store, api: api))
            {
                first.Desktops.DefaultIsOnCurrent = false; first.Workspace.Start(); first.Workspace.Stop(save: true);
            }
            var loaded = store.Load(out _);
            Assert.True(loaded.PlacementPolicyInitialized); Assert.Single(loaded.PendingPlacementAdoptions);
            Assert.Empty(loaded.Items);
            api.AddWindow((IntPtr)43, 10, 20, 800, 600, title: "Later arrival");
            using (var restarted = new WorkspaceHarness(store: store, api: api))
            {
                restarted.Workspace.Start(); restarted.Workspace.Stop(save: true);
            }
            var adopted = store.Load(out _);
            Assert.Empty(adopted.PendingPlacementAdoptions);
            Assert.Equal("Original", Assert.Single(adopted.Items).Title);
            Assert.Equal(WorkspacePlacement.Deliberate, adopted.Items[0].Placement);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void ClosingAnOutstandingUpgradeWindowDoesNotAdoptItsReplacement()
    {
        using var rig = new WorkspaceHarness();
        rig.Api.AddWindow((IntPtr)42, 10, 20, 800, 600, title: "Original");
        rig.Desktops.DefaultIsOnCurrent = false; rig.Workspace.Start();
        Assert.Single(rig.Store.Document.PendingPlacementAdoptions);
        rig.Api.Windows.Remove((IntPtr)42); rig.Workspace.SaveNow();
        Assert.Empty(rig.Store.Document.PendingPlacementAdoptions);
        rig.Desktops.DefaultIsOnCurrent = true;
        rig.Api.AddWindow((IntPtr)42, 10, 20, 800, 600, title: "New window"); rig.Workspace.SaveNow();
        Assert.True(Assert.Single(rig.Workspace.Items).Temporary);
    }

    [Fact]
    public void TemporaryIdentityInspectionOutageDoesNotDiscardOutstandingAdoption()
    {
        using var rig = new WorkspaceHarness();
        rig.Api.AddWindow((IntPtr)42, 10, 20, 800, 600, title: "Original");
        rig.Desktops.DefaultIsOnCurrent = false; rig.Workspace.Start();
        rig.Identity.Unavailable = true; rig.Workspace.SaveNow();
        Assert.Single(rig.Store.Document.PendingPlacementAdoptions);
        rig.Identity.Unavailable = false; rig.Desktops.DefaultIsOnCurrent = true; rig.Workspace.SaveNow();
        Assert.False(Assert.Single(rig.Workspace.Items).Temporary);
        Assert.Empty(rig.Store.Document.PendingPlacementAdoptions);
    }
}
