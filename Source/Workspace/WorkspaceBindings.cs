using System;
using System.Collections.Generic;
using System.Linq;

namespace CanvasDesktop;

// Live bindings are session-only. The upgrade's outstanding adoption records
// persist this identity separately and never authorize native actions by themselves.
internal readonly record struct WorkspaceProcessIdentity(uint ProcessId, long StartedAtUtcTicks, string ExecutablePath);
internal readonly record struct WorkspaceWindowIdentity(IntPtr Handle, uint ProcessId, long ProcessStartedAtUtcTicks);

internal sealed class WorkspaceLiveWindow
{
    public WorkspaceWindowIdentity Identity { get; }
    public Guid Token { get; } = Guid.NewGuid();
    public Guid? ItemId { get; internal set; }
    public bool Forgotten { get; set; }
    public long? WaitingForFolderSince { get; set; }
    public long? WaitingForProjectSince { get; set; }
    public bool NeedsNativeGeometry { get; set; }
    public bool NeedsInitialMatch { get; set; } = true;
    public bool StartupArrival { get; set; }
    public WorkspaceLiveWindow(WorkspaceWindowIdentity identity) { Identity = identity; }
}

/// <summary>Session-only, one-to-one bindings, independent of native calls and storage.</summary>
internal sealed class WorkspaceBindings
{
    private readonly Dictionary<IntPtr, WorkspaceLiveWindow> _windows = new();
    private readonly Dictionary<Guid, WorkspaceLiveWindow> _items = new();
    public long Revision { get; private set; }
    public IEnumerable<WorkspaceLiveWindow> Windows => _windows.Values;
    public IEnumerable<Guid> BoundIds => Windows.Where(w => w.ItemId.HasValue).Select(w => w.ItemId!.Value);
    public WorkspaceLiveWindow? At(IntPtr handle) => _windows.GetValueOrDefault(handle);
    public WorkspaceLiveWindow? ForItem(Guid id) => _items.GetValueOrDefault(id);
    public WorkspaceLiveWindow? ForToken(Guid token) => Windows.FirstOrDefault(w => w.Token == token);

    public WorkspaceLiveWindow Observe(WorkspaceWindowIdentity identity, out bool replaced)
    {
        var old = At(identity.Handle);
        replaced = old != null && old.Identity != identity;
        if (old != null && !replaced) return old;
        if (old != null) Unbind(old);
        Revision++;
        return _windows[identity.Handle] = new WorkspaceLiveWindow(identity) { NeedsNativeGeometry = replaced };
    }

    public bool Bind(WorkspaceLiveWindow window, Guid itemId)
    {
        if (itemId == Guid.Empty || At(window.Identity.Handle) != window || window.Forgotten || window.ItemId.HasValue
            || ForItem(itemId) != null) return false;
        window.ItemId = itemId;
        _items.Add(itemId, window);
        Revision++;
        return true;
    }

    public void Unbind(WorkspaceLiveWindow window)
    {
        if (window.ItemId is not { } id) return;
        if (_items.GetValueOrDefault(id) == window) _items.Remove(id);
        window.ItemId = null;
        Revision++;
    }

    public WorkspaceLiveWindow? Remove(IntPtr handle)
    {
        if (!_windows.Remove(handle, out var window)) return null;
        // Preserve the removed entry's ID for the caller's final geometry save.
        if (window.ItemId is { } id && _items.GetValueOrDefault(id) == window) _items.Remove(id);
        Revision++;
        return window;
    }
    public void Clear() { _windows.Clear(); _items.Clear(); Revision++; }
}
