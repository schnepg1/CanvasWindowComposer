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
    public void FuturePrimaryNeverFallsBackToAnOlderBackup()
    {
        string folder=Path.Combine(Path.GetTempPath(),"canvas-tutorial-future-"+Guid.NewGuid().ToString("N"));
        try
        {
            string path=Path.Combine(folder,"tutorial.json");var desktop=Guid.NewGuid();
            var store=new TutorialPersistence(path);
            store.SetLayout(desktop,TutorialLayout.Create(desktop,new(0,0,1920,1080),[],[]));store.MarkPresented();
            string backup=File.ReadAllText(path+".bak");
            const string future="{\"Version\":3,\"NewerSchema\":{\"keep\":true}}";
            File.WriteAllText(path,future);
            var loaded=new TutorialPersistence(path);Assert.False(loaded.CanPresent);Assert.NotNull(loaded.Status);
            loaded.MarkPresented();loaded.SetVisible(desktop,true);
            Assert.Equal(future,File.ReadAllText(path));Assert.Equal(backup,File.ReadAllText(path+".bak"));
            Assert.Empty(Directory.GetFiles(folder,"*.corrupt-*"));
        }
        finally { if(Directory.Exists(folder))Directory.Delete(folder,true); }
    }

    [Fact]
    public void DpiMigrationPreservesVisibilityIdsAndNeighbourhoodOriginAcrossRestarts()
    {
        string folder=Path.Combine(Path.GetTempPath(),"canvas-tutorial-dpi-"+Guid.NewGuid().ToString("N"));
        try
        {
            string path=Path.Combine(folder,"tutorial.json");var desktop=Guid.NewGuid();
            var store=new TutorialPersistence(path);
            var layout=TutorialLayout.Create(desktop,new(0,0,1920,1080),[],[]);
            store.SetLayout(desktop,layout);store.SetVisible(desktop,false);store.MarkPresented();
            store.SetLayout(desktop,layout.WithScale(2));
            var loaded=new TutorialPersistence(path);var notes=loaded.ForDesktop(desktop)!;
            Assert.False(notes.Visible);Assert.False(loaded.CanPresent);
            Assert.Equal(layout.Items.Select(a=>a.Id),notes.Layout.Items.Select(a=>a.Id));
            Assert.Equal(layout.Welcome.X,notes.Layout.Welcome.X);Assert.Equal(layout.Welcome.Y,notes.Layout.Welcome.Y);
            Assert.Equal(2,notes.Layout.Scale);Assert.Equal(layout.Welcome.Width*2,notes.Layout.Welcome.Width);
        }
        finally { if(Directory.Exists(folder))Directory.Delete(folder,true); }
    }

    [Fact]
    public void StageOneFilesUpgradeWithoutLosingArtworkAndTourProgressSurvivesRestart()
    {
        string folder=Path.Combine(Path.GetTempPath(),"canvas-tour-upgrade-"+Guid.NewGuid().ToString("N"));
        try
        {
            string path=Path.Combine(folder,"tutorial.json");var desktop=Guid.NewGuid();
            var store=new TutorialPersistence(path);var layout=TutorialLayout.Create(desktop,new(0,0,1920,1080),[],[]);
            store.SetLayout(desktop,layout);store.SetVisible(desktop,false);store.MarkPresented();
            var json=System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;json["Version"]=1;json.AsObject().Remove("Tours");File.WriteAllText(path,json.ToJsonString());
            var migrated=new TutorialPersistence(path);Assert.False(migrated.CanPresent);Assert.Equal(2,migrated.Data.Version);
            Assert.False(migrated.ForDesktop(desktop)!.Visible);Assert.Equal(layout.Items,migrated.ForDesktop(desktop)!.Layout.Items);
            migrated.SetTour(new(desktop,2,3,TutorialTourStatus.Paused));
            var reloaded=new TutorialPersistence(path);Assert.Equal(2,reloaded.TourForDesktop(desktop)!.Step);Assert.Equal(3,reloaded.TourForDesktop(desktop)!.Practised);
            Assert.False(reloaded.ForDesktop(desktop)!.Visible);Assert.Equal(layout.Items,reloaded.ForDesktop(desktop)!.Layout.Items);
        }
        finally { if(Directory.Exists(folder))Directory.Delete(folder,true); }
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
            string repaired=File.ReadAllText(path),backup=File.ReadAllText(path+".bak");
            Assert.Equal(backup,repaired);
            var again=new TutorialPersistence(path);Assert.False(again.CanPresent);
            Assert.Single(Directory.GetFiles(folder,"*.corrupt-*"));
            Assert.Equal(repaired,File.ReadAllText(path));
        }
        finally { if(Directory.Exists(folder))Directory.Delete(folder,true); }
    }
}
