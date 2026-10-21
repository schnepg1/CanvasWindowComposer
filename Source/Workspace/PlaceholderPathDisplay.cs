using System;
using System.IO;

namespace CanvasDesktop;

/// <summary>Leaf-first path text for placeholder cards; the full path stays in a tooltip.</summary>
internal readonly record struct PlaceholderPathDisplay(string Leaf, string Parent, string Kind, string Full)
{
    public static PlaceholderPathDisplay For(WorkspaceItemView item)
    {
        string full = item.Target.Length > 0 ? item.Target : item.ExecutablePath;
        string trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string leaf = Path.GetFileName(trimmed);
        string parent = Path.GetDirectoryName(trimmed) ?? "";
        if (leaf.Length == 0)
        {
            leaf = full;
            parent = "";
        }
        string kind = item.IsFolder ? "FOLDER"
            : item.Target.Length == 0 ? "APPLICATION"
            : FileKind(trimmed);
        return new(leaf, parent, kind, full);
    }

    private static string FileKind(string path)
    {
        string extension = Path.GetExtension(path);
        if (extension.Length <= 1) return "FILE";
        string label = extension[1..].ToUpperInvariant();
        return label.Length <= 5 ? label + " FILE" : "FILE";
    }
}
