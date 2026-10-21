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
        var store=new AnnotationPersistence(FilePath);var item=Item() with
        {
            FontSize=32, TextAlign=AnnotationTextAlign.Center,
            Emphasis=AnnotationTextEmphasis.BoldItalic, CardFill=AnnotationCardFill.None
        };
        store.Document.Put(item);Assert.True(store.Save());
        store.Document.Put(item with { Text="Edited" });Assert.True(store.Save());
        var saved=Assert.Single(new AnnotationPersistence(FilePath).Document.Items);
        Assert.Equal("Edited",saved.Text);Assert.Equal(32,saved.FontSize);
        Assert.Equal(AnnotationTextAlign.Center,saved.TextAlign);
        Assert.Equal(AnnotationTextEmphasis.BoldItalic,saved.Emphasis);
        Assert.Equal(AnnotationCardFill.None,saved.CardFill);
        Assert.Equal(item,Assert.Single(new AnnotationPersistence(FilePath+".bak").Document.Items));
    }
    [Fact]
    public void LegacyFilesWithoutTextSizeLoadAtTheDefaultSize()
    {
        Directory.CreateDirectory(_folder);
        var id=Guid.NewGuid();var desktop=Guid.NewGuid();
        File.WriteAllText(FilePath,
            $"{{\"Version\":1,\"Items\":[{{\"Id\":\"{id}\",\"DesktopId\":\"{desktop}\",\"Kind\":3," +
            "\"A\":{\"X\":0,\"Y\":0},\"B\":{\"X\":200,\"Y\":100},\"Color\":\"#FBBF24\",\"Stroke\":3,\"Text\":\"Legacy\"}]}");
        var item=Assert.Single(new AnnotationPersistence(FilePath).Document.Items);
        Assert.Equal(AnnotationDocument.DefaultFontSize,item.FontSize);
        Assert.Equal(AnnotationTextAlign.Left,item.TextAlign);
        Assert.Equal(AnnotationTextEmphasis.Normal,item.Emphasis);
        Assert.Equal(AnnotationCardFill.Solid,item.CardFill);
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
    [InlineData("{\"Version\":1,\"Items\":null}")]
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
