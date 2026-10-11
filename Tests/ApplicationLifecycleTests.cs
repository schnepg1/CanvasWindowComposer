using CanvasDesktop;

namespace CanvasDesktop.Tests;

public class ApplicationLifecycleTests
{
    [Fact]
    public void CleanupUsesPhaseOrderAndPreservesOrderWithinPhase()
    {
        var calls = new List<string>();
        using var lifetime = new ApplicationLifecycle();
        lifetime.Register(ShutdownPhase.RecoverWindows, "recover", () => calls.Add("recover"));
        lifetime.Register(ShutdownPhase.SaveWorkspace, "save", () => calls.Add("save"));
        lifetime.Register(ShutdownPhase.FinalizeLayout, "commit", () => calls.Add("commit"));
        lifetime.Register(ShutdownPhase.Quiesce, "timer", () => calls.Add("timer"));
        lifetime.Register(ShutdownPhase.Quiesce, "hooks", () => calls.Add("hooks"));
        lifetime.Register(ShutdownPhase.ReleaseUi, "ui", () => calls.Add("ui"));
        lifetime.CompleteStartup(); lifetime.Shutdown();
        Assert.Equal(new[] { "timer", "hooks", "commit", "save", "recover", "ui" }, calls);
        Assert.True(lifetime.StartupCompleted);
        Assert.Equal(ApplicationLifetimeState.Stopped, lifetime.State);
    }

    [Theory]
    [InlineData((int)ShutdownPhase.Quiesce)]
    [InlineData((int)ShutdownPhase.FinalizeLayout)]
    [InlineData((int)ShutdownPhase.SaveWorkspace)]
    [InlineData((int)ShutdownPhase.StopServices)]
    [InlineData((int)ShutdownPhase.RecoverWindows)]
    [InlineData((int)ShutdownPhase.ReleaseUi)]
    public void FailureInAnyPhaseStillAttemptsEveryLaterStep(int failedPhase)
    {
        var calls = new List<ShutdownPhase>();
        var reported = new List<ShutdownFailure>();
        using var lifetime = new ApplicationLifecycle(reported.Add);
        foreach (var phase in Enum.GetValues<ShutdownPhase>())
            lifetime.Register(phase, phase.ToString(), () =>
            {
                calls.Add(phase);
                if ((int)phase == failedPhase) throw new IOException("Injected failure");
            });
        lifetime.CompleteStartup(); lifetime.Shutdown();
        Assert.Equal(Enum.GetValues<ShutdownPhase>(), calls);
        Assert.Same(Assert.Single(lifetime.Failures), Assert.Single(reported));
        Assert.Equal(((ShutdownPhase)failedPhase).ToString(), reported[0].Step);
    }

    [Fact]
    public void ThrowingErrorReporterCannotPreventRecovery()
    {
        bool recovered = false;
        using var lifetime = new ApplicationLifecycle(_ => throw new IOException("Log unavailable"));
        lifetime.Register(ShutdownPhase.SaveWorkspace, "save", () => throw new IOException("Disk unavailable"));
        lifetime.Register(ShutdownPhase.RecoverWindows, "recover", () => recovered = true);
        lifetime.Shutdown();
        Assert.True(recovered);
        Assert.Single(lifetime.Failures);
    }

    [Fact]
    public void ReentrantAndRepeatedShutdownReleaseEachResourceOnlyOnce()
    {
        var resource = new TestResource();
        using var lifetime = new ApplicationLifecycle();
        lifetime.Own(resource, ShutdownPhase.StopServices, "resource");
        lifetime.Register(ShutdownPhase.Quiesce, "reentrant", lifetime.Shutdown);
        lifetime.CompleteStartup(); lifetime.Shutdown(); lifetime.Shutdown(); lifetime.Dispose();
        Assert.Equal(1, resource.Disposals);
        Assert.True(lifetime.IsStopping);
    }

    [Fact]
    public void FailedPartialStartupCleansAcquiredResourcesAndKeepsOriginalException()
    {
        var resource = new TestResource();
        using var lifetime = new ApplicationLifecycle();
        var original = new InvalidOperationException("Startup failed");
        var actual = Assert.Throws<InvalidOperationException>((Action)(() =>
        {
            try
            {
                lifetime.Own(resource, ShutdownPhase.StopServices, "first acquisition");
                lifetime.Register(ShutdownPhase.ReleaseUi, "failing cleanup", () => throw new IOException("Cleanup failed"));
                throw original;
            }
            catch { lifetime.Shutdown(); throw; }
        }));
        Assert.Same(original, actual);
        Assert.Equal(1, resource.Disposals);
        Assert.False(lifetime.StartupCompleted);
        Assert.Single(lifetime.Failures);
    }

    [Fact]
    public void StartupAndRegistrationCannotBeRepeatedAfterCompletionOrShutdown()
    {
        using var lifetime = new ApplicationLifecycle();
        lifetime.CompleteStartup();
        Assert.Throws<InvalidOperationException>(lifetime.CompleteStartup);
        Assert.Throws<InvalidOperationException>(() => lifetime.Register(ShutdownPhase.ReleaseUi, "late", () => { }));
        lifetime.Shutdown();
        Assert.Throws<InvalidOperationException>(lifetime.CompleteStartup);
    }

    [Fact]
    public void SaveFailureDoesNotPreventPlaceholderCleanupOrDesktopRecovery()
    {
        var item = new WorkspaceItem { Title = "Saved", ExecutablePath = @"C:\Apps\Editor.exe",
            Bounds = new WorldRect { X = 5000, Y = 5000, W = 800, H = 600 } };
        using var rig = new WorkspaceHarness(new WorkspaceDocument { Items = new() { item } });
        rig.Workspace.Start();
        rig.Api.AddWindow((IntPtr)42, 6000, 6000, 800, 600, manageable: false);
        rig.Wm.TrackForDesktopRestore((IntPtr)42);
        rig.Canvas.SetWindow(Assert.Single(rig.Cards.Created).Handle, 5100, 5300, 800, 600);
        rig.Store.SaveError = new IOException("Disk full");
        using var lifetime = new ApplicationLifecycle();
        lifetime.Register(ShutdownPhase.SaveWorkspace, "workspace", rig.Workspace.Dispose);
        lifetime.Register(ShutdownPhase.RecoverWindows, "recover", rig.Wm.RestoreDesktopViewport);
        lifetime.CompleteStartup(); lifetime.Shutdown();
        Assert.Equal("workspace", Assert.Single(lifetime.Failures).Step);
        Assert.All(rig.Cards.Created, card => Assert.True(card.Disposed));
        Assert.InRange(rig.Api.GetWindowRect((IntPtr)42).x, 0, 1120);
        Assert.Empty(rig.Canvas.Windows);
    }

    [Fact]
    public void FailedCaptureStillReleasesAllWorkspacePlaceholders()
    {
        var item = new WorkspaceItem { Bounds = new WorldRect { W = 800, H = 600 } };
        using var rig = new WorkspaceHarness(new WorkspaceDocument { Items = new() { item } });
        rig.Workspace.Start();
        int saved = rig.Store.Saves;
        rig.Identity.Error = new IOException("Explorer unavailable");
        Assert.Throws<AggregateException>(rig.Workspace.Dispose);
        Assert.All(rig.Cards.Created, card => Assert.True(card.Disposed));
        Assert.Empty(rig.Canvas.Windows);
        Assert.Equal(saved, rig.Store.Saves);
        rig.Workspace.Dispose();
    }

    [Fact]
    public void StartupRollbackDoesNotSavePartiallyRestoredCanvas()
    {
        var item = new WorkspaceItem { Bounds = new WorldRect { W = 800, H = 600 } };
        using var rig = new WorkspaceHarness(new WorkspaceDocument { Items = new() { item } });
        rig.Workspace.Start();
        var card = Assert.Single(rig.Cards.Created);
        rig.Canvas.SetWindow(card.Handle, 9999, 9999, 800, 600);
        int saved = rig.Store.Saves;
        rig.Workspace.Stop(save: false);
        Assert.Equal(saved, rig.Store.Saves);
        Assert.Equal(0, item.Bounds.X);
        Assert.True(card.Disposed);
    }

    [Fact]
    public void WindowRecoveryFailureDoesNotSkipOtherWindows()
    {
        var canvas = new Canvas(); var api = new FakeWindowApi();
        using var wm = new WindowManager(canvas, api, new FakeAppConfig(), new FakeInputRouter());
        api.AddWindow((IntPtr)1, 6000, 6000, 800, 600);
        api.AddWindow((IntPtr)2, 7000, 7000, 800, 600);
        wm.TrackForDesktopRestore((IntPtr)1); wm.TrackForDesktopRestore((IntPtr)2);
        api.BeforeSetPosition = handle => { if (handle == (IntPtr)1) throw new IOException("Unavailable window"); };
        var failure = Assert.Throws<AggregateException>(wm.RestoreDesktopViewport);
        Assert.Single(failure.InnerExceptions);
        Assert.InRange(api.GetWindowRect((IntPtr)2).x, 0, 1120);
        wm.RestoreDesktopViewport(); // Idempotent even after a failed recovery.
    }

    private sealed class TestResource : IDisposable
    {
        public int Disposals;
        public void Dispose() => Disposals++;
    }
}
