using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace CanvasDesktop;

internal sealed class WorkspaceStore : IWorkspaceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, IncludeFields = true };
    public string FilePath { get; }

    public WorkspaceStore(string path) { FilePath = path; }

    public WorkspaceDocument Load(out string? warning)
    {
        warning = null;
        if (!File.Exists(FilePath) && !File.Exists(FilePath + ".bak")) return new() { PlacementPolicyInitialized = true };
        Exception? failure = null;
        foreach (var path in new[] { FilePath, FilePath + ".bak" })
        {
            if (!File.Exists(path)) continue;
            try
            {
                if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new InvalidDataException("Workspace file is too large.");
                var doc = JsonSerializer.Deserialize<WorkspaceDocument>(File.ReadAllText(path), JsonOptions)
                    ?? throw new InvalidDataException("Empty workspace file.");
                Validate(doc);
                if (path != FilePath)
                {
                    // Preserve the damaged file before a later save rotates the backup.
                    if (File.Exists(FilePath)) File.Copy(FilePath, FilePath + ".corrupt-" + Guid.NewGuid().ToString("N"));
                    warning = "Recovered the saved workspace from its backup.";
                }
                return doc;
            }
            catch (Exception ex) when (ex is IOException || ex is JsonException || ex is UnauthorizedAccessException)
            { failure = ex; }
        }
        throw new IOException("Could not read the workspace or its backup. Existing files were preserved.", failure);
    }

    public void Save(WorkspaceDocument document)
    {
        Validate(document);
        document = WorkspacePersistence.SavedState(document);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath))!);
        var temp = FilePath + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, document, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(FilePath)) File.Replace(temp, FilePath, FilePath + ".bak");
            else File.Move(temp, FilePath);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static void Validate(WorkspaceDocument doc)
    {
        if (doc.Version != 1 || doc.Items == null || doc.Cameras == null || doc.Items.Count > 2000
            || doc.Zones == null || doc.Zones.Count(z => z == null || !z.IsTemporaryArea) > ZoneService.MaximumZones
            || doc.Zones.Count > ZoneService.MaximumZones * 2 || doc.TemporaryAreas == null
            || doc.PendingPlacementAdoptions == null || doc.PendingPlacementAdoptions.Count > 20000)
            throw new InvalidDataException("Unsupported or invalid workspace format.");
        var ids = new HashSet<Guid>();
        var adoptions = new HashSet<WorkspacePlacementAdoption>();
        foreach (var adoption in doc.PendingPlacementAdoptions)
            if (adoption == null || adoption.Handle == 0 || adoption.ProcessId == 0
                || adoption.ProcessStartedAtUtcTicks <= 0 || !adoptions.Add(adoption))
                throw new InvalidDataException("Invalid outstanding workspace adoption.");
        foreach (var i in doc.Items)
        {
            if (i == null || !Enum.IsDefined(i.Placement) || i.Id == Guid.Empty || !ids.Add(i.Id) || i.Title == null
                || i.Name == null || i.Name.Length > 200
                || i.ExecutablePath == null || i.Target == null || !WorkspaceGeometry.ValidBounds(i.Bounds)
                || !ChromeProfiles.ValidPath(i.ChromeProfilePath)
                || !BrowserSupport.ValidResource(i.ExecutablePath, i.WebResource)
                || (i.WebResource != null && (i.IsFolder || i.Target.Length > 0 || i.ChromeProfilePath.Length > 0 || i.CaptureChromeProfile))
                || (i.ChromeProfilePath.Length > 0 && (i.IsFolder || !ChromeProfiles.Supports(i.ExecutablePath))))
                throw new InvalidDataException("Invalid workspace item.");
        }
        var zoneIds = new HashSet<Guid>();
        var temporaryDesktops = new HashSet<Guid>();
        var itemsById = new Dictionary<Guid, WorkspaceItem>();
        foreach (var item in doc.Items) itemsById.Add(item.Id, item);
        foreach (var zone in doc.Zones)
        {
            if (zone == null || zone.Id == Guid.Empty || !zoneIds.Add(zone.Id)
                || zone.IsTemporaryArea && !temporaryDesktops.Add(zone.DesktopId)
                || !ZoneService.ValidName(zone.Name) || !ZoneService.ValidColor(zone.Color)
                || !ZoneGeometry.ValidBounds(zone.Bounds) || zone.ItemIds == null || zone.ItemIds.Count > 2000)
                throw new InvalidDataException("Invalid workspace zone.");
            var memberIds = new HashSet<Guid>();
            foreach (var id in zone.ItemIds)
                if (!memberIds.Add(id) || !itemsById.TryGetValue(id, out var item) || item.DesktopId != zone.DesktopId)
                    throw new InvalidDataException("Invalid workspace zone membership.");
        }
        foreach (var c in doc.Cameras.Values)
            if (c == null || !double.IsFinite(c.X) || !double.IsFinite(c.Y)
                || !double.IsFinite(c.Zoom) || c.Zoom < 0.01 || c.Zoom > 10)
                throw new InvalidDataException("Invalid workspace camera.");
        foreach (var area in doc.TemporaryAreas.Values)
            if (area == null || !double.IsFinite(area.X) || !double.IsFinite(area.Y)
                || Math.Abs(area.X) >= 10000000 || Math.Abs(area.Y) >= 10000000)
                throw new InvalidDataException("Invalid temporary area anchor.");
    }

}
