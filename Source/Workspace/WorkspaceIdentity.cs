using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace CanvasDesktop;

internal sealed class WorkspaceIdentity : IWorkspaceIdentity
{
    private readonly VsCodeProjectDiscovery _vsCodeProjects = new();
    public string? ReadVsCodeTarget(WorkspaceWindowIdentity window, string executablePath, string title)
        => _vsCodeProjects.Read(window, executablePath, title);
    public string? ReadWindowAppId(IntPtr handle) => WorkspaceWindowProperties.ReadAppId(handle);
    private Dictionary<IntPtr, string> _folderSnapshot = new();
    private int _folderRequest;
    private DateTime _nextFolderPoll;
    private readonly Func<Dictionary<IntPtr, string>> _readFolders;

    public WorkspaceIdentity(Func<Dictionary<IntPtr, string>>? folderReader = null)
    { _readFolders = folderReader ?? ReadExplorerFoldersCore; }

    public Dictionary<IntPtr, string> ReadExplorerFolders()
    {
        // Cross-process COM can block indefinitely when an Explorer tab hangs.
        // Never call it on the canvas UI thread, and never accumulate workers.
        if (DateTime.UtcNow >= _nextFolderPoll && Interlocked.CompareExchange(ref _folderRequest, 1, 0) == 0)
        {
            _nextFolderPoll = DateTime.UtcNow.AddSeconds(5);
            var worker = new Thread(() =>
            {
                try { Volatile.Write(ref _folderSnapshot, _readFolders()); }
                catch { /* Keep the last good snapshot if Explorer is unavailable. */ }
                finally { Interlocked.Exchange(ref _folderRequest, 0); }
            }) { IsBackground = true, Name = "WorkspaceExplorerFolders" };
            worker.SetApartmentState(ApartmentState.STA);
            worker.Start();
        }
        return Volatile.Read(ref _folderSnapshot);
    }

    // Shell.Application exposes the active Explorer tab's actual folder, rather
    // than guessing from captions such as "Projects" shared by many directories.
    private Dictionary<IntPtr, string> ReadExplorerFoldersCore()
    {
        var result = new Dictionary<IntPtr, string>();
        object? shell = null, windows = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!);
            windows = ((dynamic)shell!).Windows();
            int count = ((dynamic)windows).Count;
            for (int n = 0; n < count; n++)
            {
                object? window = null, document = null, folder = null, self = null;
                try
                {
                    window = ((dynamic)windows).Item(n);
                    if (!string.Equals(Path.GetFileName((string)((dynamic)window!).FullName), "explorer.exe", StringComparison.OrdinalIgnoreCase)) continue;
                    document = ((dynamic)window).Document;
                    folder = ((dynamic)document).Folder;
                    self = ((dynamic)folder).Self;
                    string path = ((dynamic)self).Path;
                    if (!string.IsNullOrWhiteSpace(path)) result[new IntPtr((long)(uint)(int)((dynamic)window).HWND)] = path;
                }
                catch { /* A tab may close during enumeration. */ }
                finally { Release(self); Release(folder); Release(document); Release(window); }
            }
        }
        catch { /* Explorer automation can be unavailable across integrity levels. */ }
        finally { Release(windows); Release(shell); }
        return result;
    }

    private static void Release(object? value)
    {
        if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
    }

    public WorkspaceProcessIdentity? InspectProcess(uint pid)
    {
        try
        {
            using var process = Process.GetProcessById(checked((int)pid));
            var exe = process.MainModule?.FileName;
            if (string.IsNullOrEmpty(exe)) return null;
            return new(pid, process.StartTime.ToUniversalTime().Ticks, exe);
        }
        catch { return null; } // Unavailable identity is not permission to guess.
    }

    public static ProcessStartInfo LaunchInfo(WorkspaceItem item)
    {
        if (item.IsFolder)
        {
            bool shellFolder = item.Target.StartsWith("::", StringComparison.Ordinal)
                && Guid.TryParse(item.Target.AsSpan(2), out _);
            if ((!Path.IsPathFullyQualified(item.Target) || !Directory.Exists(item.Target)) && !shellFolder)
                throw new IOException("The saved folder is unavailable: " + item.Target);
            var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"))
                { UseShellExecute = false, WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows) };
            // No shell command line: the folder is a single escaped argument.
            info.ArgumentList.Add("/n,");
            info.ArgumentList.Add(item.Target);
            return info;
        }
        if (!Path.IsPathFullyQualified(item.ExecutablePath) || !File.Exists(item.ExecutablePath)
            || !string.Equals(Path.GetExtension(item.ExecutablePath), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new IOException("The saved application is unavailable: " + item.ExecutablePath);
        if (!string.IsNullOrEmpty(item.Target) && (!Path.IsPathFullyQualified(item.Target)
            || (!File.Exists(item.Target) && !Directory.Exists(item.Target))))
            throw new IOException("Choose an existing file or folder, not command-line options or a URL, as the launch target.");
        // No associations, shell command string, runas verb, or UAC elevation.
        var launch = new ProcessStartInfo(item.ExecutablePath)
            { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(item.ExecutablePath)! };
        if (item.ChromeProfilePath.Length > 0) ChromeProfiles.AddLaunchArguments(launch, item.ChromeProfilePath);
        if (WorkspaceTargetIdentity.IsVsCode(item.ExecutablePath)) launch.ArgumentList.Add("--new-window");
        if (!string.IsNullOrEmpty(item.Target)) launch.ArgumentList.Add(item.Target);
        return launch;
    }
}
