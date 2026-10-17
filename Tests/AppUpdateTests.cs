using CanvasDesktop;

namespace CanvasDesktop.Tests;

public sealed class AppUpdateTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "canvas-updates-" + Guid.NewGuid().ToString("N"));
    private AppConfig Config()
    {
        var config = new AppConfig(directory: _directory); config.Load(); return config;
    }

    [Fact]
    public async Task DevelopmentBuildNeverContactsAnUpdateSource()
    {
        using var config = Config(); var backend = new Backend { Installed = false };
        using var updates = new AppUpdateController(backend, config);
        updates.CheckAutomatically(0); await updates.CheckAsync(); await updates.DownloadAsync();
        Assert.Equal(0, backend.Checks); Assert.Equal(0, backend.Downloads); Assert.Null(updates.ApplyAfterExit);
    }

    [Fact]
    public async Task DownloadDoesNotInstallAndChangingChannelDropsThePreparedUpdate()
    {
        using var config = Config(); var backend = new Backend();
        using var updates = new AppUpdateController(backend, config);
        await updates.CheckAsync(); Assert.Equal("stable", backend.Channel);
        await updates.DownloadAsync(); Assert.NotNull(updates.ApplyAfterExit); Assert.Equal(0, backend.Applies);
        updates.ChangePreferences(true, "preview"); Assert.Null(updates.ApplyAfterExit); Assert.Null(updates.Available);
        await updates.CheckAsync(); Assert.Equal("preview", backend.Channel);
        Assert.Equal(0, backend.Applies);
    }

    [Fact]
    public async Task ConcurrentRequestsAreIgnoredAndShutdownCannotPrepareAnUpdateLater()
    {
        using var config = Config(); var gate = new TaskCompletionSource<AppUpdate?>();
        var backend = new Backend { Check = _ => gate.Task };
        using var updates = new AppUpdateController(backend, config);
        var first = updates.CheckAsync(); await updates.CheckAsync(); Assert.Equal(1, backend.Checks);
        updates.Dispose(); gate.SetResult(new("2.0.0", new object())); await first;
        Assert.Null(updates.Available); Assert.Null(updates.ApplyAfterExit);
    }

    [Fact]
    public async Task FailedDownloadKeepsTheAppUsableAndNeverSchedulesAnUpdater()
    {
        using var config = Config(); var backend = new Backend { DownloadError = new IOException("Offline") };
        using var updates = new AppUpdateController(backend, config);
        await updates.CheckAsync(); await updates.DownloadAsync();
        Assert.False(updates.Busy); Assert.Null(updates.ApplyAfterExit); Assert.Contains("Offline", updates.ErrorDetails);
        Assert.Equal(0, backend.Applies);
        backend.DownloadError = null; await updates.DownloadAsync(); Assert.NotNull(updates.ApplyAfterExit);
    }

    [Fact]
    public void AutomaticChecksRespectOptOutAndSixHourInterval()
    {
        using var config = Config(); var backend = new Backend();
        using var updates = new AppUpdateController(backend, config);
        updates.CheckAutomatically(0); updates.CheckAutomatically(1000); Assert.Equal(1, backend.Checks);
        updates.CheckAutomatically((long)TimeSpan.FromHours(6).TotalMilliseconds); Assert.Equal(2, backend.Checks);
        updates.ChangePreferences(false, "stable"); updates.CheckAutomatically(long.MaxValue / 2); Assert.Equal(2, backend.Checks);
    }

    [Theory]
    [InlineData((int)ShutdownPhase.Quiesce)]
    [InlineData((int)ShutdownPhase.FinalizeLayout)]
    [InlineData((int)ShutdownPhase.SaveWorkspace)]
    [InlineData((int)ShutdownPhase.StopServices)]
    [InlineData((int)ShutdownPhase.RecoverWindows)]
    [InlineData((int)ShutdownPhase.ReleaseUi)]
    public void AnyCleanupFailureBlocksTheUpdaterAfterStillAttemptingRecovery(int failed)
    {
        var calls = new List<string>(); using var lifecycle = new ApplicationLifecycle();
        foreach (var phase in Enum.GetValues<ShutdownPhase>())
            lifecycle.Register(phase, phase.ToString(), () => { calls.Add(phase.ToString()); if ((int)phase == failed) throw new IOException("Injected failure"); });
        lifecycle.CompleteStartup();
        Assert.False(AppUpdateHandoff.TrySchedule(lifecycle, () => calls.Add("update")));
        Assert.Equal(Enum.GetValues<ShutdownPhase>().Select(p => p.ToString()), calls);
    }

    [Fact]
    public void UpdaterStartsOnlyAfterSaveRecoveryAndUiDisposal()
    {
        var calls = new List<string>(); using var lifecycle = new ApplicationLifecycle();
        lifecycle.Register(ShutdownPhase.SaveWorkspace, "save", () => calls.Add("save"));
        lifecycle.Register(ShutdownPhase.RecoverWindows, "recover", () => calls.Add("recover"));
        lifecycle.Register(ShutdownPhase.ReleaseUi, "dispose", () => calls.Add("dispose"));
        lifecycle.CompleteStartup();
        Assert.True(AppUpdateHandoff.TrySchedule(lifecycle, () => calls.Add("update")));
        Assert.Equal(new[] { "save", "recover", "dispose", "update" }, calls);
    }

    [Fact]
    public void PreferencesPersistWithoutOverwritingOtherSettings()
    {
        using var config = Config(); config.SetSelectionRadius(2345); config.SetUpdates(false, "preview");
        using var reopened = Config(); Assert.False(reopened.CheckForUpdates); Assert.Equal("preview", reopened.UpdateChannel); Assert.Equal(2345, reopened.SelectionRadius);
        Assert.Throws<ArgumentException>(() => config.SetUpdates(true, "untrusted-feed"));
    }

    [Fact]
    public async Task PreviewInstallDefaultsToItsOwnChannelUntilTheUserChoosesOne()
    {
        using var config = Config(); var backend = new Backend { DefaultChannel = "preview" };
        using var updates = new AppUpdateController(backend, config);
        await updates.CheckAsync(); Assert.Equal("preview", backend.Channel);
        updates.ChangePreferences(true, "stable"); await updates.CheckAsync(); Assert.Equal("stable", backend.Channel);
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }

    private sealed class Backend : IAppUpdateBackend
    {
        public bool Installed { get; set; } = true;
        public string CurrentVersion => "1.0.0";
        public string DefaultChannel { get; set; } = "stable";
        public int Checks, Downloads, Applies;
        public string? Channel;
        public Exception? DownloadError;
        public Func<CancellationToken, Task<AppUpdate?>>? Check;
        public Task<AppUpdate?> CheckAsync(string channel, CancellationToken token)
        { Checks++; Channel = channel; return Check?.Invoke(token) ?? Task.FromResult<AppUpdate?>(new("2.0.0", new object())); }
        public Task DownloadAsync(AppUpdate update, CancellationToken token)
        { Downloads++; return DownloadError == null ? Task.CompletedTask : Task.FromException(DownloadError); }
        public Action ApplyAfterExit(AppUpdate update) => () => Applies++;
    }
}
