using System;
using System.Collections.Generic;
using System.Linq;

namespace CanvasDesktop;

// User actions on saved places and explicit window linking.
internal sealed partial class WorkspaceService
{
    public void Open(Guid id) { if (Find(id) is { } item) Open(item, focusPlaceholder: true); }
    public void FocusItem(Guid id) { if (Find(id) is { } item) FocusItem(item); }
    public void CloseToPlaceholder(Guid id) { if (Find(id) is { } item) CloseToPlaceholder(item); }
    public void Forget(Guid id) { if (Find(id) is { } item) Forget(item); }

    private void FocusItem(WorkspaceItem item)
    {
        if (item.Bounds.InInventory) { InventoryRequested?.Invoke(item.Id); return; }
        if (IsOpen(item)) { Open(item); return; }
        if (_cards.TryGetValue(item.Id, out var card)) Focus(card.Handle, item.Bounds);
    }

    private void Focus(IntPtr handle, WorldRect bounds)
    {
        var screen = _windowActions.Viewport;
        _canvas.CenterOn(bounds.X, bounds.Y, bounds.W, bounds.H, screen.Width, screen.Height);
        _canvas.Commit();
        _windowActions.Focus(handle, (_api.GetWindowStyle(handle) & (int)WINDOW_STYLE.WS_MINIMIZE) != 0);
    }

    private void CloseToPlaceholder(WorkspaceItem item)
    {
        if (!IsOpen(item)) return;
        Capture(); Save();
        if (BoundWindow(item.Id) is not { } live) return;
        // Normal close: application save prompts and cancellation still work.
        _windowActions.RequestClose(live.Identity.Handle);
    }

    private void Forget(WorkspaceItem item)
    {
        if (item.Bounds.InInventory && IsOpen(item) && !DropFromInventory(item.Id)) return;
        if (BoundWindow(item.Id) is { } live) { live.Forgotten = true; _bindings.Unbind(live); }
        _launching.Remove(item.Id);
        _launchBaselines.Remove(item.Id);
        _ambiguous.Remove(item.Id);
        _queued.Remove(item.Id);
        _launchMessages.Remove(item.Id);
        Document.Items.Remove(item);
        Zones.ForgetItem(item.Id);
        RemoveCard(item.Id);
        Save(); Changed?.Invoke();
    }

    public IReadOnlyList<WorkspaceLinkCandidate> LinkCandidates(Guid id)
    {
        if (Find(id) is not { } item || item.DesktopId != _desktop || IsOpen(item))
            return Array.Empty<WorkspaceLinkCandidate>();
        return _bindings.Windows.Where(live => CanLink(live, item))
            .Select(live => new WorkspaceLinkCandidate(live.Token,
                $"{_api.GetWindowTitle(live.Identity.Handle)}  [PID {live.Identity.ProcessId}, window 0x{live.Identity.Handle.ToInt64():X}]"))
            .ToArray();
    }

    private bool CanLink(WorkspaceLiveWindow live, WorkspaceItem item)
        => !live.Forgotten && (!live.ItemId.HasValue || Find(live.ItemId.Value)?.Placement == WorkspacePlacement.Temporary)
            && MatchesLinkWindow(live, item);

    private bool MatchesLinkWindow(WorkspaceLiveWindow live, WorkspaceItem item)
        => !live.Forgotten && _api.IsWindowVisible(live.Identity.Handle)
            && _desktops.IsOnCurrentDesktop(live.Identity.Handle) && StillCurrent(live)
            && _windowInspector.Inspect(live.Identity.Handle) is { } observed
            && WorkspaceAppIdentity.Same(observed.Exe, item.ExecutablePath)
            && ((item.WebResource?.ProfilePath ?? item.ChromeProfilePath).Length == 0 || _windowInspector.ReadChromeProfile(live.Identity.Handle, observed.Exe) is not { } profile
                || string.Equals(item.WebResource?.ProfilePath ?? item.ChromeProfilePath, profile, StringComparison.OrdinalIgnoreCase));

    public string? LinkCandidateHint(Guid id)
    {
        if (Find(id) is not { } item || item.DesktopId != _desktop) return null;
        int bound = _bindings.Windows.Count(live => live.ItemId.HasValue
            && Find(live.ItemId.Value) is { Placement: not WorkspacePlacement.Temporary } && MatchesLinkWindow(live, item));
        return bound switch
        {
            0 => null,
            1 => "A matching open window is already linked to another saved place. A window can only be linked to one place at a time.",
            _ => $"{bound} matching open windows are already linked to other saved places. A window can only be linked to one place at a time."
        };
    }

    public bool LinkWindow(Guid id, Guid candidateToken)
    {
        if (Find(id) is not { } item || item.DesktopId != _desktop || IsOpen(item)
            || _bindings.ForToken(candidateToken) is not { } live || !CanLink(live, item)
            || _windowInspector.Inspect(live.Identity.Handle) is not { } observed || observed.Identity != live.Identity) return false;
        DetachTemporary(live);
        if (!_bindings.Bind(live, id)) return false;
        item.ExecutablePath = observed.Exe;
        if (item.CaptureChromeProfile && _windowInspector.ReadChromeProfile(live.Identity.Handle, item.ExecutablePath) is { } profile) item.ChromeProfilePath = profile;
        RestoreBinding(live, item);
        EnsureCards(); Save(); PublishTemporaryAreaChange(); Changed?.Invoke();
        return IsOpen(item);
    }
}
