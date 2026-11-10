using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CanvasDesktop;

/// <summary>Automatic history; capture UI state on its thread and inspect/write native state on a worker.</summary>
internal sealed class RollingWindowDiagnostics : IDisposable
{
    private readonly Timer _timer;
    private readonly WindowDiagnosticHistoryStore _store;
    private readonly Func<object> _capture, _native;
    private readonly Func<DateTime> _utcNow;
    private readonly Func<bool>? _cameraMoving;
    private readonly int _intervalMilliseconds;
    private readonly Guid _session = Guid.NewGuid();
    private readonly object _statusGate = new();
    private Task _pending = Task.CompletedTask;
    private long _sequence;
    private bool _disposed;
    private bool _deferred;
    private DateTime? _lastSuccessUtc;
    private string? _lastError;

    public RollingWindowDiagnostics(WindowDiagnosticHistoryStore store, Func<object> capture,
        int intervalMilliseconds = 5000, Func<object>? native = null, Func<DateTime>? utcNow = null,
        Func<bool>? cameraMoving = null)
    {
        _store = store; _capture = capture; _native = native ?? (() => WindowStateSnapshot.Capture());
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _cameraMoving = cameraMoving;
        _intervalMilliseconds = intervalMilliseconds;
        _timer = new Timer { Interval = intervalMilliseconds };
        _timer.Tick += (_, _) => _ = CaptureIfIdleAsync();
    }

    public void Start() { if (_disposed) return; _ = CaptureIfIdleAsync(); _timer.Start(); }

    internal Task CaptureIfIdleAsync()
    {
        if (_disposed) return _pending;
        if (_cameraMoving?.Invoke() == true)
        {
            if (!_deferred)
            {
                PerformanceDiagnostics.Shared.Record(NativeOperationResult.Success("RollingWindowDiagnosticsDeferred"));
                _deferred = true;
                _timer.Interval = Math.Min(500, _intervalMilliseconds);
            }
            return _pending;
        }
        if (_deferred && _pending.IsCompleted)
        {
            _deferred = false;
            _timer.Interval = _intervalMilliseconds;
        }
        return CaptureNowAsync();
    }

    internal Task CaptureNowAsync()
    {
        if (_disposed || !_pending.IsCompleted) return _pending;
        try
        {
            long started = Stopwatch.GetTimestamp();
            object canvas;
            try { canvas = _capture(); }
            catch
            {
                PerformanceDiagnostics.Shared.Complete("WindowDiagnosticSnapshotUi", IntPtr.Zero, started, false);
                throw;
            }
            PerformanceDiagnostics.Shared.Complete("WindowDiagnosticSnapshotUi", IntPtr.Zero, started, true);
            var capturedUtc = _utcNow();
            long sequence = ++_sequence;
            _pending = Task.Run(() =>
            {
                try
                {
                    var native = _native();
                    _store.Append(capturedUtc, new { SchemaVersion = 1, Session = _session, Sequence = sequence,
                        CapturedUtc = capturedUtc, Native = native, Canvas = canvas });
                    lock (_statusGate) { _lastSuccessUtc = capturedUtc; _lastError = null; }
                }
                catch (Exception ex) { Failed(ex); }
            });
        }
        catch (Exception ex) { Failed(ex); }
        return _pending;
    }

    public async Task<string> PreserveAsync(TimeSpan? lookback = null)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(RollingWindowDiagnostics));
        var pending = CaptureNowAsync();
        // Preserve prior evidence even if native inspection is stuck in the bug
        // being investigated. A fresh sample is helpful, but must not block export.
        bool currentSamplePending = await Task.WhenAny(pending, Task.Delay(2000)) != pending;
        var status = new { Collection = Status, CurrentSamplePending = currentSamplePending };
        return await Task.Run(() => _store.Preserve(_utcNow(), status, lookback));
    }

    internal object Status
    {
        get { lock (_statusGate) return new { LastSuccessUtc = _lastSuccessUtc, LastError = _lastError }; }
    }

    private void Failed(Exception error)
    {
        lock (_statusGate)
        {
            if (_lastError == null) TrayApp.Log("Rolling window diagnostics failed: " + error.Message);
            _lastError = error.Message;
        }
    }

    public void Dispose() { if (_disposed) return; _disposed = true; _timer.Dispose(); }
}
