using CanvasDesktop;
using Windows.Win32.UI.WindowsAndMessaging;

namespace CanvasDesktop.Tests;

public class MinimizedPlaceholderTests
{
    private static readonly IntPtr App = (IntPtr)42;
    private static WorkspaceHarness Start(bool minimized = false, bool folder = false)
    {
        var h = new WorkspaceHarness();
        if (folder)
        {
            h.Identity.Exe = @"C:\Windows\explorer.exe";
            h.Identity.Folders[App] = @"C:\Projects";
        }
        h.Api.AddWindow(App, 100, 200, 800, 600, pid: 7, title: "Project",
            style: minimized ? (int)WINDOW_STYLE.WS_MINIMIZE : 0);
        h.Workspace.Start();
        return h;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InitiallyMinimizedAppOrFolderGetsOneCardWithoutLaunchingOrRestoring(bool folder)
    {
        using var h = Start(minimized: true, folder: folder);
        var item = Assert.Single(h.Workspace.Items);
        Assert.Equal("Minimized", item.State);
        var card = Assert.Single(h.Cards.Created);
        Assert.Equal(item.Id, h.Workspace.LiveCardItem(card.Handle));
        Assert.Equal(card.Handle, h.Workspace.GetRepresentedWindows()[App]);
        Assert.True(h.Workspace.IsOpen(h.Workspace.Document.Items.Single()));
        Assert.Empty(h.Launcher.Opened);
        Assert.Empty(h.Actions.Focused);
        Assert.Equal(WindowState.Minimized, h.Canvas.Windows[App].State);
        Assert.Equal(WindowState.Normal, h.Canvas.Windows[card.Handle].State);
        h.Workspace.SaveNow();
        Assert.Single(h.Workspace.Document.Items);
        Assert.Equal(WindowState.Minimized, h.Workspace.Document.Items.Single().Bounds.State);
    }

    [Fact]
    public void MatchingSavedNormalSlotDoesNotWakeAlreadyMinimizedApp()
    {
        var item = new WorkspaceItem { Title = "Project", ExecutablePath = @"C:\Apps\Editor.exe",
            Bounds = new WorldRect { X = 4000, Y = 200, W = 800, H = 600 } };
        using var h = new WorkspaceHarness(new WorkspaceDocument { Items = [item] });
        h.Api.AddWindow(App, 100, 200, 800, 600, pid: 7, title: "Project", style: (int)WINDOW_STYLE.WS_MINIMIZE);
        h.Workspace.Start();
        Assert.All(h.Api.NativeStateCalls, call => Assert.Equal(WindowState.Minimized, call.state));
        Assert.Equal(4000, h.Canvas.Windows[App].X);
        Assert.Equal(WindowState.Minimized, item.Bounds.State);
        Assert.Single(h.Cards.Created);
        Assert.Empty(h.Actions.Focused);
    }

    [Fact]
    public void RuntimeMinimizeCreatesCardAndTaskbarRestoreRemovesIt()
    {
        using var h = Start();
        Assert.Empty(h.Cards.Created);
        h.Workspace.Tick(); // Set the regular two-second capture deadline.
        h.Api.Windows[App].Style = (int)WINDOW_STYLE.WS_MINIMIZE;
        h.Input.RaiseWindowMinimized(App);
        h.Clock.Now = 100;
        h.Workspace.Tick(); // State changes bypass that deadline.
        var card = Assert.Single(h.Cards.Created);
        Assert.Equal("Minimized", card.Item.State);
        h.Api.Windows[App].Style = 0;
        h.Input.RaiseWindowRestored(App);
        h.Clock.Now = 200; h.Workspace.Tick();
        Assert.True(card.Disposed);
        Assert.Empty(h.Workspace.GetRepresentedWindows());
        Assert.Equal("Open", h.Workspace.Items.Single().State);
        Assert.Empty(h.Launcher.Opened);
    }

    [Fact]
    public void ExplicitCardActivationRestoresExistingBindingWithoutLaunchingDuplicate()
    {
        using var h = Start(minimized: true);
        var card = h.Cards.Created.Single();
        h.Workspace.Open(h.Workspace.LiveCardItem(card.Handle)!.Value);
        Assert.Equal((App, true), Assert.Single(h.Actions.Focused));
        Assert.Empty(h.Launcher.Opened);
        Assert.True(card.Disposed);
    }

    [Fact]
    public void ZoomedOutCardsReplaceRunningWindowsAndDisappearOnDetailedView()
    {
        using var h = Start();
        Assert.True(h.Workspace.SetOverviewCardMode(true));
        var card = Assert.Single(h.Cards.Created);
        Assert.Equal("Open", card.Item.State);
        var windows = new OverviewWindowList(h.Canvas, h.Api);
        windows.Refresh(representedWindows: h.Workspace.GetRepresentedWindows());
        Assert.Equal(card.Handle, Assert.Single(windows.Windows).HWnd);
        Assert.Empty(h.Actions.Focused); Assert.Empty(h.Launcher.Opened);
        Assert.True(h.Workspace.SetOverviewCardMode(false));
        Assert.True(card.Disposed);
        windows.Refresh(representedWindows: h.Workspace.GetRepresentedWindows());
        Assert.Equal(App, Assert.Single(windows.Windows).HWnd);
    }

    [Fact]
    public void MovingMinimizedCardUpdatesSavedAndLiveGeometryWithoutRestoringIt()
    {
        using var h = Start(minimized: true);
        var card = h.Cards.Created.Single();
        h.Canvas.SetWindow(card.Handle, 4000, -300, 800, 600); // Overview drag changes logical geometry.
        h.Workspace.SaveNow();
        var item = h.Workspace.Document.Items.Single();
        Assert.Equal(4000, item.Bounds.X); Assert.Equal(-300, item.Bounds.Y);
        Assert.Equal(4000, h.Canvas.Windows[App].X);
        Assert.Equal(WindowState.Minimized, item.Bounds.State);
        Assert.Equal(WindowState.Minimized, h.Canvas.Windows[App].State);
        Assert.Empty(h.Actions.Focused);
    }

    [Fact]
    public void MinimizedWindowClosingKeepsSameSavedCardAndLosesRestoreBinding()
    {
        using var h = Start(minimized: true);
        var card = h.Cards.Created.Single();
        h.Api.Windows.Remove(App); h.Input.RaiseWindowDestroyed(App);
        h.Clock.Now = 2000; h.Workspace.Tick();
        Assert.Null(h.Workspace.LiveCardItem(card.Handle));
        Assert.Equal("Placeholder", card.Item.State);
        Assert.Single(h.Workspace.Document.Items);
        Assert.Single(h.Cards.Created);
        Assert.False(card.Disposed);
    }

    [Fact]
    public void StaleCardCannotRestoreRecycledWindowHandle()
    {
        using var h = Start(minimized: true);
        var card = h.Cards.Created.Single();
        h.Identity.StartedAt = 2;
        Assert.Null(h.Workspace.LiveCardItem(card.Handle));
        Assert.Empty(h.Workspace.GetRepresentedWindows());
        Assert.Empty(h.Actions.Focused);
    }
}
