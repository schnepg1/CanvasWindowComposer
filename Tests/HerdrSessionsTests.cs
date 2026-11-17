using System;
using System.Text.Json;
using Xunit;

namespace CanvasDesktop.Tests;

public class HerdrSessionsTests
{
    [Theory]
    [InlineData("t0")]
    [InlineData("t1")]
    [InlineData("t7")]
    [InlineData("t14")]
    [InlineData("t1b")]
    [InlineData("t1h")]
    [InlineData("t2z")]
    [InlineData("T1H")]
    [InlineData("T14")]
    public void LocalAddressMatchesDecimalAndBase36Identifiers(string address)
    {
        Assert.Matches(HerdrSessions.LocalAddress(), address);
    }

    [Theory]
    [InlineData("")]
    [InlineData("t")]
    [InlineData("14")]
    [InlineData("1h")]
    [InlineData("machine/t14")]
    [InlineData("vps/t1h")]
    [InlineData("t-1")]
    [InlineData("t_1")]
    [InlineData("t1h/child")]
    public void LocalAddressRejectsNonLocalOrMalformedAddresses(string address)
    {
        Assert.DoesNotMatch(HerdrSessions.LocalAddress(), address);
    }

    [Fact]
    public void ReadTerminalParsesBase36TerminalAddressSuccessfully()
    {
        using var json = JsonDocument.Parse("""
        {
            "address": "t1h",
            "state": "live",
            "status": "idle",
            "cwd": "G:\\Coding\\Projects\\AI Knowledge Brain"
        }
        """);

        var terminal = HerdrSessions.ReadTerminal(json.RootElement);

        Assert.Equal("t1h", terminal.Address);
        Assert.Equal("live", terminal.State);
        Assert.True(terminal.IsLive);
        Assert.Equal("idle", terminal.AgentState);
        Assert.Equal(@"G:\Coding\Projects\AI Knowledge Brain", terminal.WorkingDirectory);
    }

    [Fact]
    public void ReadTerminalParsesNestedStatusObject()
    {
        using var json = JsonDocument.Parse("""
        {
            "address": "t14",
            "state": "live",
            "status": {
                "app": "codex",
                "state": "working"
            },
            "cwd": "D:\\Videos"
        }
        """);

        var terminal = HerdrSessions.ReadTerminal(json.RootElement);

        Assert.Equal("t14", terminal.Address);
        Assert.Equal("live", terminal.State);
        Assert.True(terminal.IsLive);
        Assert.Equal("working", terminal.AgentState);
        Assert.Equal(@"D:\Videos", terminal.WorkingDirectory);
    }

    [Fact]
    public void ReadTerminalThrowsWhenAddressIsNonLocal()
    {
        using var json = JsonDocument.Parse("""
        {
            "address": "vps/t7",
            "state": "live",
            "status": "idle"
        }
        """);

        var ex = Assert.Throws<InvalidOperationException>(() => HerdrSessions.ReadTerminal(json.RootElement));
        Assert.Equal("Herdr did not return a local terminal ID.", ex.Message);
    }

    [Fact]
    public void ReadTerminalThrowsWhenAddressIsMissing()
    {
        using var json = JsonDocument.Parse("""
        {
            "state": "live",
            "status": "idle"
        }
        """);

        var ex = Assert.Throws<InvalidOperationException>(() => HerdrSessions.ReadTerminal(json.RootElement));
        Assert.Equal("Herdr did not return a local terminal ID.", ex.Message);
    }

    [Fact]
    public void SupportedAgentsIncludesCoreAgentsWithDefaults()
    {
        Assert.Contains(HerdrSessions.SupportedAgents, a => a.Id == "codex" && a.DisplayName == "Codex");
        Assert.Contains(HerdrSessions.SupportedAgents, a => a.Id == "claude" && a.DisplayName == "Claude Code");
        Assert.Contains(HerdrSessions.SupportedAgents, a => a.Id == "gemini" && a.DisplayName == "Gemini CLI");
        Assert.Contains(HerdrSessions.SupportedAgents, a => a.Id == "aider" && a.DisplayName == "Aider");
        Assert.Contains(HerdrSessions.SupportedAgents, a => a.Id == "opencode" && a.DisplayName == "OpenCode");

        Assert.Equal("codex", HerdrSessions.DefaultAgent.Id);
        Assert.Equal("claude", HerdrSessions.FindAgent("claude").Id);
        Assert.Equal("codex", HerdrSessions.FindAgent("non_existent").Id);
    }

    [Fact]
    public async Task StartAgentRejectsInvalidWorkingFolder()
    {
        var sessions = new HerdrSessions();
        var agent = HerdrSessions.DefaultAgent;
        var card = Guid.NewGuid();

        await Assert.ThrowsAsync<HerdrException>(() =>
            sessions.StartAgent(agent, "relative\\path", card, default));

        await Assert.ThrowsAsync<HerdrException>(() =>
            sessions.StartAgent(agent, "C:\\NonExistentFolder_123456789", card, default));
    }

    [Theory]
    [InlineData("agy.exe", true)]
    [InlineData("agy.cmd", true)]
    [InlineData("agy.bat", true)]
    [InlineData(@"C:\Users\Dev\AppData\Local\agy\bin\agy.exe", true)]
    [InlineData(@"C:\Tools\AGY.CMD", true)]
    [InlineData("gemini.exe", false)]
    [InlineData("gemini.cmd", false)]
    [InlineData("codex.cmd", false)]
    [InlineData("myagy.exe", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsAntigravityExecutableIdentifiesAgyBinariesOnly(string? executablePath, bool expected)
    {
        Assert.Equal(expected, HerdrSessions.IsAntigravityExecutable(executablePath));
    }

    [Fact]
    public void BuildAgentArgsEnablesTurboPermissionsForAgyWithoutPrompt()
    {
        var geminiProfile = HerdrSessions.FindAgent("gemini");
        var agyPath = @"C:\Users\Dev\AppData\Local\agy\bin\agy.exe";

        Assert.Equal(["--dangerously-skip-permissions"], HerdrSessions.BuildAgentArgs(geminiProfile, agyPath));
        Assert.Equal(["--dangerously-skip-permissions"], HerdrSessions.BuildAgentArgs(geminiProfile, agyPath, "   "));
    }

    [Fact]
    public void BuildAgentArgsUsesPromptInteractiveAndTurboPermissionsForAgyWithPrompt()
    {
        var geminiProfile = HerdrSessions.FindAgent("gemini");
        var agyPath = @"C:\Users\Dev\AppData\Local\agy\bin\agy.cmd";

        var args = HerdrSessions.BuildAgentArgs(geminiProfile, agyPath, "Investigate permissions");

        Assert.Equal(["--dangerously-skip-permissions", "--prompt-interactive", "Investigate permissions"], args);
    }

    [Fact]
    public void BuildAgentArgsStripsGeminiYoloAndDeduplicatesTurboFlagForAgy()
    {
        var profileWithYolo = new AgentProfile(
            "gemini",
            "Gemini CLI",
            "Google Gemini CLI / Antigravity",
            ["gemini.cmd", "agy.exe"],
            ["--yolo", "--dangerously-skip-permissions"],
            "#2563EB");

        var args = HerdrSessions.BuildAgentArgs(profileWithYolo, @"C:\bin\agy.exe");

        Assert.Equal(["--dangerously-skip-permissions"], args);
    }

    [Fact]
    public void BuildAgentArgsPreservesNonAgyCliArguments()
    {
        var geminiProfile = HerdrSessions.FindAgent("gemini");

        var argsNoPrompt = HerdrSessions.BuildAgentArgs(geminiProfile, @"C:\tools\nodejs\gemini.cmd");
        Assert.Empty(argsNoPrompt);

        var argsWithPrompt = HerdrSessions.BuildAgentArgs(geminiProfile, @"C:\tools\nodejs\gemini.cmd", "Run tests");
        Assert.Equal(["Run tests"], argsWithPrompt);

        var codexProfile = HerdrSessions.FindAgent("codex");
        var codexArgs = HerdrSessions.BuildAgentArgs(codexProfile, @"C:\tools\nodejs\codex.cmd", "Fix bug");
        Assert.Equal(["--no-daemon", "Fix bug"], codexArgs);
    }

    [Fact]
    public void BuildRunArgsPassesAgyExecutableWithTurboFlagAfterHerdrSeparator()
    {
        var geminiProfile = HerdrSessions.FindAgent("gemini");
        var agyPath = @"C:\Users\Dev\AppData\Local\agy\bin\agy.exe";
        var cardId = Guid.Parse("8a0fe3a5-e00b-4c29-8e85-a52f88aa751c");
        var folder = @"C:\Projects\CanvasWindowComposer";

        var runArgs = HerdrSessions.BuildRunArgs(geminiProfile, agyPath, folder, cardId);

        Assert.Equal(
            [
                "run", "--json", "--bg", "--no-parent", "--cwd", folder,
                "--name", "8a0fe3a5e00b4c298e85a52f88aa751c",
                "--label", "canvas_managed=1",
                "--label", "canvas_agent=8a0fe3a5e00b4c298e85a52f88aa751c",
                "--label", "agent_type=gemini",
                "--", agyPath, "--dangerously-skip-permissions"
            ],
            runArgs);

        var runArgsWithPrompt = HerdrSessions.BuildRunArgs(geminiProfile, agyPath, folder, cardId, "Start task");

        Assert.Equal(
            [
                "run", "--json", "--bg", "--no-parent", "--cwd", folder,
                "--name", "8a0fe3a5e00b4c298e85a52f88aa751c",
                "--label", "canvas_managed=1",
                "--label", "canvas_agent=8a0fe3a5e00b4c298e85a52f88aa751c",
                "--label", "agent_type=gemini",
                "--", agyPath, "--dangerously-skip-permissions", "--prompt-interactive", "Start task"
            ],
            runArgsWithPrompt);
    }
}
