using System;
using System.IO;

namespace CanvasDesktop;

internal interface IWorkspaceTargetAdapter
{
    bool Supports(string executablePath);
    string ReadTarget(string windowTitle);
}

/// <summary>Application-specific identity evidence, not guesses from folder basenames.</summary>
internal static class WorkspaceTargetIdentity
{
    private static readonly VsCodeTargetAdapter VsCode = new();
    private static readonly IWorkspaceTargetAdapter[] Adapters = { VsCode };
    public static bool IsVsCode(string exe) => VsCode.Supports(exe);
    public static string? ReadProjectCaption(string exe, string title)
        => VsCode.Supports(exe) ? VsCode.ReadProjectCaption(title) : null;
    public static string Read(string exe, string title)
    {
        foreach (var adapter in Adapters)
            if (adapter.Supports(exe)) return adapter.ReadTarget(title);
        return "";
    }
}

internal sealed class VsCodeTargetAdapter : IWorkspaceTargetAdapter
{
    // Default VS Code captions have a changing active file followed by the
    // workspace and app name. This is weaker evidence than an exact caption or
    // full target path; duplicate project names must still require linking.
    public string? ReadProjectCaption(string title)
    {
        foreach (string separator in new[] { " - ", " — ", " – " })
        {
            foreach (string app in new[] { "Visual Studio Code", "Visual Studio Code - Insiders" })
            {
                string suffix = separator + app;
                if (!title.EndsWith(suffix, StringComparison.Ordinal)) continue;
                string content = title[..^suffix.Length];
                int split = content.LastIndexOf(separator, StringComparison.Ordinal);
                if (split < 0) return null; // Welcome/untitled windows have no workspace evidence.
                string project = content[(split + separator.Length)..].Trim();
                return project.Length > 0 ? project : null;
            }
        }
        return null;
    }

    // Opt-in window.title suffix: " [CanvasRoot:${rootPath}]". Do not change
    // VS Code settings automatically or infer C:\A\Project from just "Project".
    public const string Marker = " [CanvasRoot:";
    public bool Supports(string executablePath)
    {
        var file = Path.GetFileName(executablePath);
        return file.Equals("Code.exe", StringComparison.OrdinalIgnoreCase)
            || file.Equals("Code - Insiders.exe", StringComparison.OrdinalIgnoreCase);
    }
    public string ReadTarget(string windowTitle)
    {
        int start = windowTitle.LastIndexOf(Marker, StringComparison.Ordinal);
        if (start < 0 || !windowTitle.EndsWith(']')) return "";
        var path = windowTitle.Substring(start + Marker.Length, windowTitle.Length - start - Marker.Length - 1);
        try { return Path.IsPathFullyQualified(path) ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) : ""; }
        catch (ArgumentException) { return ""; }
        catch (NotSupportedException) { return ""; }
        catch (PathTooLongException) { return ""; }
    }
}
