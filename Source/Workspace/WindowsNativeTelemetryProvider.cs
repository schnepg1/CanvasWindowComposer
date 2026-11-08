using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using CanvasDesktop.CanvasView;

namespace CanvasDesktop;

internal sealed class WindowsNativeTelemetryProvider : ITeamTelemetryProvider
{
    public const string NamedMutexName = @"Global\CanvasWindowComposer.Tests";
    private static readonly Regex TaskIdRegex = new(@"\[(?<taskId>[a-zA-Z0-9_\-]+)\]", RegexOptions.Compiled);

    private readonly string _stateFilePath;
    private readonly Func<bool>? _mutexProbe;
    private readonly FileSystemWatcher? _watcher;
    private readonly System.Threading.Timer? _pollTimer;
    private readonly object _syncLock = new();
    private string? _lastWrittenJson;
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
        var teams = _current.Teams != null
            ? new Dictionary<string, TeamInstanceTelemetry>(_current.Teams, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, TeamInstanceTelemetry>(StringComparer.OrdinalIgnoreCase);

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

            // Also update all active teams
            foreach (var (tKey, team) in teams.ToList())
            {
                if (string.Equals(team.Status, "Active", StringComparison.OrdinalIgnoreCase))
                {
                    var teamRoles = new Dictionary<string, AgentStatusReport>(team.Roles, StringComparer.OrdinalIgnoreCase);
                    bool teamChanged = false;

                    if (!teamRoles.TryGetValue("programmer", out var tProg) || tProg.State != AgentExecutionState.Testing)
                    {
                        teamRoles["programmer"] = new AgentStatusReport("programmer", AgentExecutionState.Testing, "Executing test suite under named mutex", DateTimeOffset.UtcNow, team.TaskId, team.Worktree);
                        teamChanged = true;
                    }
                    if (!teamRoles.TryGetValue("reviewer", out var tRev) || tRev.State != AgentExecutionState.Testing)
                    {
                        teamRoles["reviewer"] = new AgentStatusReport("reviewer", AgentExecutionState.Testing, "Verifying tests under named mutex", DateTimeOffset.UtcNow, team.TaskId, team.Worktree);
                        teamChanged = true;
                    }

                    if (teamChanged)
                    {
                        teams[tKey] = team with { Roles = teamRoles, UpdatedAt = DateTimeOffset.UtcNow };
                        stateChanged = true;
                    }
                }
            }
        }

        if (stateChanged)
        {
            _current = new TeamTelemetrySnapshot(roles, DateTimeOffset.UtcNow, teams, _current.ActiveTaskId);
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
                string json = "";
                bool readSuccess = false;

                for (int attempt = 0; attempt < 3; attempt++)
                {
                    try
                    {
                        lock (_syncLock)
                        {
                            json = File.ReadAllText(_stateFilePath);
                        }
                        readSuccess = true;
                        break;
                    }
                    catch (IOException) when (attempt < 2)
                    {
                        Thread.Sleep(25);
                    }
                }

                if (!readSuccess || string.IsNullOrWhiteSpace(json)) return;

                if (json == _lastWrittenJson) return;
                _lastWrittenJson = json;
                using var doc = JsonDocument.Parse(json);
                var rootRoles = new Dictionary<string, AgentStatusReport>(StringComparer.OrdinalIgnoreCase);
                var teams = new Dictionary<string, TeamInstanceTelemetry>(StringComparer.OrdinalIgnoreCase);
                string? activeTaskId = null;
                DateTimeOffset updatedAt = DateTimeOffset.UtcNow;

                if (doc.RootElement.TryGetProperty("updatedAt", out var updProp) &&
                    updProp.ValueKind == JsonValueKind.String &&
                    DateTimeOffset.TryParse(updProp.GetString(), out var parsedUpd))
                {
                    updatedAt = parsedUpd;
                }

                if (doc.RootElement.TryGetProperty("activeTaskId", out var actProp) &&
                    actProp.ValueKind == JsonValueKind.String)
                {
                    activeTaskId = actProp.GetString();
                }

                if (doc.RootElement.TryGetProperty("roles", out var rolesElem) && rolesElem.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in rolesElem.EnumerateObject())
                    {
                        string role = prop.Name;
                        string stateStr = prop.Value.TryGetProperty("state", out var s) ? s.GetString() ?? "Idle" : "Idle";
                        string detail = prop.Value.TryGetProperty("detail", out var d) ? d.GetString() ?? "" : "";
                        DateTimeOffset ts = updatedAt;
                        if (prop.Value.TryGetProperty("timestamp", out var tsProp) &&
                            DateTimeOffset.TryParse(tsProp.GetString(), out var parsedTs))
                        {
                            ts = parsedTs;
                        }

                        if (Enum.TryParse<AgentExecutionState>(stateStr, true, out var state))
                        {
                            rootRoles[role] = new AgentStatusReport(role, state, detail, ts);
                        }
                    }
                }

                if (doc.RootElement.TryGetProperty("teams", out var teamsElem) && teamsElem.ValueKind == JsonValueKind.Object)
                {
                    foreach (var tProp in teamsElem.EnumerateObject())
                    {
                        string taskId = tProp.Name;
                        var tObj = tProp.Value;
                        string name = tObj.TryGetProperty("name", out var n) ? n.GetString() ?? taskId : taskId;
                        string convId = tObj.TryGetProperty("conversationId", out var c) ? c.GetString() ?? "" : "";
                        string worktree = tObj.TryGetProperty("worktree", out var w) ? w.GetString() ?? "" : "";
                        string activeRole = tObj.TryGetProperty("activeRole", out var ar) ? ar.GetString() ?? "orchestrator" : "orchestrator";
                        string status = tObj.TryGetProperty("status", out var st) ? st.GetString() ?? "Active" : "Active";
                        DateTimeOffset teamUpdated = updatedAt;
                        if (tObj.TryGetProperty("updatedAt", out var tuProp) &&
                            DateTimeOffset.TryParse(tuProp.GetString(), out var parsedTu))
                        {
                            teamUpdated = parsedTu;
                        }

                        var teamRoles = new Dictionary<string, AgentStatusReport>(StringComparer.OrdinalIgnoreCase);
                        if (tObj.TryGetProperty("roles", out var trElem) && trElem.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var rProp in trElem.EnumerateObject())
                            {
                                string role = rProp.Name;
                                string stateStr = rProp.Value.TryGetProperty("state", out var rs) ? rs.GetString() ?? "Idle" : "Idle";
                                string detail = rProp.Value.TryGetProperty("detail", out var rd) ? rd.GetString() ?? "" : "";
                                DateTimeOffset ts = teamUpdated;
                                if (rProp.Value.TryGetProperty("timestamp", out var rtsProp) &&
                                    DateTimeOffset.TryParse(rtsProp.GetString(), out var parsedRts))
                                {
                                    ts = parsedRts;
                                }

                                if (Enum.TryParse<AgentExecutionState>(stateStr, true, out var state))
                                {
                                    teamRoles[role] = new AgentStatusReport(role, state, detail, ts, taskId, worktree);
                                }
                            }
                        }

                        teams[taskId] = new TeamInstanceTelemetry(
                            taskId, name, convId, worktree, activeRole, status, teamRoles, teamUpdated);
                    }
                }

                // If root roles was empty but teams exists, project active team (or first team)
                if (rootRoles.Count == 0 && teams.Count > 0)
                {
                    var primaryTeam = (!string.IsNullOrEmpty(activeTaskId) && teams.TryGetValue(activeTaskId, out var at))
                        ? at
                        : teams.Values.First();

                    foreach (var (rName, rReport) in primaryTeam.Roles)
                    {
                        rootRoles[rName] = rReport;
                    }
                }

                _current = new TeamTelemetrySnapshot(rootRoles, updatedAt, teams, activeTaskId);
                TelemetryUpdated?.Invoke(_current);
            }
        }
        catch { }
    }

    private void PersistState(
        IReadOnlyDictionary<string, AgentStatusReport> rootRoles,
        IReadOnlyDictionary<string, TeamInstanceTelemetry>? teams,
        string? activeTaskId)
    {
        lock (_syncLock)
        {
            try
            {
                var dir = Path.GetDirectoryName(_stateFilePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                var rootObj = new Dictionary<string, object>();
                rootObj["updatedAt"] = DateTimeOffset.UtcNow.ToString("o");
                if (!string.IsNullOrEmpty(activeTaskId))
                    rootObj["activeTaskId"] = activeTaskId;

                var rolesDict = new Dictionary<string, object>();
                foreach (var r in rootRoles)
                {
                    rolesDict[r.Key] = new { state = r.Value.State.ToString(), detail = r.Value.Detail, timestamp = r.Value.Timestamp.ToString("o") };
                }
                rootObj["roles"] = rolesDict;

                if (teams != null && teams.Count > 0)
                {
                    var teamsDict = new Dictionary<string, object>();
                    foreach (var (tKey, tVal) in teams)
                    {
                        var teamRolesDict = new Dictionary<string, object>();
                        foreach (var tr in tVal.Roles)
                        {
                            teamRolesDict[tr.Key] = new { state = tr.Value.State.ToString(), detail = tr.Value.Detail, timestamp = tr.Value.Timestamp.ToString("o") };
                        }

                        teamsDict[tKey] = new
                        {
                            name = tVal.Name,
                            conversationId = tVal.ConversationId,
                            worktree = tVal.Worktree,
                            activeRole = tVal.ActiveRole,
                            status = tVal.Status,
                            updatedAt = tVal.UpdatedAt.ToString("o"),
                            roles = teamRolesDict
                        };
                    }
                    rootObj["teams"] = teamsDict;
                }

                string json = JsonSerializer.Serialize(rootObj, new JsonSerializerOptions { WriteIndented = true });
                _lastWrittenJson = json;
                File.WriteAllText(_stateFilePath, json);
            }
            catch { }
        }
    }

    public void SetState(string role, AgentExecutionState state, string detail = "")
    {
        var roles = new Dictionary<string, AgentStatusReport>(_current.Roles, StringComparer.OrdinalIgnoreCase);
        roles[role] = new AgentStatusReport(role, state, detail, DateTimeOffset.UtcNow);

        var teams = _current.Teams != null
            ? new Dictionary<string, TeamInstanceTelemetry>(_current.Teams, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, TeamInstanceTelemetry>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrEmpty(_current.ActiveTaskId) && teams.TryGetValue(_current.ActiveTaskId, out var activeTeam))
        {
            var teamRoles = new Dictionary<string, AgentStatusReport>(activeTeam.Roles, StringComparer.OrdinalIgnoreCase);
            teamRoles[role] = new AgentStatusReport(role, state, detail, DateTimeOffset.UtcNow, activeTeam.TaskId, activeTeam.Worktree);
            teams[_current.ActiveTaskId] = activeTeam with
            {
                ActiveRole = role,
                Roles = teamRoles,
                UpdatedAt = DateTimeOffset.UtcNow
            };
        }

        _current = new TeamTelemetrySnapshot(roles, DateTimeOffset.UtcNow, teams, _current.ActiveTaskId);
        PersistState(roles, teams, _current.ActiveTaskId);
        TelemetryUpdated?.Invoke(_current);
    }

    public void SetTeamState(string taskId, string role, AgentExecutionState state, string detail = "")
    {
        var teams = _current.Teams != null
            ? new Dictionary<string, TeamInstanceTelemetry>(_current.Teams, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, TeamInstanceTelemetry>(StringComparer.OrdinalIgnoreCase);

        if (!teams.TryGetValue(taskId, out var team))
        {
            team = new TeamInstanceTelemetry(
                taskId, $"Team {taskId}", "", "", role, "Active",
                new Dictionary<string, AgentStatusReport>(StringComparer.OrdinalIgnoreCase), DateTimeOffset.UtcNow);
        }

        var teamRoles = new Dictionary<string, AgentStatusReport>(team.Roles, StringComparer.OrdinalIgnoreCase);
        teamRoles[role] = new AgentStatusReport(role, state, detail, DateTimeOffset.UtcNow, taskId, team.Worktree);
        teams[taskId] = team with
        {
            ActiveRole = role,
            Roles = teamRoles,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        var roles = new Dictionary<string, AgentStatusReport>(_current.Roles, StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(_current.ActiveTaskId) || string.Equals(_current.ActiveTaskId, taskId, StringComparison.OrdinalIgnoreCase) || teams.Count <= 1)
        {
            roles[role] = new AgentStatusReport(role, state, detail, DateTimeOffset.UtcNow, taskId, team.Worktree);
        }

        string activeTaskId = string.IsNullOrEmpty(_current.ActiveTaskId) ? taskId : _current.ActiveTaskId;

        _current = new TeamTelemetrySnapshot(roles, DateTimeOffset.UtcNow, teams, activeTaskId);
        PersistState(roles, teams, activeTaskId);
        TelemetryUpdated?.Invoke(_current);
    }

    public void RegisterTeam(string taskId, string name, string conversationId = "", string worktree = "")
    {
        var teams = _current.Teams != null
            ? new Dictionary<string, TeamInstanceTelemetry>(_current.Teams, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, TeamInstanceTelemetry>(StringComparer.OrdinalIgnoreCase);

        var existingRoles = teams.TryGetValue(taskId, out var existing)
            ? existing.Roles
            : new Dictionary<string, AgentStatusReport>(StringComparer.OrdinalIgnoreCase);

        teams[taskId] = new TeamInstanceTelemetry(
            taskId,
            string.IsNullOrEmpty(name) ? $"Team {taskId}" : name,
            conversationId,
            worktree,
            "orchestrator",
            "Active",
            existingRoles,
            DateTimeOffset.UtcNow);

        string activeTaskId = string.IsNullOrEmpty(_current.ActiveTaskId) ? taskId : _current.ActiveTaskId;

        _current = new TeamTelemetrySnapshot(_current.Roles, DateTimeOffset.UtcNow, teams, activeTaskId);
        PersistState(_current.Roles, teams, activeTaskId);
        TelemetryUpdated?.Invoke(_current);
    }

    public void CompleteTeam(string taskId, bool success = true)
    {
        var teams = _current.Teams != null
            ? new Dictionary<string, TeamInstanceTelemetry>(_current.Teams, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, TeamInstanceTelemetry>(StringComparer.OrdinalIgnoreCase);

        if (teams.TryGetValue(taskId, out var team))
        {
            teams[taskId] = team with
            {
                Status = success ? "Completed" : "Failed",
                UpdatedAt = DateTimeOffset.UtcNow
            };

            _current = new TeamTelemetrySnapshot(_current.Roles, DateTimeOffset.UtcNow, teams, _current.ActiveTaskId);
            PersistState(_current.Roles, teams, _current.ActiveTaskId);
            TelemetryUpdated?.Invoke(_current);
        }
    }

    public void UnregisterTeam(string taskId)
    {
        var teams = _current.Teams != null
            ? new Dictionary<string, TeamInstanceTelemetry>(_current.Teams, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, TeamInstanceTelemetry>(StringComparer.OrdinalIgnoreCase);

        if (teams.Remove(taskId))
        {
            string? activeTaskId = _current.ActiveTaskId;
            if (string.Equals(activeTaskId, taskId, StringComparison.OrdinalIgnoreCase))
            {
                activeTaskId = teams.Keys.FirstOrDefault();
            }

            _current = new TeamTelemetrySnapshot(_current.Roles, DateTimeOffset.UtcNow, teams, activeTaskId);
            PersistState(_current.Roles, teams, activeTaskId);
            TelemetryUpdated?.Invoke(_current);
        }
    }

    public AgentExecutionState GetStateForCard(string cardText)
    {
        return GetStateForCard(cardText, taskId: null);
    }

    public AgentExecutionState GetStateForCard(string cardText, string? taskId)
    {
        if (!string.IsNullOrWhiteSpace(taskId) && _current.Teams != null && _current.Teams.TryGetValue(taskId, out var team))
        {
            var state = MatchCardToRole(cardText, team.Roles);
            if (state != AgentExecutionState.Idle) return state;
        }

        return MatchCardToRole(cardText, _current.Roles);
    }

    public AgentExecutionState GetStateForAnnotation(CanvasAnnotation item)
    {
        if (!string.IsNullOrWhiteSpace(item.Text))
        {
            var match = TaskIdRegex.Match(item.Text);
            if (match.Success)
            {
                string taskId = match.Groups["taskId"].Value;
                return GetStateForCard(item.Text, taskId);
            }
        }

        return GetStateForCard(item.Text);
    }

    private static AgentExecutionState MatchCardToRole(string cardText, IReadOnlyDictionary<string, AgentStatusReport> roles)
    {
        if (string.IsNullOrWhiteSpace(cardText) || roles.Count == 0) return AgentExecutionState.Idle;

        foreach (var (roleKey, report) in roles)
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
