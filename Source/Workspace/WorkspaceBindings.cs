using System;
using System.Collections.Generic;
using System.Linq;

namespace CanvasDesktop;

// Runtime identities never enter workspace.json. PID alone is also recyclable.
internal readonly record struct WorkspaceProcessIdentity(uint ProcessId, long StartedAtUtcTicks, string ExecutablePath);
internal readonly record struct WorkspaceWindowIdentity(IntPtr Handle, uint ProcessId, long ProcessStartedAtUtcTicks);

internal sealed class WorkspaceLiveWindow
{
    public WorkspaceWindowIdentity Identity { get; }
    public Guid Token { get; } = Guid.NewGuid();
    public Guid? ItemId { get; set; }
    public bool Forgotten { get; set; }
    public long? WaitingForFolderSince { get; set; }
    public bool NeedsNativeGeometry { get; set; }
    public WorkspaceLiveWindow(WorkspaceWindowIdentity identity) { Identity = identity; }
}

/// <summary>Session-only, one-to-one bindings, independent of native calls and storage.</summary>
internal sealed class WorkspaceBindings
{
    private readonly Dictionary<IntPtr, WorkspaceLiveWindow> _windows = new();
    public IEnumerable<WorkspaceLiveWindow> Windows => _windows.Values;
    public IEnumerable<Guid> BoundIds => Windows.Where(w => w.ItemId.HasValue).Select(w => w.ItemId!.Value);
    public WorkspaceLiveWindow? At(IntPtr handle) => _windows.GetValueOrDefault(handle);
    public WorkspaceLiveWindow? ForItem(Guid id) => Windows.FirstOrDefault(w => w.ItemId == id);
    public WorkspaceLiveWindow? ForToken(Guid token) => Windows.FirstOrDefault(w => w.Token == token);

    public WorkspaceLiveWindow Observe(WorkspaceWindowIdentity identity, out bool replaced)
    {
        var old = At(identity.Handle);
        replaced = old != null && old.Identity != identity;
        if (old != null && !replaced) return old;
        return _windows[identity.Handle] = new WorkspaceLiveWindow(identity) { NeedsNativeGeometry = replaced };
    }

    public bool Bind(WorkspaceLiveWindow window, Guid itemId)
    {
        if (itemId == Guid.Empty || At(window.Identity.Handle) != window || window.Forgotten || window.ItemId.HasValue
            || ForItem(itemId) != null) return false;
        window.ItemId = itemId;
        return true;
    }

    public WorkspaceLiveWindow? Remove(IntPtr handle)
        => _windows.Remove(handle, out var window) ? window : null;
    public void Clear() => _windows.Clear();
}
