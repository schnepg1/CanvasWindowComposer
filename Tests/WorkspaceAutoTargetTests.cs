using System.Text.Json;
using CanvasDesktop;

namespace CanvasDesktop.Tests;

public class WorkspaceAutoTargetTests
{
    private static readonly IntPtr Handle = (IntPtr)42;
    private const string Exe = @"C:\Apps\Code.exe";
    private const string Title = "README.md - Project - Visual Studio Code";
    private static WorkspaceItem Item(string target = "") => new() { ExecutablePath = Exe, Title = Title, Target = target,
        Bounds = new() { X = -6000, Y = -8000, W = 1200, H = 800 } };

    [Fact]
    public void ExistingCaptionSlotIsUpgradedAfterDiagnosticsArriveWithoutMovingItsSavedPlace()
    {
        var item = Item();
        using var rig = new WorkspaceHarness(new() { Items = new() { item } });
        rig.Identity.Exe = Exe; rig.Identity.VsCodeTargets[Handle] = null;
        rig.Api.AddWindow(Handle, 20, 30, 800, 600, title: Title);
        rig.Workspace.Start();
        Assert.Single(rig.Workspace.Items);
        Assert.False(rig.Workspace.IsOpen(item));
        Assert.Empty(rig.Api.NativeStateCalls);

        rig.Identity.VsCodeTargets[Handle] = @"C:\Work\Project";
        rig.Workspace.SaveNow();
        Assert.True(rig.Workspace.IsOpen(item));
        Assert.Single(rig.Workspace.Items);
        Assert.Equal(@"C:\Work\Project", item.Target);
        Assert.True(item.AutoDetectedTarget); Assert.False(item.IsFolder);
        Assert.Equal(-8000, rig.Canvas.Windows[Handle].Y);
    }

    [Fact]
    public void NewWindowAutomaticallySavesItsFolderAndUpdatesItAfterNavigation()
    {
        using var rig = new WorkspaceHarness();
        rig.Identity.Exe = Exe; rig.Identity.VsCodeTargets[Handle] = @"C:\Work\Project";
        rig.Api.AddWindow(Handle, 20, 30, 800, 600, title: Title);
        rig.Workspace.Start();
        var item = Assert.Single(rig.Workspace.Document.Items);
        Assert.Equal(@"C:\Work\Project", item.Target); Assert.True(item.AutoDetectedTarget);
        rig.Identity.VsCodeTargets[Handle] = @"C:\Other\Project";
        rig.Workspace.SaveNow();
        Assert.Equal(@"C:\Other\Project", item.Target);
        rig.Identity.VsCodeTargets[Handle] = null;
        rig.Workspace.SaveNow();
        Assert.Equal(@"C:\Other\Project", item.Target);
    }

    [Fact]
    public void ManualTargetTakesPrecedenceOverLaterAutomaticDetection()
    {
        using var rig = new WorkspaceHarness();
        rig.Identity.Exe = Exe; rig.Identity.VsCodeTargets[Handle] = @"C:\Detected\Project";
        rig.Api.AddWindow(Handle, 20, 30, 800, 600, title: Title); rig.Workspace.Start();
        var item = Assert.Single(rig.Workspace.Document.Items);
        rig.Workspace.SetTarget(item.Id, @"C:\Chosen\Project.code-workspace", false);
        rig.Workspace.SaveNow();
        Assert.Equal(@"C:\Chosen\Project.code-workspace", item.Target);
        Assert.False(item.AutoDetectedTarget);
    }

    [Fact]
    public void OldManuallyChosenTargetsAreNotOverwrittenWhenUpgradingTheApp()
    {
        var item = Item(@"C:\Chosen\Project");
        using var rig = new WorkspaceHarness(new() { Items = new() { item } });
        rig.Identity.Exe = Exe; rig.Identity.VsCodeTargets[Handle] = item.Target;
        rig.Api.AddWindow(Handle, 20, 30, 800, 600, title: Title); rig.Workspace.Start();
        rig.Identity.VsCodeTargets[Handle] = @"C:\Another\Project";
        rig.Workspace.SaveNow();
        Assert.Equal(@"C:\Chosen\Project", item.Target); Assert.False(item.AutoDetectedTarget);
    }

    [Fact]
    public void RestartMatchesIdenticalCaptionsByDetectedFullPathsRegardlessOfEnumerationOrder()
    {
        var first = Item(@"C:\One\Project"); var second = Item(@"C:\Two\Project");
        using var rig = new WorkspaceHarness(new() { Items = new() { first, second } });
        rig.Identity.Exe = Exe;
        rig.Identity.VsCodeTargets[Handle] = second.Target;
        rig.Identity.VsCodeTargets[(IntPtr)43] = first.Target;
        rig.Api.AddWindow(Handle, 20, 30, 800, 600, title: Title);
        rig.Api.AddWindow((IntPtr)43, 40, 50, 800, 600, title: Title);
        rig.Workspace.Start();
        Assert.True(rig.Workspace.IsOpen(first)); Assert.True(rig.Workspace.IsOpen(second));
        Assert.Equal(2, rig.Workspace.Items.Count);
    }

    [Fact]
    public void ChangedFileCaptionCanUpgradeALegacyProjectSlotButCannotStealADifferentFullTarget()
    {
        var item = Item(); item.Title = "AGENTS.md - Project - Visual Studio Code";
        var wrong = Item(@"C:\Wrong\Project");
        var result = WorkspaceMatcher.Evaluate(new[] { item, wrong }, Guid.Empty, Exe, Title,
            @"C:\Right\Project", Array.Empty<Guid>());
        Assert.Same(item, result.Item);
    }

    [Fact]
    public void UnavailableDiagnosticsEventuallyFallBackWithoutLosingTheSavedWindow()
    {
        var item = Item();
        using var rig = new WorkspaceHarness(new() { Items = new() { item } });
        rig.Identity.Exe = Exe; rig.Identity.VsCodeTargets[Handle] = null;
        rig.Api.AddWindow(Handle, 20, 30, 800, 600, title: Title); rig.Workspace.Start();
        rig.Clock.Advance(8001);
        rig.Workspace.SaveNow();
        Assert.True(rig.Workspace.IsOpen(item)); Assert.Single(rig.Workspace.Items);
        Assert.Equal("", item.Target);
    }

    [Fact]
    public void StartupWaitsForExistingCodeWindowBeforeLaunchingAnotherCopy()
    {
        var item = Item(@"C:\Work\Project"); item.OpenOnStartup = true;
        using var rig = new WorkspaceHarness(new() { Items = new() { item } });
        rig.Identity.Exe = Exe; rig.Identity.VsCodeTargets[Handle] = null;
        rig.Api.AddWindow(Handle, 20, 30, 800, 600, title: Title); rig.Workspace.Start();
        Assert.Empty(rig.Launcher.Opened);
        rig.Identity.VsCodeTargets[Handle] = item.Target;
        rig.Clock.Advance(2000); rig.Workspace.Tick();
        Assert.True(rig.Workspace.IsOpen(item)); Assert.Empty(rig.Launcher.Opened);
    }

    [Fact]
    public void DiagnosticsOutageDoesNotDuplicateAPreviouslyDetectedProjectOrLaunchAnotherWindow()
    {
        var item = Item(@"C:\Work\Project"); item.AutoDetectedTarget = true; item.OpenOnStartup = true;
        using var rig = new WorkspaceHarness(new() { Items = new() { item } });
        rig.Identity.Exe = Exe; rig.Identity.VsCodeTargets[Handle] = null;
        rig.Api.AddWindow(Handle, 20, 30, 800, 600, title: Title); rig.Workspace.Start();
        rig.Clock.Advance(8001); rig.Workspace.Tick();
        Assert.Single(rig.Workspace.Items);
        Assert.Equal("Needs linking", rig.Workspace.ItemState(item));
        Assert.Empty(rig.Launcher.Opened);
        Assert.Equal(@"C:\Work\Project", item.Target);
    }

    [Fact]
    public void DetectionOwnershipRoundTripsAndOlderDocumentsKeepManualTargets()
    {
        var item = Item(@"C:\Work\Project"); item.AutoDetectedTarget = true;
        var json = JsonSerializer.Serialize(item, new JsonSerializerOptions { IncludeFields = true });
        Assert.True(JsonSerializer.Deserialize<WorkspaceItem>(json)!.AutoDetectedTarget);
        var old = JsonSerializer.SerializeToNode(item, new JsonSerializerOptions { IncludeFields = true })!;
        old.AsObject().Remove("AutoDetectedTarget");
        Assert.False(old.Deserialize<WorkspaceItem>()!.AutoDetectedTarget);
    }
}
