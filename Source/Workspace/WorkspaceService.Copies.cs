using System;
using System.Linq;

namespace CanvasDesktop;

internal sealed partial class WorkspaceService
{
    private WorkspaceItem MainCopy(WorkspaceItem item) => item.CopyGroupId == Guid.Empty ? item
        : Document.Items.FirstOrDefault(i => i.CopyGroupId == item.CopyGroupId
            && i.DesktopId == item.DesktopId && i.IsMainCopy) ?? item;

    private WorkspaceItem? FindMain(Guid id) => Find(id) is { } item ? MainCopy(item) : null;

    private void SynchronizeCopyTargets()
    {
        foreach (var item in Document.Items.Where(i => !i.IsMainCopy))
            WorkspaceCopies.CopyTarget(MainCopy(item), item);
    }

    public void Duplicate(Guid id) => RunItemCommand(id, item =>
    {
        if (item.DesktopId != _desktop || item.Placement == WorkspacePlacement.Temporary
            || Document.Items.Count >= 2000) return;
        Capture();
        var main = MainCopy(item);
        if (main.CopyGroupId == Guid.Empty) main.CopyGroupId = Guid.NewGuid();
        var bounds = item.Bounds;
        bounds.X += 48; bounds.Y += 48;
        bounds.State = WindowState.Normal; bounds.PinnedToScreen = false; bounds.InInventory = false;
        if (!WorkspaceGeometry.ValidBounds(bounds)) return;
        var copy = new WorkspaceItem { DesktopId = item.DesktopId, CopyGroupId = main.CopyGroupId,
            IsMainCopy = false, Name = item.Name, Bounds = bounds };
        WorkspaceCopies.CopyTarget(main, copy);
        Document.Items.Add(copy);
        EnsureCards(); Save(); Changed?.Invoke();
    });

    public void SetAsMain(Guid id) => RunItemCommand(id, item =>
    {
        if (item.DesktopId != _desktop || item.IsMainCopy) return;
        Capture();
        PromoteCopy(item);
        EnsureCards(); Save(); Changed?.Invoke();
    });

    private void PromoteCopy(WorkspaceItem item)
    {
        var previous = MainCopy(item);
        if (previous == item) return;
        WorkspaceCopies.CopyTarget(previous, item);
        var live = BoundWindow(previous.Id);
        previous.IsMainCopy = false;
        item.IsMainCopy = true;
        if (_launching.Remove(previous.Id, out var deadline)) _launching[item.Id] = deadline;
        if (_launchBaselines.Remove(previous.Id, out var baseline)) _launchBaselines[item.Id] = baseline;
        if (_launchMessages.Remove(previous.Id, out var message)) _launchMessages[item.Id] = message;
        if (_queued.Remove(previous.Id)) _queued.Add(item.Id);
        if (_startupAttempted.Remove(previous.Id)) _startupAttempted.Add(item.Id);
        _ambiguous.Remove(previous.Id);
        if (live != null)
        {
            _bindings.Unbind(live);
            if (_bindings.Bind(live, item.Id)) RestoreBinding(live, item);
        }
    }

    // Resolve before starting the standard overview flight, including the real
    // window when the main's card has been retired after a successful open.
    internal IntPtr ResolveMainNavigationWindow(IntPtr handle)
    {
        if (_disposed || CardItem(handle) is not { } id || Find(id) is not { } item) return handle;
        var main = MainCopy(item);
        if (main == item) return handle;
        if (_cards.TryGetValue(main.Id, out var card)) return card.Handle;
        return BoundWindow(main.Id)?.Identity.Handle ?? handle;
    }
}
