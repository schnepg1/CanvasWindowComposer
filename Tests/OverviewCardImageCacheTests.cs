using System;
using System.Drawing;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CanvasDesktop;
using Xunit;

namespace CanvasDesktop.Tests;

public class OverviewCardImageCacheTests
{
    private static BitmapSource CreateFrozenBitmap(int width, int height)
    {
        int stride = width * 4;
        var pixels = new byte[stride * height];
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
        bitmap.Freeze();
        return bitmap;
    }

    [Fact]
    public void CachesAndRetrievesCardBitmaps()
    {
        using var cache = new OverviewCardImageCache(maxCapacity: 10, maxMemoryBytes: 10 * 1024 * 1024);
        var cardId = Guid.NewGuid();
        var image = CreateFrozenBitmap(100, 100);
        var entry = CachedCardImage.Create(image, 100, 96, new Size(200, 200));

        cache.Set(cardId, entry);

        Assert.True(cache.TryGet(cardId, out var cached));
        Assert.NotNull(cached);
        Assert.Same(image, cached.Image);
        Assert.Equal(100, cached.Dimension);
        Assert.Equal(96, cached.Dpi);
        Assert.Equal(new Size(200, 200), cached.Size);
        Assert.Equal(100 * 100 * 4, cached.Bytes);
    }

    [Fact]
    public void EnforcesByteBudgetDuringMultipleSimulatedZoomGenerations()
    {
        // 1 MB budget = 1,048,576 bytes.
        // A 512x512 image is exactly 512 * 512 * 4 = 1,048,576 bytes.
        using var cache = new OverviewCardImageCache(maxCapacity: 10, maxMemoryBytes: 1024 * 1024);

        var card1 = Guid.NewGuid();
        var card2 = Guid.NewGuid();

        var bmp1 = CreateFrozenBitmap(512, 512);
        var bmp2 = CreateFrozenBitmap(512, 512);

        cache.Set(card1, CachedCardImage.Create(bmp1, 512, 96, new Size(512, 512)));
        Assert.Equal(1, cache.Count);
        Assert.Equal(1024 * 1024, cache.TotalBytes);

        // Adding card2 at 512x512 should evict card1 to stay within 1 MB budget
        cache.Set(card2, CachedCardImage.Create(bmp2, 512, 96, new Size(512, 512)));

        Assert.Equal(1, cache.Count);
        Assert.Equal(1024 * 1024, cache.TotalBytes);
        Assert.False(cache.TryGet(card1, out _));
        Assert.True(cache.TryGet(card2, out _));
    }

    [Fact]
    public void DemotedLiveCardsAreEvictedBeforeActiveDetailedCards()
    {
        using var cache = new OverviewCardImageCache(maxCapacity: 3, maxMemoryBytes: 100 * 1024 * 1024);

        var liveCard = Guid.NewGuid();
        var detailedCard1 = Guid.NewGuid();
        var detailedCard2 = Guid.NewGuid();
        var newCard = Guid.NewGuid();

        cache.Set(liveCard, CachedCardImage.Create(CreateFrozenBitmap(64, 64), 64, 96, new Size(64, 64)));
        cache.Set(detailedCard1, CachedCardImage.Create(CreateFrozenBitmap(64, 64), 64, 96, new Size(64, 64)));
        cache.Set(detailedCard2, CachedCardImage.Create(CreateFrozenBitmap(64, 64), 64, 96, new Size(64, 64)));

        // Both detailed cards and the live card are in cache.
        // Demote the live card so it becomes the primary eviction candidate.
        Assert.True(cache.Demote(liveCard));

        // Add 4th card, triggering eviction of 1 entry
        cache.Set(newCard, CachedCardImage.Create(CreateFrozenBitmap(64, 64), 64, 96, new Size(64, 64)));

        Assert.Equal(3, cache.Count);
        Assert.False(cache.TryGet(liveCard, out _)); // Demoted card evicted!
        Assert.True(cache.TryGet(detailedCard1, out _));
        Assert.True(cache.TryGet(detailedCard2, out _));
        Assert.True(cache.TryGet(newCard, out _));
    }
}
