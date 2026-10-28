using System;
using System.Collections.Generic;
using System.Linq;

namespace CanvasDesktop;

internal sealed partial class WorkspaceService
{
    private readonly Dictionary<Guid, long> _launching = new();
    private readonly Dictionary<Guid, HashSet<IntPtr>> _launchBaselines = new();
    private readonly HashSet<Guid> _queued = new();
    private readonly HashSet<Guid> _startupAttempted = new();
    private readonly Dictionary<Guid, string> _launchMessages = new();

    private void OpenStartupItems()
    {
        foreach (var item in Document.Items.Where(i => i.DesktopId == _desktop && i.OpenOnStartup).ToArray())
        {
            // Existing Code windows may still be waiting on their first folder
            // snapshot. Launching now would duplicate a window already open.
            if (WorkspaceTargetIdentity.IsVsCode(item.ExecutablePath) && (_ambiguous.Contains(item.Id)
                || _bindings.Windows.Any(w => w.WaitingForProjectSince is { } since
                    && _clock.TickCount64 - since < 8000 && _desktops.IsOnCurrentDesktop(w.Identity.Handle)))) continue;
            if (_startupAttempted.Add(item.Id) && !IsOpen(item)) Open(item);
        }
    }

    private bool IsLaunchingApp(string exe) => _launching.Keys.Any(id => Document.Items.Any(i => i.Id == id
        && WorkspaceAppIdentity.Same(i.ExecutablePath, exe)));

    private void Open(WorkspaceItem item, bool focusPlaceholder = false)
    {
        if (BoundWindow(item.Id) is { } live)
        {
            RemoveCard(item.Id);
            Focus(live.Identity.Handle, item.Bounds);
            _nextCapture = 0;
            Changed?.Invoke();
            return;
        }
        if (_launching.ContainsKey(item.Id)) { RefreshLaunchCard(item, focusPlaceholder); return; }
        _launchMessages.Remove(item.Id);
        // Launch windows from the same app one at a time, so project/file slots
        // cannot steal one another's first new HWND.
        if (item.DesktopId != _desktop || IsLaunchingApp(item.ExecutablePath))
        { _queued.Add(item.Id); RefreshLaunchCard(item, focusPlaceholder); Changed?.Invoke(); return; }
        _queued.Remove(item.Id);
        try
        {
            // Include even inaccessible HWNDs. Later gaining permission to inspect
            // an already-existing window does not make it evidence of this launch.
            var baseline = new HashSet<IntPtr>();
            _api.EnumWindows(handle => { baseline.Add(handle); return true; });
            _launchBaselines[item.Id] = baseline;
            _launching[item.Id] = _clock.TickCount64 + 30000;
            RefreshLaunchCard(item, focusPlaceholder);
            _nextCapture = 0;
            _launcher.Open(item);
            _status = null;
        }
        catch (Exception ex)
        {
            _launching.Remove(item.Id);
            _launchBaselines.Remove(item.Id);
            _status = "Could not open " + item.Title + ": " + ex.Message;
            _launchMessages[item.Id] = "Could not start the app. Double-click to retry.";
            RefreshLaunchCard(item, focus: false);
            LaunchFailed?.Invoke(_status);
        }
        Changed?.Invoke();
    }

    private void RefreshLaunchCard(WorkspaceItem item, bool focus)
    {
        if (!_cards.TryGetValue(item.Id, out var card)) return;
        if (focus && item.DesktopId == _desktop) Focus(card.Handle, item.Bounds);
        card.UpdateItem(View(item));
    }

    private bool CanFinishLaunch(WorkspaceItem item, WorkspaceLiveWindow live, WorkspaceMatchInput input)
    {
        if (WorkspaceTargetIdentity.IsVsCode(input.ExecutablePath))
        {
            // Code's launcher may hand off to an existing process. A new blank
            // Code shell is not proof that the requested project has opened.
            if (item.Target.Length > 0 && !string.Equals(item.Target, input.Target, StringComparison.OrdinalIgnoreCase))
                return false;
            if (string.IsNullOrWhiteSpace(input.Title) || input.Title is "Visual Studio Code" or "Visual Studio Code - Insiders")
                return false;
            if (item.Target.Length == 0 && WorkspaceTargetIdentity.ReadProjectCaption(item.ExecutablePath, item.Title) is { } project
                && WorkspaceTargetIdentity.ReadProjectCaption(input.ExecutablePath, input.Title) != project)
                return false;
        }
        return StillCurrent(live) && _api.HasVisibleClientArea(live.Identity.Handle);
    }
}
