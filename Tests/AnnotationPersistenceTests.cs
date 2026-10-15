using CanvasDesktop;
using CanvasDesktop.CanvasView;

namespace CanvasDesktop.Tests;

public class AnnotationPersistenceTests : IDisposable
{
    private readonly string _folder=Path.Combine(Path.GetTempPath(),"canvas-annotations-"+Guid.NewGuid().ToString("N"));
    private string FilePath=>Path.Combine(_folder,"annotations.json");
    private static CanvasAnnotation Item()=>new(Guid.NewGuid(),Guid.NewGuid(),AnnotationKind.Text,new(-20,30),new(220,150),Text:"سلام 🌸\nSecond line");
    public void Dispose() { if(Directory.Exists(_folder))Directory.Delete(_folder,true); }
    [Fact]
    public void UnicodeNotesRoundTripAndSuccessfulUpdatesRotateBackup()
    {
        var store=new AnnotationPersistence(FilePath);var item=Item();store.Document.Put(item);Assert.True(store.Save());
        store.Document.Put(item with { Text="Edited" });Assert.True(store.Save());
        Assert.Equal("Edited",Assert.Single(new AnnotationPersistence(FilePath).Document.Items).Text);
        Assert.Equal(item,Assert.Single(new AnnotationPersistence(FilePath+".bak").Document.Items));
    }
    [Fact]
    public void CorruptPrimaryRecoversBackupAndPreservesDamagedFile()
    {
        var store=new AnnotationPersistence(FilePath);store.Document.Put(Item());Assert.True(store.Save());Assert.True(store.Save());
        File.WriteAllText(FilePath,"{broken");var recovered=new AnnotationPersistence(FilePath);
        Assert.Single(recovered.Document.Items);Assert.Contains("Recovered",recovered.Status);
        Assert.Single(Directory.GetFiles(_folder,"*.corrupt-*"));Assert.True(recovered.Save());
    }
    [Theory]
    [InlineData("{broken")]
    [InlineData("{\"Version\":99,\"Items\":[]}")]
    [InlineData("{\"Version\":1,\"Items\":[null]}")]
    public void FailedLoadNeverOverwritesExistingData(string original)
    {
        Directory.CreateDirectory(_folder);File.WriteAllText(FilePath,original);var store=new AnnotationPersistence(FilePath);
        store.Document.Put(Item());Assert.False(store.Save());Assert.Equal(original,File.ReadAllText(FilePath));Assert.NotNull(store.Status);
    }
    [Fact]
    public void FailedSaveLeavesDocumentEditableAndRetryCanSucceed()
    {
        Directory.CreateDirectory(_folder);File.WriteAllText(FilePath,"{\"Version\":1,\"Items\":[]}");
        var store=new AnnotationPersistence(FilePath);Directory.CreateDirectory(FilePath+".tmp");store.Document.Put(Item());
        Assert.False(store.Save());Assert.NotNull(store.Status);Directory.Delete(FilePath+".tmp");Assert.True(store.Save());Assert.Null(store.Status);
    }
}
