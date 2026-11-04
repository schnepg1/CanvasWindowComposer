using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CanvasDesktop;

/// <summary>Owns native mouse filters in the receiving GUI threads, across process bitness.</summary>
internal sealed class WindowInteractionInputGuard : IDisposable
{
    private readonly Dictionary<uint, Process> _hosts = new();
    private readonly object _gate = new();
    private readonly Func<ProcessStartInfo, Process?> _startHost;
    private volatile bool _disposed;
    private volatile Process[] _snapshot = [];

    internal WindowInteractionInputGuard(Func<ProcessStartInfo, Process?>? startHost = null)
        => _startHost = startHost ?? Process.Start;

    internal bool IsHealthy
    {
        get
        {
            // Called on the low-level hook thread: never wait for helper startup
            // or teardown under the UI thread's lock.
            var snapshot = _snapshot;
            try { return !_disposed && snapshot.Length > 0 && System.Linq.Enumerable.All(snapshot, host => !host.HasExited); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
        }
    }

    internal bool Protect(IntPtr window)
    {
        lock (_gate)
        {
            if (_disposed) return false;
            uint thread = PInvoke.GetWindowThreadProcessId((HWND)window, out uint processId);
            if (thread == 0 || processId == 0) return false;
            if (_hosts.TryGetValue(thread, out var existing)) return !existing.HasExited;
            Process? host = null;
            try
            {
                using var target = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, processId);
                if (target.IsInvalid || !IsWow64Process2(target, out ushort processMachine, out ushort nativeMachine)) return false;
                string architecture = (processMachine == 0 ? nativeMachine : processMachine) switch
                {
                    0x8664 => "x64",
                    0x014c => "x86",
                    _ => throw new NotSupportedException("This app's native input architecture is not supported.")
                };
                var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "native", architecture, "CanvasInputHost.exe"))
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardOutput = true
                };
                start.ArgumentList.Add(thread.ToString(System.Globalization.CultureInfo.InvariantCulture));
                start.ArgumentList.Add(processId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                start.ArgumentList.Add(window.ToInt64().ToString(System.Globalization.CultureInfo.InvariantCulture));
                host = _startHost(start) ?? throw new InvalidOperationException("Native input host did not start.");
                var ready = host.StandardOutput.ReadLineAsync();
                if (!ready.Wait(TimeSpan.FromSeconds(2)) || ready.Result != "ready" || host.HasExited)
                    throw new InvalidOperationException("Native input filter did not become ready.");
                _hosts.Add(thread, host);
                _snapshot = System.Linq.Enumerable.ToArray(_hosts.Values);
                return true;
            }
            catch (Exception ex)
            {
                TrayApp.Log("Window interaction native input could not start: " + ex.Message);
                if (host != null) Stop(host);
                return false;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _snapshot = [];
            foreach (var host in _hosts.Values) Stop(host);
            _hosts.Clear();
        }
    }

    private static void Stop(Process host)
    {
        try
        {
            try { host.StandardInput.Close(); } // EOF also releases hooks if Canvas exits unexpectedly.
            catch (IOException) { /* The helper already closed its pipe. Still ensure it exits. */ }
            if (!host.WaitForExit(1000))
            {
                // Only this guard's own child is terminated. Windows removes
                // hooks owned by an exiting process, even if the helper hung.
                host.Kill();
                if (!host.WaitForExit(1000)) TrayApp.Log("Native input host did not exit after termination.");
            }
        }
        catch (InvalidOperationException) { /* The helper has already exited. */ }
        catch (System.ComponentModel.Win32Exception ex) { TrayApp.Log("Native input cleanup failed: " + ex.Message); }
        finally { host.Dispose(); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWow64Process2(SafeProcessHandle process, out ushort processMachine, out ushort nativeMachine);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);
}
