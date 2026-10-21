using System;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using CanvasDesktop;
using Xunit;

namespace CanvasDesktop.Tests;

public class WorkspaceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "canvas-workspace-tests-" + Guid.NewGuid().ToString("N"));
    private string SavePath => Path.Combine(_dir, "workspace.json");
    private static WorkspaceItem Item(string title = "Editor", string target = "") => new()
    {
        Title = title, ExecutablePath = @"C:\Apps\Editor.exe", Target = target,
        Bounds = new WorldRect { X = -3400, Y = 2100, W = 900, H = 650, ZOrder = 4 }
    };

    [Fact]
    public void RoundTrip_PreservesGeometryCameraAndLaunchPolicy_WithoutHandles()
    {
        var doc = new WorkspaceDocument();
        var item = Item(); item.OpenOnStartup = true;
        var bounds = item.Bounds; bounds.State = WindowState.Maximized; item.Bounds = bounds;
        doc.Items.Add(item);
        doc.Cameras[Guid.Empty] = new() { X = -2000, Y = 300, Zoom = 0.8 };
        var store = new WorkspaceStore(SavePath);
        store.Save(doc);
        var loaded = store.Load(out var warning);
        Assert.Null(warning);
        Assert.Equal(item.Id, loaded.Items[0].Id);
        Assert.Equal(-3400, loaded.Items[0].Bounds.X);
        Assert.Equal(650, loaded.Items[0].Bounds.H);
        Assert.Equal(WindowState.Maximized, loaded.Items[0].Bounds.State);
        Assert.True(loaded.Items[0].OpenOnStartup);
        Assert.Equal(0.8, loaded.Cameras[Guid.Empty].Zoom);
        Assert.DoesNotContain("HWnd", File.ReadAllText(SavePath));
    }

    [Fact]
    public void DamagedPrimary_RecoversBackupAndPreservesCorruptBytes()
    {
        var store = new WorkspaceStore(SavePath);
        var doc = new WorkspaceDocument(); doc.Items.Add(Item("Original")); store.Save(doc);
        doc.Items[0].Title = "New"; store.Save(doc);
        File.WriteAllText(SavePath, "{broken");
        var recovered = store.Load(out var warning);
        Assert.NotNull(warning);
        Assert.Equal("Original", recovered.Items[0].Title);
        Assert.Equal("{broken", File.ReadAllText(Directory.GetFiles(_dir, "*.corrupt-*").Single()));
        store.Save(recovered);
        Assert.Equal("Original", store.Load(out _).Items[0].Title);
    }

    [Fact]
    public void UnreadableFiles_DoNotSilentlyBecomeEmptyWorkspace()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SavePath, "{broken"); File.WriteAllText(SavePath + ".bak", "bad");
        Assert.Throws<IOException>(() => new WorkspaceStore(SavePath).Load(out _));
        Assert.Equal("{broken", File.ReadAllText(SavePath));
    }

    [Fact]
    public void RejectsNonFiniteGeometryAndDuplicateIds()
    {
        var doc = new WorkspaceDocument(); var item = Item(); doc.Items.Add(item);
        var bounds = item.Bounds; bounds.X = double.NaN; item.Bounds = bounds;
        Assert.Throws<InvalidDataException>(() => new WorkspaceStore(SavePath).Save(doc));
        bounds.X = 0; item.Bounds = bounds; doc.Items.Add(item);
        Assert.Throws<InvalidDataException>(() => new WorkspaceStore(SavePath).Save(doc));
    }

    [Fact]
    public void FolderIdentityBeatsIdenticalCaptionsAndDoesNotReuseBoundSlot()
    {
        var doc = new WorkspaceDocument();
        var first = Item("Projects", @"C:\Work\Projects"); var second = Item("Projects", @"D:\Work\Projects");
        doc.Items.AddRange(new[] { first, second });
        Assert.Same(second, WorkspaceMatcher.Match(doc.Items, Guid.Empty, first.ExecutablePath, "Projects", @"d:\work\projects", Array.Empty<Guid>()));
        Assert.Null(WorkspaceMatcher.Match(doc.Items, Guid.Empty, first.ExecutablePath, "Projects", second.Target, new[] { second.Id }));
        Assert.Null(WorkspaceMatcher.Match(doc.Items, Guid.NewGuid(), first.ExecutablePath, "Projects", second.Target, Array.Empty<Guid>()));
    }

    [Fact]
    public void AmbiguousAppsRequireTitleMatchOrExplicitLaunch()
    {
        var doc = new WorkspaceDocument(); var a = Item("Project A"); var b = Item("Project B");
        doc.Items.AddRange(new[] { a, b });
        Assert.Same(b, WorkspaceMatcher.Match(doc.Items, Guid.Empty, a.ExecutablePath, "Project B", "", Array.Empty<Guid>()));
        Assert.Null(WorkspaceMatcher.Match(doc.Items, Guid.Empty, a.ExecutablePath, "Welcome", "", Array.Empty<Guid>()));
        Assert.Same(a, WorkspaceMatcher.Match(doc.Items, Guid.Empty, a.ExecutablePath, "Welcome", "", Array.Empty<Guid>(), a.Id));
        Assert.Null(WorkspaceMatcher.Match(doc.Items, Guid.Empty, @"D:\Different\Editor.exe", "Project A", "", Array.Empty<Guid>()));
    }

    [Fact]
    public void WindowSearchIncludesEnrolledPlaceholdersButNotOtherOwnWindows()
    {
        var canvas = new Canvas(); var api = new FakeWindowApi();
        api.AddWindow((IntPtr)81, 20, 30, 800, 600, pid: (uint)Environment.ProcessId, title: "Saved: Project notes");
        api.AddWindow((IntPtr)82, 20, 30, 800, 600, pid: (uint)Environment.ProcessId, title: "Project notes manager");
        canvas.SetWindow((IntPtr)81, 20, 30, 800, 600);
        var result = Assert.Single(new WindowSearchService(canvas, api).Search("Project notes"));
        Assert.Equal((IntPtr)81, result.HWnd);
    }

    [Fact]
    public void BlockedExplorerLookupDoesNotBlockCanvasOrAccumulateWorkers()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int calls = 0;
        var identity = new WorkspaceIdentity(() =>
        {
            Interlocked.Increment(ref calls); entered.Set();
            release.Wait(TimeSpan.FromSeconds(10));
            return new() { [(IntPtr)42] = @"C:\Projects" };
        });
        try
        {
            Assert.Empty(identity.ReadExplorerFolders());
            Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
            var timer = System.Diagnostics.Stopwatch.StartNew();
            for (int n = 0; n < 20; n++) Assert.Empty(identity.ReadExplorerFolders());
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(1));
            Assert.Equal(1, Volatile.Read(ref calls));
        }
        finally { release.Set(); }
        Assert.True(SpinWait.SpinUntil(() => identity.ReadExplorerFolders().Count == 1, TimeSpan.FromSeconds(3)));
        Assert.Equal(@"C:\Projects", identity.ReadExplorerFolders()[(IntPtr)42]);
    }

    [Fact]
    public void ClosedWindowBecomesCard_ThenNewHandleRestoresItsSavedPosition()
    {
        OnSta(() =>
        {
            var canvas = new Canvas(); var api = new FakeWindowApi(); var input = new FakeInputRouter();
            var vds = new FakeVirtualDesktops();
            using var wm = new WindowManager(canvas, api, new FakeAppConfig(), input, new FakeClock(), vds);
            var cache = new DesktopStateCache(canvas, wm, new FakeOverviewController(), vds);
            var old = (IntPtr)17;
            api.AddWindow(old, 400, 260, 800, 600, pid: (uint)Environment.ProcessId, title: "Test editor");
            canvas.SetWindow(old, 400, 260, 800, 600);
            using (var workspace = WorkspaceComposition.Create(canvas, wm, api, vds, cache, SavePath))
            {
                workspace.Start();
                var item = Assert.Single(workspace.Document.Items);
                Assert.True(workspace.IsOpen(item));
                canvas.SetWindow(old, -2400, 1500, 920, 680);
                api.Windows[old].Visible = false;
                wm.RemoveWindow(old);
                workspace.SaveNow();
                Assert.False(workspace.IsOpen(item));
                Assert.Single(canvas.Windows); // a native placeholder, not the closed HWND
                Assert.False(canvas.HasWindow(old));
                Assert.Equal(-2400, item.Bounds.X);
            }
            Assert.Empty(canvas.Windows); // disposing removes cards but keeps saved entries
            var reopened = (IntPtr)29;
            api.AddWindow(reopened, 10, 10, 600, 400, pid: (uint)Environment.ProcessId, title: "Test editor");
            canvas.SetWindow(reopened, 10, 10, 600, 400);
            using var next = WorkspaceComposition.Create(canvas, wm, api, vds, cache, SavePath);
            next.Start();
            Assert.Single(next.Document.Items);
            Assert.True(next.IsOpen(next.Document.Items[0]));
            Assert.Equal(-2400, canvas.Windows[reopened].X);
            Assert.Equal(920, canvas.Windows[reopened].W);
            Assert.Contains(api.SetPositionCalls, c => c.hWnd == reopened && c.x == -2400 && c.w == 920);
        });
    }

    [Fact]
    public void ForgetLiveWindowDoesNotImmediatelyRecaptureIt()
    {
        OnSta(() =>
        {
            var canvas = new Canvas(); var api = new FakeWindowApi(); var vds = new FakeVirtualDesktops();
            using var wm = new WindowManager(canvas, api, new FakeAppConfig(), new FakeInputRouter());
            var cache = new DesktopStateCache(canvas, wm, new FakeOverviewController(), vds);
            api.AddWindow((IntPtr)31, 20, 30, 800, 600, pid: (uint)Environment.ProcessId, title: "Forgotten editor");
            canvas.SetWindow((IntPtr)31, 20, 30, 800, 600);
            using var workspace = WorkspaceComposition.Create(canvas, wm, api, vds, cache, SavePath);
            workspace.Start(); workspace.Forget(Assert.Single(workspace.Document.Items).Id);
            workspace.SaveNow();
            Assert.Empty(workspace.Document.Items);
            Assert.True(canvas.HasWindow((IntPtr)31)); // forgetting never closes the real app
        });
    }

    [Fact]
    public void CardsFollowDesktopSwitchesWithoutEnteringTheDesktopCache()
    {
        OnSta(() =>
        {
            var firstDesktop = Guid.NewGuid(); var secondDesktop = Guid.NewGuid();
            var doc = new WorkspaceDocument();
            var first = Item("First desktop"); first.DesktopId = firstDesktop;
            var second = Item("Second desktop"); second.DesktopId = secondDesktop;
            doc.Items.AddRange(new[] { first, second });
            new WorkspaceStore(SavePath).Save(doc);
            var canvas = new Canvas(); var api = new FakeWindowApi();
            var vds = new FakeVirtualDesktops { CurrentDesktopId = firstDesktop };
            using var wm = new WindowManager(canvas, api, new FakeAppConfig(), new FakeInputRouter());
            var cache = new DesktopStateCache(canvas, wm, new FakeOverviewController(), vds);
            using var workspace = WorkspaceComposition.Create(canvas, wm, api, vds, cache, SavePath);
            workspace.Start();
            var firstHandle = Assert.Single(canvas.Windows).Key;
            canvas.SetWindow(firstHandle, -5000, 2300, 900, 650);
            canvas.SetCamera(-4700, 2000);
            vds.SwitchTo(secondDesktop);
            Assert.Single(canvas.Windows);
            Assert.Equal(second.Bounds.X, canvas.Windows.First().Value.X);
            vds.SwitchTo(firstDesktop);
            Assert.Single(canvas.Windows); // no stale card resurrected by CanvasState
            Assert.Equal(-5000, canvas.Windows.First().Value.X);
            Assert.Equal(-4700, canvas.CamX);
            Assert.Equal(2, workspace.Document.Items.Count);
        });
    }

    [Fact]
    public void PlaceholderRendersWithAnOpenAction()
    {
        OnSta(() =>
        {
            var canvas = new Canvas(); var api = new FakeWindowApi(); var vds = new FakeVirtualDesktops();
            using var wm = new WindowManager(canvas, api, new FakeAppConfig(), new FakeInputRouter());
            var cache = new DesktopStateCache(canvas, wm, new FakeOverviewController(), vds);
            using var workspace = WorkspaceComposition.Create(canvas, wm, api, vds, cache, SavePath);
            var item = Item("Project notes"); item.Target = @"G:\Coding\Projects\notes.txt";
            using var form = new PlaceholderForm(workspace, new WorkspaceItemView(item.Id, item.Title, item.ExecutablePath,
                item.Target, item.OpenOnStartup, item.Bounds, "Placeholder")) { Size = new System.Drawing.Size(800, 550) };
            form.Show(); form.PerformLayout();
            using var bitmap = new System.Drawing.Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, form.Width, form.Height));
            bitmap.Save(Path.Combine(AppContext.BaseDirectory, "placeholder-preview.png"));
            Assert.Contains(AllControls(form).OfType<System.Windows.Forms.Button>(),
                b => b.Text == "Open" && b.Visible);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExitRecoveryDoesNotOverwriteSavedLayout_AndNextStartReappliesIt(bool disableClipping)
    {
        var item = Item("Test helper");
        item.Bounds = new WorldRect { X = 5000, Y = 7000, W = 800, H = 600 };
        var doc = new WorkspaceDocument(); doc.Items.Add(item);
        doc.Cameras[item.DesktopId] = new WorkspaceCamera { X = 2300, Y = -400, Zoom = 1 };
        new WorkspaceStore(SavePath).Save(doc);
        var api = new FakeWindowApi();
        var config = new FakeAppConfig { DisableGreedyDraw = disableClipping };
        var handle = (IntPtr)123;
        api.AddWindow(handle, 100, 100, 800, 600, title: item.Title);
        using (var first = new WorkspaceHarness(store: new WorkspaceStore(SavePath), api: api, config: config))
        {
            first.Workspace.Start();
            Assert.Equal(5000, first.Canvas.Windows[handle].X);
            first.Workspace.Dispose(); // Save BEFORE recovering desktop coordinates.
            string saved = File.ReadAllText(SavePath);
            first.Wm.RestoreDesktopViewport();
            Assert.Equal(saved, File.ReadAllText(SavePath));
            Assert.InRange(api.GetWindowRect(handle).x, 0, 1120);
            Assert.InRange(api.GetWindowRect(handle).y, 0, 480);
        }
        using var next = new WorkspaceHarness(store: new WorkspaceStore(SavePath), api: api, config: config);
        next.Workspace.Start();
        Assert.Single(next.Workspace.Document.Items);
        Assert.True(next.Workspace.IsOpen(next.Workspace.Document.Items[0]));
        Assert.Equal(5000, next.Canvas.Windows[handle].X);
        Assert.Equal(7000, next.Canvas.Windows[handle].Y);
        Assert.Equal(2300, next.Canvas.CamX);
        Assert.Equal(-400, next.Canvas.CamY);
        Assert.Equal(disableClipping ? (2700, 7400, 800, 600) : (1919, 1079, 800, 600), api.GetWindowRect(handle));
        Assert.Equal(!disableClipping, api.ClippedWindows.Contains(handle));
    }
    private static IEnumerable<System.Windows.Forms.Control> AllControls(System.Windows.Forms.Control root)
    {
        foreach (System.Windows.Forms.Control child in root.Controls)
        {
            yield return child;
            foreach (var descendant in AllControls(child)) yield return descendant;
        }
    }

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Workspace test timed out.");
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
}
