using System.Diagnostics;
using System.Text.Json;
using CanvasDesktop;

namespace CanvasDesktop.Tests;

public class NativeOperationTests
{
    private static readonly List<BatchMoveItem> Items = [new((IntPtr)1, new(100, 100, 800, 600), true),
        new((IntPtr)2, new(200, 200, 800, 600), false), new((IntPtr)3, new(300, 300, 800, 600), true)];

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DeferredBatchFailure_ReplaysEntireDiscardedPrefix(bool failBegin)
    {
        var native = new Native { FailBegin = failBegin, FailDeferAt = failBegin ? 0 : 2 };
        var diagnostics = new PerformanceDiagnostics();
        var result = new WindowBatchMover(native, diagnostics).Execute(Items, false, false, default);
        Assert.True(result.Summary.Accepted);
        Assert.Equal(new long[] { 1, 2, 3 }, native.Moved.Select(i => i.HWnd.ToInt64()));
        Assert.Equal(0, native.Ends); // Never finalize the invalidated deferred handle.
        Assert.Single(diagnostics.Snapshot().RecentFailures);
        Assert.Equal(3, result.Items.Count);
    }

    [Fact]
    public void EndFailure_IsReportedForEveryQueuedWindow()
    {
        var native = new Native { FailEnd = true };
        var result = new WindowBatchMover(native, new()).Execute(Items, false, false, default);
        Assert.Equal(NativeOperationStatus.Failed, result.Summary.Status);
        Assert.All(result.Items, r => { Assert.Equal(NativeOperationStatus.Failed, r.Status); Assert.Equal(5, r.ErrorCode); });
    }

    [Fact]
    public void FallbackMoveFailure_DoesNotPreventRemainingWindows()
    {
        var native = new Native { FailBegin = true, FailedMove = (IntPtr)2 };
        var result = new WindowBatchMover(native, new()).Execute(Items, false, false, default);
        Assert.Equal(3, native.Moved.Count);
        Assert.Equal(NativeOperationStatus.Failed, result.Summary.Status);
        Assert.True(result.Items[0].Accepted);
        Assert.False(result.Items[1].Accepted);
        Assert.True(result.Items[2].Accepted);
    }

    [Fact]
    public void Cancellation_FinalizesQueuedPrefixAndLabelsUnattemptedWindows()
    {
        using var cancellation = new CancellationTokenSource();
        var native = new Native { AfterDefer = () => cancellation.Cancel() };
        var result = new WindowBatchMover(native, new()).Execute(Items, false, false, cancellation.Token);
        Assert.Equal(1, native.Ends);
        Assert.True(result.Items[0].Accepted);
        Assert.All(result.Items.Skip(1), r => Assert.Equal(NativeOperationStatus.Cancelled, r.Status));
        Assert.Equal(NativeOperationStatus.Cancelled, result.Summary.Status);
    }

    [Fact]
    public void PreCancelledBatch_DoesNotCallNativeCode()
    {
        var native = new Native();
        var result = new WindowBatchMover(native, new()).Execute(Items, false, false, new CancellationToken(true));
        Assert.Equal(0, native.Begins);
        Assert.All(result.Items, r => Assert.Equal(NativeOperationStatus.Cancelled, r.Status));
    }

    [Fact]
    public void AsyncAcknowledgement_IsQueuedAndNotClaimedAsCompleted()
    {
        var native = new Native();
        var result = new WindowBatchMover(native, new()).Execute(Items, true, true, default);
        Assert.Equal(0, native.Begins); // Async positioning must bypass the unsupported deferred flag.
        Assert.Equal(NativeOperationStatus.Queued, result.Summary.Status);
        Assert.All(result.Items, r => Assert.Equal(NativeOperationStatus.Queued, r.Status));
        Assert.All(native.Flags, flags => Assert.NotEqual(0u, flags & 0x4000u));
        Assert.NotEqual(0u, native.Flags[0] & 0x1u);
        Assert.Equal(0u, native.Flags[1] & 0x1u);
    }

    [Fact]
    public void Diagnostics_AreBoundedAndRetainFailuresSeparatelyFromRecentSuccesses()
    {
        var diagnostics = new PerformanceDiagnostics();
        diagnostics.Record(new("ClipWindow", 12, NativeOperationStatus.Failed, null, 2));
        for (int i = 0; i < 1000; i++) diagnostics.Record(new("SetWindowPos", 12, NativeOperationStatus.Succeeded, null, 1));
        var snapshot = diagnostics.Snapshot();
        Assert.Equal(256, snapshot.RecentOperations.Length);
        Assert.Single(snapshot.RecentFailures);
        var moves = Assert.Single(snapshot.Operations, o => o.Operation == "SetWindowPos");
        Assert.Equal(1000, moves.Calls);
        Assert.Equal(1000, moves.TotalMs);
        Assert.Equal(1, moves.MaxMs);
        Assert.Contains("ClipWindow", JsonSerializer.Serialize(snapshot)); // No unsupported IntPtr values.
    }

    [Fact]
    public void Diagnostics_ResetKeepsLiveThumbnailOwnershipAndCorrectPeak()
    {
        var diagnostics = new PerformanceDiagnostics();
        diagnostics.ThumbnailRegistered((IntPtr)1);
        diagnostics.ThumbnailRegistered((IntPtr)1);
        diagnostics.ThumbnailRegistered((IntPtr)2);
        diagnostics.ThumbnailUnregistered((IntPtr)1);
        diagnostics.Reset();
        Assert.Equal(1, diagnostics.Snapshot().ActiveThumbnails);
        Assert.Equal(1, diagnostics.Snapshot().PeakThumbnails);
        diagnostics.ThumbnailUnregistered((IntPtr)2);
        Assert.Equal(0, diagnostics.Snapshot().ActiveThumbnails);
    }

    [Fact]
    public void Diagnostics_AggregatesConcurrentWorkerAndRenderCalls()
    {
        var diagnostics = new PerformanceDiagnostics();
        Parallel.For(0, 10000, _ => diagnostics.Complete("Move", (IntPtr)1, Stopwatch.GetTimestamp(), true));
        Assert.Equal(10000, Assert.Single(diagnostics.Snapshot().Operations).Succeeded);
    }

    [Fact]
    public void FailedClip_IsRetriedAndDoesNotParkWindowAsIfHidden()
    {
        var api = new FakeWindowApi(); var canvas = new Canvas();
        using var wm = new WindowManager(canvas, api, new FakeAppConfig(), new FakeInputRouter());
        api.AddWindow((IntPtr)1, 100, 100, 800, 600);
        canvas.SetWindow((IntPtr)1, 5000, 5000, 800, 600);
        api.FailedClips.Add((IntPtr)1);
        wm.ReprojectSync();
        Assert.Empty(api.LastBatch);
        Assert.Equal(100, api.Windows[(IntPtr)1].X);
        api.FailedClips.Clear();
        wm.ReprojectSync();
        Assert.Single(api.LastBatch);
        Assert.Contains((IntPtr)1, api.ClippedWindows);
    }

    [Fact]
    public void FailedUnclip_DoesNotMoveInvisibleWindowIntoViewAndIsRetried()
    {
        var api = new FakeWindowApi(); var canvas = new Canvas();
        using var wm = new WindowManager(canvas, api, new FakeAppConfig(), new FakeInputRouter());
        api.AddWindow((IntPtr)1, 100, 100, 800, 600);
        canvas.SetWindow((IntPtr)1, 5000, 5000, 800, 600); wm.ReprojectSync();
        canvas.SetWindow((IntPtr)1, 100, 100, 800, 600);
        api.FailedUnclips.Add((IntPtr)1); wm.ReprojectSync();
        Assert.Empty(api.LastBatch);
        api.FailedUnclips.Clear(); wm.ReprojectSync();
        Assert.Empty(api.ClippedWindows);
        Assert.Single(api.LastBatch);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedParkingMove_IsRetriedEvenThoughClippingSucceeded(bool single)
    {
        var api = new FakeWindowApi(); var canvas = new Canvas();
        using var wm = new WindowManager(canvas, api, new FakeAppConfig(), new FakeInputRouter());
        api.AddWindow((IntPtr)1, 100, 100, 800, 600);
        canvas.SetWindow((IntPtr)1, 5000, 5000, 800, 600);
        api.FailedMoves.Add((IntPtr)1);
        if (single) Assert.False(wm.ReprojectWindow((IntPtr)1)); else wm.ReprojectSync();
        Assert.Contains((IntPtr)1, api.ClippedWindows);
        Assert.Equal(100, api.Windows[(IntPtr)1].X);
        api.FailedMoves.Clear();
        if (single) Assert.True(wm.ReprojectWindow((IntPtr)1)); else wm.ReprojectSync();
        Assert.Equal(1919, api.Windows[(IntPtr)1].X);
    }

    [Fact]
    public void CancelledWorkerParking_IsRetriedBySynchronousCommit()
    {
        var api = new FakeWindowApi(); var canvas = new Canvas();
        using var wm = new WindowManager(canvas, api, new FakeAppConfig(), new FakeInputRouter(), useAsyncProjection: true);
        using var entered = new ManualResetEventSlim();
        api.BeforeBatchMove = ct => { entered.Set(); Assert.True(ct.WaitHandle.WaitOne(5000)); };
        api.AddWindow((IntPtr)1, 100, 100, 800, 600);
        canvas.SetWindow((IntPtr)1, 5000, 5000, 800, 600);
        wm.Reproject();
        Assert.True(entered.Wait(5000));
        api.BeforeBatchMove = null;
        wm.ReprojectSync(); // Cancels the worker, drains its outcome and retries.
        Assert.Equal(1919, api.Windows[(IntPtr)1].X);
        Assert.Single(api.LastBatch);
        wm.ReprojectSync();
        Assert.Empty(api.LastBatch); // Once accepted, unchanged parked windows stay skipped.
    }

    [Fact]
    public void Recovery_CollectsNativeReturnFailuresAndContinuesOtherWindows()
    {
        var api = new FakeWindowApi(); var canvas = new Canvas();
        using var wm = new WindowManager(canvas, api, new FakeAppConfig(), new FakeInputRouter());
        foreach (var item in Items)
        {
            api.AddWindow(item.HWnd, 5000, 5000, 800, 600);
            wm.TrackForDesktopRestore(item.HWnd);
        }
        api.FailedMoves.Add((IntPtr)1);
        Assert.Single(Assert.Throws<AggregateException>(wm.RestoreDesktopViewport).InnerExceptions);
        Assert.InRange(api.Windows[(IntPtr)2].X, 0, 1120);
        Assert.InRange(api.Windows[(IntPtr)3].X, 0, 1120);
    }

    private sealed class Native : IWindowPositionNative
    {
        public bool FailBegin, FailEnd;
        public int FailDeferAt, Begins, Defers, Ends;
        public IntPtr FailedMove;
        public Action? AfterDefer;
        public List<BatchMoveItem> Moved = new();
        public List<uint> Flags = new();
        public int LastError => 5;
        public IntPtr Begin(int count) { Begins++; return FailBegin ? IntPtr.Zero : (IntPtr)123; }
        public IntPtr Defer(IntPtr batch, BatchMoveItem item, uint flags)
        {
            Defers++; Flags.Add(flags); AfterDefer?.Invoke();
            return Defers == FailDeferAt ? IntPtr.Zero : (IntPtr)123;
        }
        public bool End(IntPtr batch) { Ends++; return !FailEnd; }
        public bool Move(BatchMoveItem item, uint flags) { Flags.Add(flags); Moved.Add(item); return item.HWnd != FailedMove; }
    }
}
