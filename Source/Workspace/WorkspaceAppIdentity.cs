using System;
using System.IO;
using System.Linq;

namespace CanvasDesktop;

/// <summary>Version-independent identity for executables inside the same Store package.</summary>
internal static class WorkspaceAppIdentity
{
    public static bool Same(string saved, string observed)
        => string.Equals(saved, observed, StringComparison.OrdinalIgnoreCase)
            || (PackageKey(saved) is { } key && string.Equals(key, PackageKey(observed), StringComparison.OrdinalIgnoreCase))
            || (BuildKey(saved) is { } build && string.Equals(build, BuildKey(observed), StringComparison.OrdinalIgnoreCase));

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

    /// <summary>
    /// Self-updating desktop programs nest the executable in a folder that carries
    /// the build number, for example "app-1.0.9265". Every update replaces that
    /// folder, so the build number is not part of the program's identity. The
    /// installation root and the rest of the path stay distinct.
    /// </summary>
    private static string? BuildKey(string path)
    {
        if (!TrySplitBuildFolder(path, out string root, out _, out string fileName)) return null;
        return root + "app" + "\\" + fileName;
    }

    /// <summary>Splits "...\app-1.0.9265\file.exe" into its root, folder and file name.</summary>
    private static bool TrySplitBuildFolder(string path, out string root, out string folder, out string fileName)
    {
        root = folder = fileName = "";
        if (!Path.IsPathFullyQualified(path)) return false;
        int index = path.LastIndexOf("\\app-", StringComparison.OrdinalIgnoreCase);
        if (index < 0) return false;
        int start = index + 1;
        int end = path.IndexOf('\\', start);
        if (end < 0) return false;
        if (BuildNumber(path[(start + 4)..end]) is null) return false;
        root = path[..start];
        folder = path[start..end];
        fileName = path[(end + 1)..];
        return true;
    }

    private static Version? BuildNumber(string text)
    {
        if (!Version.TryParse(text, out var parsed) || parsed is null) return null;
        return parsed.Major < 0 || parsed.Minor < 0 ? null : parsed;
    }

    /// <summary>
    /// The executable of the same program in the build that is installed now.
    /// Returns null when the saved path is current or cannot be resolved.
    /// </summary>
    public static string? CurrentBuild(string path)
    {
        if (!TrySplitBuildFolder(path, out string root, out _, out string fileName)) return null;
        if (!string.Equals(Path.GetExtension(fileName), ".exe", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            var installed = Directory.EnumerateDirectories(root, "app-*")
                .Select(directory => (Directory: directory, Version: BuildNumber(Path.GetFileName(directory)[4..])))
                .Where(candidate => candidate.Version is not null
                    && File.Exists(Path.Combine(candidate.Directory, fileName)))
                .OrderByDescending(candidate => candidate.Version!)
                .FirstOrDefault();
            return installed.Directory == null ? null : Path.Combine(installed.Directory, fileName);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
