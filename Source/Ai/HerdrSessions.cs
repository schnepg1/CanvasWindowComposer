using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CanvasDesktop;

internal sealed partial class HerdrSessions : IAgentSessionManager
{
    private const string AgentLabel = "canvas_agent";

    public static readonly IReadOnlyList<AgentProfile> SupportedAgents = [
        new("codex", "Codex", "OpenAI Codex CLI", ["codex.cmd", "codex.exe", "codex.bat"], ["--no-daemon"], "#10B981"),
        new("claude", "Claude Code", "Anthropic Claude Code CLI", ["claude.cmd", "claude.exe", "claude.bat"], [], "#EA580C"),
        new("gemini", "Gemini CLI", "Google Gemini CLI / Antigravity", ["gemini.cmd", "gemini.exe", "gemini.bat", "agy.cmd", "agy.exe", "agy.bat"], [], "#2563EB"),
        new("aider", "Aider", "Aider AI Pair Programming", ["aider.exe", "aider.cmd", "aider.bat"], [], "#7C3AED"),
        new("opencode", "OpenCode", "OpenCode Autonomous Agent", ["opencode.cmd", "opencode.exe", "opencode.bat"], [], "#DB2777"),
    ];

    IReadOnlyList<AgentProfile> IAgentSessionManager.SupportedAgents => SupportedAgents;

    public static AgentProfile DefaultAgent => SupportedAgents[0];
    AgentProfile IAgentSessionManager.DefaultAgent => DefaultAgent;

    public static AgentProfile FindAgent(string? id) =>
        SupportedAgents.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase)) ?? DefaultAgent;
    AgentProfile IAgentSessionManager.FindAgent(string? id) => FindAgent(id);

    internal sealed record Terminal(string Address, string State, string? AgentState, string? WorkingDirectory)
    {
        public bool IsLive => State == "live";
    }

    [GeneratedRegex(@"\At[0-9a-z]+\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    internal static partial Regex LocalAddress();

    internal static bool IsAntigravityExecutable(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath)) return false;
        var fileName = Path.GetFileNameWithoutExtension(executablePath);
        return string.Equals(fileName, "agy", StringComparison.OrdinalIgnoreCase);
    }

    internal static List<string> BuildAgentArgs(AgentProfile agent, string executablePath, string? prompt = null)
    {
        var args = new List<string>(agent.DefaultArgs);
        var isAntigravity = IsAntigravityExecutable(executablePath);
        if (isAntigravity)
        {
            // Gemini CLI uses --yolo, which agy.exe rejects; Antigravity CLI uses --dangerously-skip-permissions (Turbo).
            args.RemoveAll(arg => string.Equals(arg, "--yolo", StringComparison.OrdinalIgnoreCase));
            if (!args.Any(arg => string.Equals(arg, "--dangerously-skip-permissions", StringComparison.Ordinal)))
            {
                args.Add("--dangerously-skip-permissions");
            }
        }

        if (!string.IsNullOrWhiteSpace(prompt))
        {
            if (isAntigravity)
            {
                args.Add("--prompt-interactive");
                args.Add(prompt);
            }
            else
            {
                args.Add(prompt);
            }
        }

        return args;
    }

    internal static string[] BuildRunArgs(AgentProfile agent, string executable, string folder, Guid card, string? prompt = null)
    {
        var args = new List<string>
        {
            "run", "--json", "--bg", "--no-parent", "--cwd", folder,
            "--name", card.ToString("N"), "--label", "canvas_managed=1",
            "--label", $"{AgentLabel}={card:N}",
            "--label", $"agent_type={agent.Id}",
            "--", executable
        };
        args.AddRange(BuildAgentArgs(agent, executable, prompt));
        return [.. args];
    }

    public async Task<Terminal> StartAgent(AgentProfile agent, string folder, Guid card, CancellationToken cancellation, string? prompt = null)
    {
        if (!Path.IsPathFullyQualified(folder) || !Directory.Exists(folder))
            throw new HerdrException("Choose an existing local working folder.");
        var executable = FindCandidateOnPath(agent.CandidateExecutables)
            ?? throw new HerdrException($"{agent.DisplayName} ({agent.CandidateExecutables.FirstOrDefault() ?? agent.Id}) was not found on PATH.");
        var reply = await Run(cancellation, BuildRunArgs(agent, executable, folder, card, prompt));
        using var json = JsonDocument.Parse(reply);
        return ReadTerminal(json.RootElement);
    }

    public Task<Terminal> StartCodex(string folder, Guid card, CancellationToken cancellation, string? prompt = null) =>
        StartAgent(DefaultAgent, folder, card, cancellation, prompt);

    public async Task StopAgent(Guid card, CancellationToken cancellation)
    {
        var terminal = await Find(card, cancellation);
        if (terminal is { IsLive: true })
        {
            await Run(cancellation, "kill", terminal.Address);
        }
    }

    public async Task SendPrompt(Guid card, string prompt, string? folder, CancellationToken cancellation, AgentProfile? agent = null)
    {
        var terminal = await Find(card, cancellation);
        if (terminal is { IsLive: true })
        {
            await Run(cancellation, "send", terminal.Address, prompt);
            return;
        }
        if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
        {
            await StartAgent(agent ?? DefaultAgent, folder, card, cancellation, prompt);
        }
    }

    public async Task<Dictionary<Guid, Terminal>> Snapshot(CancellationToken cancellation)
    {
        using var json = JsonDocument.Parse(await Run(cancellation, "ls", "--json", "--all", "--label", "canvas_managed=1"));
        var snapshot = new Dictionary<Guid, Terminal>();
        foreach (var record in json.RootElement.GetProperty("terminals").EnumerateArray())
        {
            if (!Guid.TryParseExact(ReadString(record, "name"), "N", out var id)) continue;
            var terminal = ReadTerminal(record);
            if (!snapshot.ContainsKey(id) || terminal.IsLive) snapshot[id] = terminal;
        }
        return snapshot;
    }

    public async Task<Terminal?> Find(Guid card, CancellationToken cancellation)
    {
        using var json = JsonDocument.Parse(await Run(cancellation, "ls", "--json", "--all", "--label", $"{AgentLabel}={card:N}"));
        // Check ownership rather than trusting persisted tN addresses after a core reset.
        var matches = json.RootElement.GetProperty("terminals").EnumerateArray().Select(ReadTerminal).ToArray();
        var live = matches.Where(t => t.IsLive).ToArray();
        if (live.Length > 1)
            throw new InvalidOperationException("More than one live Herdr terminal is linked to this card.");
        return live.SingleOrDefault() ?? matches.FirstOrDefault();
    }

    public async Task Open(Guid card, string expectedAddress, bool observe, CancellationToken cancellation)
    {
        var terminal = await Find(card, cancellation);
        if (terminal is not { IsLive: true } || terminal.Address != expectedAddress)
            throw new InvalidOperationException("The linked terminal is no longer live. Refresh the card.");

        var viewerPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsApps", "wt.exe");
        if (!File.Exists(viewerPath)) throw new InvalidOperationException("Windows Terminal (wt.exe) was not found.");
        var viewer = new ProcessStartInfo(viewerPath)
        {
            UseShellExecute = false,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        foreach (var arg in new[] { "-w", "new", "new-tab", FindExecutable(), "attach" })
            viewer.ArgumentList.Add(arg);
        if (observe) viewer.ArgumentList.Add("--observe");
        viewer.ArgumentList.Add(card.ToString("N"));
        using var process = Process.Start(viewer) ?? throw new InvalidOperationException("Windows Terminal could not start.");
    }

    internal static Terminal ReadTerminal(JsonElement record)
    {
        var address = ReadString(record, "address");
        if (address == null || !LocalAddress().IsMatch(address))
            throw new InvalidOperationException("Herdr did not return a local terminal ID.");
        var status = record.TryGetProperty("status", out var value) && value.ValueKind == JsonValueKind.Object
            ? ReadString(value, "state") : ReadString(record, "status");
        return new(address, ReadString(record, "state") ?? "", status, ReadString(record, "cwd"));
    }

    private static string? ReadString(JsonElement item, string key)
        => item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? FindOnPath(string executable)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            var path = Path.Combine(directory.Trim('"'), executable);
            if (Path.IsPathFullyQualified(path) && File.Exists(path)) return path;
        }
        return null;
    }

    internal static string? FindCandidateOnPath(IEnumerable<string> candidates)
    {
        foreach (var candidate in candidates)
        {
            var found = FindOnPath(candidate);
            if (found != null) return found;
        }
        return null;
    }

    internal static string FindExecutable()
    {
        var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "herdr-core", "hc.exe");
        return FindOnPath("hc.exe") ?? (File.Exists(installed) ? installed
            : throw new HerdrException("Herdr Core (hc.exe) was not found."));
    }

    internal static bool DisableProcessExecution { get; set; } =
        AppDomain.CurrentDomain.FriendlyName.Contains("testhost", StringComparison.OrdinalIgnoreCase);

    private static async Task<string> Run(CancellationToken cancellation, params string[] args)
    {
        if (DisableProcessExecution)
        {
            if (args.Length > 0 && args[0] == "ls")
                return "{\"terminals\":[]}";
            if (args.Length > 0 && args[0] == "run")
                return "{\"address\":\"t0\",\"state\":\"live\",\"status\":\"idle\",\"cwd\":\"\"}";
            return "{}";
        }

        var start = new ProcessStartInfo(FindExecutable())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new HerdrException("Herdr Core could not start.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            if (process.ExitCode != 0)
                throw new HerdrException("Herdr Core returned an error: " + (await error).Trim());
            return await output;
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(); }
            catch (InvalidOperationException) { }
            if (cancellation.IsCancellationRequested) throw;
            throw new HerdrException("Herdr Core did not respond in time. Refresh before trying again.");
        }
        catch (Exception ex) when (ex is not HerdrException and not OperationCanceledException)
        {
            throw new HerdrException(ex.Message, ex);
        }
    }
}
