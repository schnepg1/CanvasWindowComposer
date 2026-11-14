using System;
using System.IO;
using System.Linq;

namespace CanvasDesktop;

// Shared persisted contract: ProjectFolderPath is independent of mission working folders.
internal static class ZoneProjectFolder
{
    internal static bool ValidPath(string path) => !string.IsNullOrWhiteSpace(path)
        && path.Length <= 32767 && path.IndexOf('\0') < 0 && Path.IsPathFullyQualified(path);

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool Matches(string? left, string right)
    {
        if (left == null || !ValidPath(left)) return false;
        try { return string.Equals(Normalize(left), right, StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { return false; }
    }

    // Called only for an explicit selection. No tick/load/layout hook may redock a folder.
    internal static bool TryAssign(WorkspaceDocument document, Guid desktop, Guid zoneId, string path,
        Func<Guid, bool> isPlaceholder, out WorkspaceItem? folder)
    {
        folder = null;
        var zone = document.Zones.FirstOrDefault(z => z.Id == zoneId && z.DesktopId == desktop);
        if (zone == null || zone.IsTemporaryArea || zone.IsMission || !ValidPath(path)) return false;
        string normalized;
        try { normalized = Normalize(path); }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { return false; }
        var item = document.Items.Where(i => i.DesktopId == desktop && i.IsFolder && Matches(i.Target, normalized)
                && isPlaceholder(i.Id))
            .OrderByDescending(i => zone.ItemIds.Contains(i.Id)).FirstOrDefault();
        if (item == null && document.Items.Count >= 2000) return false;
        bool dock = !Matches(zone.ProjectFolderPath, normalized) || item == null
            || !zone.ItemIds.Contains(item.Id) || item.Bounds.InInventory;
        var bounds = item?.Bounds ?? new WorldRect { W = 480, H = 320 };
        if (dock)
        {
            bounds.X = zone.Bounds.X + 24; bounds.Y = zone.Bounds.Y + 24;
            bounds.InInventory = false; bounds.PinnedToScreen = false; bounds.State = WindowState.Normal;
            if (!WorkspaceGeometry.ValidBounds(bounds)) return false;
        }
        if (item == null)
        {
            item = new WorkspaceItem { DesktopId = desktop, IsFolder = true, Target = normalized,
                Title = Path.GetFileName(normalized),
                ExecutablePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe") };
            if (item.Title.Length == 0) item.Title = normalized;
            document.Items.Add(item);
        }
        item.Bounds = bounds;
        item.Placement = WorkspacePlacement.Deliberate;
        foreach (var temporary in document.Zones.Where(z => z.IsTemporaryArea)) temporary.ItemIds.Remove(item.Id);
        if (!zone.ItemIds.Contains(item.Id)) zone.ItemIds.Add(item.Id);
        zone.ProjectFolderPath = normalized;
        folder = item;
        return true;
    }
}
