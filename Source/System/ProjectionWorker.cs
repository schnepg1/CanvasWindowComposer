using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CanvasDesktop;

/// <summary>
/// Offloads the SetWindowPos phase of reprojection to a dedicated thread so the
/// UI thread isn't blocked waiting on slow-drawing windows to ack their moves.
///
/// Single-producer / single-consumer: UI thread builds a batch and calls
/// Schedule; the worker thread consumes the latest scheduled batch via a
/// volatile reference swap and applies it with BatchMove(sync). Newer batches
/// replace older ones that haven't been consumed yet, so the worker always
/// converges on the most recent canvas state.
/// </summary>
internal sealed class ProjectionWorker : IDisposable
{
    private readonly IWindowApi _win32;
    private readonly int _ownerThread = Environment.CurrentManagedThreadId;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _signal = new(false);
    // Held during the ordinary projection batch. ClearPending grabs
    // this briefly after cancelling so the follow-up sync BatchMove can't
    // race the in-flight worker batch on the same HWNDs.
    private readonly object _processLock = new();
    // Doubles as the shutdown signal AND the in-flight-batch canceller.
    // ClearPending cancels + replaces with a fresh CTS so the next batch has
    // a live token; Dispose cancels without replacing.
    private CancellationTokenSource _cts = new();
    private volatile bool _disposed;
    private Job? _pending;
    private PlacementLease? _placementLease;
    private PlacementJob? _pendingPlacement;
    private readonly ConcurrentQueue<(List<BatchMoveItem> Items, WindowBatchResult Result)> _completed = new();

    private sealed class Job
    {
        public required List<BatchMoveItem> Items;
        public bool IsTransient;
        public bool IsAsync;
    }

    private sealed record PlacementJob((BatchMoveItem Item, uint ProcessId)[] Moves,
        TaskCompletionSource<WindowBatchResult> Completion);

    internal sealed class PlacementLease : IDisposable
    {
        private readonly ProjectionWorker _worker;
        private readonly HashSet<IntPtr>? _checkedTargets;
        private readonly Action? _onReleased;
        private Task<WindowBatchResult>? _placement;

        internal PlacementLease(ProjectionWorker worker, ReadOnlySpan<IntPtr> checkedTargets, Action? onReleased)
        {
            _worker = worker;
            _onReleased = onReleased;
            if (checkedTargets.IsEmpty) return;
            _checkedTargets = new(checkedTargets.Length);
            foreach (IntPtr handle in checkedTargets) _checkedTargets.Add(handle);
        }

        public Task<WindowBatchResult> Submit(IReadOnlyList<(BatchMoveItem Item, uint ProcessId)> moves)
        {
            _worker.RequireOwnerThread();
            if (!ReferenceEquals(_worker._placementLease, this) || _placement != null)
                throw new InvalidOperationException("A placement lease can submit one batch.");

            var snapshot = moves.ToArray();
            if (_checkedTargets != null)
                foreach (var (item, _) in snapshot)
                    if (!_checkedTargets.Contains(item.HWnd))
                        throw new InvalidOperationException("Placement targets must belong to the checked cohort.");
            _worker.ValidateTargets(snapshot);
            var completion = new TaskCompletionSource<WindowBatchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _placement = completion.Task;
            Volatile.Write(ref _worker._pendingPlacement, new PlacementJob(snapshot, completion));
            _worker._signal.Set();
            return _placement;
        }

        internal bool IsSubmitted => _placement != null;

        public void Dispose()
        {
            _worker.RequireOwnerThread();
            if (!ReferenceEquals(_worker._placementLease, this)) return;
            if (_placement is { IsCompleted: false })
                throw new InvalidOperationException("Keep the lease until native placement completes.");
            Volatile.Write(ref _worker._placementLease, null);
            _onReleased?.Invoke();
        }
    }

    public ProjectionWorker(IWindowApi win32)
    {
        _win32 = win32;
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "Projection"
        };
        _thread.Start();
    }

    /// <summary>UI thread: hand off the latest batch. Overwrites any earlier pending batch.</summary>
    public void Schedule(
        List<BatchMoveItem> items,
        bool isAsync,
        bool isTransient)
    {
        RequireUnreservedWorker();
        Volatile.Write(ref _pending, new Job { Items = items, IsAsync = isAsync, IsTransient = isTransient });
        _signal.Set();
    }

    /// <summary>
    /// Drop any queued batch and cancel the in-flight one if any. The
    /// in-flight BatchMove exits between items and finalizes the queued prefix.
    /// This shortens the remaining work but cannot interrupt an in-flight USER32 call.
    /// </summary>
    public void ClearPending()
    {
        RequireUnreservedWorker();
        var prev = Interlocked.Exchange(ref _cts, new CancellationTokenSource());
        prev.Cancel();
        prev.Dispose();
        lock (_processLock)
        {
            Interlocked.Exchange(ref _pending, null);
        }
    }

    public bool TryTakeCompleted(out (List<BatchMoveItem> Items, WindowBatchResult Result) completed)
        => _completed.TryDequeue(out completed);

    /// <summary>
    /// Reserve an idle projection worker until the UI has processed its final placement result.
    /// Supply the cohort checked for prior posted moves to restrict submission to those HWNDs.
    /// The caller must also fence direct native writers.
    /// </summary>
    public PlacementLease? TryAcquirePlacementLease(ReadOnlySpan<IntPtr> checkedTargets = default, Action? onReleased = null)
    {
        RequireOwnerThread();
        if (_disposed || _placementLease != null || !Monitor.TryEnter(_processLock)) return null;
        try
        {
            if (Volatile.Read(ref _pending) != null || !_completed.IsEmpty) return null;
            var lease = new PlacementLease(this, checkedTargets, onReleased);
            Volatile.Write(ref _placementLease, lease);
            return lease;
        }
        finally { Monitor.Exit(_processLock); }
    }

    // Owner thread only. Reservation also covers preparation before submission.
    public bool HasReservedPlacement
    {
        get
        {
            RequireOwnerThread();
            return _placementLease != null;
        }
    }

    // Owner thread only. This remains true until the owner releases the completed lease.
    public bool HasSubmittedPlacement => _placementLease?.IsSubmitted == true;

    private void RequireOwnerThread()
    {
        if (Environment.CurrentManagedThreadId != _ownerThread)
            throw new InvalidOperationException("Placement leases belong to the worker's owner thread.");
    }

    private void RequireUnreservedWorker()
    {
        if (Volatile.Read(ref _placementLease) != null)
            throw new InvalidOperationException("The placement lease must be released before using the ordinary projection worker.");
    }

    private void ValidateTargets((BatchMoveItem Item, uint ProcessId)[] moves)
    {
        foreach (var (item, processId) in moves)
            if (processId == 0 || processId == Environment.ProcessId || _win32.GetWindowProcessId(item.HWnd) != processId)
                throw new InvalidOperationException("Placement targets must belong to their expected foreign process.");
    }

    private void Place(PlacementJob placement)
    {
        try
        {
            ValidateTargets(placement.Moves);
            var items = placement.Moves.Select(move => move.Item).ToList();
            placement.Completion.SetResult(_win32.BatchMove(items, isAsync: false, isTransient: false));
        }
        catch (Exception ex) { placement.Completion.SetException(ex); }
    }

    private void Loop()
    {
        while (!_disposed)
        {
            CancellationToken token;
            try
            {
                // ClearPending can cancel/dispose the old source before this
                // thread reads Token. Once captured, use the token throughout.
                token = Volatile.Read(ref _cts).Token;
                _signal.Wait(token);
            }
            catch (OperationCanceledException)
            {
                continue; // _cts was swapped or disposed — re-read next iteration.
            }
            catch (ObjectDisposedException)
            {
                continue;
            }
            _signal.Reset();
            if (_disposed) break;
            PlacementJob? placement;
            lock (_processLock)
            {
                placement = Interlocked.Exchange(ref _pendingPlacement, null);
                Job? job = Interlocked.Exchange(ref _pending, null);
                if (job != null && !token.IsCancellationRequested && !_disposed)
                    _completed.Enqueue((job.Items, _win32.BatchMove(job.Items, isAsync: job.IsAsync, isTransient: job.IsTransient, ct: token)));
            }
            // The UI lease keeps other projection work out while USER32 is running.
            if (placement != null) Place(placement);
        }
    }

    public void Dispose()
    {
        RequireUnreservedWorker();
        _disposed = true;
        _cts.Cancel();
        if (_thread.Join(TimeSpan.FromSeconds(1)))
        {
            _signal.Dispose();
            _cts.Dispose();
        }
    }
}
