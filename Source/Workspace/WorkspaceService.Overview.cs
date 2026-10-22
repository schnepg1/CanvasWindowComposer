using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace CanvasDesktop;

// Cosmetic render data only. Never use this snapshot to authorize a native action.
internal sealed record WorkspaceOverviewSnapshot(
    IReadOnlyDictionary<IntPtr, Guid> ItemIds,
    IReadOnlyDictionary<IntPtr, IntPtr> RepresentedWindows,
    IReadOnlyDictionary<IntPtr, IntPtr> CardSources)
{
    public static WorkspaceOverviewSnapshot Empty { get; } = new(
        ReadOnlyDictionary<IntPtr, Guid>.Empty,
        ReadOnlyDictionary<IntPtr, IntPtr>.Empty,
        ReadOnlyDictionary<IntPtr, IntPtr>.Empty);
}

internal sealed partial class WorkspaceService
{
    private WorkspaceOverviewSnapshot? _overviewSnapshot;
    private long _overviewSnapshotRevision;
    private long _overviewGeneration;
    private readonly List<WorkspaceWindowIdentity> _overviewIdentities = new();

    private void InvalidateOverviewSnapshot() { _overviewSnapshot = null; _overviewGeneration++; }

    public WorkspaceOverviewSnapshot GetOverviewSnapshot()
    {
        if (_disposed || _switching || !_started) return WorkspaceOverviewSnapshot.Empty;
        if (_overviewSnapshot != null && _overviewSnapshotRevision == _bindings.Revision)
        {
            // USER32 checks are cheap. A changed PID or hidden/destroyed window
            // retires the map immediately, even if its lifecycle event was missed.
            // Same-PID process reuse is detected by events/the regular capture;
            // commands still validate process start time immediately before use.
            foreach (var identity in _overviewIdentities)
                if (_api.GetWindowProcessId(identity.Handle) != identity.ProcessId
                    || !_api.IsWindowVisible(identity.Handle))
                { InvalidateOverviewSnapshot(); break; }
            if (_overviewSnapshot != null) return _overviewSnapshot;
        }

        var ids = new Dictionary<IntPtr, Guid>();
        var represented = new Dictionary<IntPtr, IntPtr>();
        var sources = new Dictionary<IntPtr, IntPtr>();
        long generation = _overviewGeneration;
        _overviewIdentities.Clear();
        foreach (var item in Document.Items.Where(i => i.DesktopId == _desktop).ToArray())
        {
            if (_cards.TryGetValue(item.Id, out var card)) ids[card.Handle] = item.Id;
            // One full validation per item when workspace data changes, shared by
            // all three maps. Steady camera/animation frames do not inspect processes.
            if (BoundWindow(item.Id) is not { } live) continue;
            var handle = live.Identity.Handle;
            ids[handle] = item.Id;
            _overviewIdentities.Add(live.Identity);
            if (card == null) continue;
            represented[handle] = card.Handle;
            sources[card.Handle] = handle;
        }
        // Native validation may dispatch lifecycle events on this UI thread.
        // Do not publish maps built across card disposal/desktop switching.
        if (_disposed || _switching || generation != _overviewGeneration) return WorkspaceOverviewSnapshot.Empty;
        _overviewSnapshotRevision = _bindings.Revision;
        return _overviewSnapshot = new(new ReadOnlyDictionary<IntPtr, Guid>(ids),
            new ReadOnlyDictionary<IntPtr, IntPtr>(represented), new ReadOnlyDictionary<IntPtr, IntPtr>(sources));
    }
}
