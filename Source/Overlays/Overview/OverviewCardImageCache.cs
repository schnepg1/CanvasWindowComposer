using System;
using System.Drawing;
using System.Windows.Media.Imaging;

namespace CanvasDesktop;

/// <summary>
/// Cached overview card snapshot bitmap with resolution and layout metadata.
/// </summary>
internal sealed record CachedCardImage(
    BitmapSource Image,
    int Dimension,
    int Dpi,
    Size Size,
    long Bytes)
{
    public static CachedCardImage Create(BitmapSource image, int dimension, int dpi, Size size)
    {
        long bytes = (long)image.PixelWidth * image.PixelHeight * 4;
        return new CachedCardImage(image, dimension, dpi, size, bytes);
    }
}

/// <summary>
/// Bounded LRU cache for overview card snapshot bitmaps, decoupling snapshot lifetime
/// from individual placeholder form controls and strictly limiting total memory consumption.
/// </summary>
internal sealed class OverviewCardImageCache : IDisposable
{
    private static readonly Lazy<OverviewCardImageCache> DefaultInstance =
        new(() => new OverviewCardImageCache(
            OverviewZoomDefaults.DefaultCardImageMaxCount,
            OverviewZoomDefaults.DefaultCardImageBudgetBytes));

    public static OverviewCardImageCache Default => DefaultInstance.Value;

    private readonly LruMemoryCache<Guid, CachedCardImage> _cache;

    public OverviewCardImageCache(
        int maxCapacity = OverviewZoomDefaults.DefaultCardImageMaxCount,
        long maxMemoryBytes = OverviewZoomDefaults.DefaultCardImageBudgetBytes)
    {
        _cache = new LruMemoryCache<Guid, CachedCardImage>(
            maxCapacity,
            maxMemoryBytes,
            entry => entry.Bytes);
    }

    public int Count => _cache.Count;
    public long TotalBytes => _cache.TotalBytes;
    public int MaxCapacity { get => _cache.MaxCapacity; set => _cache.MaxCapacity = value; }
    public long MaxMemoryBytes { get => _cache.MaxMemoryBytes; set => _cache.MaxMemoryBytes = value; }

    public bool TryGet(Guid cardId, out CachedCardImage? cached)
    {
        return _cache.TryGet(cardId, out cached);
    }

    public void Set(Guid cardId, CachedCardImage image)
    {
        _cache.Set(cardId, image);
    }

    /// <summary>
    /// Demotes a card snapshot to the eviction head (LRU tail), ensuring that cards
    /// occluded by live DWM thumbnails are evicted first under memory pressure.
    /// </summary>
    public bool Demote(Guid cardId)
    {
        return _cache.Demote(cardId);
    }

    public bool Remove(Guid cardId)
    {
        return _cache.Remove(cardId);
    }

    public void Clear()
    {
        _cache.Clear();
    }

    public void Dispose()
    {
        _cache.Dispose();
    }
}
