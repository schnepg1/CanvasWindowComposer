using CanvasDesktop;

namespace CanvasDesktop.Tests;

public sealed class BackgroundPersistenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "canvas-background-store-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_directory, "backgrounds.json");
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    private static BackgroundSurface Item() => new(Guid.NewGuid(), Guid.NewGuid(), "مطالعه Study", "builtin:study",
        new WorldRect { X = -1234, Y = 500, W = 10000, H = 6000 }, CropX: .2, CropY: .8, Dim: .4, Opacity: .75);

    [Fact]
    public void MissingDocumentLoadsEmptyAndSavingPreservesGeometryAndSettings()
    {
        var store = new BackgroundPersistence(FilePath); Assert.Empty(store.Items); Assert.True(store.CanSave);
        var original = Item(); store.Items.Add(original); Assert.True(store.Save());
        Assert.Equal(original, Assert.Single(new BackgroundPersistence(FilePath).Items));
        store.Items[0] = original with { Fit = BackgroundFit.Tile, Name = "Edited" }; Assert.True(store.Save());
        Assert.Equal(original, Assert.Single(new BackgroundPersistence(FilePath + ".bak").Items));
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("{\"Version\":2,\"Items\":[]}")]
    [InlineData("{\"Version\":1,\"Items\":null}")]
    [InlineData("{\"Version\":1,\"Items\":[null]}")]
    public void InvalidPrimaryAndBackupStayProtectedAgainstOverwrite(string content)
    {
        Directory.CreateDirectory(_directory); File.WriteAllText(FilePath, content); File.WriteAllText(FilePath + ".bak", content);
        var store = new BackgroundPersistence(FilePath); Assert.False(store.CanSave); Assert.NotNull(store.Status);
        store.Items.Add(Item()); Assert.False(store.Save());
        Assert.Equal(content, File.ReadAllText(FilePath)); Assert.Equal(content, File.ReadAllText(FilePath + ".bak"));
    }

    [Fact]
    public void CorruptPrimaryRecoversBackupAndPreservesOriginalBeforeSaving()
    {
        var store = new BackgroundPersistence(FilePath); var item = Item(); store.Items.Add(item);
        Assert.True(store.Save()); Assert.True(store.Save());
        File.WriteAllText(FilePath, "{broken"); var recovered = new BackgroundPersistence(FilePath);
        Assert.Equal(item, Assert.Single(recovered.Items)); Assert.Contains("Recovered", recovered.Status);
        Assert.Equal("{broken", File.ReadAllText(Assert.Single(Directory.GetFiles(_directory, "*.corrupt-*"))));
        Assert.True(recovered.Save()); Assert.Equal(item, Assert.Single(new BackgroundPersistence(FilePath).Items));
    }

    [Fact]
    public void InvalidOrDuplicateItemsCannotBeSaved()
    {
        var store = new BackgroundPersistence(FilePath); var item = Item(); store.Items.Add(item); store.Items.Add(item);
        Assert.False(store.Save()); Assert.False(File.Exists(FilePath));
        store.Items.RemoveAt(1); store.Items[0] = item with { AssetId = "builtin:unknown" };
        Assert.False(store.Save()); Assert.False(File.Exists(FilePath));
        store.Items[0] = item; Assert.True(store.Save());
    }

    [Fact]
    public void MaximumCountIsEnforcedWhenLoadingAndSaving()
    {
        var store = new BackgroundPersistence(FilePath);
        for (int i = 0; i < BackgroundRules.MaximumSurfaces; i++) store.Items.Add(Item());
        Assert.True(store.Save()); store.Items.Add(Item()); Assert.False(store.Save());
        Assert.Equal(BackgroundRules.MaximumSurfaces, new BackgroundPersistence(FilePath).Items.Count);
    }

    [Theory]
    [InlineData("builtin:study", true)]
    [InlineData("builtin:unknown", false)]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.png", true)]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA.png", false)]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.exe", false)]
    [InlineData("../study.png", false)]
    [InlineData("external:relative.png", false)]
    public void AssetReferencesCannotEscapeManagedStorage(string id, bool valid)
        => Assert.Equal(valid, BackgroundRules.ValidAssetId(id));
}
