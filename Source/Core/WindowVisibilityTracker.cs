using System;
using System.Collections.Generic;

namespace CanvasDesktop;

internal enum RuntimeWindowVisibility { Visible, NearViewport, OffScreen, AutomaticallyMinimized, UserMinimized }

// A token belongs to one observation lifetime, including HWND reuse in the same process.
internal readonly record struct AutomaticParkingToken(WorkspaceWindowIdentity Identity, Guid Lifetime);
internal readonly record struct WindowVisibilitySnapshot(RuntimeWindowVisibility State,
    bool CanPark, bool ShouldRestore, bool AutomaticallyParked, WorkspaceWindowIdentity? Identity);

/// <summary>
/// Session-only resource policy. Observations and ownership never change WorldRect or call USER32.
/// A future native controller must validate a token immediately before every parking/restore call.
/// </summary>
internal sealed class WindowVisibilityTracker
{
    internal const int ApproachMarginPx = 256;
    internal const int DepartureMarginPx = 512;
    internal const long ParkingDelayMs = 1500;
    internal const long RestoreDelayMs = 150;

    private sealed class Entry
    {
        public WorkspaceWindowIdentity? Identity;
        public Guid Lifetime = Guid.NewGuid();
        public bool Far, Owned, Pending;
        public long? FarSince, ApproachSince;
        public WindowVisibilitySnapshot Snapshot;
    }
    private readonly Dictionary<IntPtr, Entry> _entries = new();
    public WindowVisibilitySnapshot? Get(IntPtr handle)
        => _entries.TryGetValue(handle, out var entry) ? entry.Snapshot : null;
    public AutomaticParkingToken? GetToken(IntPtr handle)
        => _entries.TryGetValue(handle, out var e) && e.Identity.HasValue
            ? new(e.Identity.Value, e.Lifetime) : null;

    public WindowVisibilitySnapshot Observe(IntPtr handle, WorkspaceWindowIdentity? identity,
        WindowRect projected, IReadOnlyList<(int x, int y, int w, int h)> screens,
        bool minimized, bool eligible, long now)
    {
        if (!_entries.TryGetValue(handle, out var e) || e.Identity != identity)
            _entries[handle] = e = new Entry { Identity = identity };

        bool visible = Intersects(projected, screens, 0);
        bool near = Intersects(projected, screens, e.Far ? ApproachMarginPx : DepartureMarginPx);
        e.Far = !visible && !near;
        bool hasViewport = false;
        foreach (var screen in screens)
            if (screen.w > 0 && screen.h > 0) { hasViewport = true; break; }
        eligible &= hasViewport && projected.W > 0 && projected.H > 0;
        // A native restoration relinquishes ownership, including taskbar/Alt-Tab restores.
        if (!minimized && e.Owned) e.Owned = false;
        if (!eligible || !e.Far || minimized || identity == null) e.FarSince = null;
        else e.FarSince ??= now;
        if (!e.Owned || e.Far) e.ApproachSince = null;
        else e.ApproachSince ??= now;

        var state = minimized
            ? e.Owned ? RuntimeWindowVisibility.AutomaticallyMinimized : RuntimeWindowVisibility.UserMinimized
            : visible ? RuntimeWindowVisibility.Visible
            : e.Far ? RuntimeWindowVisibility.OffScreen : RuntimeWindowVisibility.NearViewport;
        return e.Snapshot = new(state,
            !e.Pending && e.FarSince.HasValue && now - e.FarSince.Value >= ParkingDelayMs,
            e.Owned && (visible || !eligible || (e.ApproachSince.HasValue && now - e.ApproachSince.Value >= RestoreDelayMs)),
            e.Owned, identity);
    }

    public AutomaticParkingToken? BeginParking(IntPtr handle)
    {
        if (!_entries.TryGetValue(handle, out var e) || !e.Snapshot.CanPark || e.Identity == null) return null;
        e.Pending = true;
        e.Snapshot = e.Snapshot with { CanPark = false };
        return new(e.Identity.Value, e.Lifetime);
    }

    public bool IsCurrent(AutomaticParkingToken token)
        => _entries.TryGetValue(token.Identity.Handle, out var e)
            && e.Identity == token.Identity && e.Lifetime == token.Lifetime;

    // Queued acknowledgement alone is insufficient: require confirmed native minimization.
    public bool CompleteParking(AutomaticParkingToken token, NativeOperationResult result, bool minimized)
    {
        if (!IsCurrent(token)) return false;
        var e = _entries[token.Identity.Handle];
        if (!e.Pending) return false;
        e.Pending = false;
        e.Owned = result.Status == NativeOperationStatus.Succeeded && minimized
            && result.Window == token.Identity.Handle.ToInt64();
        e.FarSince = null;
        e.Snapshot = e.Snapshot with { CanPark = false, AutomaticallyParked = e.Owned,
            State = e.Owned ? RuntimeWindowVisibility.AutomaticallyMinimized : e.Snapshot.State };
        return e.Owned;
    }

    public void Remove(IntPtr handle) => _entries.Remove(handle);
    public void RelinquishParking(IntPtr handle)
    {
        if (!_entries.TryGetValue(handle, out var e)) return;
        e.Owned = e.Pending = false;
        e.FarSince = e.ApproachSince = null;
        e.Snapshot = e.Snapshot with { AutomaticallyParked = false, CanPark = false, ShouldRestore = false };
    }
    public void Clear() => _entries.Clear();

    private static bool Intersects(WindowRect rect, IReadOnlyList<(int x, int y, int w, int h)> screens, int margin)
    {
        if (rect.W <= 0 || rect.H <= 0) return false;
        foreach (var s in screens)
            if (s.w > 0 && s.h > 0 && (long)rect.X + rect.W > (long)s.x - margin
                && rect.X < (long)s.x + s.w + margin && (long)rect.Y + rect.H > (long)s.y - margin
                && rect.Y < (long)s.y + s.h + margin) return true;
        return false;
    }
}
