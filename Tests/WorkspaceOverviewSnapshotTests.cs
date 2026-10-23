using CanvasDesktop;
using Windows.Win32.UI.WindowsAndMessaging;

namespace CanvasDesktop.Tests;

public class WorkspaceOverviewSnapshotTests
{
    private static readonly IntPtr App = (IntPtr)42;

    private static WorkspaceHarness Start(bool lowQuality = false)
    {
        var h = new WorkspaceHarness(config: new FakeAppConfig { LowQualityMode = lowQuality });
        h.Api.AddWindow(App, 100, 200, 800, 600, pid: 7, title: "Project");
        h.Workspace.Start();
        h.Wm.SuspendProjection = true;
        h.Wm.SetOverviewViewport(new CanvasCamera(0, 0, .4));
        h.Workspace.SetOverviewCardMode(true);
        return h;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CameraBurstsReuseAllMapsWithoutOpeningProcesses(bool lowQuality)
    {
        using var h = Start(lowQuality);
        var snapshot = h.Workspace.GetOverviewSnapshot();
        var card = Assert.Single(h.Cards.Created);
        Assert.Equal(card.Item.Id, snapshot.ItemIds[App]);
        Assert.Equal(card.Item.Id, snapshot.ItemIds[card.Handle]);
        Assert.Equal(card.Handle, snapshot.RepresentedWindows[App]);
        Assert.Equal(App, snapshot.CardSources[card.Handle]);
        int inspections = h.Identity.ProcessInspections;
        for (int frame = 0; frame < 120; frame++)
        {
            h.Canvas.SetCamera(frame, -frame);
            h.Wm.SetOverviewViewport(new CanvasCamera(frame, -frame, .3 + frame / 2000.0));
            Assert.Same(snapshot, h.Workspace.GetOverviewSnapshot());
            Assert.Same(snapshot.ItemIds, h.Workspace.GetOverviewItemIds());
            Assert.Same(snapshot.RepresentedWindows, h.Workspace.GetRepresentedWindows());
        }
        Assert.Equal(inspections, h.Identity.ProcessInspections);
        h.Clock.Now = 2000;
        h.Workspace.Tick();
        Assert.NotSame(snapshot, h.Workspace.GetOverviewSnapshot());
        Assert.True(h.Identity.ProcessInspections > inspections);
    }

    [Theory]
    [InlineData("pid")]
    [InlineData("hidden")]
    [InlineData("destroyed")]
    public void MissedNativeEventsRetireStaleRenderAssociation(string change)
    {
        using var h = Start();
        var before = h.Workspace.GetOverviewSnapshot();
        switch (change)
        {
            case "pid": h.Api.Windows[App].ProcessId = 8; break;
            case "hidden": h.Api.Windows[App].Visible = false; break;
            case "destroyed": h.Api.Windows.Remove(App); break;
        }
        var after = h.Workspace.GetOverviewSnapshot();
        Assert.DoesNotContain(App, after.ItemIds.Keys);
        Assert.Empty(after.RepresentedWindows);
        Assert.Empty(after.CardSources);
        // Previously published data is immutable for its readers.
        Assert.Contains(App, before.ItemIds.Keys);
    }

    [Fact]
    public void CachedRenderDataCannotAuthorizeActionsAfterSamePidProcessReuse()
    {
        using var h = Start();
        var snapshot = h.Workspace.GetOverviewSnapshot();
        var item = Assert.Single(h.Workspace.Document.Items);
        h.Identity.StartedAt++;
        h.Workspace.FocusItem(item.Id);
        h.Workspace.CloseToPlaceholder(item.Id);
        Assert.DoesNotContain(h.Actions.Focused, action => action.Handle == App);
        Assert.Empty(h.Actions.Closed);
        Assert.Empty(h.Workspace.GetRepresentedWindows());
        Assert.Contains(App, snapshot.ItemIds.Keys);
    }

    [Fact]
    public void WindowEventsRevalidateSamePidProcessLifetime()
    {
        using var h = Start();
        h.Workspace.GetOverviewSnapshot();
        h.Identity.StartedAt++;
        h.Api.Windows[App].Style = (int)WINDOW_STYLE.WS_MINIMIZE;
        h.Input.RaiseWindowMinimized(App);
        Assert.Empty(h.Workspace.GetOverviewSnapshot().RepresentedWindows);
    }

    [Fact]
    public void CardRemovalAndRecreationPublishNewMaps()
    {
        using var h = Start();
        var first = h.Workspace.GetOverviewSnapshot();
        var card = h.Cards.Created.Single();
        h.Workspace.SetOverviewCardMode(false);
        var detailed = h.Workspace.GetOverviewSnapshot();
        Assert.Empty(detailed.RepresentedWindows);
        Assert.DoesNotContain(card.Handle, detailed.ItemIds.Keys);
        Assert.Contains(App, detailed.ItemIds.Keys);
        h.Workspace.SetOverviewCardMode(true);
        var nextCard = h.Cards.Created.Last();
        var next = h.Workspace.GetOverviewSnapshot();
        Assert.Equal(nextCard.Handle, next.RepresentedWindows[App]);
        Assert.Equal(card.Handle, first.RepresentedWindows[App]);
        Assert.True(card.Disposed);
    }

    [Fact]
    public void DesktopSwitchAndShutdownDiscardOldRenderMaps()
    {
        using var h = Start();
        // Complete initial desktop detection before switching away.
        h.Desktops.SwitchTo(Guid.NewGuid());
        var before = h.Workspace.GetOverviewSnapshot();
        Assert.Contains(App, before.ItemIds.Keys);
        h.Desktops.DefaultIsOnCurrent = false;
        h.Desktops.SwitchTo(Guid.NewGuid());
        Assert.Empty(h.Workspace.GetOverviewSnapshot().ItemIds);
        h.Workspace.Stop(save: false);
        Assert.Same(WorkspaceOverviewSnapshot.Empty, h.Workspace.GetOverviewSnapshot());
    }
}
