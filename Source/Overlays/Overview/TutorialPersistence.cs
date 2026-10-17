using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace CanvasDesktop;

/// <summary>Tutorial artwork never enters the personal annotation document or its undo history.</summary>
internal sealed class TutorialPersistence
{
    internal sealed record DesktopNotes(Guid DesktopId, TutorialLayout Layout, bool Visible = true);
    internal sealed record State(int Version = 1, bool Presented = false, DesktopNotes[]? Desktops = null);
    private readonly string _path;
    private bool _canSave = true;
    internal State Data { get; private set; } = new(Desktops: []);
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
                var state=JsonSerializer.Deserialize<State>(File.ReadAllText(candidate));
                if(state is null || state.Version!=1 || state.Desktops is null || state.Desktops.Length>100 || state.Desktops.Any(d=>d is null)
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
                Data=state;
                // Do not overwrite a damaged primary when recovering its backup.
                if(candidate!=path && File.Exists(path)) File.Copy(path,path+".corrupt-"+Guid.NewGuid().ToString("N"));
                return;
            }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidDataException)
            { failure=ex; }
        }
        if(failure!=null) { _canSave=false;Status="Tutorial notes could not be loaded. Existing files are preserved: "+failure.Message; }
    }
    internal DesktopNotes? ForDesktop(Guid id) => Data.Desktops?.FirstOrDefault(d=>d.DesktopId==id);
    internal void SetLayout(Guid desktop,TutorialLayout layout)
    {
        Data=Data with { Desktops=(Data.Desktops??[]).Where(d=>d.DesktopId!=desktop).Append(new(desktop,layout)).ToArray() };
        Save();
    }
    internal void SetVisible(Guid desktop,bool visible)
    {
        Data=Data with { Desktops=(Data.Desktops??[]).Select(d=>d.DesktopId==desktop?d with { Visible=visible }:d).ToArray() };
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
