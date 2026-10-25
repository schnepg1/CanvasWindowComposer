using System;
using System.Collections.Generic;

namespace CanvasDesktop;

internal sealed partial class WorkspaceService
{
    private readonly Dictionary<Guid, string> _launchMessages = new();

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
