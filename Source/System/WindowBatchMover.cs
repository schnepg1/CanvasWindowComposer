using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace CanvasDesktop;

internal interface IWindowPositionNative
{
    IntPtr Begin(int count);
    IntPtr Defer(IntPtr batch, BatchMoveItem item, uint flags);
    bool End(IntPtr batch);
    bool Move(BatchMoveItem item, uint flags);
    int LastError { get; }
}

internal sealed unsafe class WindowPositionNative : IWindowPositionNative
{
    public int LastError => Marshal.GetLastPInvokeError();
    public IntPtr Begin(int count) => (IntPtr)PInvoke.BeginDeferWindowPos(count).Value;
    public IntPtr Defer(IntPtr batch, BatchMoveItem item, uint flags)
        => (IntPtr)PInvoke.DeferWindowPos(new HDWP(batch), (HWND)item.HWnd, HWND.Null,
            item.Rect.X, item.Rect.Y, item.Rect.W, item.Rect.H, (SET_WINDOW_POS_FLAGS)flags).Value;
    public bool End(IntPtr batch) => PInvoke.EndDeferWindowPos(new HDWP(batch));
    public bool Move(BatchMoveItem item, uint flags)
        => PInvoke.SetWindowPos((HWND)item.HWnd, HWND.Null, item.Rect.X, item.Rect.Y,
            item.Rect.W, item.Rect.H, (SET_WINDOW_POS_FLAGS)flags);
}

/// <summary>Owns deferred-batch acknowledgement, cancellation and fallback; independently failure-testable.</summary>
internal sealed class WindowBatchMover(IWindowPositionNative native, PerformanceDiagnostics diagnostics)
{
    public NativeOperationResult Move(BatchMoveItem item, uint flags)
    {
        long started = Stopwatch.GetTimestamp();
        bool ok = native.Move(item, flags);
        int error = ok ? 0 : native.LastError;
        return diagnostics.Complete("SetWindowPos", item.HWnd, started, ok, error,
            queued: (flags & (uint)SET_WINDOW_POS_FLAGS.SWP_ASYNCWINDOWPOS) != 0);
    }

    public WindowBatchResult Execute(List<BatchMoveItem> items, bool isAsync, bool isTransient, CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();
        var outcomes = new List<NativeOperationResult>(items.Count);
        if (items.Count == 0 || ct.IsCancellationRequested)
            return Finish(ct.IsCancellationRequested);

        // DeferWindowPos does not support SWP_ASYNCWINDOWPOS (USER32 rejects
        // it with ERROR_INVALID_PARAMETER). Queue async moves individually;
        // synchronous calls still use the deferred positioning optimization.
        if (isAsync)
        {
            MoveIndividually(0, items.Count);
            return Finish(ct.IsCancellationRequested);
        }

        long callStarted = Stopwatch.GetTimestamp();
        IntPtr batch = native.Begin(items.Count);
        int error = batch == IntPtr.Zero ? native.LastError : 0;
        diagnostics.Complete("BeginDeferWindowPos", IntPtr.Zero, callStarted, batch != IntPtr.Zero, error, itemCount: items.Count);
        if (batch == IntPtr.Zero)
        {
            MoveIndividually(0, items.Count);
            return Finish(ct.IsCancellationRequested);
        }

        int queued = 0;
        bool abandoned = false;
        for (; queued < items.Count && !ct.IsCancellationRequested; queued++)
        {
            callStarted = Stopwatch.GetTimestamp();
            batch = native.Defer(batch, items[queued], Flags(items[queued]));
            error = batch == IntPtr.Zero ? native.LastError : 0;
            diagnostics.Complete("DeferWindowPos", items[queued].HWnd, callStarted, batch != IntPtr.Zero, error,
                queued: batch != IntPtr.Zero);
            if (batch != IntPtr.Zero) continue;
            // A failed Defer destroys the batch. Replay the ENTIRE prefix, not only
            // the failing item; otherwise earlier queued moves silently disappear.
            abandoned = true;
            MoveIndividually(0, items.Count);
            break;
        }

        if (!abandoned)
        {
            // Even cancellation must release/apply the successfully queued prefix.
            // This does not cancel an in-flight USER32 call or roll back prior moves.
            callStarted = Stopwatch.GetTimestamp();
            bool ok = native.End(batch);
            error = ok ? 0 : native.LastError;
            var end = diagnostics.Complete("EndDeferWindowPos", IntPtr.Zero, callStarted, ok, error,
                queued: isAsync, itemCount: queued);
            for (int i = 0; i < queued; i++)
            {
                var itemResult = new NativeOperationResult("BatchMoveItem", items[i].HWnd.ToInt64(), end.Status, end.ErrorCode, 0);
                outcomes.Add(itemResult);
                if (!ok) diagnostics.Record(itemResult); // Preserve affected HWNDs in exported failure history.
            }
        }
        return Finish(ct.IsCancellationRequested);

        uint Flags(BatchMoveItem item)
        {
            var flags = SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE;
            if (item.PosOnly) flags |= SET_WINDOW_POS_FLAGS.SWP_NOSIZE;
            if (isAsync) flags |= SET_WINDOW_POS_FLAGS.SWP_ASYNCWINDOWPOS;
            if (isTransient) flags |= SET_WINDOW_POS_FLAGS.SWP_NOSENDCHANGING;
            return (uint)flags;
        }

        void MoveIndividually(int from, int to)
        {
            for (int i = from; i < to && !ct.IsCancellationRequested; i++) outcomes.Add(Move(items[i], Flags(items[i])));
        }

        WindowBatchResult Finish(bool cancelled)
        {
            for (int i = outcomes.Count; i < items.Count; i++)
                outcomes.Add(new("BatchMoveItem", items[i].HWnd.ToInt64(), NativeOperationStatus.Cancelled, null, 0));
            bool failed = outcomes.Exists(o => o.Status == NativeOperationStatus.Failed);
            var status = failed ? NativeOperationStatus.Failed : cancelled ? NativeOperationStatus.Cancelled
                : isAsync && items.Count > 0 ? NativeOperationStatus.Queued : NativeOperationStatus.Succeeded;
            var summary = new NativeOperationResult("BatchMove", 0, status,
                outcomes.Find(o => o.Status == NativeOperationStatus.Failed).ErrorCode,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds, items.Count);
            diagnostics.Record(summary);
            return new(summary, outcomes);
        }
    }
}
