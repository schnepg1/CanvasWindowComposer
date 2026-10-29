using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CanvasDesktop;

/// <summary>A bounded recording started explicitly from the tray, before restarting anything.</summary>
internal sealed class WindowDiagnosticsRecorder : IDisposable
{
    private readonly Timer _timer = new() { Interval = 500 };
    private readonly Func<object> _capture;
    private readonly Action<string> _completed;
    private readonly int _durationMilliseconds;
    private StreamWriter? _writer;
    private string? _path;
    private long _until, _generation;
    private bool _capturing;
    private static readonly JsonSerializerOptions Json = new() { IncludeFields = true };

    public WindowDiagnosticsRecorder(Func<object> capture, Action<string> completed, int durationMilliseconds = 15000)
    {
        if (durationMilliseconds is <= 0 or > 60000) throw new ArgumentOutOfRangeException(nameof(durationMilliseconds));
        _durationMilliseconds = durationMilliseconds;
        _capture = capture; _completed = completed;
        _timer.Tick += (_, _) => Capture();
    }

    public string Start(string directory)
    {
        if (_writer != null) return _path!;
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, $"windows-{DateTime.Now:yyyyMMdd-HHmmss-fff}.jsonl");
        _writer = new StreamWriter(_path, append: false);
        _until = Environment.TickCount64 + _durationMilliseconds;
        _generation++;
        Capture();
        if (_writer != null) _timer.Start();
        return _path;
    }

    private async void Capture()
    {
        if (_writer == null || _capturing) return;
        _capturing = true;
        long generation = _generation;
        try
        {
            var canvas = _capture();
            // Cross-process inspection can be slow when an application is busy.
            // Keep it off the UI thread and never overlap native snapshots.
            var native = await Task.Run(() => WindowStateSnapshot.Capture());
            if (_writer == null || generation != _generation) return;
            _writer.WriteLine(JsonSerializer.Serialize(new { Native = native, Canvas = canvas }, Json));
            _writer.Flush();
            if (Environment.TickCount64 < _until) return;
            Stop();
            _completed(_path!);
        }
        catch (Exception ex) { Stop(); TrayApp.Log("Window diagnostics recording failed: " + ex.Message); }
        finally { _capturing = false; }
    }

    private void Stop() { _generation++; _timer.Stop(); _writer?.Dispose(); _writer = null; }
    public void Dispose() { Stop(); _timer.Dispose(); }
}
