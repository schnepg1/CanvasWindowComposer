using CanvasDesktop;

namespace CanvasDesktop.Tests;

public class WorkspaceLaunchFeedbackTests
{
    private static WorkspaceItem Item(string exe = @"C:\Apps\Editor.exe") => new()
    {
        Title = "Project", ExecutablePath = exe,
        Bounds = new WorldRect { X = 12000, Y = 8000, W = 800, H = 550 }
    };

    [Fact]
    public void OpenCentersTheProgressCardAndRepeatedDoubleClicksDoNotLaunchAgain()
    {
        var item = Item();
        using var rig = new WorkspaceHarness(new() { Items = [item] });
        rig.Workspace.Start(); var card = Assert.Single(rig.Cards.Created);
        rig.Workspace.Open(item.Id); rig.Workspace.Open(item.Id);
        Assert.Single(rig.Launcher.Opened);
        Assert.All(rig.Actions.Focused, focus => Assert.Equal(card.Handle, focus.Handle));
        Assert.NotEmpty(rig.Actions.Focused);
        Assert.Equal(item.Bounds.X + item.Bounds.W / 2 - 320, rig.Canvas.CamX);
        Assert.False(card.Disposed);
        Assert.Equal("Opening…", card.Item.State);
    }

    [Fact]
    public void CardIsReplacedOnlyAfterTheMatchedWindowHasAVisibleClientArea()
    {
        var item = Item();
        using var rig = new WorkspaceHarness(new() { Items = [item] });
        rig.Workspace.Start(); rig.Workspace.Open(item.Id);
        var card = Assert.Single(rig.Cards.Created); var handle = (IntPtr)42;
        rig.Api.AddWindow(handle, 10, 20, 800, 550, title: "Loading");
        rig.Api.WindowsWithoutClientArea.Add(handle);
        rig.Clock.Advance(2000); rig.Workspace.Tick();
        Assert.False(card.Disposed); Assert.False(rig.Workspace.IsOpen(item));
        Assert.Equal("Opening…", card.Item.State); Assert.Empty(rig.Api.NativeStateCalls);
        rig.Api.WindowsWithoutClientArea.Clear();
        rig.Clock.Advance(2000); rig.Workspace.Tick();
        Assert.True(card.Disposed); Assert.True(rig.Workspace.IsOpen(item));
        Assert.Equal(item.Bounds, rig.Canvas.Windows[handle]);
    }

    [Fact]
    public void CodeStartupShellWaitsForTheRequestedProjectEvenWhenDiscoveryReturnsEmpty()
    {
        var item = Item(@"C:\Apps\Code.exe"); item.Target = @"C:\Work\Project";
        using var rig = new WorkspaceHarness(new() { Items = [item] });
        rig.Identity.Exe = item.ExecutablePath;
        rig.Workspace.Start(); rig.Workspace.Open(item.Id);
        var card = Assert.Single(rig.Cards.Created); var handle = (IntPtr)42;
        rig.Api.AddWindow(handle, 10, 20, 800, 550, title: "Visual Studio Code");
        rig.Identity.VsCodeTargets[handle] = "";
        rig.Clock.Advance(10000); rig.Workspace.Tick();
        Assert.False(card.Disposed); Assert.Equal("Opening…", card.Item.State);
        Assert.Single(rig.Workspace.Items); Assert.Empty(rig.Api.NativeStateCalls);
        rig.Identity.VsCodeTargets[handle] = item.Target;
        rig.Clock.Advance(2000); rig.Workspace.Tick();
        Assert.False(card.Disposed); // The generic Code shell still has no workbench title.
        rig.Api.Windows[handle].Title = "README.md - Project - Visual Studio Code";
        rig.Clock.Advance(2000); rig.Workspace.Tick();
        Assert.True(card.Disposed); Assert.True(rig.Workspace.IsOpen(item));
        Assert.Single(rig.Workspace.Items);
    }

    [Fact]
    public void FailedNativePlacementKeepsTheCardAndRetriesTheSameWindow()
    {
        var item = Item();
        using var rig = new WorkspaceHarness(new() { Items = [item] });
        rig.Workspace.Start(); rig.Workspace.Open(item.Id);
        var card = Assert.Single(rig.Cards.Created); var handle = (IntPtr)42;
        rig.Api.AddWindow(handle, 10, 20, 800, 550, title: "Project");
        rig.Api.FailedMoves.Add(handle);
        rig.Clock.Advance(2000); rig.Workspace.Tick();
        Assert.False(card.Disposed); Assert.Equal("Opening…", card.Item.State);
        rig.Api.FailedMoves.Clear();
        rig.Clock.Advance(2000); rig.Workspace.Tick();
        Assert.True(card.Disposed); Assert.Single(rig.Launcher.Opened);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailureOrTimeoutLeavesVisibleRetryFeedbackOnTheSameCard(bool timeout)
    {
        var item = Item();
        using var rig = new WorkspaceHarness(new() { Items = [item] });
        if (!timeout) rig.Launcher.Error = new IOException("Missing app");
        rig.Workspace.Start(); rig.Workspace.Open(item.Id);
        if (timeout) { rig.Clock.Advance(30001); rig.Workspace.Tick(); }
        var card = Assert.Single(rig.Cards.Created);
        Assert.False(card.Disposed); Assert.Equal("Placeholder", card.Item.State);
        Assert.Contains("retry", card.Item.LaunchMessage);
        rig.Launcher.Error = null; rig.Workspace.Open(item.Id);
        Assert.Equal("Opening…", card.Item.State); Assert.Null(card.Item.LaunchMessage);
    }

    [Fact]
    public void QueuedLaunchUpdatesItsCardImmediately()
    {
        var first = Item(); var second = Item(); second.Title = "Second";
        using var rig = new WorkspaceHarness(new() { Items = [first, second] });
        rig.Workspace.Start(); rig.Workspace.Open(first.Id); rig.Workspace.Open(second.Id);
        Assert.Equal("Queued", rig.Cards.Created.Single(card => card.Item.Id == second.Id).Item.State);
        Assert.Single(rig.Launcher.Opened);
        rig.Api.AddWindow((IntPtr)42, 10, 20, 800, 550, title: first.Title);
        rig.Workspace.SaveNow();
        rig.Launcher.Error = new IOException("Unavailable");
        rig.Workspace.Open(second.Id);
        var card = rig.Cards.Created.Single(card => card.Item.Id == second.Id);
        Assert.Equal("Placeholder", card.Item.State); // A failed manual retry must not remain silently queued.
        Assert.Contains("retry", card.Item.LaunchMessage);
    }

    [Fact]
    public void ASlowLaunchCanStillReplaceItsCardAfterTheRetryNoticeAppears()
    {
        var item = Item();
        using var rig = new WorkspaceHarness(new() { Items = [item] });
        rig.Workspace.Start(); rig.Workspace.Open(item.Id);
        rig.Clock.Advance(30001); rig.Workspace.Tick();
        var card = Assert.Single(rig.Cards.Created);
        rig.Api.AddWindow((IntPtr)42, 10, 20, 800, 550, title: item.Title);
        rig.Api.WindowsWithoutClientArea.Add((IntPtr)42);
        rig.Clock.Advance(2000); rig.Workspace.Tick();
        Assert.False(card.Disposed);
        rig.Api.WindowsWithoutClientArea.Clear();
        rig.Clock.Advance(2000); rig.Workspace.Tick();
        Assert.True(card.Disposed); Assert.True(rig.Workspace.IsOpen(item));
        Assert.Null(Assert.Single(rig.Workspace.Items).LaunchMessage);
        Assert.Single(rig.Launcher.Opened);
    }
}

[Collection("ShellViews")]
public class PlaceholderLaunchFeedbackViewTests
{
    [Fact]
    public void ProgressIsVisibleInNativeAndCachedCardsAndClearsAfterOpening()
        => WorkspaceViewTests.OnSta(() =>
        {
            using var rig = new WorkspaceHarness();
            var item = new WorkspaceItemView(Guid.NewGuid(), "Project", @"C:\Apps\Editor.exe", @"C:\Work",
                false, new WorldRect { W = 800, H = 550 }, "Placeholder");
            using var form = new PlaceholderForm(rig.Workspace, item);
            form.ShowAt(new WindowRect(-10000, -10000, 800, 550));
            Assert.Null(form.LaunchStatus);
            var normal = form.GetOverviewImage(512);
            form.UpdateItem(item with { State = "Opening…" });
            Assert.Contains("Starting application", form.LaunchStatus);
            var labels = form.Controls.OfType<System.Windows.Forms.TableLayoutPanel>().Single()
                .Controls.OfType<System.Windows.Forms.Label>();
            Assert.Contains(labels, label => label.Visible && label.Text.Contains("Starting application"));
            var opening = form.GetOverviewImage(512); Assert.NotSame(normal, opening);
            Assert.Same(opening, form.GetOverviewImage(512));
            form.UpdateItem(item with { State = "Open" });
            Assert.Null(form.LaunchStatus); Assert.NotSame(opening, form.GetOverviewImage(512));
        });
}
