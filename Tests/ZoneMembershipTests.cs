using CanvasDesktop;

namespace CanvasDesktop.Tests;

public class ZoneMembershipTests
{
    private static WorkspaceItem Item(string title, double x = 100, bool pinned = false) => new()
    {
        Title = title, ExecutablePath = @"C:\Apps\Editor.exe",
        Bounds = new WorldRect { X = x, Y = 200, W = 800, H = 600, PinnedToScreen = pinned, ZOrder = 9 }
    };

    private static (WorkspaceDocument Document, WorkspaceZone Zone, ZoneService Service) Setup(params WorkspaceItem[] items)
    {
        var zone = new WorkspaceZone { Bounds = new WorldRect { W = 2000, H = 1500 } };
        var document = new WorkspaceDocument { Items = items.ToList(), Zones = new() { zone } };
        return (document, zone, new ZoneService(document, () => Guid.Empty, () => true));
    }

    [Fact]
    public void MembershipIsExplicitSharedAndScopedToDesktop()
    {
        var item = Item("Shared reference"); var other = Item("Other desktop"); other.DesktopId = Guid.NewGuid();
        var (doc, zone, service) = Setup(item, other);
        var second = new WorkspaceZone { Bounds = zone.Bounds }; doc.Zones.Add(second);
        Assert.False(Assert.Single(service.Members(zone.Id)).Assigned); // Overlap alone adds nothing.
        Assert.True(service.SetMembership(zone.Id, new[] { item.Id }));
        Assert.True(service.SetMembership(second.Id, new[] { item.Id }));
        Assert.True(Assert.Single(service.Members(zone.Id)).Assigned);
        Assert.False(service.SetMembership(zone.Id, new[] { other.Id }));
        Assert.False(service.SetMembership(zone.Id, new[] { item.Id, item.Id }));
        Assert.False(service.SetMembership(zone.Id, new[] { Guid.NewGuid() }));
        Assert.Equal(item.Id, Assert.Single(zone.ItemIds));
        Assert.True(service.Remove(zone.Id));
        Assert.Equal(item.Id, Assert.Single(second.ItemIds));
        Assert.Contains(item, doc.Items);
    }

    [Fact]
    public void MovePlanPreservesStateAndSkipsPinnedMembersWithoutMutatingInputs()
    {
        var normal = Item("Editor"); var pinned = Item("Pinned", 400, true); var hidden = Item("Minimized", 600);
        var state = hidden.Bounds; state.State = WindowState.Minimized; hidden.Bounds = state;
        var (_, zone, service) = Setup(normal, pinned, hidden);
        service.SetMembership(zone.Id, new[] { normal.Id, pinned.Id, hidden.Id });
        Assert.True(service.TryPlanMove(zone.Id, -300, 500, out var plan));
        Assert.Equal(-300, plan!.Bounds.X);
        Assert.Equal(2, plan.Items.Count);
        var moved = plan.Items.Single(p => p.Id == hidden.Id).Bounds;
        Assert.Equal(300, moved.X); Assert.Equal(700, moved.Y);
        Assert.Equal(WindowState.Minimized, moved.State); Assert.Equal(9, moved.ZOrder);
        Assert.Equal(100, normal.Bounds.X); Assert.Equal(0, zone.Bounds.X);
        Assert.DoesNotContain(plan.Items, p => p.Id == pinned.Id);
    }

    [Fact]
    public void InvalidMemberDestinationRejectsWholeMove()
    {
        var item = Item("At boundary", 9999900);
        var (_, zone, service) = Setup(item);
        service.SetMembership(zone.Id, new[] { item.Id });
        Assert.False(service.TryPlanMove(zone.Id, 200, 0, out var plan));
        Assert.Null(plan);
        Assert.Equal(0, zone.Bounds.X); Assert.Equal(9999900, item.Bounds.X);
        Assert.False(service.TryPlanMove(zone.Id, double.NaN, 0, out _));
    }

    [Fact]
    public void GroupMoveUpdatesLiveWindowAndPlaceholderOnceAndSurvivesCapture()
    {
        var open = Item("Open editor"); var closed = Item("Closed reference", 1300); var unrelated = Item("Other project", 2400);
        var (document, zone, _) = Setup(open, closed, unrelated);
        zone.ItemIds.AddRange(new[] { open.Id, closed.Id });
        using var rig = new WorkspaceHarness(document);
        rig.Api.AddWindow((IntPtr)42, 10, 20, 800, 600, title: open.Title);
        rig.Workspace.Start();
        var card = rig.Cards.Created.Single(c => c.Item.Id == closed.Id);
        var unrelatedCard = rig.Cards.Created.Single(c => c.Item.Id == unrelated.Id);
        rig.Api.SetPositionCalls.Clear();
        int saves = rig.Store.Saves;
        Assert.True(rig.Workspace.MoveZone(zone.Id, 250, -75));
        Assert.Equal(1, rig.Store.Saves - saves);
        Assert.Equal(350, rig.Canvas.Windows[(IntPtr)42].X);
        Assert.Equal(1550, rig.Canvas.Windows[card.Handle].X);
        Assert.Equal(2400, rig.Canvas.Windows[unrelatedCard.Handle].X);
        Assert.Equal(250, zone.Bounds.X);
        Assert.Equal(9, rig.Canvas.Windows[(IntPtr)42].ZOrder);
        rig.Clock.Advance(2000); rig.Workspace.Tick();
        Assert.Equal(350, open.Bounds.X); Assert.Equal(1550, closed.Bounds.X);
        Assert.Equal(125, card.Item.Bounds.Y);
    }

    [Fact]
    public void ForgettingPlaceCleansEveryMembershipBeforeSave()
    {
        var item = Item("Reference"); var (document, zone, _) = Setup(item);
        var second = new WorkspaceZone { Bounds = zone.Bounds }; document.Zones.Add(second);
        zone.ItemIds.Add(item.Id); second.ItemIds.Add(item.Id);
        using var rig = new WorkspaceHarness(document);
        rig.Workspace.Start(); rig.Workspace.Forget(item.Id);
        Assert.Empty(zone.ItemIds); Assert.Empty(second.ItemIds);
        Assert.True(rig.Workspace.MoveZone(zone.Id, 200, 100)); // Empty area still moves.
        Assert.Empty(document.Items);
    }

    [Fact]
    public void FailedSaveRetainsConsistentGroupLayoutForRetry()
    {
        var item = Item("Closed"); var (document, zone, _) = Setup(item); zone.ItemIds.Add(item.Id);
        using var rig = new WorkspaceHarness(document);
        rig.Workspace.Start(); rig.Store.SaveError = new IOException("Disk full");
        Assert.False(rig.Workspace.MoveZone(zone.Id, 400, 0));
        Assert.Equal(400, zone.Bounds.X); Assert.Equal(500, item.Bounds.X);
        Assert.Equal(500, rig.Canvas.Windows[Assert.Single(rig.Cards.Created).Handle].X);
        rig.Store.SaveError = null;
        Assert.True(rig.Workspace.SaveNow());
        Assert.Equal(500, item.Bounds.X);
    }

    [Fact]
    public void RecycledWindowHandleDoesNotReceiveThePreviousMembersMove()
    {
        var item = Item("Original project"); var (document, zone, _) = Setup(item); zone.ItemIds.Add(item.Id);
        using var rig = new WorkspaceHarness(document);
        var handle = (IntPtr)42;
        rig.Api.AddWindow(handle, 10, 20, 800, 600, title: item.Title);
        rig.Workspace.Start();
        rig.Api.Windows[handle].ProcessId = 99;
        rig.Identity.Executables[99] = @"C:\Other\Other.exe";
        rig.Api.Windows[handle].Title = "Different project";
        rig.Api.Windows[handle].X = 4000;
        Assert.True(rig.Workspace.MoveZone(zone.Id, 250, 0));
        Assert.Equal(350, item.Bounds.X);
        Assert.Equal(4000, rig.Canvas.Windows[handle].X);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PinnedPlacesStayFixedWhileCollapsedRestoreBoundsMove(bool maximized)
    {
        var collapsed = Item("Collapsed"); var b = collapsed.Bounds;
        b.State = maximized ? WindowState.Maximized : WindowState.Minimized; collapsed.Bounds = b;
        var pinned = Item("Pinned", 500, true);
        var (document, zone, _) = Setup(collapsed, pinned); zone.ItemIds.AddRange(new[] { collapsed.Id, pinned.Id });
        using var rig = new WorkspaceHarness(document);
        int style = maximized ? (int)Windows.Win32.UI.WindowsAndMessaging.WINDOW_STYLE.WS_MAXIMIZE
            : (int)Windows.Win32.UI.WindowsAndMessaging.WINDOW_STYLE.WS_MINIMIZE;
        rig.Api.AddWindow((IntPtr)42, 0, 0, 800, 600, style: style, title: collapsed.Title);
        rig.Workspace.Start(); rig.Api.NativeStateCalls.Clear();
        Assert.True(rig.Workspace.MoveZone(zone.Id, 200, 300));
        Assert.Equal(300, collapsed.Bounds.X); Assert.Equal(500, pinned.Bounds.X);
        var native = Assert.Single(rig.Api.NativeStateCalls);
        Assert.Equal(collapsed.Bounds.State, native.state); Assert.Equal(300, native.rect.X);
    }

    [Fact]
    public void MembershipRoundTripAndValidationRejectsDanglingCrossDesktopAndDuplicateReferences()
    {
        string path = Path.Combine(Path.GetTempPath(), "canvas-zone-members-" + Guid.NewGuid(), "workspace.json");
        var item = Item("Reference"); var (document, zone, service) = Setup(item);
        try
        {
            service.SetMembership(zone.Id, new[] { item.Id });
            var store = new WorkspaceStore(path); store.Save(document);
            Assert.Equal(item.Id, Assert.Single(Assert.Single(store.Load(out _).Zones).ItemIds));
            zone.ItemIds.Add(item.Id);
            Assert.Throws<InvalidDataException>(() => store.Save(document));
            zone.ItemIds.RemoveAt(1); item.DesktopId = Guid.NewGuid();
            Assert.Throws<InvalidDataException>(() => store.Save(document));
            document.Items.Clear(); Assert.Throws<InvalidDataException>(() => store.Save(document));
        }
        finally { if (Directory.Exists(Path.GetDirectoryName(path))) Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }
}
