using System;
using System.IO;

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
            || InspectWindow(handle) is not { } observed) return null;
        var live = _bindings.Observe(observed.Identity, out _);
        if (live.Forgotten || live.ItemId.HasValue || Document.Items.Count >= 2000) return null;
        if (live.NeedsNativeGeometry)
        {
            EnrollNativeGeometry(handle);
            live.NeedsNativeGeometry = false;
        }
        if (!_canvas.Windows.TryGetValue(handle, out var bounds) || !WorkspaceGeometry.ValidBounds(bounds)) return null;
        string exe = observed.Exe, title = _api.GetWindowTitle(handle);
        bool explorer = string.Equals(Path.GetFileName(exe), "explorer.exe", StringComparison.OrdinalIgnoreCase);
        string target = explorer && _folders.TryGetValue(handle, out var folder) ? folder : WorkspaceTargetIdentity.Read(exe, title);
        var item = new WorkspaceItem
        {
            DesktopId = _desktop, ExecutablePath = exe, Title = title, Target = target,
            IsFolder = explorer && target.Length > 0,
            AutoDetectedTarget = WorkspaceTargetIdentity.IsVsCode(exe) && target.Length > 0,
            ChromeProfilePath = ReadChromeProfile(handle, exe) ?? "",
            Bounds = bounds, Placement = WorkspacePlacement.Temporary
        };
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
