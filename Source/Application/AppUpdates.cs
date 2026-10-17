using System;
using System.Threading;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace CanvasDesktop;

internal sealed record AppUpdate(string Version, object Payload);

internal interface IAppUpdateBackend
{
    bool Installed { get; }
    string CurrentVersion { get; }
    string DefaultChannel { get; }
    Task<AppUpdate?> CheckAsync(string channel, CancellationToken token);
    Task DownloadAsync(AppUpdate update, CancellationToken token);
    Action ApplyAfterExit(AppUpdate update);
}

/// <summary>The adapter owns provider objects; the controller owns user-visible state.</summary>
internal sealed class VelopackUpdateBackend : IAppUpdateBackend
{
    internal const string Repository = "https://github.com/schnepg1/CanvasWindowComposer";
    private readonly UpdateManager _installed = new(new GithubSource(Repository, null, false));
    public bool Installed => _installed.IsInstalled;
    public string DefaultChannel => Velopack.Locators.VelopackLocator.Current.Channel == "preview" ? "preview" : "stable";
    public string CurrentVersion => _installed.CurrentVersion?.ToString()
        ?? typeof(Program).Assembly.GetName().Version?.ToString() ?? "development";

    public async Task<AppUpdate?> CheckAsync(string channel, CancellationToken token)
    {
        var manager = new UpdateManager(new GithubSource(Repository, null, channel == "preview"),
            new UpdateOptions { ExplicitChannel = channel, AllowVersionDowngrade = false });
        token.ThrowIfCancellationRequested();
        var result = await manager.CheckForUpdatesAsync().WaitAsync(TimeSpan.FromSeconds(45), token).ConfigureAwait(false);
        return result == null ? null : new(result.TargetFullRelease.Version.ToString(), (manager, result));
    }

    public Task DownloadAsync(AppUpdate update, CancellationToken token)
    {
        var (manager, info) = ((UpdateManager, UpdateInfo))update.Payload;
        return manager.DownloadUpdatesAsync(info, cancelToken: token);
    }

    public Action ApplyAfterExit(AppUpdate update)
    {
        var (manager, info) = ((UpdateManager, UpdateInfo))update.Payload;
        return () => manager.WaitExitThenApplyUpdates(info.TargetFullRelease, silent: false, restart: true);
    }
}

/// <summary>UI-thread owned. A download never installs or restarts the application.</summary>
internal sealed class AppUpdateController(IAppUpdateBackend backend, AppConfig config) : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private bool _disposed;
    private long? _nextAutomaticCheck;
    public bool Installed => backend.Installed;
    public string Version => backend.CurrentVersion;
    public string Channel => config.HasUpdateChannel ? config.UpdateChannel : backend.DefaultChannel;
    public bool Busy { get; private set; }
    public AppUpdate? Available { get; private set; }
    public Action? ApplyAfterExit { get; private set; }
    public string Message { get; private set; } = backend.Installed
        ? "Check for a newer release." : "This is a development or unmanaged build. Pull and run Start-Canvas.cmd to rebuild, or install a release.";
    public string? ErrorDetails { get; private set; }
    public event Action? Changed;

    public void CheckAutomatically(long now)
    {
        if (_disposed || !Installed || !config.CheckForUpdates || Busy || _nextAutomaticCheck is {} next && now < next) return;
        _nextAutomaticCheck = now + (long)TimeSpan.FromHours(6).TotalMilliseconds;
        _ = CheckAsync();
    }

    public Task CheckAsync() => RunAsync(async token =>
    {
        Available = null; ApplyAfterExit = null;
        Message = "Checking for updates…"; Notify();
        var update = await backend.CheckAsync(Channel, token);
        token.ThrowIfCancellationRequested();
        Available = update;
        Message = update == null ? "You are up to date on this channel." : $"Version {update.Version} is available. Download it when you are ready.";
    });

    public Task DownloadAsync() => RunAsync(async token =>
    {
        if (Available is not {} update) return;
        ApplyAfterExit = null; Message = "Downloading update…"; Notify();
        await backend.DownloadAsync(update, token);
        token.ThrowIfCancellationRequested();
        ApplyAfterExit = backend.ApplyAfterExit(update);
        Message = "Update downloaded. Restart when ready; your workspace will be saved first.";
    });

    public void ChangePreferences(bool enabled, string channel)
    {
        if (_disposed || Busy) return;
        config.SetUpdates(enabled, channel);
        Available = null; ApplyAfterExit = null; ErrorDetails = null;
        _nextAutomaticCheck = null;
        Message = Installed ? "Update preferences saved. Check for updates on the selected channel." : Message;
        Notify();
    }

    private async Task RunAsync(Func<CancellationToken, Task> work)
    {
        if (_disposed || Busy || !Installed) return;
        Busy = true; ErrorDetails = null;
        try { await work(_stop.Token); }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            ApplyAfterExit = null;
            Message = "The update could not be completed. Canvas remains usable; try again later.";
            ErrorDetails = ex.ToString(); TrayApp.Log("Update request failed: " + ex);
        }
        finally { Busy = false; Notify(); }
    }

    private void Notify() { if (!_disposed) Changed?.Invoke(); }
    public void Dispose() { if (_disposed) return; _disposed = true; _stop.Cancel(); _stop.Dispose(); }
}

internal static class AppUpdateHandoff
{
    // No updater process exists until every cleanup phase has finished successfully.
    internal static bool TrySchedule(ApplicationLifecycle lifecycle, Action schedule)
    {
        lifecycle.Shutdown();
        if (lifecycle.Failures.Count != 0) return false;
        schedule(); return true;
    }
}
