using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace CanvasDesktop;

/// <summary>Atomic sidecar persistence. A failed load never authorizes overwriting the original.</summary>
internal sealed class BackgroundPersistence
{
    internal sealed class FileDocument
    {
        public int Version { get; set; } = 1;
        public List<BackgroundSurface> Items { get; set; } = new();
    }

    private const int MaximumBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, IncludeFields = true };
    private readonly string _path;
    public List<BackgroundSurface> Items { get; }
    public string? Status { get; private set; }
    public bool CanSave { get; private set; } = true;

    public BackgroundPersistence(string path)
    {
        _path = path;
        try { Items = Load(); }
        catch (Exception ex) when (Recoverable(ex))
        {
            Items = new();
            CanSave = false;
            Status = "Backgrounds could not be loaded. Existing files are preserved: " + ex.Message;
        }
    }

    private List<BackgroundSurface> Load()
    {
        Exception? failure = null;
        foreach (var path in new[] { _path, _path + ".bak" })
        {
            if (!File.Exists(path)) continue;
            try
            {
                if (new FileInfo(path).Length > MaximumBytes) throw new InvalidDataException("Background file is too large.");
                var document = JsonSerializer.Deserialize<FileDocument>(File.ReadAllText(path), Options);
                if (document == null || document.Version != 1) throw new InvalidDataException("Unsupported background format.");
                Validate(document.Items);
                if (path != _path)
                {
                    if (File.Exists(_path)) File.Copy(_path, _path + ".corrupt-" + Guid.NewGuid().ToString("N"));
                    Status = "Recovered canvas backgrounds from backup.";
                }
                return document.Items;
            }
            catch (Exception ex) when (Recoverable(ex)) { failure = ex; }
        }
        if (failure != null) throw new IOException("Could not read backgrounds or backup.", failure);
        return new();
    }

    public bool Save()
    {
        if (!CanSave) return false;
        var temporary = _path + ".tmp";
        try
        {
            Validate(Items);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new FileDocument { Items = Items }, Options);
            if (bytes.Length > MaximumBytes) throw new InvalidDataException("Background file is too large.");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(flushToDisk: true); }
            if (File.Exists(_path)) File.Replace(temporary, _path, _path + ".bak");
            else File.Move(temporary, _path);
            Status = null;
            return true;
        }
        catch (Exception ex) when (Recoverable(ex))
        { Status = "Backgrounds could not be saved: " + ex.Message; return false; }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void Validate(List<BackgroundSurface>? items)
    {
        if (items == null || items.Count > BackgroundRules.MaximumSurfaces
            || items.Any(item => !BackgroundRules.Valid(item)) || items.Select(item => item.Id).Distinct().Count() != items.Count)
            throw new InvalidDataException("Invalid background document.");
    }

    private static bool Recoverable(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException;
}
