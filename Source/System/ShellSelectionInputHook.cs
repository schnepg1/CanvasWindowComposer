using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CanvasDesktop;

internal sealed record ShellClickObservation(DateTime CapturedUtc, uint NativeTimestamp, int X, int Y,
    long HitRoot, string RootClass, bool Taskbar, long GeometryAgeMilliseconds);

// Observe, without blocking or replacing, the shell's normal taskbar hit testing.
// A dedicated message loop keeps native mouse callbacks independent of rendering.
internal sealed class ShellSelectionInputHook : IDisposable
{
    private readonly TaskbarSelectionIntent _intent;
    private readonly AltTabSelectionIntent _altTab;
    private readonly Action _beginSwitch;
    private readonly Action<long> _endSwitch;
    private readonly HOOKPROC _callback;
    private readonly HOOKPROC _keyboardCallback;
    private readonly Thread _thread;
    private Control? _dispatcher;
    private UnhookWindowsHookExSafeHandle? _hook;
    private UnhookWindowsHookExSafeHandle? _keyboardHook;
    private volatile bool _disposed;
    private WindowStateSnapshot.HitSurface[] _surfaces = Array.Empty<WindowStateSnapshot.HitSurface>();
    private long _geometryTick, _mouseCallbacks, _lastMouseTick, _maxOwnCallbackTicks;
    private int _refreshing;
    private readonly object _diagnosticGate = new();
    private readonly Queue<ShellClickObservation> _clicks = new();

    internal object Diagnostics
    {
        get
        {
            lock (_diagnosticGate) return new
            {
                ThreadAlive = _thread.IsAlive,
                MouseHookAvailable = _hook is { IsInvalid: false, IsClosed: false },
                KeyboardHookAvailable = _keyboardHook is { IsInvalid: false, IsClosed: false },
                MouseCallbacks = Interlocked.Read(ref _mouseCallbacks),
                LastMouseCallbackAgeMilliseconds = _mouseCallbacks == 0 ? (long?)null : Environment.TickCount64 - Interlocked.Read(ref _lastMouseTick),
                MaximumOwnCallbackMilliseconds = Interlocked.Read(ref _maxOwnCallbackTicks) * 1000.0 / Stopwatch.Frequency,
                GeometryAgeMilliseconds = Environment.TickCount64 - Interlocked.Read(ref _geometryTick),
                RecentClicks = _clicks.ToArray()
            };
        }
    }

    public ShellSelectionInputHook(TaskbarSelectionIntent intent, AltTabSelectionIntent altTab,
        Action beginSwitch, Action<long> endSwitch)
    {
        _intent = intent; _callback = OnMouse;
        _altTab = altTab; _beginSwitch = beginSwitch; _endSwitch = endSwitch; _keyboardCallback = OnKeyboard;
        Exception? failure = null;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            bool reported = false;
            try
            {
                using var dispatcher = new Control();
                _ = dispatcher.Handle;
                _dispatcher = dispatcher;
                _surfaces = WindowStateSnapshot.CaptureHitSurfaces();
                _geometryTick = Environment.TickCount64;
                using var geometryTimer = new System.Windows.Forms.Timer { Interval = 100 };
                geometryTimer.Tick += (_, _) => RefreshGeometry();
                geometryTimer.Start();
                _hook = PInvoke.SetWindowsHookEx(WINDOWS_HOOK_ID.WH_MOUSE_LL, _callback,
                    PInvoke.GetModuleHandle((string?)null), 0);
                if (_hook.IsInvalid) throw new InvalidOperationException("Taskbar navigation input could not start.");
                _keyboardHook = PInvoke.SetWindowsHookEx(WINDOWS_HOOK_ID.WH_KEYBOARD_LL, _keyboardCallback,
                    PInvoke.GetModuleHandle((string?)null), 0);
                if (_keyboardHook.IsInvalid) throw new InvalidOperationException("App-switch navigation input could not start.");
                reported = true; ready.Set();
                Application.Run();
            }
            catch (Exception ex)
            {
                if (!reported) { failure = ex; ready.Set(); }
                else { _intent.Enabled = false; TrayApp.Log("Taskbar navigation input stopped: " + ex.Message); }
            }
            finally { _hook?.Dispose(); _keyboardHook?.Dispose(); _dispatcher = null; }
        }) { IsBackground = true, Name = "Canvas shell selection" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait();
        if (failure != null) { _thread.Join(); throw new InvalidOperationException("Taskbar navigation input could not start.", failure); }
    }

    private unsafe LRESULT OnMouse(int code, WPARAM wParam, LPARAM lParam)
    {
        long started = Stopwatch.GetTimestamp();
        if (code >= 0 && !_disposed)
        {
            Interlocked.Increment(ref _mouseCallbacks);
            Interlocked.Exchange(ref _lastMouseTick, Environment.TickCount64);
            if ((uint)wParam.Value == 0x0201)
            {
                var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>((IntPtr)lParam.Value);
                var surfaces = Volatile.Read(ref _surfaces);
                long age = Environment.TickCount64 - Interlocked.Read(ref _geometryTick);
                int index = WindowStateSnapshot.HitSurfaceIndex(surfaces,
                    new WindowStateSnapshot.Point { X = data.pt.X, Y = data.pt.Y });
                var hit = index < 0 ? null : surfaces[index];
                bool taskbar = age <= 1000 && hit != null && TaskbarSelectionIntent.IsTaskbarClass(hit.ClassName);
                if (_intent.Enabled)
                {
                    if (taskbar) _intent.Click(data.time, PInvoke.GetForegroundWindow());
                    else _intent.Cancel();
                }
                lock (_diagnosticGate)
                {
                    if (_clicks.Count == 64) _clicks.Dequeue();
                    _clicks.Enqueue(new(DateTime.UtcNow, data.time, data.pt.X, data.pt.Y,
                        hit?.Handle ?? 0, hit?.ClassName ?? "", taskbar, age));
                }
            }
        }
        long elapsed = Stopwatch.GetTimestamp() - started;
        if (elapsed > Interlocked.Read(ref _maxOwnCallbackTicks)) Interlocked.Exchange(ref _maxOwnCallbackTicks, elapsed);
        return PInvoke.CallNextHookEx(HHOOK.Null, code, wParam, lParam);
    }

    private void RefreshGeometry()
    {
        if (_disposed || Interlocked.Exchange(ref _refreshing, 1) != 0) return;
        _ = Task.Run(() =>
        {
            try
            {
                var surfaces = WindowStateSnapshot.CaptureHitSurfaces();
                if (!_disposed)
                {
                    Volatile.Write(ref _surfaces, surfaces);
                    Interlocked.Exchange(ref _geometryTick, Environment.TickCount64);
                }
            }
            catch { /* Expired geometry cancels navigation intent; native input always passes through. */ }
            finally { Volatile.Write(ref _refreshing, 0); }
        });
    }

    private LRESULT OnKeyboard(int code, WPARAM wParam, LPARAM lParam)
    {
        if (code >= 0 && !_disposed)
        {
            uint message = (uint)wParam.Value;
            var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>((IntPtr)lParam.Value);
            bool down = message is 0x0100 or 0x0104;
            bool up = message is 0x0101 or 0x0105;
            if (down && data.vkCode == 0x09 && ((uint)data.flags & 0x20) != 0)
            {
                if (_altTab.Begin()) _beginSwitch();
            }
            else if (down && data.vkCode == 0x1B) _altTab.Cancel();
            else if (up && data.vkCode is 0x12 or 0xA4 or 0xA5 && _altTab.Release() is { } generation)
                _endSwitch(generation);
        }
        return PInvoke.CallNextHookEx(HHOOK.Null, code, wParam, lParam);
    }

    internal static bool IsTaskbarWindow(HWND window)
    {
        var root = PInvoke.GetAncestor(window, GET_ANCESTOR_FLAGS.GA_ROOT);
        Span<char> name = stackalloc char[128];
        int length = PInvoke.GetClassName(root, name);
        return length > 0 && TaskbarSelectionIntent.IsTaskbarClass(new string(name[..length]));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _intent.Enabled = false;
        _hook?.Dispose();
        _keyboardHook?.Dispose();
        try { _dispatcher?.BeginInvoke(() => Application.ExitThread()); }
        catch (InvalidOperationException) { }
        _thread.Join();
        GC.KeepAlive(_callback);
        GC.KeepAlive(_keyboardCallback);
    }
}
