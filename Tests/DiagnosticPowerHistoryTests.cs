using System.Text.Json;
using CanvasDesktop;
using Microsoft.Win32;

namespace CanvasDesktop.Tests;

public class DiagnosticPowerHistoryTests
{
    [Fact]
    public void SamplesRetainSleepResumeContextBoundTheHistoryAndIgnoreNotificationsAfterDisposal()
    {
        DateTime now = new(2026, 10, 30, 15, 0, 0, DateTimeKind.Utc);
        using var history = new DiagnosticPowerHistory(subscribe: false, utcNow: () => now);
        history.Record(PowerModes.Suspend);
        now = now.AddHours(3);
        history.Record(PowerModes.Resume);
        using var first = JsonDocument.Parse(JsonSerializer.Serialize(history.Snapshot()));
        var events = first.RootElement.GetProperty("RecentEvents");
        Assert.Equal("Suspend", events[0].GetProperty("Mode").GetString());
        Assert.Equal("Resume", events[1].GetProperty("Mode").GetString());
        Assert.Equal(now, events[1].GetProperty("CapturedUtc").GetDateTime());
        for (int i = 0; i < 40; i++) history.Record(PowerModes.StatusChange);
        history.Dispose();
        history.Record(PowerModes.Resume);
        using var last = JsonDocument.Parse(JsonSerializer.Serialize(history.Snapshot()));
        Assert.Equal(42, last.RootElement.GetProperty("TotalEvents").GetInt64());
        Assert.Equal(10, last.RootElement.GetProperty("DiscardedEvents").GetInt64());
        Assert.Equal(32, last.RootElement.GetProperty("RecentEvents").GetArrayLength());
        Assert.False(last.RootElement.GetProperty("NotificationsEnabled").GetBoolean());
        Assert.Equal(2, first.RootElement.GetProperty("RecentEvents").GetArrayLength());
    }
}
