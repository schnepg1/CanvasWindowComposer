using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;

namespace CanvasDesktop;

/// <summary>Immutable compressed samples, bounded by age, bytes and count across app restarts.</summary>
internal sealed class WindowDiagnosticHistoryStore
{
    internal static readonly TimeSpan Retention = TimeSpan.FromMinutes(30);
    internal const long DefaultMaximumBytes = 64L * 1024 * 1024;
    private readonly string _directory, _reports;
    private readonly long _maximumBytes;
    private readonly int _maximumSamples;
    private readonly object _gate = new();
    private static readonly JsonSerializerOptions Json = new() { IncludeFields = true };

    public WindowDiagnosticHistoryStore(string directory, string reports,
        long maximumBytes = DefaultMaximumBytes, int maximumSamples = 361)
    {
        if (maximumBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        if (maximumSamples <= 0) throw new ArgumentOutOfRangeException(nameof(maximumSamples));
        _directory = Path.GetFullPath(directory); _reports = Path.GetFullPath(reports);
        _maximumBytes = maximumBytes; _maximumSamples = maximumSamples;
    }

    public void Append(DateTime capturedUtc, object sample)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(_directory);
            string path = Path.Combine(_directory, $"sample-{capturedUtc.Ticks:D19}-{Guid.NewGuid():N}.json.gz");
            string pending = path + ".tmp";
            try
            {
                using (var file = File.Create(pending))
                using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
                {
                    JsonSerializer.Serialize(gzip, sample, Json);
                    gzip.WriteByte((byte)'\n');
                }
                if (new FileInfo(pending).Length > _maximumBytes)
                    throw new IOException("A diagnostic sample exceeds the history storage limit.");
                File.Move(pending, path);
            }
            finally { if (File.Exists(pending)) File.Delete(pending); }
            Prune(capturedUtc);
        }
    }

    public string Preserve(DateTime nowUtc, object? collectorStatus = null)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(_directory); Directory.CreateDirectory(_reports);
            Prune(nowUtc);
            var samples = Samples();
            if (samples.Length == 0) throw new InvalidOperationException("No diagnostic history is available yet.");
            string path = Path.Combine(_reports, $"window-history-{nowUtc:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.zip");
            string pending = path + ".tmp";
            try
            {
                using (var archive = ZipFile.Open(pending, ZipArchiveMode.Create))
                {
                    using (var manifest = archive.CreateEntry("manifest.json").Open())
                        JsonSerializer.Serialize(manifest, new
                        {
                            SchemaVersion = 1, PreservedAtUtc = nowUtc, RetentionMinutes = 30,
                            MaximumHistoryBytes = _maximumBytes, SampleCount = samples.Length,
                            Collector = collectorStatus,
                            OldestSampleUtc = SampleTime(samples[0]), NewestSampleUtc = SampleTime(samples[^1]),
                            Content = "Window state and event metadata. No titles, URLs or typed content."
                        });
                    using var output = archive.CreateEntry("samples.jsonl", CompressionLevel.Fastest).Open();
                    foreach (var sample in samples)
                    {
                        using var file = sample.OpenRead();
                        using var gzip = new GZipStream(file, CompressionMode.Decompress);
                        gzip.CopyTo(output);
                    }
                }
                File.Move(pending, path);
            }
            finally { if (File.Exists(pending)) File.Delete(pending); }
            return path;
        }
    }

    private FileInfo[] Samples() => new DirectoryInfo(_directory).EnumerateFiles("sample-*.json.gz")
        .Where(f => SampleTime(f) != null).OrderBy(f => f.Name, StringComparer.Ordinal).ToArray();

    private static DateTime? SampleTime(FileInfo file)
    {
        string name = file.Name;
        return name.Length == 67 && name.StartsWith("sample-", StringComparison.Ordinal)
            && name[26] == '-' && name.EndsWith(".json.gz", StringComparison.Ordinal)
            && long.TryParse(name.AsSpan(7, 19), NumberStyles.None, CultureInfo.InvariantCulture, out long ticks)
            && ticks >= DateTime.MinValue.Ticks && ticks <= DateTime.MaxValue.Ticks
            && Guid.TryParseExact(name.Substring(27, 32), "N", out _)
            ? new DateTime(ticks, DateTimeKind.Utc) : null;
    }

    private void Prune(DateTime nowUtc)
    {
        // Delete only files owned by this recorder. Preserved reports are outside
        // this rolling directory and are never part of retention cleanup.
        foreach (var sample in Samples())
            if (SampleTime(sample) < nowUtc - Retention) sample.Delete();
        var remaining = Samples();
        long bytes = remaining.Sum(f => f.Length);
        int count = remaining.Length;
        foreach (var sample in remaining)
        {
            if (bytes <= _maximumBytes && count <= _maximumSamples) break;
            bytes -= sample.Length; count--; sample.Delete();
        }
        foreach (var pending in new DirectoryInfo(_directory).EnumerateFiles("sample-*.json.gz.tmp"))
            if (SampleTime(new FileInfo(pending.FullName[..^4])) != null && pending.LastWriteTimeUtc < nowUtc - Retention)
                pending.Delete();
    }
}
