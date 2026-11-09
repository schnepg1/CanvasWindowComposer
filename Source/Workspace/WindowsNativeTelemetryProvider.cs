using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using CanvasDesktop.CanvasView;

namespace CanvasDesktop;

internal sealed class WindowsNativeTelemetryProvider : ITeamTelemetryProvider
{
    public const string DefaultNamedMutexName = @"Global\CanvasWindowComposer.Tests";
    public const string NamedMutexName = DefaultNamedMutexName;
    private static readonly Regex TaskIdRegex = new(@"\[(?<taskId>[a-zA-Z0-9_\-]+)\]", RegexOptions.Compiled);

    private readonly string _stateFilePath;
    private readonly IReadOnlyList<string> _watchedPaths;
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly Func<bool>? _mutexProbe;
    private readonly string? _mutexName;
    private readonly System.Threading.Timer? _pollTimer;
    private readonly object _gate = new();
    private TeamTelemetrySnapshot _fileState = TeamTelemetrySnapshot.Empty;
    private TeamTelemetrySnapshot _current = TeamTelemetrySnapshot.Empty;
    private string? _lastJson;
    private bool _mutexActive, _disposed;

    public TeamTelemetrySnapshot Current => Volatile.Read(ref _current);
    public string? MutexName => _mutexName;
    public bool IsPollingActive => _pollTimer != null;
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

    public static IReadOnlyList<string> ResolveWatchedPaths(string? explicitPath = null)
    {
        if (!string.IsNullOrEmpty(explicitPath))
            return new[] { Path.GetFullPath(explicitPath) };

        var paths = new List<string>();
        string resolved = ResolveStateFilePath();
        if (!string.IsNullOrEmpty(resolved))
            paths.Add(Path.GetFullPath(resolved));

        string appDataState = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CanvasWindowComposer", "Teams", "state.json");
        if (!paths.Any(p => string.Equals(p, Path.GetFullPath(appDataState), StringComparison.OrdinalIgnoreCase)))
            paths.Add(Path.GetFullPath(appDataState));

        return paths;
    }

    public WindowsNativeTelemetryProvider(string? stateFilePath = null, Func<bool>? mutexProbe = null, string? mutexName = NamedMutexName)
    {
        _stateFilePath = Path.GetFullPath(ResolveStateFilePath(stateFilePath));
        _watchedPaths = ResolveWatchedPaths(stateFilePath);
        _mutexProbe = mutexProbe;
        _mutexName = mutexName;

        foreach (var path in _watchedPaths)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                try
                {
                    var watcher = new FileSystemWatcher(dir, Path.GetFileName(path))
                    {
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                        EnableRaisingEvents = true
                    };
                    watcher.Changed += (_, _) => ReloadState();
                    watcher.Created += (_, _) => ReloadState();
                    watcher.Deleted += (_, _) => ReloadState();
                    watcher.Renamed += (_, _) => ReloadState();
                    _watchers.Add(watcher);
                }
                catch { }
            }
        }

        ReloadState();

        if (!string.IsNullOrEmpty(_mutexName))
        {
            // Also finds files/directories created after startup and recovers missed replace events.
            _pollTimer = new System.Threading.Timer(_ => CheckHardwareAndState(), null, 300, 300);
        }
    }

    internal void CheckHardwareAndState()
    {
        ReloadState();
        if (string.IsNullOrEmpty(_mutexName)) return;

        bool active = false;
        if (_mutexProbe != null) active = _mutexProbe();
        else
        {
            try
            {
                if (Mutex.TryOpenExisting(_mutexName, out var mutex))
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
        var roles = new Dictionary<string, AgentStatusReport>(_fileState.Roles, StringComparer.OrdinalIgnoreCase);
        var teams = new Dictionary<string, TeamStatusReport>(_fileState.Teams, StringComparer.OrdinalIgnoreCase);

        if (_mutexActive)
        {
            foreach (var role in new[] { "programmer", "reviewer" })
                roles[role] = new(role, AgentExecutionState.Testing, "Executing test suite under named mutex", DateTimeOffset.UtcNow);

            foreach (var (tKey, tVal) in _fileState.Teams)
            {
                var teamRoles = new Dictionary<string, AgentStatusReport>(tVal.Roles, StringComparer.OrdinalIgnoreCase);
                bool modified = false;
                foreach (var role in new[] { "programmer", "reviewer" })
                {
                    if (teamRoles.ContainsKey(role))
                    {
                        teamRoles[role] = new(role, AgentExecutionState.Testing, "Executing test suite under named mutex", DateTimeOffset.UtcNow, tVal.TaskId, tVal.Worktree);
                        modified = true;
                    }
                }
                if (modified)
                {
                    teams[tKey] = tVal with { Roles = teamRoles };
                }
            }
        }

        var snapshot = _fileState with { Roles = roles, Teams = teams, UpdatedAt = DateTimeOffset.UtcNow };
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
                string foundPath = _stateFilePath;
                if (!File.Exists(foundPath))
                {
                    foreach (var p in _watchedPaths)
                    {
                        if (File.Exists(p)) { foundPath = p; break; }
                    }
                }

                if (File.Exists(foundPath))
                {
                    if (new FileInfo(foundPath).Length > TeamTelemetryJson.MaximumBytes) return;
                    json = File.ReadAllText(foundPath);
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

    private void MutateState(Action<JsonObject> mutate)
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

                var json = File.Exists(_stateFilePath) ? File.ReadAllText(_stateFilePath).TrimStart('\uFEFF') : "{}";
                var root = JsonNode.Parse(json)?.AsObject() ?? new JsonObject();
                mutate(root);

                var next = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                _ = TeamTelemetryJson.Parse(next);

                Directory.CreateDirectory(Path.GetDirectoryName(_stateFilePath)!);
                string temp = _stateFilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllText(temp, next);
                    File.Move(temp, _stateFilePath, true);
                }
                finally
                {
                    if (File.Exists(temp)) File.Delete(temp);
                }
            }
            finally { if (held) writer.ReleaseMutex(); }
        }
        ReloadState();
    }

    public void SetState(string role, AgentExecutionState state, string detail = "")
    {
        MutateState(root =>
        {
            var roles = root["roles"] as JsonObject ?? new JsonObject();
            root["roles"] = roles;
            roles[role] = new JsonObject
            {
                ["state"] = state.ToString(),
                ["detail"] = detail,
                ["timestamp"] = DateTimeOffset.UtcNow.ToString("O")
            };
        });
    }

    public void SetTeamState(string taskId, string role, AgentExecutionState state, string detail = "")
    {
        MutateState(root =>
        {
            var teams = root["teams"] as JsonObject ?? new JsonObject();
            root["teams"] = teams;
            var team = teams[taskId] as JsonObject;
            if (team == null)
            {
                team = new JsonObject
                {
                    ["taskId"] = taskId,
                    ["name"] = taskId,
                    ["status"] = "Active",
                    ["activeRole"] = role,
                    ["roles"] = new JsonObject()
                };
                teams[taskId] = team;
            }
            team["activeRole"] = role;
            var roles = team["roles"] as JsonObject ?? new JsonObject();
            team["roles"] = roles;
            roles[role] = new JsonObject
            {
                ["state"] = state.ToString(),
                ["detail"] = detail,
                ["timestamp"] = DateTimeOffset.UtcNow.ToString("O")
            };

            // Mirror to root roles for legacy compatibility
            var rootRoles = root["roles"] as JsonObject ?? new JsonObject();
            root["roles"] = rootRoles;
            rootRoles[role] = new JsonObject
            {
                ["state"] = state.ToString(),
                ["detail"] = detail,
                ["timestamp"] = DateTimeOffset.UtcNow.ToString("O")
            };
        });
    }

    public void RegisterTeam(string taskId, string name, string conversationId = "", string worktree = "")
    {
        MutateState(root =>
        {
            var teams = root["teams"] as JsonObject ?? new JsonObject();
            root["teams"] = teams;
            if (!teams.ContainsKey(taskId))
            {
                teams[taskId] = new JsonObject
                {
                    ["taskId"] = taskId,
                    ["name"] = name,
                    ["conversationId"] = conversationId,
                    ["worktree"] = worktree,
                    ["status"] = "Active",
                    ["activeRole"] = "",
                    ["roles"] = new JsonObject()
                };
            }
        });
    }

    public void CompleteTeam(string taskId, bool success = true)
    {
        MutateState(root =>
        {
            if (root["teams"] is JsonObject teams && teams[taskId] is JsonObject team)
            {
                team["status"] = success ? "Completed" : "Failed";
            }
        });
    }

    public void UnregisterTeam(string taskId)
    {
        MutateState(root =>
        {
            if (root["teams"] is JsonObject teams)
            {
                teams.Remove(taskId);
            }
        });
    }

    public AgentStatusReport? GetReportForCard(CanvasAnnotation card) => TeamTelemetryJson.ReportForCard(Current, card);

    public AgentExecutionState GetStateForCard(string cardText)
        => GetStateForCard(cardText, null);

    public AgentExecutionState GetStateForCard(string cardText, string? taskId)
    {
        if (!string.IsNullOrWhiteSpace(taskId) && Current.Teams.TryGetValue(taskId, out var team))
        {
            if (team.Roles.Count > 0)
            {
                var match = TeamTelemetryJson.LegacyReport(new TeamTelemetrySnapshot(team.Roles, DateTimeOffset.UtcNow), cardText);
                if (match != null && match.State != AgentExecutionState.Idle)
                    return match.State;
            }
        }
        return TeamTelemetryJson.LegacyReport(Current, cardText)?.State ?? AgentExecutionState.Idle;
    }

    public AgentExecutionState GetStateForAnnotation(CanvasAnnotation item)
    {
        if (!string.IsNullOrEmpty(item.AgentTaskId))
        {
            var report = GetReportForCard(item);
            if (report != null && report.State != AgentExecutionState.Idle)
                return report.State;
        }

        if (!string.IsNullOrWhiteSpace(item.Text))
        {
            var match = TaskIdRegex.Match(item.Text);
            if (match.Success)
            {
                string taskId = match.Groups["taskId"].Value;
                var state = GetStateForCard(item.Text, taskId);
                if (state != AgentExecutionState.Idle)
                    return state;
            }
        }

        return GetReportForCard(item)?.State ?? GetStateForCard(item.Text);
    }

    public void Dispose()
    {
        lock (_gate) { _disposed = true; }
        foreach (var watcher in _watchers)
        {
            try { watcher.Dispose(); } catch { }
        }
        _watchers.Clear();
        _pollTimer?.Dispose();
    }
}
