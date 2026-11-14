using CanvasDesktop;

namespace CanvasDesktop.Tests;

// A saved place must claim the window of its own application, including when the
// application is single-instance and reveals the window it already had.
public class WorkspaceLaunchAdoptionTests
{
    private const string SignalExe = @"C:\Users\me\AppData\Local\Programs\signal-desktop\Signal.exe";

    private static WorkspaceItem SignalItem(string title = "Signal") => new()
    {
        Title = title, ExecutablePath = SignalExe,
        Bounds = new WorldRect { X = 4683, Y = -9627, W = 1442, H = 894 }
    };

    [Fact]
    public void ASingleInstanceAppRevealingItsRunningWindowStillClaimsItsSavedPlace()
    {
        var item = SignalItem();
        using var rig = new WorkspaceHarness(new() { Items = new() { item } });
        rig.Identity.Exe = SignalExe;
        var handle = (IntPtr)42;
        rig.Api.AddWindow(handle, 100, 200, 1442, 894, title: item.Title);
        rig.Api.Windows[handle].Visible = false; // The app keeps a hidden window while it runs.
        rig.Workspace.Start();
        rig.Workspace.Open(item.Id);
        Assert.Single(rig.Launcher.Opened);
        rig.Api.Windows[handle].Visible = true; // The running instance reveals that same window.
        rig.Clock.Advance(2000);
        rig.Workspace.Tick();
        Assert.True(rig.Workspace.IsOpen(item));
        Assert.Single(rig.Workspace.Items);
        Assert.Equal(item.Bounds, rig.Canvas.Windows[handle]);
    }

    [Fact]
    public void ASingleInstanceAppWithAnUnchangedCaptionStillClaimsItsSavedPlace()
    {
        var item = SignalItem("Signal");
        using var rig = new WorkspaceHarness(new() { Items = new() { item } });
        rig.Identity.Exe = SignalExe;
        var handle = (IntPtr)42;
        rig.Api.AddWindow(handle, 100, 200, 1442, 894, title: "Signal");
        rig.Api.Windows[handle].Visible = false;
        rig.Workspace.Start();
        rig.Workspace.Open(item.Id);
        rig.Api.Windows[handle].Title = "Conversation - Signal"; // The caption changed while hidden.
        rig.Api.Windows[handle].Visible = true;
        rig.Clock.Advance(2000);
        rig.Workspace.Tick();
        Assert.True(rig.Workspace.IsOpen(item));
        Assert.Single(rig.Workspace.Items);
    }

    [Fact]
    public void AWindowThatExistedBeforeTheLaunchDoesNotStealAnotherAppsLaunchedPlace()
    {
        var signal = SignalItem();
        var other = new WorkspaceItem
        {
            Title = "Editor", ExecutablePath = @"C:\Apps\Editor.exe",
            Bounds = new WorldRect { X = 9000, Y = -9000, W = 800, H = 600 }
        };
        using var rig = new WorkspaceHarness(new() { Items = new() { signal, other } });
        rig.Identity.Exe = other.ExecutablePath;
        var existing = (IntPtr)42;
        var launched = (IntPtr)43;
        rig.Api.AddWindow(existing, 100, 200, 1442, 894, title: signal.Title);
        rig.Api.Windows[existing].Visible = false;
        rig.Workspace.Start();
        rig.Workspace.Open(other.Id);
        rig.Api.Windows[existing].Visible = true;
        rig.Api.AddWindow(launched, 300, 400, 800, 600, title: other.Title);
        rig.Identity.Executables[2] = other.ExecutablePath;
        rig.Api.ReadProcessId = hWnd => hWnd == launched ? 2u : 1u;
        rig.Clock.Advance(2000);
        rig.Workspace.Tick();
        Assert.True(rig.Workspace.IsOpen(other));
        Assert.False(rig.Workspace.IsOpen(signal));
    }

    [Fact]
    public void AReopenedTrayApplicationReplacesItsPlaceholderInsteadOfArrivingInTheTemporaryArea()
    {
        var item = SignalItem();
        using var rig = new WorkspaceHarness(new() { Items = new() { item } });
        rig.Identity.Exe = SignalExe;
        var handle = (IntPtr)42;
        rig.Api.AddWindow(handle, 100, 200, 1442, 894, title: item.Title);
        rig.Workspace.Start();
        Assert.True(rig.Workspace.IsOpen(item));

        rig.Api.Windows[handle].Visible = false; // The application hides to its tray icon.
        rig.Clock.Advance(2000);
        rig.Workspace.Tick();
        Assert.False(rig.Workspace.IsOpen(item));
        var card = Assert.Single(rig.Cards.Created);
        Assert.False(card.Disposed);
        Assert.Equal("Placeholder", card.Item.State);

        rig.Workspace.Open(item.Id); // The user opens the saved place.
        Assert.Single(rig.Launcher.Opened);
        rig.Api.Windows[handle].Visible = true; // The running instance reveals that window again.
        rig.Clock.Advance(2000);
        rig.Workspace.Tick();

        Assert.True(rig.Workspace.IsOpen(item));
        Assert.True(card.Disposed);
        Assert.Single(rig.Workspace.Items);
        Assert.Equal(item.Bounds, rig.Canvas.Windows[handle]);
        Assert.DoesNotContain(rig.Workspace.Document.Items, i => i.Placement == WorkspacePlacement.Temporary);
    }
}
