using System;
using System.Linq;

namespace CanvasDesktop;

internal static class WorkspaceCopies
{
    // Repair incomplete/older copy metadata deterministically. Never group by target.
    public static void Normalize(WorkspaceDocument document)
    {
        foreach (var item in document.Items.Where(i => i.CopyGroupId == Guid.Empty)) item.IsMainCopy = true;
        foreach (var group in document.Items.Where(i => i.CopyGroupId != Guid.Empty)
                     .GroupBy(i => (i.DesktopId, i.CopyGroupId)))
        {
            var main = group.FirstOrDefault(i => i.IsMainCopy) ?? group.First();
            foreach (var item in group) item.IsMainCopy = item == main;
        }
    }

    public static void CopyTarget(WorkspaceItem source, WorkspaceItem target)
    {
        target.Title = source.Title;
        target.ExecutablePath = source.ExecutablePath;
        target.Target = source.Target;
        target.AutoDetectedTarget = source.AutoDetectedTarget;
        target.ChromeProfilePath = source.ChromeProfilePath;
        target.CaptureChromeProfile = source.CaptureChromeProfile;
        target.WebResource = source.WebResource;
        target.IsFolder = source.IsFolder;
        target.OpenOnStartup = source.OpenOnStartup;
        target.ExcludeFromAutomaticMinimization = source.ExcludeFromAutomaticMinimization;
    }
}
