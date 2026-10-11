using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace CanvasDesktop;

internal static class Program
{
    internal static EventWaitHandle? ShutdownSignal;
    private const string ShutdownEventName = "CanvasDesktop_GracefulShutdown";
    private const string InstanceMutexName = "CanvasDesktop_SingleInstance";

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [STAThread]
    static void Main(string[] args)
    {
        bool Has(string argument) => Array.Exists(args, a => a.Equals(argument, StringComparison.OrdinalIgnoreCase));
        if (Has("--exit"))
        {
            try
            {
                using var signal = EventWaitHandle.OpenExisting(ShutdownEventName);
                signal.Set();
                // Installers/build scripts wait for cleanup rather than kill us.
                using var instance = Mutex.OpenExisting(InstanceMutexName);
                try
                {
                    if (instance.WaitOne(TimeSpan.FromSeconds(15))) instance.ReleaseMutex();
                    else Environment.ExitCode = 1;
                }
                catch (AbandonedMutexException) { instance.ReleaseMutex(); }
            }
            catch (WaitHandleCannotBeOpenedException) { }
            catch (UnauthorizedAccessException) { Environment.ExitCode = 1; }
            return;
        }
        if (Has("--diagnostics"))
        {
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
            {
                Framework = RuntimeInformation.FrameworkDescription,
                Elevated = ProcessPrivileges.IsElevated(),
                Executable = Environment.ProcessPath,
                StartupEnabled = WorkspaceStartup.Enabled()
            }));
            return;
        }
        if (Has("--enable-startup"))
        {
            try { WorkspaceStartup.SetEnabled(true); }
            catch (Exception ex) { Environment.ExitCode = 1; MessageBox.Show(ex.Message, "Canvas startup"); }
            return;
        }
        if (ProcessPrivileges.IsElevated())
        {
            // The previous local installation used a highest-privilege task.
            // Migrate only matching current-user tasks, then exit. A subsequent
            // ordinary launch runs unelevated; no workspace is loaded here.
            try { if (WorkspaceStartup.MigrateLegacyTasks() > 0) return; }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Canvas startup migration"); Environment.ExitCode = 1; return; }
            MessageBox.Show("Start Canvas normally, not with Run as administrator. Elevated Canvas sessions are disabled to keep workspace launches unelevated.",
                "Canvas security", MessageBoxButtons.OK, MessageBoxIcon.Information);
            Environment.ExitCode = 1;
            return;
        }

        SetProcessDpiAwarenessContext((IntPtr)(-4)); // PER_MONITOR_AWARE_V2
        using var mutex = new Mutex(true, InstanceMutexName, out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("Canvas Desktop is already running.", "Canvas Desktop",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        // Route UI-dispatch exceptions through the owned context's finally/disposal
        // path, instead of leaving hooks/windows alive behind an exception dialog.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        if (SynchronizationContext.Current == null)
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        using var shutdown = new EventWaitHandle(false, EventResetMode.AutoReset, ShutdownEventName);
        ShutdownSignal = shutdown;
        try
        {
            using var app = new TrayApp();
            Application.Run(app);
        }
        catch (Exception ex)
        {
            Environment.ExitCode = 1;
            TrayApp.Log("Application startup/run failed: " + ex);
            MessageBox.Show("Canvas stopped after an error and attempted desktop recovery.\n\n" + ex.Message,
                "Canvas Desktop", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { ShutdownSignal = null; }
    }
}
