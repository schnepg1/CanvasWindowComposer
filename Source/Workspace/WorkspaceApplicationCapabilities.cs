using System;
using System.IO;

namespace CanvasDesktop;

// Derived from the executable, never from the caption or the selected target.
// Unknown apps keep manual arguments available without implying app support.
internal readonly record struct WorkspaceApplicationCapabilities(
    bool Website, bool File, bool Folder, bool AdvancedTargets = false)
{
    public static WorkspaceApplicationCapabilities For(string executable, bool isFolder = false, bool webResource = false)
    {
        if (webResource) return new(true, false, false);
        if (isFolder || Path.GetFileName(executable).Equals("explorer.exe", StringComparison.OrdinalIgnoreCase))
            return new(false, false, true);
        if (BrowserSupport.Supports(executable)) return new(true, false, false);
        if (WorkspaceTargetIdentity.IsVsCode(executable)) return new(false, true, true);
        return new(false, true, true, AdvancedTargets: true);
    }
}
