using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace CanvasDesktop;

/// <summary>One bounded background poll; no pipe or process inspection on the canvas UI thread.</summary>
internal sealed class VsCodeProjectDiscovery
{
    private readonly object _gate = new();
    private readonly Func<Task<IReadOnlyList<VsCodeProjectWindow>>> _read;
    private readonly Func<long> _ticks;
    private IReadOnlyList<VsCodeProjectWindow> _snapshot = Array.Empty<VsCodeProjectWindow>();
    private bool _polling;
    private long _nextPoll = long.MinValue, _snapshotAt = long.MinValue;

    public VsCodeProjectDiscovery(Func<Task<IReadOnlyList<VsCodeProjectWindow>>>? read = null, Func<long>? ticks = null)
    { _read = read ?? VsCodeDiagnostics.ReadAsync; _ticks = ticks ?? (() => Environment.TickCount64); }

    // null means still waiting for evidence; empty means a known unsupported or
    // ambiguous window. A previous session's PID/caption cannot identify a slot.
    public string? Read(WorkspaceWindowIdentity window, string exe, string title)
    {
        if (!WorkspaceTargetIdentity.IsVsCode(exe)) return "";
        lock (_gate)
        {
            long now = _ticks();
            if (!_polling && now >= _nextPoll)
            {
                _polling = true; _nextPoll = now + 3000;
                _ = Task.Run(PollAsync);
            }
            if (_snapshotAt == long.MinValue || now - _snapshotAt > 8000) return null;
            var windows = _snapshot.Where(w => w.Process.ProcessId == window.ProcessId
                && w.Process.StartedAtUtcTicks == window.ProcessStartedAtUtcTicks
                && string.Equals(w.Process.ExecutablePath, exe, StringComparison.OrdinalIgnoreCase) && w.Title == title).ToArray();
            return windows.Length == 1 ? windows[0].Target : windows.Length > 1 ? "" : null;
        }
    }

    private async Task PollAsync()
    {
        IReadOnlyList<VsCodeProjectWindow> snapshot;
        try { snapshot = await _read().ConfigureAwait(false); }
        catch { snapshot = Array.Empty<VsCodeProjectWindow>(); }
        lock (_gate)
        {
            _snapshot = snapshot; _snapshotAt = _ticks(); _polling = false;
        }
    }
}
