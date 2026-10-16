using System;
using System.Collections.Generic;
using System.Linq;

namespace CanvasDesktop;

/// <summary>Commit validated session-window geometry, including the restore position of non-normal windows.</summary>
internal static class CanvasWindowPlacement
{
    internal static bool ApplySelection(Canvas canvas, IWindowApi api, IReadOnlyList<(IntPtr Handle, WorldRect Bounds)> placements)
    {
        if (placements.Any(p=>!canvas.Windows.TryGetValue(p.Handle,out var current) || current.PinnedToScreen
            || !WorkspaceGeometry.ValidBounds(p.Bounds))) throw new ArgumentException("A selected window or destination changed.");
        foreach(var p in placements) canvas.SetWindow(p.Handle,p.Bounds.X,p.Bounds.Y,p.Bounds.W,p.Bounds.H);
        bool accepted=true;
        foreach(var p in placements)
        {
            var state=canvas.GetWindowState(p.Handle);
            if(state!=WindowState.Normal) accepted &= api.RestoreNativeState(p.Handle,canvas.WorldToScreen(p.Bounds),state).Accepted;
        }
        return accepted; // Logical placement remains committed and undoable after a native acknowledgement failure.
    }
}
