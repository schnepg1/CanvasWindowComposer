using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using CanvasDesktop.CanvasView;

namespace CanvasDesktop;

internal sealed class WindowsNativeTelemetryProvider : ITeamTelemetryProvider
{
    public const string NamedMutexName = @"Global\CanvasWindowComposer.Tests";
    private readonly string _stateFilePath;
    private readonly Func<bool>? _mutexProbe;
    private readonly FileSystemWatcher? _watcher;
    private readonly System.Threading.Timer? _pollTimer;
    private TeamTelemetrySnapshot _current = TeamTelemetrySnapshot.Empty;
    private bool _disposed;

    public TeamTelemetrySnapshot Current => _current;
    public event Action<TeamTelemetrySnapshot>? TelemetryUpdated;

    public static string ResolveStateFilePath(string? explicitPath = null)
    {
        if (!string.IsNullOrEmpty(explicitPath))
            return explicitPath;

        // 1. Project folder (.canvas or .agents in CurrentDirectory or BaseDirectory)
        var candidates = new[]
        {
            Path.Combine(Environment.CurrentDirectory, ".canvas", "state.json"),
            Path.Combine(Environment.CurrentDirectory, ".agents", "teams", "state.json"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".canvas", "state.json"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".agents", "teams", "state.json")
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        // 2. User AppData (Vendor-neutral Windows standard)
        var appDataState = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CanvasWindowComposer", "Teams", "state.json");

        if (File.Exists(appDataState))
            return appDataState;

        // 3. Fallback: project directory if in dev/launcher, else AppData
        if (File.Exists(Path.Combine(Environment.CurrentDirectory, ".agents", "teams", "teams.yaml")) ||
            Directory.Exists(Path.Combine(Environment.CurrentDirectory, ".agents")))
        {
            return Path.Combine(Environment.CurrentDirectory, ".agents", "teams", "state.json");
        }

        return appDataState;
    }

    public WindowsNativeTelemetryProvider(string? stateFilePath = null, Func<bool>? mutexProbe = null)
    {
        _stateFilePath = ResolveStateFilePath(stateFilePath);
        _mutexProbe = mutexProbe;

        try
        {
            var dir = Path.GetDirectoryName(_stateFilePath);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                _watcher = new FileSystemWatcher(dir, Path.GetFileName(_stateFilePath))
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                    EnableRaisingEvents = true
                };
                _watcher.Changed += (_, _) => ReloadState();
                _watcher.Created += (_, _) => ReloadState();
            }
        }
        catch { }

        // Poll every 300ms for hardware mutex truth
        _pollTimer = new System.Threading.Timer(_ => CheckHardwareAndState(), null, 300, 300);

        ReloadState();
    }

    internal void CheckHardwareAndState()
    {
        if (_disposed) return;

        bool mutexActive = false;
        if (_mutexProbe != null)
        {
            mutexActive = _mutexProbe();
        }
        else
        {
            try
            {
                if (Mutex.TryOpenExisting(NamedMutexName, out var mutex))
                {
                    mutexActive = true;
                    mutex.Dispose();
                }
            }
            catch { }
        }

        bool stateChanged = false;
        var roles = new Dictionary<string, AgentStatusReport>(_current.Roles, StringComparer.OrdinalIgnoreCase);

        // Hardware truth: if mutex is held, the test runner is actively running native tests
        if (mutexActive)
        {
            if (!roles.TryGetValue("programmer", out var prog) || prog.State != AgentExecutionState.Testing)
            {
                roles["programmer"] = new AgentStatusReport("programmer", AgentExecutionState.Testing, "Executing test suite under named mutex", DateTimeOffset.UtcNow);
                stateChanged = true;
            }
            if (!roles.TryGetValue("reviewer", out var rev) || rev.State != AgentExecutionState.Testing)
            {
                roles["reviewer"] = new AgentStatusReport("reviewer", AgentExecutionState.Testing, "Verifying tests under named mutex", DateTimeOffset.UtcNow);
                stateChanged = true;
            }
        }

        if (stateChanged)
        {
            _current = new TeamTelemetrySnapshot(roles, DateTimeOffset.UtcNow);
            TelemetryUpdated?.Invoke(_current);
        }
    }

    public void ReloadState()
    {
        if (_disposed) return;
        try
        {
            if (File.Exists(_stateFilePath))
            {
                string json = File.ReadAllText(_stateFilePath);
                using var doc = JsonDocument.Parse(json);
                var roles = new Dictionary<string, AgentStatusReport>(StringComparer.OrdinalIgnoreCase);

                if (doc.RootElement.TryGetProperty("roles", out var rolesElem) && rolesElem.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in rolesElem.EnumerateObject())
                    {
                        string role = prop.Name;
                        string stateStr = prop.Value.TryGetProperty("state", out var s) ? s.GetString() ?? "Idle" : "Idle";
                        string detail = prop.Value.TryGetProperty("detail", out var d) ? d.GetString() ?? "" : "";

                        if (Enum.TryParse<AgentExecutionState>(stateStr, true, out var state))
                        {
                            roles[role] = new AgentStatusReport(role, state, detail, DateTimeOffset.UtcNow);
                        }
                    }
                }

                _current = new TeamTelemetrySnapshot(roles, DateTimeOffset.UtcNow);
                TelemetryUpdated?.Invoke(_current);
            }
        }
        catch { }
    }

    public void SetState(string role, AgentExecutionState state, string detail = "")
    {
        var roles = new Dictionary<string, AgentStatusReport>(_current.Roles, StringComparer.OrdinalIgnoreCase);
        roles[role] = new AgentStatusReport(role, state, detail, DateTimeOffset.UtcNow);
        _current = new TeamTelemetrySnapshot(roles, DateTimeOffset.UtcNow);

        try
        {
            var dir = Path.GetDirectoryName(_stateFilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var dict = new Dictionary<string, object>();
            var rolesDict = new Dictionary<string, object>();
            foreach (var r in roles)
            {
                rolesDict[r.Key] = new { state = r.Value.State.ToString(), detail = r.Value.Detail, timestamp = r.Value.Timestamp };
            }
            dict["roles"] = rolesDict;
            dict["updatedAt"] = DateTimeOffset.UtcNow;

            string json = JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_stateFilePath, json);
        }
        catch { }

        TelemetryUpdated?.Invoke(_current);
    }

    public AgentExecutionState GetStateForCard(string cardText)
    {
        if (string.IsNullOrWhiteSpace(cardText) || _current.Roles.Count == 0) return AgentExecutionState.Idle;

        foreach (var (roleKey, report) in _current.Roles)
        {
            if (report.State == AgentExecutionState.Idle) continue;

            if (cardText.Contains(roleKey, StringComparison.OrdinalIgnoreCase))
                return report.State;

            // Common aliases and titles
            bool matches = roleKey.ToLowerInvariant() switch
            {
                "programmer" or "dev" => cardText.Contains("Programmer", StringComparison.OrdinalIgnoreCase) || cardText.Contains("Developer", StringComparison.OrdinalIgnoreCase) || cardText.Contains("Dev", StringComparison.OrdinalIgnoreCase),
                "architect" or "arch" => cardText.Contains("Architect", StringComparison.OrdinalIgnoreCase) || cardText.Contains("Planner", StringComparison.OrdinalIgnoreCase),
                "reviewer" or "rev" => cardText.Contains("Reviewer", StringComparison.OrdinalIgnoreCase) || cardText.Contains("Quality", StringComparison.OrdinalIgnoreCase),
                "merger" or "mrg" => cardText.Contains("Merger", StringComparison.OrdinalIgnoreCase) || cardText.Contains("Branch Merger", StringComparison.OrdinalIgnoreCase),
                "conflict-resolver" or "res" => cardText.Contains("Conflict", StringComparison.OrdinalIgnoreCase) || cardText.Contains("Resolver", StringComparison.OrdinalIgnoreCase),
                "branch-cleaner" or "cln" => cardText.Contains("Cleaner", StringComparison.OrdinalIgnoreCase) || cardText.Contains("Hygiene", StringComparison.OrdinalIgnoreCase),
                "orchestrator" or "orch" => cardText.Contains("Orchestrator", StringComparison.OrdinalIgnoreCase) || cardText.Contains("Mission", StringComparison.OrdinalIgnoreCase),
                _ => false
            };

            if (matches) return report.State;
        }

        return AgentExecutionState.Idle;
    }

    public void Dispose()
    {
        _disposed = true;
        _watcher?.Dispose();
        _pollTimer?.Dispose();
    }
}
