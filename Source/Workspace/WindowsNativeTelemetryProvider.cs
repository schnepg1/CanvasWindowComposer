using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using CanvasDesktop.CanvasView;

namespace CanvasDesktop;

internal sealed class WindowsNativeTelemetryProvider : ITeamTelemetryProvider
{
    public const string NamedMutexName = @"Global\CanvasWindowComposer.Tests";
    private readonly string _stateFilePath;
    private readonly Func<bool>? _mutexProbe;
    private readonly FileSystemWatcher? _watcher;
    private readonly System.Threading.Timer _pollTimer;
    private readonly object _gate = new();
    private TeamTelemetrySnapshot _fileState = TeamTelemetrySnapshot.Empty;
    private TeamTelemetrySnapshot _current = TeamTelemetrySnapshot.Empty;
    private string? _lastJson;
    private bool _mutexActive, _disposed;
    public TeamTelemetrySnapshot Current => Volatile.Read(ref _current);
    public event Action<TeamTelemetrySnapshot>? TelemetryUpdated;

    public static string ResolveStateFilePath(string? explicitPath = null)
    {
        if (!string.IsNullOrEmpty(explicitPath)) return explicitPath;
        if (Environment.GetEnvironmentVariable("CANVAS_TEAM_STATE_PATH") is { Length: > 0 } sharedPath) return sharedPath;
        var candidates = new[]
        {
            Path.Combine(Environment.CurrentDirectory, ".canvas", "state.json"),
            Path.Combine(Environment.CurrentDirectory, ".agents", "teams", "state.json"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".canvas", "state.json"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".agents", "teams", "state.json")
        };
        foreach (var candidate in candidates) if (File.Exists(candidate)) return candidate;
        var appDataState = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CanvasWindowComposer", "Teams", "state.json");
        if (File.Exists(appDataState)) return appDataState;
        return Directory.Exists(Path.Combine(Environment.CurrentDirectory, ".agents"))
            ? Path.Combine(Environment.CurrentDirectory, ".agents", "teams", "state.json") : appDataState;
    }

    public WindowsNativeTelemetryProvider(string? stateFilePath = null, Func<bool>? mutexProbe = null)
    {
        _stateFilePath = Path.GetFullPath(ResolveStateFilePath(stateFilePath));
        _mutexProbe = mutexProbe;
        var dir = Path.GetDirectoryName(_stateFilePath)!;
        if (Directory.Exists(dir))
        {
            _watcher = new FileSystemWatcher(dir, Path.GetFileName(_stateFilePath))
            { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
            _watcher.Changed += (_, _) => ReloadState();
            _watcher.Created += (_, _) => ReloadState();
            _watcher.Deleted += (_, _) => ReloadState();
            _watcher.Renamed += (_, _) => ReloadState();
            _watcher.EnableRaisingEvents = true;
        }
        ReloadState();
        // Also finds files/directories created after startup and recovers missed replace events.
        _pollTimer = new System.Threading.Timer(_ => CheckHardwareAndState(), null, 300, 300);
    }

    internal void CheckHardwareAndState()
    {
        ReloadState();
        bool active = false;
        if (_mutexProbe != null) active = _mutexProbe();
        else
        {
            try
            {
                if (Mutex.TryOpenExisting(NamedMutexName, out var mutex))
                { active = true; mutex.Dispose(); }
            }
            catch (UnauthorizedAccessException) { }
        }
        TeamTelemetrySnapshot snapshot;
        lock (_gate)
        {
            if (_disposed || active == _mutexActive) return;
            _mutexActive = active;
            snapshot = Publish();
        }
        TelemetryUpdated?.Invoke(snapshot);
    }

    private TeamTelemetrySnapshot Publish()
    {
        // A machine-wide mutex cannot identify a task. Only legacy global cards use it.
        var roles = new Dictionary<string, AgentStatusReport>(_fileState.Roles, StringComparer.OrdinalIgnoreCase);
        if (_mutexActive)
            foreach (var role in new[] { "programmer", "reviewer" })
                roles[role] = new(role, AgentExecutionState.Testing, "Executing test suite under named mutex", DateTimeOffset.UtcNow);
        var snapshot = _fileState with { Roles = roles, UpdatedAt = DateTimeOffset.UtcNow };
        Volatile.Write(ref _current, snapshot);
        return snapshot;
    }

    public void ReloadState()
    {
        TeamTelemetrySnapshot snapshot;
        lock (_gate)
        {
            if (_disposed) return;
            try
            {
                string json = "{}";
                if (File.Exists(_stateFilePath))
                {
                    if (new FileInfo(_stateFilePath).Length > TeamTelemetryJson.MaximumBytes) return;
                    json = File.ReadAllText(_stateFilePath);
                }
                if (_lastJson == json) return;
                var parsed = TeamTelemetryJson.Parse(json);
                _lastJson = json;
                _fileState = parsed;
                snapshot = Publish();
            }
            // Preserve the last valid snapshot during partial writes or atomic replacements.
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return; }
        }
        TelemetryUpdated?.Invoke(snapshot);
    }

    public void SetState(string role, AgentExecutionState state, string detail = "")
    {
        lock (_gate)
        {
            if (_disposed) return;
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_stateFilePath.ToUpperInvariant())));
            using var writer = new Mutex(false, @"Local\CanvasWindowComposer.Telemetry." + hash);
            bool held = false;
            try
            {
                try { held = writer.WaitOne(TimeSpan.FromSeconds(10)); }
                catch (AbandonedMutexException) { held = true; }
                if (!held) throw new IOException("Timed out waiting to update team telemetry.");
                // Preserve registered teams and unrelated fields when a legacy caller writes a role.
                var json = File.Exists(_stateFilePath) ? File.ReadAllText(_stateFilePath).TrimStart('\uFEFF') : "{}";
                var root = JsonNode.Parse(json)!.AsObject();
                var roles = root["roles"] as JsonObject ?? new JsonObject();
                root["roles"] = roles;
                roles[role] = new JsonObject { ["state"] = state.ToString(), ["detail"] = detail,
                    ["timestamp"] = DateTimeOffset.UtcNow.ToString("O") };
                var next = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                _ = TeamTelemetryJson.Parse(next);
                Directory.CreateDirectory(Path.GetDirectoryName(_stateFilePath)!);
                string temp = _stateFilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try { File.WriteAllText(temp, next); File.Move(temp, _stateFilePath, true); }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }
            finally { if (held) writer.ReleaseMutex(); }
        }
        ReloadState();
    }

    public AgentStatusReport? GetReportForCard(CanvasAnnotation card) => TeamTelemetryJson.ReportForCard(Current, card);
    public AgentExecutionState GetStateForCard(string cardText)
        => TeamTelemetryJson.LegacyReport(Current, cardText)?.State ?? AgentExecutionState.Idle;

    public void Dispose()
    {
        lock (_gate) { _disposed = true; }
        _watcher?.Dispose(); _pollTimer.Dispose();
    }
}
