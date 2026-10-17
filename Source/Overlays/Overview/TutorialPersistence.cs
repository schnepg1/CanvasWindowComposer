using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace CanvasDesktop;

/// <summary>Tutorial artwork never enters the personal annotation document or its undo history.</summary>
internal sealed class TutorialPersistence
{
    internal sealed record DesktopNotes(Guid DesktopId, TutorialLayout Layout, bool Visible = true);
    internal sealed record State(int Version = 2, bool Presented = false, DesktopNotes[]? Desktops = null, TutorialTourProgress[]? Tours = null);
    private readonly string _path;
    private bool _canSave = true;
    internal State Data { get; private set; } = new(Desktops: [], Tours: []);
    internal string? Status { get; private set; }
    internal bool CanPresent => _canSave && !Data.Presented;
    internal TutorialPersistence(string path)
    {
        _path=path;
        Exception? failure=null;
        foreach(var candidate in new[]{path,path+".bak"})
        {
            if(!File.Exists(candidate)) continue;
            try
            {
                if(new FileInfo(candidate).Length>4*1024*1024) throw new InvalidDataException("Tutorial file is too large.");
                string json=File.ReadAllText(candidate);
                using(var raw=JsonDocument.Parse(json))
                {
                    // A downgrade must never replace newer data with an older backup.
                    if(raw.RootElement.ValueKind==JsonValueKind.Object && raw.RootElement.TryGetProperty("Version",out var version)
                        && version.ValueKind==JsonValueKind.Number && (!version.TryGetInt32(out int number) || number is < 1 or > 2))
                    {
                        _canSave=false;Status="This tutorial file was written by an unsupported version. Existing files are preserved.";
                        return;
                    }
                }
                var state=JsonSerializer.Deserialize<State>(json);
                if(state is null || state.Version is < 1 or > 2 || state.Desktops is null || state.Desktops.Length>100 || state.Desktops.Any(d=>d is null)
                    || state.Desktops.Select(d=>d.DesktopId).Distinct().Count()!=state.Desktops.Length)
                    throw new InvalidDataException("Unsupported tutorial state.");
                foreach(var notes in state.Desktops)
                {
                    var layout=notes.Layout;
                    if(layout is null || layout.Items is null || layout.Items.Length is < 1 or > 20 || !double.IsFinite(layout.Scale) || layout.Scale is < 1 or > 4
                        || layout.Items.Any(a=>a is null || a.DesktopId!=notes.DesktopId) || layout.Items[0].Kind!=AnnotationKind.Text || layout.Welcome!=layout.Items[0].Bounds)
                        throw new InvalidDataException("Invalid tutorial layout.");
                    AnnotationDocument.Validate(layout.Items);
                }
                var tours=state.Tours??[];
                if(tours.Length>100 || tours.Any(t=>t is null) || tours.Select(t=>t.DesktopId).Distinct().Count()!=tours.Length)
                    throw new InvalidDataException("Invalid tutorial progress.");
                foreach(var tour in tours)TutorialTour.Validate(tour);
                Data=state with { Version=2,Tours=tours };
                if(candidate!=path) RestoreBackup(candidate);
                return;
            }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidDataException)
            { failure=ex; }
        }
        if(failure!=null) { _canSave=false;Status="Tutorial notes could not be loaded. Existing files are preserved: "+failure.Message; }
    }
    private void RestoreBackup(string backup)
    {
        if(File.Exists(_path))
        {
            // Stable names avoid duplicate damage archives if repair itself cannot complete.
            string hash=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(_path)));
            string damaged=_path+".corrupt-"+hash;
            if(!File.Exists(damaged)) File.Copy(_path,damaged);
        }
        string temp=_path+".recovery";
        try
        {
            byte[] bytes=File.ReadAllBytes(backup);
            using(var file=new FileStream(temp,FileMode.Create,FileAccess.Write,FileShare.None)) { file.Write(bytes);file.Flush(true); }
            File.Move(temp,_path,overwrite:true); // Keep the known-good backup intact.
        }
        finally { try { if(File.Exists(temp))File.Delete(temp); } catch(IOException) {} catch(UnauthorizedAccessException) {} }
    }
    internal DesktopNotes? ForDesktop(Guid id) => Data.Desktops?.FirstOrDefault(d=>d.DesktopId==id);
    internal void SetLayout(Guid desktop,TutorialLayout layout)
    {
        Data=Data with { Desktops=(Data.Desktops??[]).Where(d=>d.DesktopId!=desktop).Append(new(desktop,layout,ForDesktop(desktop)?.Visible??true)).ToArray() };
        Save();
    }
    internal void SetVisible(Guid desktop,bool visible)
    {
        Data=Data with { Desktops=(Data.Desktops??[]).Select(d=>d.DesktopId==desktop?d with { Visible=visible }:d).ToArray() };
        Save();
    }
    internal TutorialTourProgress? TourForDesktop(Guid desktop) => Data.Tours?.FirstOrDefault(t=>t.DesktopId==desktop);
    internal void SetTour(TutorialTourProgress progress)
    {
        TutorialTour.Validate(progress);
        Data=Data with { Tours=(Data.Tours??[]).Where(t=>t.DesktopId!=progress.DesktopId).Append(progress).ToArray() };
        Save();
    }
    internal void MarkPresented() { Data=Data with { Presented=true }; Save(); }
    private void Save()
    {
        if(!_canSave) return;
        string temp=_path+".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            var bytes=JsonSerializer.SerializeToUtf8Bytes(Data,new JsonSerializerOptions { WriteIndented=true,IgnoreReadOnlyProperties=true });
            using(var file=new FileStream(temp,FileMode.Create,FileAccess.Write,FileShare.None)) { file.Write(bytes);file.Flush(true); }
            if(File.Exists(_path)) File.Replace(temp,_path,_path+".bak"); else File.Move(temp,_path);
            Status=null;
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or JsonException)
        { Status="Tutorial progress could not be saved: "+ex.Message; }
        finally { try { if(File.Exists(temp))File.Delete(temp); } catch(IOException) {} catch(UnauthorizedAccessException) {} }
    }
}
