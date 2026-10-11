using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace CanvasDesktop;

internal enum NativeOperationStatus { Succeeded, Queued, Failed, Cancelled }

/// <summary>API acknowledgement. Queued does not confirm cross-thread application or a displayed frame.</summary>
internal readonly record struct NativeOperationResult(string Operation, long Window,
    NativeOperationStatus Status, int? ErrorCode, double DurationMs, int ItemCount = 1)
{
    public bool Accepted => Status is NativeOperationStatus.Succeeded or NativeOperationStatus.Queued;
    public static NativeOperationResult Success(string operation, IntPtr window = default)
        => new(operation, window.ToInt64(), NativeOperationStatus.Succeeded, null, 0);
}

internal sealed record WindowBatchResult(NativeOperationResult Summary, IReadOnlyList<NativeOperationResult> Items);
internal sealed record OperationStatistics(string Operation, long Calls, long Succeeded, long Queued,
    long Failed, long Cancelled, double TotalMs, double MaxMs);
internal sealed record PerformanceSnapshot(int SchemaVersion, DateTime CapturedUtc, int ProcessId,
    double CollectionSeconds, int ActiveThumbnails, int PeakThumbnails,
    OperationStatistics[] Operations, NativeOperationResult[] RecentOperations, NativeOperationResult[] RecentFailures);

/// <summary>Bounded, thread-safe in-memory diagnostics. No per-call disk I/O or window titles/paths.</summary>
internal sealed class PerformanceDiagnostics
{
    public static PerformanceDiagnostics Shared { get; } = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, OperationStatistics> _operations = new();
    private readonly Queue<NativeOperationResult> _recent = new();
    private readonly Queue<NativeOperationResult> _failures = new();
    private readonly HashSet<IntPtr> _thumbnails = new();
    private long _started = Stopwatch.GetTimestamp();
    private int _peakThumbnails;

    public NativeOperationResult Complete(string operation, IntPtr window, long started, bool success,
        int? error = null, bool queued = false, int itemCount = 1)
    {
        var result = new NativeOperationResult(operation, window.ToInt64(),
            !success ? NativeOperationStatus.Failed : queued ? NativeOperationStatus.Queued : NativeOperationStatus.Succeeded,
            success ? null : error, Stopwatch.GetElapsedTime(started).TotalMilliseconds, itemCount);
        Record(result);
        return result;
    }

    public void Record(NativeOperationResult result)
    {
        lock (_gate)
        {
            var previous = _operations.GetValueOrDefault(result.Operation)
                ?? new(result.Operation, 0, 0, 0, 0, 0, 0, 0);
            _operations[result.Operation] = previous with
            {
                Calls = previous.Calls + 1,
                Succeeded = previous.Succeeded + (result.Status == NativeOperationStatus.Succeeded ? 1 : 0),
                Queued = previous.Queued + (result.Status == NativeOperationStatus.Queued ? 1 : 0),
                Failed = previous.Failed + (result.Status == NativeOperationStatus.Failed ? 1 : 0),
                Cancelled = previous.Cancelled + (result.Status == NativeOperationStatus.Cancelled ? 1 : 0),
                TotalMs = previous.TotalMs + result.DurationMs,
                MaxMs = Math.Max(previous.MaxMs, result.DurationMs)
            };
            Append(_recent, result, 256);
            if (result.Status == NativeOperationStatus.Failed) Append(_failures, result, 128);
        }
    }

    private static void Append(Queue<NativeOperationResult> queue, NativeOperationResult result, int capacity)
    {
        if (queue.Count == capacity) queue.Dequeue();
        queue.Enqueue(result);
    }

    public void ThumbnailRegistered(IntPtr thumbnail)
    {
        lock (_gate)
        {
            _thumbnails.Add(thumbnail);
            _peakThumbnails = Math.Max(_peakThumbnails, _thumbnails.Count);
        }
    }

    public void ThumbnailUnregistered(IntPtr thumbnail) { lock (_gate) _thumbnails.Remove(thumbnail); }

    public PerformanceSnapshot Snapshot()
    {
        lock (_gate)
            return new(1, DateTime.UtcNow, Environment.ProcessId, Stopwatch.GetElapsedTime(_started).TotalSeconds,
                _thumbnails.Count, _peakThumbnails, _operations.Values.OrderBy(o => o.Operation).ToArray(),
                _recent.ToArray(), _failures.ToArray());
    }

    /// <summary>Begin a measurement phase without forgetting still-live thumbnail registrations.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _operations.Clear(); _recent.Clear(); _failures.Clear();
            _peakThumbnails = _thumbnails.Count;
            _started = Stopwatch.GetTimestamp();
        }
    }
}
