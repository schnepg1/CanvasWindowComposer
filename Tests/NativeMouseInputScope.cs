using System.Runtime.InteropServices;
using System.Windows.Forms;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace CanvasDesktop.Tests;

// Keep unrelated desktop movement out of a short native-input experiment.
// Tagged moves still traverse the real hook chain and target message queue.
// This hook has its own pump so waiting for a probe cannot time it out.
internal sealed class NativeMouseInputScope : IDisposable
{
    internal static readonly UIntPtr Tag = new(0x43414E56);
    private readonly Thread _thread;
    private readonly HOOKPROC _callback;
    private Control? _dispatcher;

    public NativeMouseInputScope()
    {
        _callback = (code, message, data) =>
        {
            if (code >= 0 && (uint)message.Value == 0x200
                && Marshal.PtrToStructure<MSLLHOOKSTRUCT>((IntPtr)data.Value).dwExtraInfo != Tag)
                return (LRESULT)1;
            return PInvoke.CallNextHookEx(HHOOK.Null, code, message, data);
        };
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
                using var hook = PInvoke.SetWindowsHookEx(WINDOWS_HOOK_ID.WH_MOUSE_LL,
                    _callback, PInvoke.GetModuleHandle((string?)null), 0);
                if (hook.IsInvalid) throw new InvalidOperationException("Could not isolate native test mouse input.");
                reported = true; ready.Set();
                Application.Run();
            }
            catch (Exception ex) { failure = ex; if (!reported) ready.Set(); }
            finally { _dispatcher = null; }
        }) { IsBackground = true, Name = "Native test mouse isolation" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait();
        if (failure != null) { _thread.Join(); throw new InvalidOperationException("Native test input setup failed.", failure); }
    }

    public void Dispose()
    {
        try { _dispatcher?.BeginInvoke(() => Application.ExitThread()); }
        catch (InvalidOperationException) { }
        _thread.Join();
        GC.KeepAlive(_callback);
    }
}
