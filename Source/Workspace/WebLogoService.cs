using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;

namespace CanvasDesktop;

/// <summary>
/// Asynchronously coordinates multi-tiered web application logo resolution and badging
/// entirely off the UI thread without impacting canvas rendering or zoom/pan physics.
/// </summary>
internal sealed class WebLogoService
{
    private static readonly Lazy<WebLogoService> DefaultInstance = new(() => new WebLogoService());
    public static WebLogoService Default => DefaultInstance.Value;

    private readonly ConcurrentDictionary<string, Task> _inFlight = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Raised when a web application logo composite is ready in the cache.
    /// Passes the cacheKey for matching cards to invalidate.
    /// </summary>
    public event Action<string>? ArtworkResolved;

    /// <summary>
    /// Queues asynchronous resolution for a browser workspace item if not already cached or in-flight.
    /// </summary>
    public void QueueResolution(WorkspaceItemView item)
    {
        if (!BrowserSupport.Supports(item.ExecutablePath))
            return;

        if (!WebDestinationParser.TryGetDestination(item, out var domain, out var brand))
            return;

        string cacheKey = WebDestinationParser.GetCacheKey(item);

        if (_inFlight.ContainsKey(cacheKey))
            return;

        var task = Task.Run(async () =>
        {
            try
            {
                await ResolveAndCacheAsync(item, cacheKey, domain, brand).ConfigureAwait(false);
            }
            finally
            {
                _inFlight.TryRemove(cacheKey, out _);
            }
        });

        _inFlight.TryAdd(cacheKey, task);
    }

    private async Task ResolveAndCacheAsync(WorkspaceItemView item, string cacheKey, string domain, string brand)
    {
        Bitmap? webLogo = null;

        // Tier 1: Local Canvas disk cache
        webLogo = WebLogoStore.LoadFromDiskCache(domain);

        // Tier 2: Browser profile Favicons database
        if (webLogo == null)
        {
            webLogo = WebLogoStore.ReadFromBrowserProfile(item.ExecutablePath, item.ChromeProfilePath, domain);
            if (webLogo != null)
            {
                // Save to local disk cache for fast future retrieval
                using var ms = new MemoryStream();
                webLogo.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                WebLogoStore.SaveToDiskCache(domain, ms.ToArray());
            }
        }

        // Tier 3: Remote favicon service
        if (webLogo == null)
        {
            webLogo = await WebLogoStore.FetchRemoteAsync(domain).ConfigureAwait(false);
        }

        // Tier 4: Stylized brand/initial vector fallback (offline resilience)
        if (webLogo == null)
        {
            webLogo = WebLogoStore.CreateBrandFallback(brand, domain);
        }

        if (webLogo == null) return;

        Bitmap? browserIcon = null;
        try
        {
            if (File.Exists(item.ExecutablePath))
            {
                using var icon = Icon.ExtractIcon(item.ExecutablePath, 0, 256);
                browserIcon = icon?.ToBitmap();
            }
        }
        catch
        {
            // Browser icon extraction failure falls back to unbadged web logo
        }

        SharedArtwork artwork;
        try
        {
            artwork = WebLogoBadgeCompositor.CreateBadgedArtwork(webLogo, browserIcon);
        }
        finally
        {
            webLogo.Dispose();
            browserIcon?.Dispose();
        }

        PlaceholderArtworkCache.Default.Set(cacheKey, artwork);
        ArtworkResolved?.Invoke(cacheKey);
    }
}
