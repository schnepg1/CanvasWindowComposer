using System;

namespace CanvasDesktop;

// One observation of app content, independent of live bindings and placement.
// Capture and menu recovery share these rules when creating workspace entries.
internal sealed record WorkspaceWindowMetadata(string ExecutablePath, string Title, string Target,
    bool IsExplorer, string? ChromeProfilePath)
{
    public WorkspaceItem CreateItem(Guid desktop, WorldRect bounds, WorkspacePlacement placement)
        => new()
        {
            DesktopId = desktop, ExecutablePath = ExecutablePath, Title = Title, Target = Target,
            IsFolder = IsExplorer && Target.Length > 0,
            AutoDetectedTarget = WorkspaceTargetIdentity.IsVsCode(ExecutablePath) && Target.Length > 0,
            ChromeProfilePath = ChromeProfilePath ?? "", Bounds = bounds, Placement = placement
        };

    public void CaptureProfileInto(WorkspaceItem item)
    {
        if (ChromeProfilePath != null && item.CaptureChromeProfile && item.WebResource == null)
            item.ChromeProfilePath = ChromeProfilePath;
    }

    public void CaptureTargetInto(WorkspaceItem item)
    {
        if (item.WebResource != null || Target.Length == 0) return;
        if (WorkspaceTargetIdentity.IsVsCode(ExecutablePath))
        {
            if (item.Target.Length == 0 || item.AutoDetectedTarget)
            { item.Target = Target; item.AutoDetectedTarget = true; }
        }
        else
        {
            item.Target = Target;
            if (IsExplorer) item.IsFolder = true;
        }
    }
}
