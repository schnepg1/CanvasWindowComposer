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
}
