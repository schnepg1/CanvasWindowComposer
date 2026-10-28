using CanvasDesktop;
using System.Windows.Forms;

namespace CanvasDesktop.Tests;

public sealed class WindowContextItemTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "canvas-window-menu-" + Guid.NewGuid().ToString("N"));
    private const string Chrome = @"C:\Apps\chrome.exe";
    private string Profile()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "Preferences"), "{}");
        return _root;
    }

    [Fact]
    public void DetachedTabHasFullMenuBeforeCaptureAndKeepsItsOwnProfileAndPlacement()
        => WorkspaceViewTests.OnSta(() =>
        {
            string profile = Profile();
            using var rig = new WorkspaceHarness();
            rig.Identity.Exe = Chrome;
            rig.Workspace.Document.Items.Add(new WorkspaceItem { ExecutablePath = Chrome, Title = "Original tab",
                ChromeProfilePath = profile, Bounds = new() { X = 10, Y = 20, W = 800, H = 600 } });
            rig.Api.AddWindow((IntPtr)42, 10, 20, 800, 600, title: "Original tab");
            rig.Identity.AppIds[(IntPtr)42] = "Chrome." + ChromeProfiles.ProfileId(profile);
            rig.Workspace.Start();
            var original = Assert.Single(rig.Workspace.Document.Items);
            rig.Api.AddWindow((IntPtr)43, 60, 70, 900, 650, title: "Detached tab");
            rig.Identity.AppIds[(IntPtr)43] = rig.Identity.AppIds[(IntPtr)42];
            rig.Wm.TryRegisterWindow((IntPtr)43);
            var before = rig.Canvas.Windows[(IntPtr)43];
            int nativeCalls = rig.Api.NativeStateCalls.Count;

            var id = rig.Workspace.GetWindowContextItem((IntPtr)43);
            Assert.NotNull(id);
            var item = rig.Workspace.Document.Items.Single(i => i.Id == id);
            Assert.NotEqual(original.Id, id);
            Assert.Equal(WorkspacePlacement.Temporary, item.Placement);
            Assert.Equal(profile, item.ChromeProfilePath);
            Assert.Equal(before, item.Bounds);
            Assert.Equal(before, rig.Canvas.Windows[(IntPtr)43]);
            Assert.Equal(nativeCalls, rig.Api.NativeStateCalls.Count);
            Assert.Equal(original.Id, rig.Workspace.GetWindowContextItem((IntPtr)42));
            Assert.Equal(id, rig.Workspace.GetWindowContextItem((IntPtr)43));
            using var menu = new WorkspaceContextMenu(rig.Workspace, item.Id);
            foreach (string label in new[] { "Rename…", "Chrome profile…", "Web resource settings…", "Exclude from auto-minimize", "Keep this place" })
                Assert.True(menu.Items.Cast<ToolStripItem>().Single(i => i.Text == label).Available, label);
            rig.Workspace.SaveNow();
            Assert.Single(rig.Store.Document.Items);
            rig.Workspace.KeepPlace(item.Id);
            var saved = rig.Store.Document.Items.Single(i => i.Id == item.Id);
            Assert.Equal(profile, saved.ChromeProfilePath);
            Assert.Equal(before, saved.Bounds);
        });

    [Fact]
    public void AmbiguousStartupWindowGetsMenuWithoutGuessingOrMovingAndCanStillBeLinked()
    {
        var first = new WorkspaceItem { ExecutablePath = Chrome, Title = "Same tab", Bounds = new() { X = 5000, W = 800, H = 600 } };
        var second = new WorkspaceItem { ExecutablePath = Chrome, Title = "Same tab", Bounds = new() { X = 7000, W = 800, H = 600 } };
        using var rig = new WorkspaceHarness(new WorkspaceDocument { Items = [first, second] });
        rig.Identity.Exe = Chrome;
        rig.Api.AddWindow((IntPtr)42, 10, 20, 800, 600, title: first.Title);
        rig.Workspace.Start();
        Assert.Equal(2, rig.Workspace.Items.Count);
        var token = Assert.Single(rig.Workspace.LinkCandidates(first.Id)).Token;
        var before = rig.Canvas.Windows[(IntPtr)42];
        var id = rig.Workspace.GetWindowContextItem((IntPtr)42);
        Assert.NotNull(id);
        Assert.NotEqual(first.Id, id); Assert.NotEqual(second.Id, id);
        Assert.Equal(before, rig.Canvas.Windows[(IntPtr)42]);
        Assert.False(rig.Workspace.IsOpen(first)); Assert.False(rig.Workspace.IsOpen(second));
        rig.Workspace.SaveNow();
        Assert.Equal(2, rig.Store.Document.Items.Count);
        Assert.Equal(token, Assert.Single(rig.Workspace.LinkCandidates(first.Id)).Token);
        Assert.True(rig.Workspace.LinkWindow(first.Id, token));
        Assert.Equal(first.Id, rig.Workspace.GetWindowContextItem((IntPtr)42));
        Assert.DoesNotContain(rig.Workspace.Items, i => i.Id == id);
    }

    [Theory]
    [InlineData("hidden")]
    [InlineData("desktop")]
    [InlineData("process")]
    [InlineData("forgotten")]
    public void InvalidOrForgottenWindowsCannotAcquireMenuBindings(string change)
    {
        using var rig = new WorkspaceHarness();
        rig.Api.AddWindow((IntPtr)42, 10, 20, 800, 600, title: "Original");
        rig.Workspace.Start();
        var original = Assert.Single(rig.Workspace.Items);
        if (change == "hidden") rig.Api.Windows[(IntPtr)42].Visible = false;
        if (change == "desktop") rig.Desktops.DefaultIsOnCurrent = false;
        if (change == "process") rig.Identity.Unavailable = true;
        if (change == "forgotten") rig.Workspace.Forget(original.Id);
        int count = rig.Workspace.Items.Count;
        Assert.Null(rig.Workspace.GetWindowContextItem((IntPtr)42));
        Assert.Equal(count, rig.Workspace.Items.Count);
    }

    [Fact]
    public void RecycledWindowDoesNotInheritSavedIdentityOrStaleCanvasGeometry()
    {
        using var rig = new WorkspaceHarness();
        rig.Api.AddWindow((IntPtr)42, 10, 20, 800, 600, title: "Original");
        rig.Workspace.Start();
        var original = Assert.Single(rig.Workspace.Items);
        rig.Canvas.SetWindow((IntPtr)42, 9999, 9999, 800, 600);
        rig.Identity.StartedAt++;
        rig.Api.Windows[(IntPtr)42].Title = "Replacement";
        var id = rig.Workspace.GetWindowContextItem((IntPtr)42);
        Assert.NotNull(id); Assert.NotEqual(original.Id, id);
        var replacement = rig.Workspace.Items.Single(i => i.Id == id);
        Assert.Equal("Replacement", replacement.Title);
        Assert.NotEqual(9999, replacement.Bounds.X);
        Assert.Equal(original.Bounds, rig.Workspace.Items.Single(i => i.Id == original.Id).Bounds);
        Assert.False(rig.Workspace.GetOverviewItemIds()[(IntPtr)42] == original.Id);
    }

    [Fact]
    public void MenuEntryCannotBeReassignedToAClosedBrowserPlaceOnTheNextTick()
    {
        string profile = Profile();
        var saved = new WorkspaceItem { ExecutablePath = Chrome, Title = "Closed tab", ChromeProfilePath = profile,
            Bounds = new() { X = 5000, W = 800, H = 600 } };
        using var rig = new WorkspaceHarness(new WorkspaceDocument { PlacementPolicyInitialized = true, Items = [saved] });
        rig.Identity.Exe = Chrome;
        rig.Workspace.Start();
        rig.Api.AddWindow((IntPtr)42, 10, 20, 800, 600, title: "New tab");
        rig.Identity.AppIds[(IntPtr)42] = "Chrome." + ChromeProfiles.ProfileId(profile);
        rig.Wm.TryRegisterWindow((IntPtr)42);
        var id = rig.Workspace.GetWindowContextItem((IntPtr)42);
        Assert.NotNull(id);
        rig.Workspace.SaveNow();
        Assert.Equal(id, rig.Workspace.GetWindowContextItem((IntPtr)42));
        Assert.False(rig.Workspace.IsOpen(saved));
        Assert.Equal(5000, saved.Bounds.X);
        Assert.Equal(id, rig.Workspace.GetOverviewItemIds()[(IntPtr)42]);
    }

    [Fact]
    public void NormallyCapturedChromeArrivalHasProfileOnItsFirstCapture()
    {
        string profile = Profile();
        using var rig = new WorkspaceHarness(new WorkspaceDocument { PlacementPolicyInitialized = true });
        rig.Identity.Exe = Chrome;
        rig.Api.AddWindow((IntPtr)42, 10, 20, 800, 600, title: "New tab");
        rig.Identity.AppIds[(IntPtr)42] = "Chrome." + ChromeProfiles.ProfileId(profile);
        // Include the custom profile in the candidate catalog without a matching slot.
        rig.Workspace.Document.Items.Add(new WorkspaceItem { ExecutablePath = Chrome, Title = "Other account",
            ChromeProfilePath = profile, Bounds = new() { X = 5000, W = 800, H = 600 } });
        rig.Workspace.Document.Items[0].DesktopId = Guid.NewGuid();
        rig.Workspace.Start();
        var arrival = rig.Workspace.Document.Items.Single(i => i.Placement == WorkspacePlacement.Temporary);
        Assert.Equal(profile, arrival.ChromeProfilePath);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
