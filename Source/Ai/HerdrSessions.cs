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

internal sealed record AgentProfile(
    string Id,
    string DisplayName,
    string Subtitle,
    string[] CandidateExecutables,
    string[] DefaultArgs,
    string AccentColorHex
);

internal sealed partial class HerdrSessions
{
    private const string AgentLabel = "canvas_agent";

    public static readonly IReadOnlyList<AgentProfile> SupportedAgents = [
        new("codex", "Codex", "OpenAI Codex CLI", ["codex.cmd", "codex.exe", "codex.bat"], ["--no-daemon"], "#10B981"),
        new("claude", "Claude Code", "Anthropic Claude Code CLI", ["claude.cmd", "claude.exe", "claude.bat"], [], "#EA580C"),
        new("gemini", "Gemini CLI", "Google Gemini CLI / Antigravity", ["gemini.cmd", "gemini.exe", "gemini.bat", "agy.cmd", "agy.exe"], [], "#2563EB"),
        new("aider", "Aider", "Aider AI Pair Programming", ["aider.exe", "aider.cmd", "aider.bat"], [], "#7C3AED"),
        new("opencode", "OpenCode", "OpenCode Autonomous Agent", ["opencode.cmd", "opencode.exe", "opencode.bat"], [], "#DB2777"),
    ];

    public static AgentProfile DefaultAgent => SupportedAgents[0];

    public static AgentProfile FindAgent(string? id) =>
        SupportedAgents.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase)) ?? DefaultAgent;

    internal sealed record Terminal(string Address, string State, string? AgentState, string? WorkingDirectory)
    {
        public bool IsLive => State == "live";
    }

    [GeneratedRegex(@"\At[0-9a-z]+\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    internal static partial Regex LocalAddress();

    public async Task<Terminal> StartAgent(AgentProfile agent, string folder, Guid card, CancellationToken cancellation, string? prompt = null)
    {
        if (!Path.IsPathFullyQualified(folder) || !Directory.Exists(folder))
            throw new InvalidOperationException("Choose an existing local working folder.");
        var executable = FindCandidateOnPath(agent.CandidateExecutables)
            ?? throw new InvalidOperationException($"{agent.DisplayName} ({agent.CandidateExecutables.FirstOrDefault() ?? agent.Id}) was not found on PATH.");
        var args = new List<string>
        {
            "run", "--json", "--bg", "--no-parent", "--cwd", folder,
            "--name", card.ToString("N"), "--label", "canvas_managed=1",
            "--label", $"{AgentLabel}={card:N}",
            "--label", $"agent_type={agent.Id}",
            "--", executable
        };
        args.AddRange(agent.DefaultArgs);
        if (!string.IsNullOrWhiteSpace(prompt)) args.Add(prompt);
        var reply = await Run(cancellation, [.. args]);
        using var json = JsonDocument.Parse(reply);
        return ReadTerminal(json.RootElement);
    }

    public Task<Terminal> StartCodex(string folder, Guid card, CancellationToken cancellation, string? prompt = null) =>
        StartAgent(DefaultAgent, folder, card, cancellation, prompt);

    public async Task SendPrompt(Guid card, string prompt, string? folder, CancellationToken cancellation)
    {
        var terminal = await Find(card, cancellation);
        if (terminal is { IsLive: true })
        {
            await Run(cancellation, "send", terminal.Address, prompt);
            return;
        }
        if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
        {
            await StartCodex(folder, card, cancellation, prompt);
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
        var viewer = new ProcessStartInfo(viewerPath) { UseShellExecute = false };
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
            : throw new InvalidOperationException("Herdr Core (hc.exe) was not found."));
    }

    private static async Task<string> Run(CancellationToken cancellation, params string[] args)
    {
        var start = new ProcessStartInfo(FindExecutable())
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Herdr Core could not start.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            if (process.ExitCode != 0)
                throw new InvalidOperationException("Herdr Core returned an error: " + (await error).Trim());
            return await output;
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(); }
            catch (InvalidOperationException) { }
            if (cancellation.IsCancellationRequested) throw;
            throw new InvalidOperationException("Herdr Core did not respond in time. Refresh before trying again.");
        }
    }
}
