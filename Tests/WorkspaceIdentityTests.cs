using System.Text.Json;
using CanvasDesktop;

namespace CanvasDesktop.Tests;

public class WorkspaceIdentityTests
{
    private static readonly IntPtr Handle = (IntPtr)42;
    private static WorkspaceItem Item(string title = "Project", string target = "") => new()
    {
        Title = title, Target = target, ExecutablePath = @"C:\Apps\Editor.exe",
        Bounds = new WorldRect { X = 5000, Y = 7000, W = 800, H = 600 }
    };
    private static WorkspaceDocument Document(params WorkspaceItem[] items) => new() { Items = items.ToList() };

    [Fact]
    public void RegistryEnforcesOneToOneBindingsAndRejectsRemovedTokens()
    {
        var registry = new WorkspaceBindings();
        var first = registry.Observe(new(Handle, 10, 123), out var replaced);
        var second = registry.Observe(new((IntPtr)43, 10, 123), out _);
        var slot = Guid.NewGuid();
        Assert.False(replaced);
        Assert.Same(first, registry.Observe(first.Identity, out _));
        Assert.True(registry.Bind(first, slot));
        Assert.False(registry.Bind(second, slot));
        Assert.False(registry.Bind(first, Guid.NewGuid()));
        Assert.Same(first, registry.Remove(Handle));
        Assert.Null(registry.ForToken(first.Token));
        Assert.False(registry.Bind(first, Guid.NewGuid()));
        Assert.True(registry.Bind(second, slot));
        registry.Clear();
        Assert.Empty(registry.Windows);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecycledProcessIdentityGetsANewTokenAndDoesNotInheritForgetState(bool reusePid)
    {
        var registry = new WorkspaceBindings();
        var first = registry.Observe(new(Handle, 10, 123), out _);
        first.Forgotten = true; first.WaitingForFolderSince = 100;
        var next = registry.Observe(new(Handle, reusePid ? 10u : 11u, 456), out var replaced);
        Assert.True(replaced);
        Assert.NotEqual(first.Token, next.Token);
        Assert.False(next.Forgotten);
        Assert.Null(next.ItemId);
        Assert.Null(next.WaitingForFolderSince);
    }

    [Theory]
    [InlineData("")]
    [InlineData(@"C:\Work\Project")]
    public void DuplicateSavedIdentitiesRemainAmbiguousEvenIfOneIsBound(string target)
    {
        var first = Item(target: target); var second = Item(target: target);
        var result = WorkspaceMatcher.Evaluate(new[] { first, second }, Guid.Empty,
            first.ExecutablePath, first.Title, target, Array.Empty<Guid>());
        Assert.True(result.IsAmbiguous);
        Assert.Equal(new[] { first.Id, second.Id }, result.AmbiguousIds);
        result = WorkspaceMatcher.Evaluate(new[] { first, second }, Guid.Empty,
            first.ExecutablePath, first.Title, target, new[] { first.Id });
        Assert.True(result.IsAmbiguous);
        Assert.Equal(new[] { second.Id }, result.AmbiguousIds);
    }

    [Fact]
    public void ExplicitLaunchHasPriorityOverAnotherSlotsMatchingCaption()
    {
        var requested = Item("One"); var caption = Item("Two");
        var result = WorkspaceMatcher.Evaluate(new[] { requested, caption }, Guid.Empty,
            requested.ExecutablePath, "Two", "", Array.Empty<Guid>(), requested.Id);
        Assert.Same(requested, result.Item);
    }

    [Fact]
    public void ExplicitLaunchDoesNotOverrideADifferentObservedFolder()
    {
        var requested = Item(target: @"C:\One"); requested.IsFolder = true;
        Assert.Null(WorkspaceMatcher.Evaluate(new[] { requested }, Guid.Empty,
            requested.ExecutablePath, "Project", @"C:\Two", Array.Empty<Guid>(), requested.Id).Item);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TwoLiveWindowsCompetingForOneSlotAreOrderIndependent(bool reverse)
    {
        var item = Item();
        using var rig = new WorkspaceHarness(Document(item));
        rig.Api.AddWindow(Handle, 10, 20, 800, 600, title: item.Title);
        rig.Api.AddWindow((IntPtr)43, 30, 40, 800, 600, title: item.Title);
        if (reverse) rig.Api.EnumOrder.Reverse();
        rig.Workspace.Start();
        Assert.False(rig.Workspace.IsOpen(item));
        Assert.Single(rig.Workspace.Document.Items);
        Assert.Empty(rig.Api.NativeStateCalls);
        Assert.Equal("Needs linking", Assert.Single(rig.Workspace.Items).State);
        Assert.Equal(2, rig.Workspace.LinkCandidates(item.Id).Count);
        Assert.Equal(10, rig.Api.Windows[Handle].X);
        Assert.Equal(30, rig.Api.Windows[(IntPtr)43].X);
        Assert.Equal(5000, item.Bounds.X);
    }

    [Fact]
    public void DuplicateSavedSlotsDoNotCreateAnExtraSlotOrMoveTheLiveWindow()
    {
        var first = Item(); var second = Item();
        using var rig = new WorkspaceHarness(Document(first, second));
        rig.Api.AddWindow(Handle, 10, 20, 800, 600, title: first.Title);
        rig.Workspace.Start(); rig.Workspace.SaveNow();
        Assert.Equal(2, rig.Workspace.Items.Count);
        Assert.All(rig.Workspace.Items, i => Assert.Equal("Needs linking", i.State));
        Assert.Empty(rig.Api.NativeStateCalls);
        Assert.Equal(10, rig.Api.Windows[Handle].X);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StaleRuntimeBindingCannotFocusCloseOrOverwriteSavedBounds(bool reusePid)
    {
        var first = Item(); var other = Item("Other");
        using var rig = new WorkspaceHarness(Document(first, other));
        rig.Api.AddWindow(Handle, 10, 20, 800, 600, title: first.Title);
        rig.Workspace.Start();
        rig.Canvas.SetWindow(Handle, 9999, 9999, 900, 700);
        rig.Api.Windows[Handle].Title = "Other";
        rig.Api.Windows[Handle].X = 222;
        if (reusePid) rig.Identity.StartedAt = 2;
        else rig.Api.Windows[Handle].ProcessId = 2;
        rig.Workspace.FocusItem(first.Id); rig.Workspace.CloseToPlaceholder(first.Id);
        Assert.Empty(rig.Actions.Focused); Assert.Empty(rig.Actions.Closed);
        rig.Workspace.SaveNow();
        Assert.Equal(5000, first.Bounds.X);
        Assert.False(rig.Workspace.IsOpen(first));
        Assert.True(rig.Workspace.IsOpen(other));
        Assert.Equal(other.Bounds.X, rig.Canvas.Windows[Handle].X);
        Assert.DoesNotContain(rig.Workspace.Items, i => i.Bounds.X == 9999);
    }

    [Fact]
    public void UnavailableProcessIdentityPreservesSlotAndRejectsNativeCommands()
    {
        var item = Item();
        using var rig = new WorkspaceHarness(Document(item));
        rig.Api.AddWindow(Handle, 10, 20, 800, 600, title: item.Title);
        rig.Workspace.Start(); rig.Identity.Unavailable = true;
        rig.Canvas.SetWindow(Handle, 9999, 9999, 900, 700);
        rig.Workspace.CloseToPlaceholder(item.Id);
        rig.Workspace.FocusItem(item.Id);
        rig.Workspace.SaveNow();
        Assert.Empty(rig.Actions.Closed); Assert.Empty(rig.Actions.Focused);
        Assert.Equal(5000, item.Bounds.X);
        Assert.Equal("Placeholder", Assert.Single(rig.Workspace.Items).State);
        Assert.Empty(rig.Workspace.LinkCandidates(item.Id));
        rig.Identity.Unavailable = false;
        rig.Workspace.SaveNow();
        Assert.True(rig.Workspace.IsOpen(item));
        Assert.Equal(5000, rig.Canvas.Windows[Handle].X);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemovalInvalidatesBindingsAndTokensEvenWithinSameProcess(bool actualDestroy)
    {
        var first = Item(); var second = Item();
        using var rig = new WorkspaceHarness(Document(first, second));
        rig.Api.AddWindow(Handle, 10, 20, 800, 600, title: first.Title);
        rig.Workspace.Start();
        var old = Assert.Single(rig.Workspace.LinkCandidates(first.Id));
        Assert.True(rig.Workspace.LinkWindow(first.Id, old.Token));
        if (actualDestroy) rig.Api.Windows.Remove(Handle);
        rig.Wm.RemoveWindow(Handle);
        rig.Api.AddWindow(Handle, 30, 40, 800, 600, title: first.Title);
        rig.Workspace.SaveNow();
        Assert.False(rig.Workspace.LinkWindow(first.Id, old.Token));
        Assert.False(rig.Workspace.IsOpen(first));
        Assert.NotEqual(old.Token, Assert.Single(rig.Workspace.LinkCandidates(first.Id)).Token);
    }

    [Fact]
    public void ForgottenWindowDoesNotSuppressARecycledHandle()
    {
        using var rig = new WorkspaceHarness();
        rig.Api.AddWindow(Handle, 10, 20, 800, 600, title: "First");
        rig.Workspace.Start(); rig.Workspace.Forget(Assert.Single(rig.Workspace.Items).Id);
        rig.Workspace.SaveNow(); Assert.Empty(rig.Workspace.Items);
        rig.Api.Windows[Handle].ProcessId = 2;
        rig.Api.Windows[Handle].Title = "Second";
        rig.Api.Windows[Handle].X = 222;
        rig.Workspace.SaveNow();
        var second = Assert.Single(rig.Workspace.Items);
        Assert.Equal("Second", second.Title); Assert.Equal(222, second.Bounds.X);
    }

    [Fact]
    public void ManualLinkMovesOnlySelectedWindowAndCannotStealABoundWindow()
    {
        var first = Item(); var second = Item();
        using var rig = new WorkspaceHarness(Document(first, second));
        rig.Api.AddWindow(Handle, 10, 20, 800, 600, title: first.Title);
        rig.Workspace.Start();
        var candidate = Assert.Single(rig.Workspace.LinkCandidates(first.Id));
        Assert.True(rig.Workspace.LinkWindow(first.Id, candidate.Token));
        Assert.Equal(5000, rig.Api.Windows[Handle].X);
        Assert.True(rig.Workspace.IsOpen(first));
        Assert.False(rig.Workspace.IsOpen(second));
        Assert.False(rig.Workspace.LinkWindow(second.Id, candidate.Token));
        Assert.False(rig.Workspace.LinkWindow(second.Id, Guid.NewGuid()));
    }

    [Theory]
    [InlineData("process")]
    [InlineData("desktop")]
    [InlineData("hidden")]
    [InlineData("exe")]
    [InlineData("forgotten-slot")]
    public void StaleLinkSelectionIsRevalidatedBeforeMovement(string change)
    {
        var first = Item(); var second = Item();
        using var rig = new WorkspaceHarness(Document(first, second));
        rig.Api.AddWindow(Handle, 10, 20, 800, 600, title: first.Title);
        rig.Workspace.Start();
        var candidate = Assert.Single(rig.Workspace.LinkCandidates(first.Id));
        switch (change)
        {
            case "process": rig.Identity.StartedAt = 2; break;
            case "desktop": rig.Desktops.DefaultIsOnCurrent = false; break;
            case "hidden": rig.Api.Windows[Handle].Visible = false; break;
            case "exe": rig.Identity.Exe = @"C:\Other\Editor.exe"; break;
            case "forgotten-slot": rig.Workspace.Forget(first.Id); break;
        }
        Assert.False(rig.Workspace.LinkWindow(first.Id, candidate.Token));
        Assert.Empty(rig.Api.NativeStateCalls);
    }

    [Fact]
    public void LaunchIntentCannotClaimAnAlreadyOpenAmbiguousWindow()
    {
        var first = Item(); var second = Item();
        using var rig = new WorkspaceHarness(Document(first, second));
        rig.Api.AddWindow(Handle, 10, 20, 800, 600, title: first.Title);
        rig.Workspace.Start(); rig.Workspace.Open(first.Id);
        rig.Workspace.SaveNow();
        Assert.False(rig.Workspace.IsOpen(first)); Assert.Empty(rig.Api.NativeStateCalls);
        rig.Api.AddWindow((IntPtr)43, 30, 40, 800, 600, title: "Welcome");
        rig.Workspace.SaveNow();
        Assert.True(rig.Workspace.IsOpen(first));
        Assert.Equal((IntPtr)43, Assert.Single(rig.Api.NativeStateCalls).hWnd);
        Assert.Equal(2, rig.Workspace.Items.Count);
    }

    [Fact]
    public void LaunchIntentCannotClaimEitherOfTwoNewIndistinguishableWindows()
    {
        var item = Item();
        using var rig = new WorkspaceHarness(Document(item));
        rig.Workspace.Start(); rig.Workspace.Open(item.Id);
        rig.Api.AddWindow(Handle, 10, 20, 800, 600, title: "Welcome");
        rig.Api.AddWindow((IntPtr)43, 30, 40, 800, 600, title: "Welcome");
        rig.Workspace.SaveNow();
        Assert.False(rig.Workspace.IsOpen(item)); Assert.Empty(rig.Api.NativeStateCalls);
        Assert.Single(rig.Workspace.Items);
    }

    [Fact]
    public void UserNameSurvivesCaptionChangesAndDoesNotActAsMatchEvidence()
    {
        var item = Item();
        using var rig = new WorkspaceHarness(Document(item));
        rig.Api.AddWindow(Handle, 10, 20, 800, 600, title: item.Title);
        rig.Workspace.Start(); rig.Workspace.SetName(item.Id, "  Canvas project  ");
        rig.Api.Windows[Handle].Title = "Another file — VS Code";
        rig.Workspace.SaveNow();
        Assert.Equal("Canvas project", Assert.Single(rig.Workspace.Items).Title);
        Assert.Equal("Another file — VS Code", item.Title);
        rig.Workspace.SetName(item.Id, new string('a', 201));
        Assert.Equal("Canvas project", item.Name);
        rig.Workspace.SetName(item.Id, "");
        Assert.Equal(item.Title, Assert.Single(rig.Workspace.Items).Title);
    }

    [Fact]
    public void ProcessIdentityAndCandidateTokensAreNotSerialized()
    {
        using var rig = new WorkspaceHarness();
        rig.Api.AddWindow(Handle, 10, 20, 800, 600, title: "Project"); rig.Workspace.Start();
        var json = JsonSerializer.Serialize(rig.Workspace.Document, new JsonSerializerOptions { IncludeFields = true });
        Assert.DoesNotContain("ProcessId", json); Assert.DoesNotContain("StartedAt", json);
        Assert.DoesNotContain("Token", json); Assert.DoesNotContain("Handle", json);
    }

    [Fact]
    public void RecycledInactiveHandleDoesNotEnrollGeometryIntoCurrentDesktop()
    {
        var item = Item();
        var firstDesktop = Guid.NewGuid(); item.DesktopId = firstDesktop;
        using var rig = new WorkspaceHarness(Document(item));
        // Harness cache starts at Guid.Empty; complete initial desktop detection.
        rig.Desktops.SwitchTo(firstDesktop);
        rig.Api.AddWindow(Handle, 10, 20, 800, 600, title: item.Title);
        rig.Workspace.Start();
        var secondDesktop = Guid.NewGuid();
        rig.Desktops.DefaultIsOnCurrent = false;
        rig.Desktops.SwitchTo(secondDesktop);
        Assert.False(rig.Canvas.HasWindow(Handle));
        rig.Api.Windows[Handle].ProcessId = 2;
        rig.Api.Windows[Handle].X = 222;
        rig.Workspace.SaveNow();
        Assert.False(rig.Canvas.HasWindow(Handle));
        Assert.Equal(5000, item.Bounds.X);
        rig.Desktops.DefaultIsOnCurrent = true;
        rig.Desktops.SwitchTo(firstDesktop);
        Assert.True(rig.Workspace.IsOpen(item));
        Assert.Equal(5000, rig.Canvas.Windows[Handle].X);
    }

    [Fact]
    public void StaleRuntimeIdentityIsNotSavedDuringDesktopSwitch()
    {
        var item = Item();
        var firstDesktop = Guid.NewGuid(); item.DesktopId = firstDesktop;
        using var rig = new WorkspaceHarness(Document(item));
        rig.Desktops.SwitchTo(firstDesktop);
        rig.Api.AddWindow(Handle, 10, 20, 800, 600, title: item.Title);
        rig.Workspace.Start();
        rig.Canvas.SetWindow(Handle, 9999, 9999, 900, 700);
        rig.Api.Windows[Handle].ProcessId = 2;
        rig.Desktops.DefaultIsOnCurrent = false;
        rig.Desktops.SwitchTo(Guid.NewGuid());
        Assert.Equal(5000, item.Bounds.X);
    }

    [Fact]
    public void NativeProcessInspectionReturnsStartIdentityAndFailsClosedForMissingProcess()
    {
        var identity = new WorkspaceIdentity();
        var process = identity.InspectProcess((uint)Environment.ProcessId);
        Assert.NotNull(process);
        Assert.True(process.Value.StartedAtUtcTicks > 0);
        Assert.True(Path.IsPathFullyQualified(process.Value.ExecutablePath));
        Assert.Null(identity.InspectProcess(uint.MaxValue));
    }
}
