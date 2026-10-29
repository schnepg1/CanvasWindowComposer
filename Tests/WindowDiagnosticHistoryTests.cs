using System.IO.Compression;
using System.Text.Json;
using CanvasDesktop;
using Forms = System.Windows.Forms;

namespace CanvasDesktop.Tests;

public class WindowDiagnosticHistoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "CanvasHistory-" + Guid.NewGuid().ToString("N"));
    private string Samples => Path.Combine(_root, "rolling");
    private string Reports => Path.Combine(_root, "reports");
    private static readonly DateTime Epoch = new(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void RetentionRemovesOldSamplesAndPreservesAnIndependentReadableReport()
    {
        var store = new WindowDiagnosticHistoryStore(Samples, Reports);
        store.Append(Epoch, new { Label = "expired" });
        store.Append(Epoch.AddMinutes(29), new { Label = "before bug" });
        store.Append(Epoch.AddMinutes(31), new { Label = "bug noticed" });
        string report = store.Preserve(Epoch.AddMinutes(31));
        var lines = ReadSamples(report);
        Assert.Equal(2, lines.Length);
        Assert.Contains("before bug", lines[0]);
        Assert.Contains("bug noticed", lines[1]);
        using (var archive = ZipFile.OpenRead(report))
        using (var manifest = JsonDocument.Parse(archive.GetEntry("manifest.json")!.Open()))
        {
            Assert.Equal(30, manifest.RootElement.GetProperty("RetentionMinutes").GetInt32());
            Assert.Equal(Epoch.AddMinutes(29), manifest.RootElement.GetProperty("OldestSampleUtc").GetDateTime());
        }
        byte[] saved = File.ReadAllBytes(report);
        // History survives reopening; a saved report survives later rolling cleanup.
        var restarted = new WindowDiagnosticHistoryStore(Samples, Reports);
        restarted.Append(Epoch.AddHours(2), new { Label = "after restart" });
        Assert.Single(Directory.GetFiles(Samples, "*.json.gz"));
        Assert.Equal(saved, File.ReadAllBytes(report));
    }

    [Fact]
    public void CountAndByteLimitsEvictOldestOwnedSamples()
    {
        var countLimited = new WindowDiagnosticHistoryStore(Samples, Reports, maximumSamples: 3);
        for (int i = 0; i < 8; i++) countLimited.Append(Epoch.AddSeconds(i), new { Sequence = i });
        Assert.Equal(3, Directory.GetFiles(Samples, "*.json.gz").Length);
        var lines = ReadSamples(countLimited.Preserve(Epoch.AddSeconds(8)));
        Assert.Contains("\"Sequence\":5", lines[0]);
        Assert.Contains("\"Sequence\":7", lines[^1]);

        var bytesLimited = new WindowDiagnosticHistoryStore(Samples, Reports, maximumBytes: 1800);
        for (int i = 0; i < 6; i++)
            bytesLimited.Append(Epoch.AddSeconds(10 + i), new { Noise = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(700)) });
        Assert.InRange(new DirectoryInfo(Samples).GetFiles("*.json.gz").Sum(f => f.Length), 1, 1800);
    }

    [Fact]
    public void OversizedSampleDoesNotDestroyExistingHistoryOrLeaveAPartialFile()
    {
        var store = new WindowDiagnosticHistoryStore(Samples, Reports, maximumBytes: 1024);
        store.Append(Epoch, new { Label = "keep" });
        Assert.Throws<IOException>(() => store.Append(Epoch.AddSeconds(1), new
            { Noise = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(3000)) }));
        Assert.Single(Directory.GetFiles(Samples, "*.json.gz"));
        Assert.Empty(Directory.GetFiles(Samples, "*.tmp"));
        Assert.Contains("keep", Assert.Single(ReadSamples(store.Preserve(Epoch))));
    }

    [Fact]
    public void CleanupIgnoresFilesWhichDoNotHaveTheOwnedSampleName()
    {
        var store = new WindowDiagnosticHistoryStore(Samples, Reports);
        store.Append(Epoch, new { Label = "old" });
        string foreign = Path.Combine(Samples, "sample-unrelated.json.gz");
        File.WriteAllText(foreign, "leave intact");
        store.Append(Epoch.AddHours(1), new { Label = "new" });
        Assert.Equal("leave intact", File.ReadAllText(foreign));
        Assert.Contains("new", Assert.Single(ReadSamples(store.Preserve(Epoch.AddHours(1)))));
    }

    [Fact]
    public void AutomaticCollectorIncludesHistoryFromBeforePreservationWithoutManualRecording()
        => WorkspaceViewTests.OnSta(() =>
        {
            using var dispatcher = new Forms.Control(); _ = dispatcher.Handle;
            var store = new WindowDiagnosticHistoryStore(Samples, Reports);
            DateTime now = Epoch;
            int captures = 0;
            using var rolling = new RollingWindowDiagnostics(store, () => new { Number = ++captures },
                intervalMilliseconds: 50, native: () => new { TestNative = true }, utcNow: () => now);
            rolling.Start();
            PumpUntil(() => Directory.Exists(Samples) && Directory.GetFiles(Samples, "*.json.gz").Length >= 2);
            now = Epoch.AddMinutes(2);
            var preservation = rolling.PreserveAsync();
            PumpUntil(() => preservation.IsCompleted);
            var lines = ReadSamples(preservation.GetAwaiter().GetResult());
            Assert.True(lines.Length >= 2);
            Assert.Contains("\"Sequence\":1", lines[0]);
            Assert.All(lines, line => Assert.Contains("\"TestNative\":true", line));
            rolling.Dispose();
            rolling.CaptureNowAsync().GetAwaiter().GetResult(); // Finish any sample already in flight before removing the test directory.
        });

    [Fact]
    public void EventHistoryIsBoundedAndReportsDiscardedEvents()
    {
        var history = new WindowEventHistory(capacity: 3);
        for (uint i = 0; i < 5; i++) history.Record(0x8002, i, "TaskListThumbnailWnd", i);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(history.Snapshot()));
        Assert.Equal(5, json.RootElement.GetProperty("TotalEvents").GetInt64());
        Assert.Equal(2, json.RootElement.GetProperty("DiscardedEvents").GetInt64());
        var events = json.RootElement.GetProperty("RecentEvents");
        Assert.Equal(3, events.GetArrayLength());
        Assert.Equal(3, events[0].GetProperty("Sequence").GetInt64());
    }

    [Fact]
    public void PreservationStillWorksWhenTheCurrentNativeInspectionIsStuck()
        => WorkspaceViewTests.OnSta(() =>
        {
            using var dispatcher = new Forms.Control(); _ = dispatcher.Handle;
            using var release = new ManualResetEventSlim();
            var store = new WindowDiagnosticHistoryStore(Samples, Reports);
            store.Append(Epoch, new { Label = "evidence before the hang" });
            using var rolling = new RollingWindowDiagnostics(store, () => new { }, native: () =>
            {
                release.Wait(TimeSpan.FromSeconds(7));
                return new { };
            }, utcNow: () => Epoch.AddSeconds(5));
            try
            {
                rolling.Start();
                var preservation = rolling.PreserveAsync();
                PumpUntil(() => preservation.IsCompleted);
                string report = preservation.GetAwaiter().GetResult();
                Assert.Contains("evidence before the hang", Assert.Single(ReadSamples(report)));
                using var archive = ZipFile.OpenRead(report);
                using var manifest = JsonDocument.Parse(archive.GetEntry("manifest.json")!.Open());
                Assert.True(manifest.RootElement.GetProperty("Collector").GetProperty("CurrentSamplePending").GetBoolean());
            }
            finally
            {
                rolling.Dispose(); release.Set(); rolling.CaptureNowAsync().GetAwaiter().GetResult();
            }
        });

    private static string[] ReadSamples(string report)
    {
        using var archive = ZipFile.OpenRead(report);
        using var reader = new StreamReader(archive.GetEntry("samples.jsonl")!.Open());
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    private static void PumpUntil(Func<bool> done)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (!done() && elapsed.Elapsed.TotalSeconds < 5) { Forms.Application.DoEvents(); Thread.Sleep(10); }
        Assert.True(done());
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
