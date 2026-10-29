using System;
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
    private readonly Guid _session = Guid.NewGuid();
    private readonly object _statusGate = new();
    private Task _pending = Task.CompletedTask;
    private long _sequence;
    private bool _disposed;
    private DateTime? _lastSuccessUtc;
    private string? _lastError;

    public RollingWindowDiagnostics(WindowDiagnosticHistoryStore store, Func<object> capture,
        int intervalMilliseconds = 5000, Func<object>? native = null, Func<DateTime>? utcNow = null)
    {
        _store = store; _capture = capture; _native = native ?? (() => WindowStateSnapshot.Capture());
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _timer = new Timer { Interval = intervalMilliseconds };
        _timer.Tick += (_, _) => CaptureNow();
    }

    public void Start() { if (_disposed) return; CaptureNow(); _timer.Start(); }

    private void CaptureNow() => _ = CaptureNowAsync();

    internal Task CaptureNowAsync()
    {
        if (_disposed || !_pending.IsCompleted) return _pending;
        try
        {
            var canvas = _capture();
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

    public async Task<string> PreserveAsync()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(RollingWindowDiagnostics));
        var pending = CaptureNowAsync();
        // Preserve prior evidence even if native inspection is stuck in the bug
        // being investigated. A fresh sample is helpful, but must not block export.
        bool currentSamplePending = await Task.WhenAny(pending, Task.Delay(2000)) != pending;
        var status = new { Collection = Status, CurrentSamplePending = currentSamplePending };
        return await Task.Run(() => _store.Preserve(_utcNow(), status));
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
