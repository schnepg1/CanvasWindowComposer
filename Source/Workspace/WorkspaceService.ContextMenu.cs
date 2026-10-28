using System;

namespace CanvasDesktop;

internal sealed partial class WorkspaceService
{
    // Resolve on demand: a detached browser tab can be right-clicked before the
    // next capture, and an ambiguous startup window may have no saved binding.
    internal Guid? GetWindowContextItem(IntPtr handle)
    {
        if (_disposed || !_started || _switching || !_desktops.IsOnCurrentDesktop(handle)
            || !_api.IsWindowVisible(handle)) return null;
        if (ValidBinding(handle) is { ItemId: { } bound } && Find(bound)?.DesktopId == _desktop) return bound;
        if (!_canvas.HasWindow(handle) || !_api.IsManageable(handle, (uint)Environment.ProcessId, true, true)
            || _windowInspector.Inspect(handle) is not { } observed) return null;
        var live = _bindings.Observe(observed.Identity, out _);
        if (live.Forgotten || live.ItemId.HasValue || Document.Items.Count >= 2000) return null;
        if (live.NeedsNativeGeometry)
        {
            EnrollNativeGeometry(handle);
            live.NeedsNativeGeometry = false;
        }
        if (!_canvas.Windows.TryGetValue(handle, out var bounds) || !WorkspaceGeometry.ValidBounds(bounds)) return null;
        var metadata = _windowInspector.ReadMetadata(handle, observed.Exe, _folders);
        var item = metadata.CreateItem(_desktop, bounds, WorkspacePlacement.Temporary);
        if (!StillCurrent(live) || !_api.IsWindowVisible(handle) || !_desktops.IsOnCurrentDesktop(handle)
            || !_bindings.Bind(live, item.Id)) return null;
        // A menu must not move the window, guess an old saved place or persist a
        // duplicate. Keep this session-only entry until explicitly kept/linked.
        Document.Items.Add(item);
        live.NeedsInitialMatch = false;
        InvalidateOverviewSnapshot();
        Changed?.Invoke();
        return item.Id;
    }
}
