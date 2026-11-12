using CanvasDesktop;

namespace CanvasDesktop.Tests;

public class ProjectionWorkerTests
{
    private static readonly IntPtr Target = (IntPtr)42;
    private const uint ForeignPid = 1;
    private static readonly BatchMoveItem Move = new(Target, new(200, 100, 100, 100), PosOnly: true);

    [Fact]
    public void PlacementLeaseFencesProjectionUntilTheOwnerReleasesIt()
    {
        var api = CreateTarget();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var worker = new ProjectionWorker(api);
        using var lease = Assert.IsType<ProjectionWorker.PlacementLease>(worker.TryAcquirePlacementLease());
        api.BeforeBatchMove = _ => { entered.Set(); release.Wait(TimeSpan.FromSeconds(5)); };
        var request = new List<(BatchMoveItem, uint)> { (Move, ForeignPid) };
        var completion = lease.Submit(request);
        request.Clear();
        try
        {
            Assert.True(entered.Wait(5000));
            Assert.Null(worker.TryAcquirePlacementLease());
            Assert.Throws<InvalidOperationException>(() => worker.Schedule([Move], true, true));
            Assert.Throws<InvalidOperationException>(worker.ClearPending);
            Assert.Throws<InvalidOperationException>(worker.Dispose);
            Assert.Throws<InvalidOperationException>(lease.Dispose);
            Assert.Throws<InvalidOperationException>(() => { _ = lease.Submit([(Move, ForeignPid)]); });
        }
        finally
        {
            release.Set();
            Await(completion);
        }

        Assert.Equal(Move, Assert.Single(api.LastBatch));
        Assert.Equal(NativeOperationStatus.Succeeded, Await(completion).Summary.Status);
        Assert.False(worker.TryTakeCompleted(out _));
        Assert.Null(worker.TryAcquirePlacementLease());
        lease.Dispose();
        using var next = Assert.IsType<ProjectionWorker.PlacementLease>(worker.TryAcquirePlacementLease());
    }

    [Fact]
    public void LegacyProjectionDrainsBeforeALeaseCanStart()
    {
        var api = CreateTarget();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var returned = new ManualResetEventSlim();
        using var worker = new ProjectionWorker(api);
        api.BeforeBatchMove = _ =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(5));
            returned.Set();
        };
        worker.Schedule([Move], true, true);
        try
        {
            Assert.True(entered.Wait(5000));
            Assert.Null(worker.TryAcquirePlacementLease());
            Assert.False(returned.IsSet);
        }
        finally { release.Set(); }

        (List<BatchMoveItem> Items, WindowBatchResult Result) completed = default;
        Assert.True(SpinWait.SpinUntil(() => worker.TryTakeCompleted(out completed), 5000));
        Assert.Equal(Move, Assert.Single(Assert.IsType<List<BatchMoveItem>>(completed.Items)));
        ProjectionWorker.PlacementLease? lease = null;
        Assert.True(SpinWait.SpinUntil(() => (lease = worker.TryAcquirePlacementLease()) != null, 5000));
        lease!.Dispose();
    }

    [Fact]
    public void InvalidOwnershipIsRejectedAndAWorkerFaultCanBeReleased()
    {
        var api = CreateTarget();
        using var worker = new ProjectionWorker(api);
        using var lease = Assert.IsType<ProjectionWorker.PlacementLease>(worker.TryAcquirePlacementLease());
        Assert.Throws<InvalidOperationException>(() => { _ = lease.Submit([(Move, 2)]); });
        Assert.Throws<InvalidOperationException>(() => { _ = lease.Submit([(Move, (uint)Environment.ProcessId)]); });
        Assert.Empty(api.LastBatch);

        var fault = new InvalidOperationException("Native placement fault");
        api.BeforeBatchMove = _ => throw fault;
        Assert.Same(fault, Assert.Throws<InvalidOperationException>(() => Await(lease.Submit([(Move, ForeignPid)]))));
        Assert.Null(worker.TryAcquirePlacementLease());
        lease.Dispose();
        api.BeforeBatchMove = null;
        using var retry = Assert.IsType<ProjectionWorker.PlacementLease>(worker.TryAcquirePlacementLease());
        Assert.Equal(NativeOperationStatus.Succeeded, Await(retry.Submit([(Move, ForeignPid)])).Summary.Status);
    }

    [Fact]
    public void OwnershipChangeBeforeTheWorkerStartsPreventsTheNativeBatch()
    {
        var api = CreateTarget();
        int ownerThread = Environment.CurrentManagedThreadId;
        api.ReadProcessId = _ => Environment.CurrentManagedThreadId == ownerThread ? ForeignPid : 2;
        using var worker = new ProjectionWorker(api);
        using var lease = Assert.IsType<ProjectionWorker.PlacementLease>(worker.TryAcquirePlacementLease());

        Assert.Throws<InvalidOperationException>(() => Await(lease.Submit([(Move, ForeignPid)])));
        Assert.Empty(api.LastBatch);
        Assert.Equal((100, 100, 100, 100), api.GetWindowRect(Target));
    }

    private static FakeWindowApi CreateTarget()
    {
        var api = new FakeWindowApi();
        api.AddWindow(Target, 100, 100, 100, 100, pid: ForeignPid);
        return api;
    }

    // Keep assertions and lease disposal on the same owner thread.
    private static WindowBatchResult Await(Task<WindowBatchResult> completion)
        => completion.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
}
