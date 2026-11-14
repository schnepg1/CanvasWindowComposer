using CanvasDesktop;

namespace CanvasDesktop.Tests;

// "Link open window…" must list the other open windows of the same application.
public class WorkspaceLinkCandidateTests
{
    private const string SignalExe = @"C:\Users\me\AppData\Local\Programs\signal-desktop\Signal.exe";
    private const string DiscordExe = @"C:\Users\me\AppData\Local\Discord\app-1.0.9265\Discord.exe";
    private const string DiscordUpdateExe = @"C:\Users\me\AppData\Local\Discord\app-1.0.9267\Discord.exe";

    private static WorkspaceDocument Document(WorkspaceItem item) => new()
    {
        PlacementPolicyInitialized = true, Items = new() { item }
    };

    private static WorkspaceItem SignalItem(string title = "Signal") => new()
    {
        Title = title, ExecutablePath = SignalExe,
        Bounds = new WorldRect { X = 4683, Y = -9627, W = 1442, H = 894 }
    };

    [Fact]
    public void OtherOpenWindowsOfTheSameApplicationAreListed()
    {
        var item = SignalItem();
        using var rig = new WorkspaceHarness(Document(item));
        rig.Identity.Exe = SignalExe;
        rig.Api.AddWindow((IntPtr)42, 10, 20, 800, 600, title: "Signal");
        rig.Api.AddWindow((IntPtr)43, 30, 40, 800, 600, title: "Alice");
        rig.Api.AddWindow((IntPtr)44, 50, 60, 800, 600, title: "Bob");
        rig.Workspace.Start();
        Assert.Equal(2, rig.Workspace.LinkCandidates(item.Id).Count);
    }

    [Fact]
    public void OtherOpenWindowsAreListedWhenThePlaceholderSharesTheirCaption()
    {
        var item = SignalItem();
        using var rig = new WorkspaceHarness(Document(item));
        rig.Identity.Exe = SignalExe;
        rig.Api.AddWindow((IntPtr)42, 10, 20, 800, 600, title: "Signal");
        rig.Api.AddWindow((IntPtr)43, 30, 40, 800, 600, title: "Signal");
        rig.Api.AddWindow((IntPtr)44, 50, 60, 800, 600, title: "Signal");
        rig.Workspace.Start();
        // None of them can claim the place on their own, so all three are linkable.
        Assert.Equal(3, rig.Workspace.LinkCandidates(item.Id).Count);
    }

    [Fact]
    public void OtherOpenWindowsOfTheSameApplicationAreListedAfterTheApplicationWasAlreadyOpen()
    {
        var item = SignalItem();
        using var rig = new WorkspaceHarness(Document(item));
        rig.Identity.Exe = SignalExe;
        rig.Api.AddWindow((IntPtr)42, 10, 20, 800, 600, title: "Signal");
        rig.Workspace.Start();
        Assert.True(rig.Workspace.IsOpen(item));
        // The application opens further windows while it runs.
        rig.Api.AddWindow((IntPtr)43, 30, 40, 800, 600, title: "Alice");
        rig.Api.AddWindow((IntPtr)44, 50, 60, 800, 600, title: "Bob");
        rig.Workspace.SaveNow();
        Assert.Equal(2, rig.Workspace.LinkCandidates(item.Id).Count);
    }

    [Fact]
    public void OtherOpenWindowsAreListedForAMinimizedPlaceholderOfTheSameApplication()
    {
        var item = SignalItem();
        using var rig = new WorkspaceHarness(Document(item));
        rig.Identity.Exe = SignalExe;
        rig.Api.AddWindow((IntPtr)42, 10, 20, 800, 600, title: "Signal");
        rig.Api.AddWindow((IntPtr)43, 30, 40, 800, 600, title: "Alice");
        rig.Api.AddWindow((IntPtr)44, 50, 60, 800, 600, title: "Bob");
        rig.Workspace.Start();
        // The placeholder's own window is minimized: the card stays, the place is open.
        rig.Api.Windows[(IntPtr)42].Style |= (int)Windows.Win32.UI.WindowsAndMessaging.WINDOW_STYLE.WS_MINIMIZE;
        rig.Workspace.SaveNow();
        Assert.True(rig.Workspace.IsOpen(item));
        Assert.Equal(2, rig.Workspace.LinkCandidates(item.Id).Count);
    }

    [Fact]
    public void AProgramThatUpdatedItsBuildFolderStillOffersItsOpenWindows()
    {
        var item = new WorkspaceItem
        {
            Title = "Discord", ExecutablePath = DiscordExe,
            Bounds = new WorldRect { X = 4000, Y = -9000, W = 1200, H = 800 }
        };
        using var rig = new WorkspaceHarness(Document(item));
        rig.Identity.Exe = DiscordUpdateExe;
        rig.Api.AddWindow((IntPtr)42, 10, 20, 1200, 800, title: "Discord");
        rig.Api.AddWindow((IntPtr)43, 30, 40, 1200, 800, title: "Friends");
        rig.Workspace.Start();
        // The saved place keeps working and refreshes its launch path.
        Assert.True(rig.Workspace.IsOpen(item));
        Assert.Equal(DiscordUpdateExe, item.ExecutablePath);
        Assert.Equal(1, rig.Workspace.LinkCandidates(item.Id).Count);
    }

    [Fact]
    public void LinkingReplacesTheWindowThatOwnedThePlace()
    {
        var item = SignalItem();
        using var rig = new WorkspaceHarness(Document(item));
        rig.Identity.Exe = SignalExe;
        rig.Api.AddWindow((IntPtr)42, 10, 20, 800, 600, title: "Signal");
        rig.Api.AddWindow((IntPtr)43, 30, 40, 800, 600, title: "Alice");
        rig.Workspace.Start();
        Assert.True(rig.Workspace.IsOpen(item));
        var candidate = Assert.Single(rig.Workspace.LinkCandidates(item.Id));
        Assert.True(rig.Workspace.LinkWindow(item.Id, candidate.Token));
        Assert.True(rig.Workspace.IsOpen(item));
        Assert.Equal("Alice", item.Title);
        // The released window becomes an ordinary arrival and cannot take the place back.
        rig.Workspace.SaveNow();
        Assert.True(rig.Workspace.IsOpen(item));
        Assert.Equal("Alice", item.Title);
    }
}
