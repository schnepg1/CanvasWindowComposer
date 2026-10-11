using System;
using System.Threading;

namespace CanvasDesktop;

/// <summary>
/// Coalesces UI invalidations, retains changes during Present, and sleeps between
/// static frames. A slow native Present keeps ownership of resources until it exits.
/// </summary>
internal sealed class RenderFramePump : IDisposable
{
    private readonly Func<bool> _draw;
    private readonly Func<bool> _continuous;
    private readonly Action _release;
    private readonly string _name;
    private readonly AutoResetEvent _wake = new(false);
    private readonly object _state = new(), _render = new();
    private readonly GridFrameSchedule _frames = new();
    private Thread? _thread;
    private volatile bool _running, _disposed;

    public RenderFramePump(string name, Func<bool> draw, Func<bool> continuous, Action release)
        => (_name, _draw, _continuous, _release) = (name, draw, continuous, release);

    public void Invalidate()
    {
        lock (_state)
        {
            if (_disposed) return;
            _frames.Invalidate();
            _wake.Set();
        }
    }

    public void StartThread()
    {
        lock (_state)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_thread != null) return;
            _thread = new Thread(Run) { IsBackground = true, Name = _name };
            _thread.Start();
        }
    }

    public void Start()
    {
        lock (_state)
        {
            if (_disposed) return;
            _running = true;
            Invalidate();
        }
    }

    public void Stop() { lock (_state) { _running = false; if (!_disposed) _wake.Set(); } }

    public void Synchronize(Action action)
    {
        lock (_render)
        {
            if (_disposed) return;
            action();
        }
    }

    private void Run()
    {
        try
        {
            while (!_disposed)
            {
                _wake.WaitOne();
                while (_running && !_disposed)
                {
                    long revision = _frames.Revision;
                    if (!_frames.ShouldRender(false, 1, _continuous())) break;
                    bool presented;
                    lock (_render)
                    {
                        if (!_running || _disposed) break;
                        presented = _draw();
                    }
                    if (presented) _frames.Presented(revision);
                    // DXGI_STATUS_OCCLUDED is successful but does not pace frames.
                    // Keep the revision pending and back off on occlusion/failure.
                    else _wake.WaitOne(100);
                }
            }
        }
        catch (Exception ex)
        {
            PerformanceDiagnostics.Shared.Record(new NativeOperationResult(_name + "Loop", 0,
                NativeOperationStatus.Failed, ex.HResult, 0));
            // A removed device must never spin or terminate the tray process.
        }
        finally { Release(); }
    }

    private void Release()
    {
        lock (_render)
        {
            lock (_state) { _disposed = true; _running = false; }
            try { _release(); }
            catch (Exception ex)
            {
                PerformanceDiagnostics.Shared.Record(new NativeOperationResult(_name + "Release", 0,
                    NativeOperationStatus.Failed, ex.HResult, 0));
            }
            finally { _wake.Dispose(); }
        }
    }

    public void Dispose()
    {
        Thread? thread;
        lock (_state)
        {
            if (_disposed) return;
            _disposed = true; _running = false;
            _wake.Set();
            thread = _thread;
        }
        if (thread == null) Release();
        else if (thread != Thread.CurrentThread) thread.Join(1000);
        // On a timeout the render thread releases everything in its finally block.
    }
}
