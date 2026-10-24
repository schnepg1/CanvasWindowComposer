using CanvasDesktop;

namespace CanvasDesktop.Tests;

public class WorkspaceRestorationTests
{
    private const string OldChatGpt = @"C:\Program Files\WindowsApps\OpenAI.Codex_26.1016.8622.0_x64__2p2nqsd0c76g0\app\ChatGPT.exe";
    private const string NewChatGpt = @"C:\Program Files\WindowsApps\OpenAI.Codex_26.1022.10000.0_x64__2p2nqsd0c76g0\app\ChatGPT.exe";
    private static WorkspaceItem Item(string exe, string title, double y = -8126) => new()
    {
        ExecutablePath = exe, Title = title,
        Bounds = new() { X = -6555, Y = y, W = 1665, H = 927 }
    };

    [Fact]
    public void ChangedActiveCodeFileRestoresTheProjectSlotWithOtherCodeSlotsPresent()
    {
        var item = Item(@"C:\Apps\Code.exe", "AGENTS.md - CanvasWindowComposer - Visual Studio Code");
        var other = Item(item.ExecutablePath, "Welcome - agent-office - Visual Studio Code", -6900);
        using var rig = new WorkspaceHarness(new() { Items = new() { item, other } });
        rig.Identity.Exe = item.ExecutablePath;
        rig.Api.AddWindow((IntPtr)42, 100, 500, 800, 600,
            title: "README.md - CanvasWindowComposer - Visual Studio Code");
        rig.Workspace.Start();
        Assert.True(rig.Workspace.IsOpen(item));
        Assert.Equal(item.Bounds, rig.Canvas.Windows[(IntPtr)42]);
        Assert.Equal(2, rig.Workspace.Items.Count);
        rig.Workspace.SaveNow();
        Assert.Equal(-8126, item.Bounds.Y);
    }

    [Fact]
    public void SameNamedCodeProjectsRemainAmbiguousEvenWhenOneSlotIsAlreadyBound()
    {
        var first = Item(@"C:\Apps\Code.exe", "one.cs - Project - Visual Studio Code");
        var second = Item(first.ExecutablePath, "two.cs - Project - Visual Studio Code");
        var result = WorkspaceMatcher.Evaluate(new[] { first, second }, Guid.Empty,
            first.ExecutablePath, "three.cs - Project - Visual Studio Code", "", new[] { first.Id });
        Assert.True(result.IsAmbiguous);
        Assert.Null(result.Item);
    }

    [Theory]
    [InlineData("Welcome - Visual Studio Code")]
    [InlineData("README.md - DifferentProject - Visual Studio Code")]
    public void CodeCaptionsWithoutMatchingProjectEvidenceDoNotStealAProjectSlot(string title)
    {
        var item = Item(@"C:\Apps\Code.exe", "AGENTS.md - CanvasWindowComposer - Visual Studio Code");
        var other = Item(item.ExecutablePath, "Welcome - agent-office - Visual Studio Code");
        Assert.Null(WorkspaceMatcher.Evaluate(new[] { item, other }, Guid.Empty,
            item.ExecutablePath, title, "", Array.Empty<Guid>()).Item);
    }

    [Fact]
    public void StoreAppUpdateRestoresTheExistingSlotAndRefreshesItsLaunchPath()
    {
        var item = Item(OldChatGpt, "ChatGPT");
        var zone = new WorkspaceZone { ItemIds = new() { item.Id } };
        using var rig = new WorkspaceHarness(new() { Items = new() { item }, Zones = new() { zone } });
        rig.Identity.Exe = NewChatGpt;
        rig.Api.AddWindow((IntPtr)42, 100, 500, 800, 600, title: "ChatGPT");
        rig.Workspace.Start();
        Assert.True(rig.Workspace.IsOpen(item));
        Assert.Single(rig.Workspace.Items);
        Assert.Equal(item.Bounds, rig.Canvas.Windows[(IntPtr)42]);
        Assert.Equal(NewChatGpt, item.ExecutablePath);
        Assert.Contains(item.Id, zone.ItemIds);
        rig.Workspace.SaveNow();
        Assert.Equal(-8126, item.Bounds.Y);
    }

    [Theory]
    [InlineData(@"C:\Program Files\WindowsApps\OpenAI.Other_26.1022.10000.0_x64__2p2nqsd0c76g0\app\ChatGPT.exe")]
    [InlineData(@"C:\Program Files\WindowsApps\OpenAI.Codex_26.1022.10000.0_x64__otherpublisher\app\ChatGPT.exe")]
    [InlineData(@"C:\Program Files\WindowsApps\OpenAI.Codex_26.1022.10000.0_x64__2p2nqsd0c76g0\other\ChatGPT.exe")]
    [InlineData(@"C:\Other\ChatGPT.exe")]
    public void DifferentPackagePublisherOrExecutableCannotClaimTheStoreAppSlot(string exe)
    {
        var item = Item(OldChatGpt, "ChatGPT");
        Assert.Null(WorkspaceMatcher.Evaluate(new[] { item }, Guid.Empty, exe, item.Title, "", Array.Empty<Guid>()).Item);
    }

    [Fact]
    public void UpdatedStoreAppCanBeExplicitlyLinkedWhenSavedSlotsAreAmbiguous()
    {
        var item = Item(OldChatGpt, "ChatGPT");
        var other = Item(OldChatGpt, "ChatGPT", -5000);
        using var rig = new WorkspaceHarness(new() { Items = new() { item, other } });
        rig.Identity.Exe = NewChatGpt;
        rig.Api.AddWindow((IntPtr)42, 100, 500, 800, 600, title: "ChatGPT");
        rig.Workspace.Start();
        var candidate = Assert.Single(rig.Workspace.LinkCandidates(item.Id));
        Assert.True(rig.Workspace.LinkWindow(item.Id, candidate.Token));
        Assert.Equal(NewChatGpt, item.ExecutablePath);
        Assert.Equal(OldChatGpt, other.ExecutablePath);
        Assert.Equal(-8126, rig.Canvas.Windows[(IntPtr)42].Y);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExactCodeCaptionWinsAgainstAnotherWindowWithTheSameProjectCaption(bool reverse)
    {
        var item = Item(@"C:\Apps\Code.exe", "AGENTS.md - Project - Visual Studio Code");
        var first = new WorkspaceMatchInput(Guid.NewGuid(), item.ExecutablePath, item.Title, "", null);
        var second = new WorkspaceMatchInput(Guid.NewGuid(), item.ExecutablePath, "README.md - Project - Visual Studio Code", "", null);
        var plan = WorkspaceMatcher.Plan(new[] { item }, Guid.Empty,
            reverse ? new[] { second, first } : new[] { first, second }, Array.Empty<Guid>());
        Assert.Same(item, plan[first.Token].Item);
        Assert.Null(plan[second.Token].Item);
    }
}
