using CanvasDesktop;

namespace CanvasDesktop.Tests;

// No STA, COM, real processes, dialogs, or real window movement required.
internal sealed class WorkspaceHarness : IDisposable
{
    public Canvas Canvas { get; } = new();
    public FakeWindowApi Api { get; }
    public FakeClock Clock { get; } = new();
    public FakeVirtualDesktops Desktops { get; } = new();
    public MemoryWorkspaceStore Store { get; } = new();
    public TestWorkspaceIdentity Identity { get; } = new();
    public TestWorkspaceLauncher Launcher { get; } = new();
    public TestWorkspaceActions Actions { get; } = new();
    public TestPlaceholderFactory Cards { get; }
    public WindowManager Wm { get; }
    public DesktopStateCache Cache { get; }
    public WorkspaceService Workspace { get; }

    public WorkspaceHarness(WorkspaceDocument? document = null, IWorkspaceStore? store = null, FakeWindowApi? api = null,
        FakeAppConfig? config = null)
    {
        Api = api ?? new();
        if (document != null) Store.Document = document;
        Cards = new(Api);
        Wm = new(Canvas, Api, config ?? new FakeAppConfig(), new FakeInputRouter(), Clock, Desktops,
            inspectProcess: Identity.InspectProcess);
        Cache = new(Canvas, Wm, new FakeOverviewController(), Desktops);
        Workspace = new(Canvas, Wm, Api, Desktops, Cache, store ?? Store, Cards, Actions, Launcher, Identity, Clock);
    }

    public void Dispose() { Workspace.Dispose(); Cache.Dispose(); Wm.Dispose(); }
}

internal sealed class MemoryWorkspaceStore : IWorkspaceStore
{
    public WorkspaceDocument Document = new();
    public string? Warning;
    public Exception? LoadError, SaveError;
    public int Saves;
    public WorkspaceDocument Load(out string? warning)
    {
        warning = Warning;
        if (LoadError != null) throw LoadError;
        return Document;
    }
    public void Save(WorkspaceDocument document)
    {
        Saves++;
        if (SaveError != null) throw SaveError;
        Document = document;
    }
}

internal sealed class TestWorkspaceIdentity : IWorkspaceIdentity
{
    public string Exe = @"C:\Apps\Editor.exe";
    public Dictionary<IntPtr, string> Folders = new();
    public Exception? Error;
    public long StartedAt = 1;
    public bool Unavailable;
    public Dictionary<uint, string> Executables = new();
    public Dictionary<IntPtr, string> ReadExplorerFolders()
    {
        if (Error != null) throw Error;
        return Folders;
    }
    public WorkspaceProcessIdentity? InspectProcess(uint pid)
        => Unavailable || pid == 0 ? null : new(pid, StartedAt, Executables.GetValueOrDefault(pid, Exe));
}

internal sealed class TestWorkspaceLauncher : IWorkspaceLauncher
{
    public List<Guid> Opened = new();
    public Exception? Error;
    public void Open(WorkspaceItem item)
    {
        if (Error != null) throw Error;
        Opened.Add(item.Id);
    }
}

internal sealed class TestWorkspaceActions : IWorkspaceWindowActions
{
    public ScreenRect Viewport => new(0, 0, 640, 480);
    public List<(IntPtr Handle, bool Restore)> Focused = new();
    public List<IntPtr> Closed = new();
    public void Focus(IntPtr handle, bool restore) => Focused.Add((handle, restore));
    public void RequestClose(IntPtr handle) => Closed.Add(handle);
}

internal sealed class TestPlaceholderFactory : IWorkspacePlaceholderFactory
{
    private readonly FakeWindowApi _api;
    public List<TestPlaceholder> Created = new();
    public TestPlaceholderFactory(FakeWindowApi api) { _api = api; }
    public IWorkspacePlaceholder Create(IWorkspaceCommands workspace, WorkspaceItemView item)
    {
        var card = new TestPlaceholder(_api, (IntPtr)(-1000 - Created.Count), item);
        Created.Add(card);
        return card;
    }
}

internal sealed class TestPlaceholder : IWorkspacePlaceholder
{
    private readonly FakeWindowApi _api;
    public IntPtr Handle { get; }
    public WorkspaceItemView Item { get; private set; }
    public bool Disposed { get; private set; }
    public WindowRect ShownAt;
    public event Action? PlacementChanged;
    public TestPlaceholder(FakeWindowApi api, IntPtr handle, WorkspaceItemView item)
    { _api = api; Handle = handle; Item = item; }
    public void UpdateItem(WorkspaceItemView item) => Item = item;
    public void ShowAt(WindowRect rect)
    {
        ShownAt = rect;
        _api.AddWindow(Handle, rect.X, rect.Y, rect.W, rect.H, pid: (uint)Environment.ProcessId, manageable: false);
    }
    public void MoveTo(int x, int y)
    {
        _api.Windows[Handle].X = x; _api.Windows[Handle].Y = y;
        PlacementChanged?.Invoke();
    }
    public void Dispose()
    {
        Disposed = true; PlacementChanged = null;
        if (_api.Windows.TryGetValue(Handle, out var window)) window.Visible = false;
    }
}
