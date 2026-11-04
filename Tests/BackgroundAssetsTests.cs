using CanvasDesktop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CanvasDesktop.Tests;

public sealed class BackgroundAssetsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "canvas-background-assets-" + Guid.NewGuid().ToString("N"));
    private BackgroundAssets Assets => new(Path.Combine(_folder, "managed"));
    public void Dispose() { if (Directory.Exists(_folder)) Directory.Delete(_folder, true); }

    private string Image(string name = "source.png", int width = 80, int height = 40)
    {
        Directory.CreateDirectory(_folder);
        string path = Path.Combine(_folder, name);
        WorkspaceViewTests.OnSta(() =>
        {
            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null,
                new byte[width * height * 4], width * 4);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(path); encoder.Save(output);
        });
        return path;
    }

    [Fact]
    public void ImportedImageSurvivesTheOriginalBeingMovedAndRepeatedImportsShareBytes()
    {
        string original = Image();
        var first = Assets.Import(original);
        var second = Assets.Import(original);
        Assert.Equal(first.Id, second.Id);
        Assert.False(first.External);
        Assert.Single(Directory.GetFiles(Path.Combine(_folder, "managed")));
        File.Move(original, original + ".moved");
        Assert.Equal(first.Path, Assets.Resolve(first.Id));
        Assert.True(File.Exists(first.Path));
    }

    [Fact]
    public void ExternalImportsLeaveTheSourceInPlaceAndDoNotCreateManagedCopies()
    {
        string original = Image();
        var asset = Assets.Import(original, external: true);
        Assert.True(asset.External);
        Assert.Equal(original, Assets.Resolve(asset.Id));
        Assert.False(Directory.Exists(Path.Combine(_folder, "managed")));
    }

    [Fact]
    public void ReimportingAnOriginalRepairsADamagedManagedCopy()
    {
        string original = Image(); var first = Assets.Import(original);
        File.WriteAllText(first.Path, "damaged");
        var repaired = Assets.Import(original);
        Assert.Equal(first.Id, repaired.Id);
        Assert.Equal(File.ReadAllBytes(original), File.ReadAllBytes(repaired.Path));
    }

    [Theory]
    [InlineData("../outside.png")]
    [InlineData("builtin:../study")]
    [InlineData("builtin:unknown")]
    [InlineData("external:relative.png")]
    [InlineData("ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff/../x.png")]
    public void InvalidReferencesCannotEscapeTheAssetDirectory(string reference)
        => Assert.Null(Assets.Resolve(reference));

    [Theory]
    [InlineData("study")]
    [InlineData("bright")]
    [InlineData("nature")]
    [InlineData("abstract")]
    public void BuiltInsHaveStablePathsSeparateFromUserImports(string name)
        => Assert.Equal(Path.Combine(AppContext.BaseDirectory, "Assets", "Backgrounds", name + ".png"), Assets.Resolve("builtin:" + name));

    [Fact]
    public void UnsupportedOrOversizedFilesDoNotCreateManagedCopies()
    {
        Directory.CreateDirectory(_folder);
        string unsupported = Path.Combine(_folder, "image.svg"); File.WriteAllText(unsupported, "<svg/>");
        Assert.Throws<InvalidDataException>(() => Assets.Import(unsupported));
        string oversized = Path.Combine(_folder, "huge.png");
        using (var output = File.Create(oversized)) output.SetLength(BackgroundAssets.MaximumFileBytes + 1);
        Assert.Throws<InvalidDataException>(() => Assets.Import(oversized));
        Assert.False(Directory.Exists(Path.Combine(_folder, "managed")));
    }

    [Fact]
    public void CacheReturnsFrozenImagesAndDoesNotReadDiskOnRepeatedLookups()
    {
        var asset = Assets.Import(Image());
        using var cache = new BackgroundImageCache(Assets, new SynchronizationContext());
        using var ready = new ManualResetEventSlim(); cache.Changed += () => ready.Set();
        Assert.Null(cache.TryGet(asset.Id, false)); Assert.True(ready.Wait(TimeSpan.FromSeconds(15)));
        Assert.True(cache.GetError(asset.Id) == null, cache.GetError(asset.Id));
        var image = Assert.IsAssignableFrom<BitmapSource>(cache.TryGet(asset.Id, false));
        Assert.True(image.IsFrozen); Assert.Equal(80, image.PixelWidth); Assert.Equal(40, image.PixelHeight);
        Assert.Equal(PixelFormats.Pbgra32, image.Format);
        File.Delete(asset.Path);
        Assert.Same(image, cache.TryGet(asset.Id, false));
        Assert.Null(cache.GetError(asset.Id));
    }

    [Fact]
    public void LowQualityDecodePreservesAspectRatioAndCapsTheLongEdge()
    {
        var asset = Assets.Import(Image(width: 2048, height: 512));
        using var cache = new BackgroundImageCache(Assets, new SynchronizationContext());
        using var ready = new ManualResetEventSlim(); cache.Changed += () => ready.Set();
        Assert.Null(cache.TryGet(asset.Id, true)); Assert.True(ready.Wait(TimeSpan.FromSeconds(15)));
        Assert.True(cache.GetError(asset.Id) == null, cache.GetError(asset.Id));
        var image = Assert.IsAssignableFrom<BitmapSource>(cache.TryGet(asset.Id, true));
        Assert.Equal(1024, image.PixelWidth); Assert.Equal(256, image.PixelHeight);
        Assert.Equal((2048, 512), cache.GetDimensions(asset.Id));
    }

    [Fact]
    public void MissingLinksStayFailedUntilExplicitlyInvalidatedAndThenRecover()
    {
        string source = Image();
        var asset = Assets.Import(source, external: true);
        File.Move(source, source + ".saved");
        using var cache = new BackgroundImageCache(Assets, new SynchronizationContext());
        using var ready = new ManualResetEventSlim(); cache.Changed += () => ready.Set();
        Assert.Null(cache.TryGet(asset.Id, false)); Assert.True(ready.Wait(TimeSpan.FromSeconds(15)));
        Assert.NotNull(cache.GetError(asset.Id));
        File.Move(source + ".saved", source);
        Assert.Null(cache.TryGet(asset.Id, false)); // Failed loads are not retried on every frame.
        cache.Invalidate(asset.Id); ready.Reset();
        Assert.Null(cache.TryGet(asset.Id, false)); Assert.True(ready.Wait(TimeSpan.FromSeconds(15)));
        Assert.NotNull(cache.TryGet(asset.Id, false)); Assert.Null(cache.GetError(asset.Id));
    }

    [Fact]
    public void ABitmapLargerThanTheCacheBudgetIsNotRetained()
    {
        var asset = Assets.Import(Image());
        using var cache = new BackgroundImageCache(Assets, new SynchronizationContext(), maximumBytes: 100);
        using var ready = new ManualResetEventSlim(); cache.Changed += () => ready.Set();
        Assert.Null(cache.TryGet(asset.Id, false)); Assert.True(ready.Wait(TimeSpan.FromSeconds(15)));
        Assert.Null(cache.TryGet(asset.Id, false)); Assert.Contains("budget", cache.GetError(asset.Id));
    }

    [Fact]
    public void MultipleVisibleImagesAboveTheBudgetSettleInsteadOfReloadingForever()
    {
        var assets=new[]{Assets.Import(Image("one.png",80,40),true),Assets.Import(Image("two.png",80,40),true),Assets.Import(Image("three.png",80,40),true)};
        using var cache=new BackgroundImageCache(Assets,new SynchronizationContext(),maximumBytes:80*40*4*2);
        int changes=0;cache.Changed+=()=>Interlocked.Increment(ref changes);
        var until=DateTime.UtcNow.AddSeconds(3);
        while(DateTime.UtcNow<until)
        { foreach(var asset in assets)cache.TryGet(asset.Id,false);Thread.Sleep(20); }
        int settled=Volatile.Read(ref changes);
        Assert.InRange(settled,3,9);
        for(int i=0;i<20;i++)foreach(var asset in assets)cache.TryGet(asset.Id,false);
        Thread.Sleep(150);Assert.Equal(settled,Volatile.Read(ref changes));
        Assert.All(assets, asset => Assert.NotNull(cache.TryGet(asset.Id, false)));
    }

    [Fact]
    public void CorruptImagesBecomeRecoverableFailuresInsteadOfStayingQueued()
    {
        string path=Image();var asset=Assets.Import(path,true);File.WriteAllText(path,"broken");
        using var cache=new BackgroundImageCache(Assets,new SynchronizationContext());
        using var ready=new ManualResetEventSlim();cache.Changed+=()=>ready.Set();
        cache.TryGet(asset.Id,false);Assert.True(ready.Wait(TimeSpan.FromSeconds(15)));
        Assert.NotNull(cache.GetError(asset.Id));
    }

    [Fact]
    public void DisposedCachesReleaseImagesAndDoNotQueueNewLoads()
    {
        var cache = new BackgroundImageCache(Assets, new SynchronizationContext()); cache.Dispose(); cache.Dispose();
        Assert.Null(cache.TryGet("builtin:study", false));
    }
}
