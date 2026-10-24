using System;
using System.IO;

namespace CanvasDesktop;

/// <summary>Version-independent identity for executables inside the same Store package.</summary>
internal static class WorkspaceAppIdentity
{
    public static bool Same(string saved, string observed)
        => string.Equals(saved, observed, StringComparison.OrdinalIgnoreCase)
            || (PackageKey(saved) is { } key && string.Equals(key, PackageKey(observed), StringComparison.OrdinalIgnoreCase));

    private static string? PackageKey(string path)
    {
        // Only remove the version component of a fully qualified WindowsApps
        // package path. Keep installation root, name, architecture, resource ID,
        // publisher and the entire relative executable path distinct.
        const string marker = "\\WindowsApps\\";
        int start = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0 || !Path.IsPathFullyQualified(path)) return null;
        start += marker.Length;
        int end = path.IndexOf('\\', start);
        if (end < 0) return null;
        var parts = path[start..end].Split('_');
        if (parts.Length != 5 || parts[0].Length == 0 || parts[2].Length == 0 || parts[4].Length == 0
            || !Version.TryParse(parts[1], out var version) || version.Revision < 0) return null;
        return path[..start] + parts[0] + "_" + parts[2] + "_" + parts[3] + "_" + parts[4] + path[end..];
    }
}
