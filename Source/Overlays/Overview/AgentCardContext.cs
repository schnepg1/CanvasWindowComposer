using System.Collections.Generic;
using System.Linq;

namespace CanvasDesktop;

internal static class AgentCardContext
{
    // Nested projects use the smallest enclosing area; mission frames are not projects.
    internal static WorkspaceZone? ProjectAt(IEnumerable<WorkspaceZone> zones, CanvasAnnotation card)
        => zones.Where(zone => zone.DesktopId == card.DesktopId && !zone.IsMission && !zone.IsTemporaryArea
            && CanvasSelection.Contains(new(zone.Bounds.X, zone.Bounds.Y, zone.Bounds.W, zone.Bounds.H), card.A))
            .OrderBy(zone => zone.Bounds.W * zone.Bounds.H).ThenBy(zone => zone.Id).FirstOrDefault();
}
