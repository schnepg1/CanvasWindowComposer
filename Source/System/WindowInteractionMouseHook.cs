using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace CanvasDesktop;

internal readonly record struct InteractionMouseEvent(Point Screen, MouseButtons Button, bool Down, bool Up,
    bool Wheel = false, int Delta = 0, bool Horizontal = false, int Clicks = 1, Keys Modifiers = Keys.None);

/// <summary>
/// Installed only during interaction. Apps may capture the physical mouse on
/// button-down; intercept before that capture can receive unscaled coordinates.
/// The callback only queues work; it never calls into the source application.
/// </summary>
internal sealed class WindowInteractionMouseHook : IDisposable
{
    private readonly Func<Point, bool> _isSurface;
    private readonly Action<InteractionMouseEvent> _enqueue;
    private readonly Action _outside;
    private readonly HOOKPROC _callback;
    private UnhookWindowsHookExSafeHandle? _hook;
    private readonly Thread _thread;
    private Control? _dispatcher;
    private volatile bool _disposed;
    private MouseButtons _held;
    private MouseButtons _lastClickButton;
    private Point _lastClick;
    private long _lastClickTick;
    private bool _updatingCursor;

    public WindowInteractionMouseHook(Func<Point, bool> isSurface, Action<InteractionMouseEvent> enqueue, Action outside)
    {
        _isSurface = isSurface; _enqueue = enqueue; _outside = outside;
        _callback = Callback;
        Exception? failure = null;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            bool reported = false;
            try
            {
                using var dispatcher = new Control();
                _ = dispatcher.Handle; // Create the thread's message queue before reporting readiness.
                _dispatcher = dispatcher;
                _hook = PInvoke.SetWindowsHookEx(WINDOWS_HOOK_ID.WH_MOUSE_LL, _callback, PInvoke.GetModuleHandle((string?)null), 0);
                if (_hook.IsInvalid) throw new InvalidOperationException("Could not start window interaction input.");
                reported = true; ready.Set();
                Application.Run();
            }
            catch (Exception ex)
            {
                if (!reported) { failure = ex; ready.Set(); }
                else { TrayApp.Log("Window interaction input stopped: " + ex.Message); _outside(); }
            }
            finally { _hook?.Dispose(); _dispatcher = null; }
        }) { IsBackground = true, Name = "Canvas window interaction mouse" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait();
        if (failure != null) { _thread.Join(); throw new InvalidOperationException("Could not start window interaction input.", failure); }
    }

    private LRESULT Callback(int code, WPARAM wParam, LPARAM lParam)
    {
        if (code < 0 || _disposed) return Next(code, wParam, lParam);
        uint message = (uint)wParam.Value;
        if (message is < 0x200 or > 0x20E) return Next(code, wParam, lParam);
        var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>((IntPtr)lParam.Value);
        var point = data.pt;
        bool down = message is 0x201 or 0x204 or 0x207 or 0x20B;
        bool up = message is 0x202 or 0x205 or 0x208 or 0x20C;
        var button = message switch
        {
            0x201 or 0x202 => MouseButtons.Left,
            0x204 or 0x205 => MouseButtons.Right,
            0x207 or 0x208 => MouseButtons.Middle,
            0x20B or 0x20C => (data.mouseData >> 16) == 1 ? MouseButtons.XButton1 : MouseButtons.XButton2,
            _ => MouseButtons.None
        };
        if (_held == MouseButtons.None && !_isSurface(point))
        {
            if (down) _outside();
            return Next(code, wParam, lParam);
        }
        int clicks = 1;
        if (down)
        {
            long now = Environment.TickCount64;
            var size = SystemInformation.DoubleClickSize;
            if (_lastClickButton == button && now - _lastClickTick <= SystemInformation.DoubleClickTime
                && Math.Abs(point.X - _lastClick.X) <= size.Width / 2 && Math.Abs(point.Y - _lastClick.Y) <= size.Height / 2)
            { clicks = 2; _lastClickButton = MouseButtons.None; }
            else { _lastClickButton = button; _lastClick = point; _lastClickTick = now; }
            _held |= button;
        }
        if (up) _held &= ~button;
        _enqueue(new(point, button, down, up, message is 0x20A or 0x20E,
            unchecked((short)(data.mouseData >> 16)), message == 0x20E, clicks, ReadModifiers()));
        if (message == 0x200 && !_updatingCursor)
        {
            // Suppressing WM_MOUSEMOVE also suppresses the OS cursor update.
            // Move the cursor explicitly while withholding the unscaled event
            // from the source app (which may own native capture during a drag).
            _updatingCursor = true;
            try
            {
                if (PInvoke.GetCursorPos(out var current) && current != point)
                    PInvoke.SetCursorPos(point.X, point.Y);
            }
            finally { _updatingCursor = false; }
        }
        return (LRESULT)1;
    }

    private static LRESULT Next(int code, WPARAM wParam, LPARAM lParam)
        => PInvoke.CallNextHookEx(HHOOK.Null, code, wParam, lParam);

    internal static Keys ReadModifiers() =>
        ((PInvoke.GetAsyncKeyState(0x10) & 0x8000) != 0 ? Keys.Shift : Keys.None)
        | ((PInvoke.GetAsyncKeyState(0x11) & 0x8000) != 0 ? Keys.Control : Keys.None)
        | ((PInvoke.GetAsyncKeyState(0x12) & 0x8000) != 0 ? Keys.Alt : Keys.None);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // The hook has its own message loop so a busy canvas frame cannot stall
        // system-wide input or cause Windows to remove a timed-out hook.
        _hook?.Dispose();
        try { _dispatcher?.BeginInvoke(() => Application.ExitThread()); }
        catch (InvalidOperationException) { /* The hook thread already exited. */ }
        _thread.Join();
        GC.KeepAlive(_callback);
    }
}
