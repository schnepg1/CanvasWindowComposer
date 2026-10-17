using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Xml.Linq;
using Microsoft.Win32;

namespace CanvasDesktop;

internal static class WorkspaceStartup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "CanvasWindowComposer-Local";

    internal static string StartupCommand(string executable)
    {
        if (!Path.IsPathFullyQualified(executable) || executable.Contains('"'))
            throw new ArgumentException("Canvas startup requires an absolute executable path.", nameof(executable));
        return "\"" + executable + "\"";
    }

    public static bool Enabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return string.Equals(key?.GetValue(ValueName) as string, StartupCommand(Environment.ProcessPath!),
            StringComparison.OrdinalIgnoreCase);
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, StartupCommand(Environment.ProcessPath!), RegistryValueKind.String);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    internal static void RemoveForCurrentExecutable()
    {
        // Never delete startup belonging to another checkout/installation.
        if (Enabled()) SetEnabled(false);
    }

    internal static void MigrateLegacyInstallation()
    {
        var previous = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "CanvasWindowComposer", "CanvasDesktop.exe");
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        if (string.Equals(key?.GetValue(ValueName) as string, StartupCommand(previous), StringComparison.OrdinalIgnoreCase)) SetEnabled(true);
    }

    // Pure validation seam: migration may never mutate another installation,
    // another user's task, or an action containing extra arguments.
    internal static bool IsOwnedLegacyTask(string xml, string executable, string userName, string userSid, out bool enabled)
    {
        enabled = false;
        var task = XDocument.Parse(xml);
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var actions = task.Descendants(ns + "Actions").SingleOrDefault()?.Elements().ToArray();
        var principal = task.Descendants(ns + "Principal").SingleOrDefault();
        string? user = principal?.Element(ns + "UserId")?.Value;
        if (actions?.Length != 1 || actions[0].Name != ns + "Exec"
            || (!string.Equals(user, userName, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(user, userSid, StringComparison.OrdinalIgnoreCase))) return false;
        var action = actions[0];
        string command = (action.Element(ns + "Command")?.Value ?? "").Trim().Trim('"');
        if (!string.IsNullOrWhiteSpace(action.Element(ns + "Arguments")?.Value)
            || !Path.IsPathFullyQualified(command)
            || !string.Equals(Path.GetFullPath(command), Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase)) return false;
        enabled = !string.Equals(task.Descendants(ns + "Settings").SingleOrDefault()?.Element(ns + "Enabled")?.Value,
            "false", StringComparison.OrdinalIgnoreCase);
        return true;
    }

    public static int MigrateLegacyTasks()
    {
        using var identity = WindowsIdentity.GetCurrent();
        int migrated = 0;
        foreach (string name in new[] { "CanvasWindowComposer-Local", "CanvasWindowComposer" })
        {
            var query = Run("/Query", "/TN", name, "/XML");
            if (query.code != 0) continue;
            if (!IsOwnedLegacyTask(query.output, Environment.ProcessPath!, identity.Name, identity.User!.Value, out bool enabled)) continue;
            if (enabled) SetEnabled(true); // preserve enabled startup before removing legacy scheduling
            var deleted = Run("/Delete", "/TN", name, "/F");
            if (deleted.code != 0) throw new InvalidOperationException("Could not remove the legacy Canvas startup task. " + deleted.error);
            migrated++;
        }
        return migrated;
    }

    private static (int code, string output, string error) Run(params string[] args)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"))
        { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(10000)) { process.Kill(); throw new TimeoutException("Task Scheduler did not respond."); }
        System.Threading.Tasks.Task.WaitAll(stdout, stderr);
        return (process.ExitCode, stdout.Result, stderr.Result);
    }
}
