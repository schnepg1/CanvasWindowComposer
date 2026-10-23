using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace CanvasDesktop;

internal sealed class AnnotationPersistence
{
    internal sealed class FileDocument
    {
        public int Version { get; set; } = 1;
        public CanvasAnnotation[] Items { get; set; } = [];
    }
    private const int MaximumBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, IgnoreReadOnlyProperties = true };
    private readonly string _path;
    private bool _canSave = true;
    public AnnotationDocument Document { get; }
    public string? Status { get; private set; }
    public AnnotationPersistence(string path)
    {
        _path=path;
        try { Document=Load(); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            Document=new(); _canSave=false; Status="Annotations could not be loaded. Existing files are preserved: "+ex.Message;
        }
    }
    private AnnotationDocument Load()
    {
        Exception? failure=null;
        foreach(var path in new[]{_path,_path+".bak"})
        {
            if(!File.Exists(path)) continue;
            try
            {
                if(new FileInfo(path).Length>MaximumBytes) throw new InvalidDataException("Annotation file is too large.");
                var data=JsonSerializer.Deserialize<FileDocument>(File.ReadAllText(path),Options);
                if(data is null || data.Version!=1 || data.Items is null) throw new InvalidDataException("Unsupported annotation format.");
                // Version-1 files predate per-note text sizes; missing values deserialize as zero.
                var items=data.Items.Select(item => item is { FontSize: 0 } ? item with { FontSize=AnnotationDocument.DefaultFontSize } : item).ToArray();
                var document=new AnnotationDocument(items);
                if(path!=_path)
                {
                    if(File.Exists(_path)) File.Copy(_path,_path+".corrupt-"+Guid.NewGuid().ToString("N"));
                    Status="Recovered canvas annotations from backup.";
                }
                return document;
            }
            catch(Exception ex) when(ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException) { failure=ex; }
        }
        if(failure!=null) throw new IOException("Could not read annotations or backup.",failure);
        return new();
    }
    public bool Save()
    {
        if(!_canSave) return false;
        var temp=_path+".tmp";
        try
        {
            var bytes=JsonSerializer.SerializeToUtf8Bytes(new FileDocument { Items=Document.Items.ToArray() },Options);
            if(bytes.Length>MaximumBytes) throw new InvalidDataException("Annotation file is too large.");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            using(var stream=new FileStream(temp,FileMode.Create,FileAccess.Write,FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
            if(File.Exists(_path)) File.Replace(temp,_path,_path+".bak"); else File.Move(temp,_path);
            Status=null; return true;
        }
        catch(Exception ex) when(ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        { Status="Annotations could not be saved: "+ex.Message; return false; }
        finally { try { if(File.Exists(temp)) File.Delete(temp); } catch(IOException) { } catch(UnauthorizedAccessException) { } }
    }
}
