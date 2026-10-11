using System;
using System.Diagnostics;

namespace CanvasDesktop;

internal interface IWorkspaceLauncher
{
    void Open(WorkspaceItem item);
}

/// <summary>Single guarded boundary for starting saved applications.</summary>
internal sealed class WorkspaceLauncher : IWorkspaceLauncher
{
    private readonly Func<bool> _isElevated;
    private readonly Action<ProcessStartInfo> _start;

    public WorkspaceLauncher(Func<bool>? isElevated = null, Action<ProcessStartInfo>? start = null)
    {
        _isElevated = isElevated ?? ProcessPrivileges.IsElevated;
        _start = start ?? (info => { using var process = Process.Start(info); });
    }

    public void Open(WorkspaceItem item)
    {
        // Defence in depth if a future host bypasses Program's startup guard.
        if (_isElevated()) throw new InvalidOperationException("Workspace applications cannot be launched from an elevated Canvas process. Restart Canvas normally.");
        _start(WorkspaceIdentity.LaunchInfo(item));
    }
}
