using CanvasDesktop;
using Windows.Win32.UI.WindowsAndMessaging;

namespace CanvasDesktop.Tests;

public class WorkspaceBoundaryTests
{
    private static WorkspaceItem Item(string title = "Editor") => new()
    {
        Title = title, ExecutablePath = @"C:\Apps\Editor.exe",
        Bounds = new WorldRect { X = 100, Y = 200, W = 800, H = 600 }
    };
    private static WorkspaceDocument Document(params WorkspaceItem[] items)
        => new() { Items = items.ToList() };

    [Fact]
    public void UnchangedPersistenceDoesNotRewriteStore()
    {
        var store = new MemoryWorkspaceStore { Document = Document(Item()) };
        var persistence = new WorkspacePersistence(store);
        Assert.True(persistence.Save());
        Assert.True(persistence.Save());
        Assert.Equal(1, store.Saves);
        store.Document.Items[0].Title = "Changed";
        Assert.True(persistence.Save());
        Assert.Equal(2, store.Saves);
    }

    [Theory]
    [InlineData("position")]
    [InlineData("state")]
    [InlineData("pinned")]
    [InlineData("zorder")]
    [InlineData("camera")]
    public void PersistenceDetectsGeometryFieldsAndCameraChanges(string change)
    {
        var store = new MemoryWorkspaceStore { Document = Document(Item()) };
        var persistence = new WorkspacePersistence(store);
        persistence.Save();
        var bounds = store.Document.Items[0].Bounds;
        switch (change)
        {
            case "position": bounds.X = -4321; break;
            case "state": bounds.State = WindowState.Maximized; break;
            case "pinned": bounds.PinnedToScreen = true; break;
            case "zorder": bounds.ZOrder = 9; break;
            case "camera": store.Document.Cameras[Guid.Empty] = new() { X = 300 }; break;
        }
        store.Document.Items[0].Bounds = bounds;
        Assert.True(persistence.Save());
        Assert.Equal(2, store.Saves);
    }

    [Fact]
    public void FailedLoadDisablesAllWritesToExistingStore()
    {
        var original = Document(Item());
        var store = new MemoryWorkspaceStore { Document = original, LoadError = new IOException("Unreadable") };
        var persistence = new WorkspacePersistence(store);
        persistence.Document.Items.Add(Item("In-memory only"));
        Assert.False(persistence.Save());
        Assert.False(persistence.Save());
        Assert.Equal(0, store.Saves);
        Assert.Same(original, store.Document);
        Assert.Equal("Unreadable", persistence.LastError);
    }

    [Fact]
    public void FailedSaveRetriesSameSnapshotAndClearsErrorAfterSuccess()
    {
        var store = new MemoryWorkspaceStore { SaveError = new IOException("Disk full") };
        var persistence = new WorkspacePersistence(store);
        Assert.False(persistence.Save());
        Assert.Contains("Disk full", persistence.LastError);
        store.SaveError = null;
        Assert.True(persistence.Save());
        Assert.True(persistence.Save());
        Assert.Equal(2, store.Saves);
        Assert.Null(persistence.LastError);
    }

    [Fact]
    public void RecoveredStoreWarningIsAvailableToPresentation()
    {
        var store = new MemoryWorkspaceStore { Warning = "Recovered backup" };
        var persistence = new WorkspacePersistence(store);
        Assert.Equal("Recovered backup", persistence.LoadWarning);
        Assert.True(persistence.Save());
        Assert.Equal("Recovered backup", persistence.LoadWarning);
    }

    [Fact]
    public void FileTargetIsMatchedOnlyDuringExplicitLaunch()
    {
        var item = Item(); item.Target = @"C:\Work\notes.txt";
        var doc = Document(item);
        Assert.Null(WorkspaceMatcher.Match(doc.Items, Guid.Empty, item.ExecutablePath, item.Title, "", Array.Empty<Guid>()));
        Assert.Same(item, WorkspaceMatcher.Match(doc.Items, Guid.Empty, item.ExecutablePath, "New caption", "", Array.Empty<Guid>(), item.Id));
        Assert.Null(WorkspaceMatcher.Match(doc.Items, Guid.Empty, item.ExecutablePath, item.Title, "", new[] { item.Id }, item.Id));
    }

    [Theory]
    [InlineData("desktop")]
    [InlineData("executable")]
    [InlineData("folder")]
    public void ExplicitLaunchFallbackCannotCrossIdentityBoundaries(string boundary)
    {
        var item = Item(); item.Target = @"C:\Work";
        var desktop = Guid.Empty; var exe = item.ExecutablePath;
        if (boundary == "desktop") desktop = Guid.NewGuid();
        if (boundary == "executable") exe = @"C:\Other\Editor.exe";
        if (boundary == "folder") item.IsFolder = true;
        Assert.Null(WorkspaceMatcher.Match(new[] { item }, desktop, exe, "Welcome", "", Array.Empty<Guid>(), item.Id));
    }

    [Fact]
    public void ViewsAreDetachedFromMutablePersistenceModel()
    {
        var item = Item();
        using var rig = new WorkspaceHarness(Document(item));
        var view = Assert.Single(rig.Workspace.Items);
        var editedView = view with { Title = "Not a model mutation", OpenOnStartup = true };
        var bounds = editedView.Bounds; bounds.X = 12345;
        Assert.Equal("Editor", item.Title);
        Assert.False(item.OpenOnStartup);
        Assert.Equal(100, item.Bounds.X);
        rig.Workspace.SetAutoOpen(item.Id, true);
        Assert.True(item.OpenOnStartup);
        Assert.False(view.OpenOnStartup); // Even a retained row stays a snapshot.
    }

    [Fact]
    public void AutomaticMinimizationExclusionIsSavedAndExposedInTheView()
    {
        var item = Item();
        using var rig = new WorkspaceHarness(Document(item));
        rig.Workspace.SetAutomaticMinimizationExcluded(item.Id, true);
        Assert.True(Assert.Single(rig.Workspace.Items).ExcludeFromAutomaticMinimization);
        Assert.True(Assert.Single(rig.Store.Document.Items).ExcludeFromAutomaticMinimization);
        Assert.Equal(1, rig.Store.Saves);
        var json = System.Text.Json.JsonSerializer.Serialize(rig.Store.Document);
        Assert.True(System.Text.Json.JsonSerializer.Deserialize<WorkspaceDocument>(json)!.Items[0].ExcludeFromAutomaticMinimization);
        rig.Workspace.SetAutomaticMinimizationExcluded(item.Id, false);
        Assert.False(Assert.Single(rig.Workspace.Items).ExcludeFromAutomaticMinimization);
        Assert.Equal(2, rig.Store.Saves);
    }

    [Fact]
    public void ExclusionFollowsTheBoundWindowAndRejectsRecycledIdentity()
    {
        var item = Item();
        using var rig = new WorkspaceHarness(Document(item));
        var handle = (IntPtr)42;
        rig.Api.AddWindow(handle, 10, 20, 800, 600, title: item.Title);
        rig.Workspace.Start();
        Assert.True(rig.Workspace.IsOpen(item));
        rig.Workspace.SetAutomaticMinimizationExcluded(item.Id, true);
        var pid = rig.Api.GetWindowProcessId(handle);
        var identity = new WorkspaceWindowIdentity(handle, pid, rig.Identity.StartedAt);
        Assert.True(rig.Wm.IsAutomaticMinimizationExcluded!(new(identity, Guid.NewGuid())));
        Assert.False(rig.Wm.IsAutomaticMinimizationExcluded!(new(identity with { ProcessStartedAtUtcTicks = rig.Identity.StartedAt + 1 }, Guid.NewGuid())));
        rig.Workspace.SetAutomaticMinimizationExcluded(item.Id, false);
        Assert.False(rig.Wm.IsAutomaticMinimizationExcluded!(new(identity, Guid.NewGuid())));
    }

    [Fact]
    public void StaleUiCommandsCannotLaunchOrResurrectForgottenItems()
    {
        var item = Item();
        using var rig = new WorkspaceHarness(Document(item));
        rig.Workspace.Start();
        rig.Workspace.Forget(item.Id);
        var saves = rig.Store.Saves;
        rig.Workspace.Open(item.Id);
        rig.Workspace.FocusItem(item.Id);
        rig.Workspace.CloseToPlaceholder(item.Id);
        rig.Workspace.SetAutoOpen(item.Id, true);
        rig.Workspace.SetTarget(item.Id, @"C:\Work", true);
        rig.Workspace.Forget(item.Id);
        Assert.Empty(rig.Launcher.Opened);
        Assert.Empty(rig.Workspace.Items);
        Assert.Equal(saves, rig.Store.Saves);
        Assert.True(Assert.Single(rig.Cards.Created).Disposed);
    }

    [Fact]
    public void LaunchFailureIsAnEventAndStatusNotABlockingDialog()
    {
        var item = Item();
        using var rig = new WorkspaceHarness(Document(item));
        rig.Launcher.Error = new IOException("Unavailable");
        string? message = null;
        rig.Workspace.LaunchFailed += value => message = value;
        rig.Workspace.Start(); rig.Workspace.Open(item.Id);
        Assert.Contains("Unavailable", message);
        Assert.Equal(message, rig.Workspace.Status);
        Assert.Equal("Placeholder", Assert.Single(rig.Workspace.Items).State);
        Assert.False(Assert.Single(rig.Cards.Created).Disposed);
    }

    [Fact]
    public void LaunchTimeoutKeepsPlaceholderAndAllowsRetryWithoutWallClockWait()
    {
        var item = Item();
        using var rig = new WorkspaceHarness(Document(item));
        rig.Workspace.Start(); rig.Workspace.Open(item.Id);
        Assert.Equal("Opening…", Assert.Single(rig.Workspace.Items).State);
        rig.Clock.Advance(30001); rig.Workspace.Tick();
        Assert.Equal("Placeholder", Assert.Single(rig.Workspace.Items).State);
        Assert.Contains("No matching window", rig.Workspace.Status);
        Assert.False(Assert.Single(rig.Cards.Created).Disposed);
        rig.Workspace.Open(item.Id);
        Assert.Equal(new[] { item.Id, item.Id }, rig.Launcher.Opened);
    }

    [Fact]
    public void SameExecutableLaunchesAreSerializedUntilFirstWindowIsBound()
    {
        var first = Item("First"); var second = Item("Second");
        using var rig = new WorkspaceHarness(Document(first, second));
        rig.Workspace.Start(); rig.Workspace.Open(first.Id); rig.Workspace.Open(second.Id);
        Assert.Equal(new[] { first.Id }, rig.Launcher.Opened);
        Assert.Equal("Queued", rig.Workspace.Items.Single(i => i.Id == second.Id).State);
        rig.Api.AddWindow((IntPtr)42, 10, 20, 500, 400, title: first.Title);
        rig.Clock.Advance(2000); rig.Workspace.Tick();
        Assert.True(rig.Workspace.IsOpen(first));
        Assert.Equal(new[] { first.Id, second.Id }, rig.Launcher.Opened);
        Assert.True(rig.Cards.Created[0].Disposed);
    }

    [Fact]
    public void RepeatedStartDoesNotDuplicateCardsOrStartupLaunches()
    {
        var item = Item(); item.OpenOnStartup = true;
        using var rig = new WorkspaceHarness(Document(item));
        rig.Workspace.Start(); rig.Workspace.Start();
        Assert.Single(rig.Cards.Created);
        Assert.Equal(new[] { item.Id }, rig.Launcher.Opened);
    }

    [Fact]
    public void DisposeReleasesPlaceholdersAndRejectsLaterCommands()
    {
        var item = Item();
        using var rig = new WorkspaceHarness(Document(item));
        rig.Workspace.Start(); rig.Workspace.Dispose();
        int saves = rig.Store.Saves;
        rig.Workspace.Start(); rig.Workspace.Tick(); rig.Workspace.Dispose();
        rig.Workspace.Open(item.Id); rig.Workspace.SetAutoOpen(item.Id, true);
        Assert.False(rig.Workspace.SaveNow());
        Assert.Empty(rig.Canvas.Windows);
        Assert.True(Assert.Single(rig.Cards.Created).Disposed);
        Assert.Empty(rig.Launcher.Opened);
        Assert.Equal(saves, rig.Store.Saves);
    }

    [Fact]
    public void CloseRequestSavesLayoutButLeavesCancellationToApplication()
    {
        var item = Item();
        using var rig = new WorkspaceHarness(Document(item));
        var handle = (IntPtr)42;
        rig.Api.AddWindow(handle, 10, 20, 800, 600, title: item.Title);
        rig.Workspace.Start();
        rig.Canvas.SetWindow(handle, -3000, 1500, 800, 600);
        rig.Workspace.CloseToPlaceholder(item.Id);
        Assert.Equal(new[] { handle }, rig.Actions.Closed);
        Assert.Equal(-3000, rig.Store.Document.Items[0].Bounds.X);
        Assert.True(rig.Workspace.IsOpen(item));
        Assert.True(rig.Api.Windows[handle].Visible);
    }

    [Fact]
    public void FocusUsesInjectedViewportAndNativeActionBoundary()
    {
        var item = Item();
        using var rig = new WorkspaceHarness(Document(item));
        var handle = (IntPtr)42;
        rig.Api.AddWindow(handle, 10, 20, 800, 600, style: (int)WINDOW_STYLE.WS_MINIMIZE, title: item.Title);
        rig.Workspace.Start(); rig.Workspace.FocusItem(item.Id);
        Assert.Equal((handle, true), Assert.Single(rig.Actions.Focused));
        Assert.Equal(item.Bounds.X + item.Bounds.W / 2 - 320, rig.Canvas.CamX);
        Assert.Equal(item.Bounds.Y + item.Bounds.H / 2 - 240, rig.Canvas.CamY);
    }

    [Fact]
    public void ExplorerIdentityCanArriveLaterWithoutARealComLookup()
    {
        using var rig = new WorkspaceHarness();
        rig.Identity.Exe = @"C:\Windows\explorer.exe";
        rig.Api.AddWindow((IntPtr)42, 10, 20, 800, 600, title: "Projects");
        rig.Workspace.Start();
        Assert.Empty(rig.Workspace.Items);
        rig.Identity.Folders[(IntPtr)42] = @"C:\Work\Projects";
        rig.Clock.Advance(2000); rig.Workspace.Tick();
        var item = Assert.Single(rig.Workspace.Document.Items);
        Assert.True(item.IsFolder);
        Assert.Equal(@"C:\Work\Projects", item.Target);
    }

    [Fact]
    public void SetTargetDistinguishesExplorerFoldersFromEditorProjectArguments()
    {
        var editor = Item(); var explorer = Item("Explorer"); explorer.ExecutablePath = @"C:\Windows\explorer.exe";
        using var rig = new WorkspaceHarness(Document(editor, explorer));
        rig.Workspace.SetTarget(editor.Id, @"C:\Work", true);
        rig.Workspace.SetTarget(explorer.Id, @"C:\Work", true);
        Assert.False(editor.IsFolder); Assert.True(explorer.IsFolder);
        rig.Workspace.SetTarget(explorer.Id, @"C:\Work\file.txt", false);
        Assert.False(explorer.IsFolder);
    }

    [Fact]
    public void PlaceholderMovementSavesGeometryWithoutLosingNativeState()
    {
        var item = Item(); var bounds = item.Bounds;
        bounds.State = WindowState.Maximized; bounds.PinnedToScreen = true; item.Bounds = bounds;
        using var rig = new WorkspaceHarness(Document(item));
        rig.Workspace.Start();
        var card = Assert.Single(rig.Cards.Created);
        Assert.Equal(new WindowRect(100, 200, 800, 600), card.ShownAt);
        card.MoveTo(250, 350); rig.Workspace.SaveNow();
        Assert.Equal(250, item.Bounds.X); Assert.Equal(350, item.Bounds.Y);
        Assert.Equal(WindowState.Maximized, item.Bounds.State);
        Assert.True(item.Bounds.PinnedToScreen);
        Assert.Equal(WindowState.Normal, rig.Canvas.Windows[card.Handle].State);
        Assert.False(rig.Canvas.Windows[card.Handle].PinnedToScreen);
    }
}
