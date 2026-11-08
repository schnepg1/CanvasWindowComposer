using System;
using System.Drawing;
using System.Windows.Media.Imaging;

namespace CanvasDesktop;

/// <summary>
/// Cached GDI symbol and frozen WPF overview logo for a distinct application or folder.
/// </summary>
internal sealed record SharedArtwork(
    Bitmap? Symbol,
    BitmapSource? OverviewLogo,
    long Bytes) : IDisposable
{
    public void Dispose()
    {
        try
        {
            Symbol?.Dispose();
        }
        catch
        {
            // Defensive disposal against native GDI handle failures
        }
    }
}

/// <summary>
/// Deduplicating LRU cache for application and folder artwork, eliminating redundant
/// icon extractions and GDI handle allocation across cards and drawer entries.
/// </summary>
internal sealed class PlaceholderArtworkCache : IDisposable
{
    public const int DefaultCapacity = 128;
    public const long DefaultMemoryBudgetBytes = 16L * 1024 * 1024; // 16 MB

    private static readonly Lazy<PlaceholderArtworkCache> DefaultInstance =
        new(() => new PlaceholderArtworkCache(DefaultCapacity, DefaultMemoryBudgetBytes));

    public static PlaceholderArtworkCache Default => DefaultInstance.Value;

    private readonly LruMemoryCache<string, SharedArtwork> _cache;

    public PlaceholderArtworkCache(int maxCapacity = DefaultCapacity, long maxMemoryBytes = DefaultMemoryBudgetBytes)
    {
        _cache = new LruMemoryCache<string, SharedArtwork>(
            maxCapacity,
            maxMemoryBytes,
            artwork => artwork.Bytes);
    }

    public int Count => _cache.Count;
    public long TotalBytes => _cache.TotalBytes;

    public static string GetCacheKey(WorkspaceItemView item)
    {
        if (item.IsFolder) return "folder";
        return string.IsNullOrEmpty(item.ExecutablePath)
            ? "empty"
            : item.ExecutablePath.Trim().ToLowerInvariant();
    }

    public SharedArtwork GetArtwork(WorkspaceItemView item)
    {
        string key = GetCacheKey(item);
        return _cache.GetOrCreate(key, _ => CreateArtwork(item));
    }

    public bool TryGet(string key, out SharedArtwork? artwork)
    {
        return _cache.TryGet(key, out artwork);
    }

    public void Set(string key, SharedArtwork artwork)
    {
        _cache.Set(key, artwork);
    }

    public void Clear()
    {
        _cache.Clear();
    }

    public void Dispose()
    {
        _cache.Dispose();
    }

    private static SharedArtwork CreateArtwork(WorkspaceItemView item)
    {
        Bitmap? symbol = PlaceholderArtwork.LoadSymbol(item);
        BitmapSource? overviewLogo = symbol != null ? PlaceholderArtwork.CreateOverviewLogo(symbol) : null;
        long bytes = (symbol == null ? 0 : (long)symbol.Width * symbol.Height * 4) +
                     (overviewLogo == null ? 0 : (long)overviewLogo.PixelWidth * overviewLogo.PixelHeight * 4);
        return new SharedArtwork(symbol, overviewLogo, bytes);
    }
}
