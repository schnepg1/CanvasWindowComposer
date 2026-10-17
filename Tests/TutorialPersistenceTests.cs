using CanvasDesktop;
using CanvasDesktop.CanvasView;

namespace CanvasDesktop.Tests;

public class TutorialPersistenceTests
{
    [Fact]
    public void ArtworkVisibilityAndFirstPresentationSurviveRestartWithoutDuplicates()
    {
        string folder=Path.Combine(Path.GetTempPath(),"canvas-tutorial-"+Guid.NewGuid().ToString("N"));
        try
        {
            string path=Path.Combine(folder,"tutorial.json");var desktop=Guid.NewGuid();
            var store=new TutorialPersistence(path);
            Assert.True(store.CanPresent);
            var layout=TutorialLayout.Create(desktop,new(0,0,1920,1080),[],[]);
            store.SetLayout(desktop,layout);store.MarkPresented();store.SetVisible(desktop,false);
            var loaded=new TutorialPersistence(path);
            Assert.False(loaded.CanPresent);
            Assert.False(loaded.ForDesktop(desktop)!.Visible);
            Assert.Equal(layout.Items,loaded.ForDesktop(desktop)!.Layout.Items);
            loaded.SetVisible(desktop,true);
            Assert.Single(new TutorialPersistence(path).Data.Desktops!);
            Assert.Null(loaded.ForDesktop(Guid.NewGuid()));
        }
        finally { if(Directory.Exists(folder))Directory.Delete(folder,true); }
    }

    [Theory]
    [InlineData("{\"Version\":999,\"Desktops\":[]}")]
    [InlineData("{\"Version\":1,\"Desktops\":[null]}")]
    [InlineData("broken JSON")]
    public void DamagedOrFutureStateDoesNotAutoPresentOrOverwriteData(string contents)
    {
        string folder=Path.Combine(Path.GetTempPath(),"canvas-tutorial-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string path=Path.Combine(folder,"tutorial.json");File.WriteAllText(path,contents);
            var store=new TutorialPersistence(path);
            Assert.False(store.CanPresent);Assert.NotNull(store.Status);
            store.MarkPresented();
            Assert.Equal(contents,File.ReadAllText(path));
        }
        finally { Directory.Delete(folder,true); }
    }

    [Fact]
    public void BackupRecoveryPreservesTheDamagedPrimary()
    {
        string folder=Path.Combine(Path.GetTempPath(),"canvas-tutorial-"+Guid.NewGuid().ToString("N"));
        try
        {
            string path=Path.Combine(folder,"tutorial.json");
            var store=new TutorialPersistence(path);var desktop=Guid.NewGuid();
            store.SetLayout(desktop,TutorialLayout.Create(desktop,new(0,0,1920,1080),[],[]));store.MarkPresented();store.SetVisible(desktop,false);
            File.WriteAllText(path,"broken");
            var loaded=new TutorialPersistence(path);
            Assert.False(loaded.CanPresent);Assert.NotNull(loaded.ForDesktop(desktop));
            Assert.Single(Directory.GetFiles(folder,"*.corrupt-*"));
        }
        finally { if(Directory.Exists(folder))Directory.Delete(folder,true); }
    }
}
