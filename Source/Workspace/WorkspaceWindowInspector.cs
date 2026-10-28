using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CanvasDesktop;

// Read-only Windows evidence. This component never binds, moves or saves a window.
internal sealed class WorkspaceWindowInspector
{
    private readonly IWindowApi _api;
    private readonly IWorkspaceIdentity _identity;
    private readonly WorkspaceDocument _document;

    public WorkspaceWindowInspector(IWindowApi api, IWorkspaceIdentity identity, WorkspaceDocument document)
    {
        _api = api;
        _identity = identity;
        _document = document;
    }

    public WorkspaceWindowMetadata ReadMetadata(IntPtr handle, string executable, IReadOnlyDictionary<IntPtr, string> folders)
    {
        string title = _api.GetWindowTitle(handle);
        string? profile = ReadChromeProfile(handle, executable);
        bool explorer = string.Equals(Path.GetFileName(executable), "explorer.exe", StringComparison.OrdinalIgnoreCase);
        string target = explorer && folders.TryGetValue(handle, out var folder) ? folder
            : WorkspaceTargetIdentity.Read(executable, title);
        return new(executable, title, target, explorer, profile);
    }

    public (WorkspaceWindowIdentity Identity, string Exe)? Inspect(IntPtr handle)
    {
        var pid = _api.GetWindowProcessId(handle);
        var process = _identity.InspectProcess(pid);
        if (pid == 0 || process is not { } p || p.ProcessId != pid || p.StartedAtUtcTicks <= 0
            || p.ExecutablePath.Length == 0 || _api.GetWindowProcessId(handle) != pid) return null;
        return (new(handle, pid, p.StartedAtUtcTicks), p.ExecutablePath);
    }

    public string? ReadChromeProfile(IntPtr handle, string exe, string? additionalProfile = null)
    {
        if (!ChromeProfiles.Supports(exe)) return null;
        var candidates = ChromeProfiles.Get(exe).Select(p => p.Path).Concat(_document.Items
            .Where(i => string.Equals(i.ExecutablePath, exe, StringComparison.OrdinalIgnoreCase)).Select(i => i.WebResource?.ProfilePath ?? i.ChromeProfilePath))
            .Append(additionalProfile ?? "");
        return ChromeProfiles.Resolve(exe, _identity.ReadWindowAppId(handle),
            candidates.Where(p => p.Length > 0 && File.Exists(Path.Combine(p, "Preferences"))));
    }
}
