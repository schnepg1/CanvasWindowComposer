using CanvasDesktop;

namespace CanvasDesktop.Tests;

public class ZoneNavigationTests
{
    private static (WorkspaceDocument Document, WorkspaceZone Area, WorkspaceItem Editor, WorkspaceItem Reference) Setup()
    {
        var editor = new WorkspaceItem { Title = "Editor", Name = "Website code", ExecutablePath = @"C:\Apps\Editor.exe",
            Target = @"C:\Work\Website", Bounds = new WorldRect { X = 100, Y = 200, W = 800, H = 600 } };
        var reference = new WorkspaceItem { Title = "Reference", ExecutablePath = @"C:\Apps\Browser.exe",
            Bounds = new WorldRect { X = 1000, Y = 200, W = 800, H = 600, PinnedToScreen = true } };
        var area = new WorkspaceZone { Name = "Website redesign", Bounds = new WorldRect { W = 2000, H = 1500 },
            ItemIds = new() { editor.Id, reference.Id } };
        return (new WorkspaceDocument { Items = new() { editor, reference }, Zones = new() { area } }, area, editor, reference);
    }

    [Fact]
    public void SearchIncludesAreaMemberNameAndPathAndRetainsFullMemberCount()
    {
        var (doc, area, editor, reference) = Setup();
        using var rig = new WorkspaceHarness(doc); rig.Workspace.Start();
        using var navigation = new ZoneNavigation(rig.Workspace.Zones, rig.Workspace, _ => { }, () => { });
        Assert.Equal(2, Assert.Single(navigation.Read("  WEBSITE REDESIGN ")).Members.Count);
        var result = Assert.Single(navigation.Read("CODE"));
        Assert.Equal(editor.Id, Assert.Single(result.Members).Id); Assert.Equal(2, result.Area.MemberCount);
        Assert.Equal(editor.Id, Assert.Single(Assert.Single(navigation.Read(@"C:\Work")).Members).Id);
        var pinned = Assert.Single(Assert.Single(navigation.Read("Reference")).Members);
        Assert.Equal(reference.Id, pinned.Id); Assert.True(pinned.Pinned);
        Assert.Empty(navigation.Read("missing"));
        int saves = rig.Store.Saves;
        navigation.Read(""); Assert.Equal(saves, rig.Store.Saves); Assert.False(rig.Workspace.CanUndoZoneLayout);
    }

    [Fact]
    public void SharedMembersAppearUnderBothAreasWhileUnassignedAndOtherDesktopsStayOut()
    {
        var (doc, area, editor, _) = Setup();
        var shared = new WorkspaceZone { Name = "Research", Bounds = area.Bounds, ItemIds = new() { editor.Id } };
        var empty = new WorkspaceZone { Name = "Empty", Bounds = area.Bounds };
        doc.Zones.AddRange(new[] { shared, empty,
            new WorkspaceZone { Name = "Other desktop", DesktopId = Guid.NewGuid(), Bounds = area.Bounds } });
        doc.Items.Add(new WorkspaceItem { Title = "Unassigned", Bounds = editor.Bounds });
        using var rig = new WorkspaceHarness(doc); rig.Workspace.Start();
        using var navigation = new ZoneNavigation(rig.Workspace.Zones, rig.Workspace, _ => { }, () => { });
        Assert.Equal(3, navigation.Read("").Count);
        Assert.Equal(2, navigation.Read("Website code").Count);
        Assert.Empty(navigation.Read("Unassigned")); Assert.Empty(navigation.Read("Other desktop"));
        Assert.Empty(Assert.Single(navigation.Read("Empty")).Members);
        var snapshot = Assert.Single(rig.Workspace.Zones.NavigationAreas, a => a.Area.Id == area.Id);
        ((Guid[])snapshot.MemberIds)[0] = Guid.NewGuid();
        Assert.Contains(editor.Id, area.ItemIds); // Detached read model cannot edit persisted membership.
    }

    [Fact]
    public void JumpUsesLatestBoundsAndDoesNotMutateLayoutHistory()
    {
        var (doc, area, _, _) = Setup();
        using var rig = new WorkspaceHarness(doc); rig.Workspace.Start();
        ZoneView? jumped = null;
        using var navigation = new ZoneNavigation(rig.Workspace.Zones, rig.Workspace, a => jumped = a, () => { });
        var b = area.Bounds; b.X = 900; rig.Workspace.ResizeZone(area.Id, b);
        int saves = rig.Store.Saves;
        Assert.True(navigation.Jump(area.Id)); Assert.Equal(900, jumped!.Bounds.X);
        Assert.Equal(saves, rig.Store.Saves);
        rig.Workspace.Zones.Remove(area.Id); jumped = null;
        Assert.False(navigation.Jump(area.Id)); Assert.Null(jumped);
    }

    [Fact]
    public void ActivateFocusesOpenMemberAndReopensClosedMemberThroughExistingCommands()
    {
        var (doc, area, editor, reference) = Setup();
        editor.Target = "";
        using var rig = new WorkspaceHarness(doc);
        rig.Api.AddWindow((IntPtr)42, 10, 20, 800, 600, title: editor.Title);
        rig.Workspace.Start();
        int before = 0;
        using var navigation = new ZoneNavigation(rig.Workspace.Zones, rig.Workspace, _ => { }, () => before++);
        var members = Assert.Single(navigation.Read("")).Members;
        Assert.Equal("Focus member", members.Single(m => m.Id == editor.Id).ActionLabel);
        Assert.Equal("Reopen member", members.Single(m => m.Id == reference.Id).ActionLabel);
        Assert.True(navigation.ActivateMember(area.Id, editor.Id));
        Assert.Equal((IntPtr)42, Assert.Single(rig.Actions.Focused).Handle); Assert.Empty(rig.Launcher.Opened);
        Assert.True(navigation.ActivateMember(area.Id, reference.Id));
        Assert.Equal(reference.Id, Assert.Single(rig.Launcher.Opened)); Assert.Equal(2, before);
        Assert.False(navigation.ActivateMember(area.Id, reference.Id)); // Launch already in progress.
        Assert.Equal(2, before); Assert.False(rig.Workspace.CanUndoZoneLayout);
    }

    [Fact]
    public void StaleMembershipDesktopAndForgottenItemsCannotTriggerActions()
    {
        var (doc, area, editor, reference) = Setup();
        using var rig = new WorkspaceHarness(doc); rig.Workspace.Start();
        int actions = 0;
        using var navigation = new ZoneNavigation(rig.Workspace.Zones, rig.Workspace, _ => actions++, () => actions++);
        rig.Workspace.Zones.SetMembership(area.Id, new[] { reference.Id });
        Assert.False(navigation.ActivateMember(area.Id, editor.Id));
        rig.Workspace.Forget(reference.Id); Assert.False(navigation.ActivateMember(area.Id, reference.Id));
        rig.Desktops.SwitchTo(Guid.NewGuid()); // Initial discovery migrates Guid.Empty areas.
        rig.Desktops.SwitchTo(Guid.NewGuid());
        Assert.False(navigation.Jump(area.Id)); Assert.Empty(navigation.Read(""));
        Assert.Empty(rig.Launcher.Opened); Assert.Equal(0, actions);
    }

    [Fact]
    public void NavigationUnsubscribesAndRejectsActionsAfterDisposal()
    {
        var (doc, area, editor, _) = Setup();
        using var rig = new WorkspaceHarness(doc); rig.Workspace.Start();
        var navigation = new ZoneNavigation(rig.Workspace.Zones, rig.Workspace, _ => { }, () => { });
        int updates = 0; navigation.Changed += () => updates++;
        rig.Workspace.Zones.Update(area.Id, "New name", "#34D399"); Assert.Equal(1, updates);
        rig.Workspace.SetName(editor.Id, "Renamed"); Assert.Equal(2, updates);
        navigation.Dispose(); navigation.Dispose();
        rig.Workspace.SetName(editor.Id, "Another name"); Assert.Equal(2, updates);
        Assert.Empty(navigation.Read("")); Assert.False(navigation.Jump(area.Id));
        Assert.False(navigation.ActivateMember(area.Id, editor.Id));
    }

    [Fact]
    public void AmbiguousMembersRequireLinkingAndLaunchFailureIsReported()
    {
        var (doc, area, editor, reference) = Setup();
        editor.Target = "";
        var duplicate = new WorkspaceItem { Title = editor.Title, Name = "Duplicate", ExecutablePath = editor.ExecutablePath,
            Target = editor.Target, Bounds = editor.Bounds };
        doc.Items.Add(duplicate);
        using var rig = new WorkspaceHarness(doc);
        rig.Api.AddWindow((IntPtr)42, 10, 20, 800, 600, title: editor.Title);
        rig.Workspace.Start();
        using var navigation = new ZoneNavigation(rig.Workspace.Zones, rig.Workspace, _ => { }, () => { });
        var member = Assert.Single(navigation.Read("")).Members.Single(m => m.Id == editor.Id);
        Assert.Equal("Needs linking", member.State); Assert.False(member.CanActivate);
        Assert.False(navigation.ActivateMember(area.Id, editor.Id)); Assert.Empty(rig.Launcher.Opened);
        rig.Launcher.Error = new IOException("Application missing");
        Assert.True(navigation.ActivateMember(area.Id, reference.Id));
        Assert.Contains("Application missing", navigation.Status);
    }
}
