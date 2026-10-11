using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using CanvasDesktop;

namespace CanvasDesktop.Tests;

public class ResourceOwnershipTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "canvas-resource-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ConfigurationWatcherIsSingleOwnedAndDisposed()
    {
        using var config = new AppConfig(directory: _dir);
        config.Load(); config.StartObservingChanges();
        var watcher = Field<FileSystemWatcher>(config, "_watcher");
        bool disposed = false;
        watcher.Disposed += (_, _) => disposed = true;
        config.StartObservingChanges();
        Assert.Same(watcher, Field<FileSystemWatcher>(config, "_watcher"));
        config.Dispose(); config.Dispose();
        Assert.True(disposed);
        Assert.Throws<ObjectDisposedException>(config.StartObservingChanges);
        Assert.Throws<ObjectDisposedException>(config.Load);
        Assert.Throws<ObjectDisposedException>(() => config.SetShowScreenFixedWindowsDuringPan(false));
    }

    [Fact]
    public void QueuedWatcherCallbackAfterDisposalDoesNotReload()
    {
        using var config = new AppConfig(directory: _dir);
        config.Load(); config.StartObservingChanges(); config.Dispose();
        File.WriteAllText(Path.Combine(_dir, "config.ini"), "DisableSearch=true");
        typeof(AppConfig).GetMethod("OnConfigFileChanged", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(config, new object[] { config, new FileSystemEventArgs(WatcherChangeTypes.Changed, _dir, "config.ini") });
        Assert.False(config.DisableSearch);
    }

    [Fact]
    public void ForegroundDisposalDetachesEventsAndPreventsLateRecentering()
    {
        var canvas = new Canvas(); var input = new FakeInputRouter(); var overview = new FakeOverviewController();
        using var foreground = new ForegroundCoordinator(canvas, overview, input, new FakeClock(10000), new FakeScreens());
        Assert.Equal(1, Subscribers(input, "WindowFocused"));
        foreground.Dispose(); foreground.Dispose();
        Assert.Equal(0, Subscribers(input, "WindowFocused"));
        Assert.Equal(0, Subscribers(overview, "BeforeModeChanged"));
        canvas.SetWindow((IntPtr)1, 5000, 5000, 800, 600);
        input.RaiseWindowFocused((IntPtr)1);
        Assert.Equal(0, canvas.CamX);
    }

    [Fact]
    public void OverviewInputDisposalDetachesAllExternalSubscriptions()
    {
        var canvas = new Canvas(); var input = new FakeInputRouter(); var overview = new FakeOverviewController();
        using var binding = new OverviewInputs(overview, input, canvas);
        binding.Dispose(); binding.Dispose();
        foreach (string name in new[] { "DragStarted", "ButtonDown", "OverviewHotkey", "InputAvailable", "EscPressed" })
            Assert.Equal(0, Subscribers(input, name));
        Assert.Equal(0, Subscribers(canvas, "CameraChanged"));
        Assert.Equal(0, Subscribers(overview, "BeforeModeChanged"));
        input.RaiseDragStarted(); input.RaiseOverviewHotkey();
        canvas.SetCamera(100, 200);
        Assert.Empty(overview.Transitions);
        Assert.Equal(0, overview.SyncCameraCalls);
    }

    [Fact]
    public void UnshownSearchDisposalReleasesHotkeySubscription()
    {
        OnSta(() =>
        {
            var canvas = new Canvas(); var input = new FakeInputRouter(); var api = new FakeWindowApi();
            using var wm = new WindowManager(canvas, api, new FakeAppConfig(), input);
            using var search = new SearchOverlay(canvas, wm, api, input, new FakeScreens());
            Assert.Equal(1, Subscribers(input, "SearchHotkey"));
            search.Dispose(); search.Dispose();
            Assert.Equal(0, Subscribers(input, "SearchHotkey"));
            input.RaiseSearchHotkey();
            Assert.False(search.Visible);
        });
    }

    [Fact]
    public void DirectMinimapDisposalReleasesRendererAndSubscriptions()
    {
        OnSta(() =>
        {
            var canvas = new Canvas(); var input = new FakeInputRouter(); var api = new FakeWindowApi();
            using var wm = new WindowManager(canvas, api, new FakeAppConfig(), input);
            using var cache = new DesktopStateCache(canvas, wm, new FakeOverviewController(), new FakeVirtualDesktops());
            var names = new[] { "CollapseChanged", "MaximizeChanged", "PinnedChanged", "FrontChanged" };
            var baseline = names.ToDictionary(name => name, name => Subscribers(canvas, name));
            using var map = new MinimapOverlay(canvas, input, cache, new FakeScreens());
            var renderer = Field<MinimapRenderer>(map, "_renderer");
            var wake = Field<ManualResetEventSlim>(renderer, "_wakeEvent");
            map.Dispose(); map.Dispose();
            Assert.Equal(0, Subscribers(input, "DragStarted"));
            Assert.Equal(0, Subscribers(cache, "AfterStateLoaded"));
            foreach (string name in names)
                Assert.Equal(baseline[name], Subscribers(canvas, name));
            Assert.Throws<ObjectDisposedException>(() => wake.Wait(0));
            input.RaiseDragStarted(); canvas.SetCamera(100, 200); map.NotifyCanvasChanged();
            Assert.False(map.Visible);
        });
    }

    [Fact]
    public void DirectOverviewFormDisposalReleasesOwnedGridWithoutCloseEvent()
    {
        OnSta(() =>
        {
            using var form = new OverviewOverlay(Screen.PrimaryScreen!);
            var renderer = new GridRenderer(); // No GPU initialization needed for ownership testing.
            typeof(OverviewOverlay).GetProperty("Grid")!.SetValue(form, renderer);
            var wake = Field<ManualResetEventSlim>(renderer, "_wakeEvent");
            form.Dispose(); form.Dispose();
            Assert.Null(form.Grid);
            Assert.Throws<ObjectDisposedException>(() => wake.Wait(0));
        });
    }

    private static T Field<T>(object owner, string name)
        => (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    private static int Subscribers(object owner, string name) => Field<Delegate?>(owner, name)?.GetInvocationList().Length ?? 0;
    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
}
