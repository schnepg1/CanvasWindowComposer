using System;
using System.Runtime.InteropServices;
using System.Reflection;
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
        // Installer hooks must run before the single-instance mutex or desktop services.
        // Downloading an update never authorizes an unattended restart.
        Velopack.VelopackApp.Build().SetAutoApplyOnStartup(false)
            .OnBeforeUninstallFastCallback(_ => WorkspaceStartup.RemoveForCurrentExecutable()).Run();
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
        if (Has("--diagnostics") || Has("--diagnostics-file"))
        {
            var report = System.Text.Json.JsonSerializer.Serialize(new
            {
                Framework = RuntimeInformation.FrameworkDescription,
                RuntimeDirectory = RuntimeEnvironment.GetRuntimeDirectory(),
                AppVersion = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                Elevated = ProcessPrivileges.IsElevated(),
                Executable = Environment.ProcessPath,
                StartupEnabled = WorkspaceStartup.Enabled()
            });
            int index = Array.FindIndex(args, a => a == "--diagnostics-file");
            if (index >= 0)
            {
                try
                {
                    if (index + 1 >= args.Length || !System.IO.Path.IsPathFullyQualified(args[index + 1])) throw new ArgumentException("Supply an absolute diagnostic output path.");
                    System.IO.File.WriteAllText(args[index + 1], report);
                }
                catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
            }
            else Console.WriteLine(report);
            return;
        }
        if (Has("--enable-startup"))
        {
            try { WorkspaceStartup.SetEnabled(true); }
            catch (Exception ex) { Environment.ExitCode = 1; ApplicationErrorDialog.ShowError(ex, "Enable startup"); }
            return;
        }
        if (ProcessPrivileges.IsElevated())
        {
            // The previous local installation used a highest-privilege task.
            // Migrate only matching current-user tasks, then exit. A subsequent
            // ordinary launch runs unelevated; no workspace is loaded here.
            try { if (WorkspaceStartup.MigrateLegacyTasks() > 0) return; }
            catch (Exception ex) { ApplicationErrorDialog.ShowError(ex, "Startup migration"); Environment.ExitCode = 1; return; }
            MessageBox.Show("Start Canvas normally, not with Run as administrator. Elevated Canvas sessions are disabled to keep workspace launches unelevated.",
                "Canvas security", MessageBoxButtons.OK, MessageBoxIcon.Information);
            Environment.ExitCode = 1;
            return;
        }

        try
        {
            if (new VelopackUpdateBackend().Installed) WorkspaceStartup.MigrateLegacyInstallation();
        }
        catch (Exception ex) { ApplicationErrorDialog.ShowError(ex, "Installed startup migration"); Environment.ExitCode = 1; return; }

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
            var app = new TrayApp();
            using (app) Application.Run(app);
            // All desktop recovery and disposal has completed before starting Update.exe.
            app.SchedulePendingUpdate();
        }
        catch (Exception ex)
        {
            Environment.ExitCode = 1;
            ApplicationErrorDialog.ShowError(ex, "Application startup/run", recoveryAttempted: true);
        }
        finally { ShutdownSignal = null; }
    }
}
