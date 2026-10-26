using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace CanvasDesktop;

/// <summary>Ten camera slots per virtual desktop, saved independently of windows and undo history.</summary>
internal sealed class CameraBookmarkStore
{
    internal sealed record Bookmark(Guid DesktopId, int Slot, CanvasCamera Camera);
    internal sealed record State(int Version = 1, Bookmark[]? Bookmarks = null);
    private readonly string _path;
    private readonly Dictionary<(Guid Desktop, int Slot), CanvasCamera> _bookmarks = new();
    private bool _canSave = true;
    internal string? Status { get; private set; }

    internal CameraBookmarkStore(string path)
    {
        _path = path;
        try
        {
            if (!File.Exists(path)) return;
            if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("Bookmark file is too large.");
            var state = JsonSerializer.Deserialize<State>(File.ReadAllText(path));
            if (state is null || state.Version != 1 || state.Bookmarks is null || state.Bookmarks.Length > 1000
                || state.Bookmarks.Any(b => b is null || b.Slot is < 0 or > 9 || !Valid(b.Camera))
                || state.Bookmarks.Select(b => (b.DesktopId, b.Slot)).Distinct().Count() != state.Bookmarks.Length)
                throw new InvalidDataException("Unsupported or invalid camera bookmarks.");
            foreach (var bookmark in state.Bookmarks) _bookmarks.Add((bookmark.DesktopId, bookmark.Slot), bookmark.Camera);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            _canSave = false;
            Status = "Camera bookmarks could not be loaded. Existing file is preserved: " + ex.Message;
        }
    }

    internal bool TryGet(Guid desktop, int slot, out CanvasCamera camera) => _bookmarks.TryGetValue((desktop, slot), out camera);

    internal bool Save(Guid desktop, int slot, CanvasCamera camera)
    {
        if (slot is < 0 or > 9 || !Valid(camera)) throw new ArgumentException("Invalid camera bookmark.");
        // Holding a shortcut down must not continuously rewrite an unchanged bookmark.
        if (_bookmarks.TryGetValue((desktop, slot), out var existing) && existing == camera && Status == null) return true;
        if (!_bookmarks.ContainsKey((desktop, slot)) && _bookmarks.Count >= 1000)
        { Status = "Camera bookmark capacity reached."; return false; }
        _bookmarks[(desktop, slot)] = camera;
        if (!_canSave) return false; // The new bookmark remains available for this session.
        string temp = _path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            var state = new State(Bookmarks: _bookmarks.Select(b => new Bookmark(b.Key.Desktop, b.Key.Slot, b.Value)).ToArray());
            var bytes = JsonSerializer.SerializeToUtf8Bytes(state, new JsonSerializerOptions { WriteIndented = true });
            using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None)) { file.Write(bytes); file.Flush(true); }
            if (File.Exists(_path)) File.Replace(temp, _path, _path + ".bak"); else File.Move(temp, _path);
            Status = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { Status = "Camera bookmark is available for this session but could not be saved: " + ex.Message; return false; }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    private static bool Valid(CanvasCamera camera) => double.IsFinite(camera.X) && double.IsFinite(camera.Y)
        && double.IsFinite(camera.Zoom) && camera.Zoom is >= OverviewCamera.ZoomMin and <= OverviewCamera.ZoomMax;
}
