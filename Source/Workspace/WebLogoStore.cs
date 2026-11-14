using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace CanvasDesktop;

/// <summary>
/// Multi-tiered store for resolving and caching web destination icons.
/// Tier 1: Local Canvas disk cache (%LOCALAPPDATA%\CanvasWindowComposer\FaviconCache\)
/// Tier 2: Browser profile SQLite database (<Profile>\Favicons)
/// Tier 3: Remote high-resolution favicon service (Google S2 API) with offline brand fallback.
/// </summary>
internal static class WebLogoStore
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(3)
    };

    public static string CacheDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CanvasWindowComposer",
        "FaviconCache");

    /// <summary>
    /// Loads an icon from the local disk cache if available.
    /// </summary>
    public static Bitmap? LoadFromDiskCache(string domain)
    {
        string filePath = GetCacheFilePath(domain);
        if (!File.Exists(filePath)) return null;

        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length < 32 || stream.Length > 5 * 1024 * 1024) return null;
            return new Bitmap(stream);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Saves icon bytes to the local disk cache.
    /// </summary>
    public static void SaveToDiskCache(string domain, byte[] imageBytes)
    {
        try
        {
            Directory.CreateDirectory(CacheDirectory);
            string filePath = GetCacheFilePath(domain);
            File.WriteAllBytes(filePath, imageBytes);
        }
        catch
        {
            // Non-critical cache write failure
        }
    }

    /// <summary>
    /// Asynchronously fetches the favicon from the remote favicon service.
    /// </summary>
    public static async Task<Bitmap?> FetchRemoteAsync(string domain, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(domain)) return null;

        string url = $"https://www.google.com/s2/favicons?domain={Uri.EscapeDataString(domain)}&sz=128";
        try
        {
            using var response = await HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            byte[] bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            if (bytes.Length < 64) return null;

            SaveToDiskCache(domain, bytes);

            using var ms = new MemoryStream(bytes);
            return new Bitmap(ms);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Attempts to read the favicon for the domain from the local browser's Favicons SQLite database.
    /// </summary>
    public static Bitmap? ReadFromBrowserProfile(string executable, string profilePath, string domain)
    {
        if (!BrowserSupport.Supports(executable) || BrowserSupport.IsFirefox(executable))
            return null;

        string faviconsDbPath = GetFaviconsDbPath(executable, profilePath);
        if (string.IsNullOrEmpty(faviconsDbPath) || !File.Exists(faviconsDbPath))
            return null;

        return NativeSqliteFaviconReader.TryReadFavicon(faviconsDbPath, domain);
    }

    /// <summary>
    /// Generates a crisp brand fallback icon (e.g. YouTube red badge, GitHub dark badge)
    /// if offline and no cached icon is found.
    /// </summary>
    public static Bitmap CreateBrandFallback(string brand, string domain, int size = 128)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.HighQuality;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        string b = brand.ToLowerInvariant();
        if (b.Contains("youtube"))
        {
            // Red rounded rectangle with white play triangle
            float pad = size * 0.12f;
            var rect = new RectangleF(pad, pad * 1.5f, size - 2 * pad, size - 3 * pad);
            using var redBrush = new SolidBrush(Color.FromArgb(255, 230, 33, 23));
            using var path = CreateRoundedRect(rect, size * 0.18f);
            g.FillPath(redBrush, path);

            // Play triangle
            PointF[] triangle =
            [
                new(size * 0.42f, size * 0.38f),
                new(size * 0.64f, size * 0.50f),
                new(size * 0.42f, size * 0.62f)
            ];
            using var whiteBrush = new SolidBrush(Color.White);
            g.FillPolygon(whiteBrush, triangle);
            return bmp;
        }

        if (b.Contains("github"))
        {
            // Dark circle with GitHub mark
            float pad = size * 0.08f;
            var circle = new RectangleF(pad, pad, size - 2 * pad, size - 2 * pad);
            using var darkBrush = new SolidBrush(Color.FromArgb(255, 36, 41, 47));
            g.FillEllipse(darkBrush, circle);

            using var textBrush = new SolidBrush(Color.White);
            using var font = new Font("Segoe UI", size * 0.42f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("GH", font, textBrush, circle, format);
            return bmp;
        }

        // Generic branded initial badge
        Color baseColor = GetDistinctColorForDomain(domain);
        float circlePad = size * 0.08f;
        var genericCircle = new RectangleF(circlePad, circlePad, size - 2 * circlePad, size - 2 * circlePad);
        using var bgBrush = new SolidBrush(baseColor);
        g.FillEllipse(bgBrush, genericCircle);

        string initial = brand.Length > 0 ? brand[..1].ToUpperInvariant() : "W";
        using var fontBrush = new SolidBrush(Color.White);
        using var initialFont = new Font("Segoe UI", size * 0.48f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var strFormat = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(initial, initialFont, fontBrush, genericCircle, strFormat);

        return bmp;
    }

    private static GraphicsPath CreateRoundedRect(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        float d = radius * 2;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static Color GetDistinctColorForDomain(string domain)
    {
        int hash = Math.Abs(domain.GetHashCode());
        Color[] palette =
        [
            Color.FromArgb(255, 59, 130, 246),  // Blue
            Color.FromArgb(255, 16, 185, 129),  // Emerald
            Color.FromArgb(255, 139, 92, 246),  // Purple
            Color.FromArgb(255, 236, 72, 153),  // Pink
            Color.FromArgb(255, 245, 158, 11),  // Amber
            Color.FromArgb(255, 14, 165, 233)   // Sky
        ];
        return palette[hash % palette.Length];
    }

    private static string GetCacheFilePath(string domain)
    {
        string safeName = string.Join("_", domain.Split(Path.GetInvalidFileNameChars())).ToLowerInvariant();
        return Path.Combine(CacheDirectory, safeName + ".png");
    }

    private static string GetFaviconsDbPath(string executable, string profilePath)
    {
        if (profilePath.Length > 0 && Directory.Exists(profilePath))
        {
            string p = Path.Combine(profilePath, "Favicons");
            if (File.Exists(p)) return p;
        }

        string userData = BrowserSupport.UserDataDirectory(executable);
        string defaultPath = Path.Combine(userData, "Default", "Favicons");
        return File.Exists(defaultPath) ? defaultPath : "";
    }
}

/// <summary>
/// Safe reader for Chromium's Favicons SQLite database using native Windows winsqlite3.dll.
/// </summary>
internal static class NativeSqliteFaviconReader
{
    private const int SQLITE_OPEN_READONLY = 0x00000001;
    private const int SQLITE_OPEN_URI = 0x00000040;
    private const int SQLITE_ROW = 100;
    private const int SQLITE_OK = 0;

    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern int sqlite3_open_v2(string filename, out IntPtr ppDb, int flags, string? zVfs);

    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern int sqlite3_prepare_v2(IntPtr db, string zSql, int nByte, out IntPtr ppStmt, IntPtr pzTail);

    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern int sqlite3_step(IntPtr pStmt);

    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern IntPtr sqlite3_column_blob(IntPtr pStmt, int iCol);

    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern int sqlite3_column_bytes(IntPtr pStmt, int iCol);

    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern int sqlite3_finalize(IntPtr pStmt);

    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern int sqlite3_close(IntPtr db);

    public static Bitmap? TryReadFavicon(string dbPath, string domain)
    {
        IntPtr db = IntPtr.Zero;
        IntPtr stmt = IntPtr.Zero;

        try
        {
            // Open read-only
            int rc = sqlite3_open_v2(dbPath, out db, SQLITE_OPEN_READONLY | SQLITE_OPEN_URI, null);
            if (rc != SQLITE_OK || db == IntPtr.Zero) return null;

            string sql = "SELECT b.image_data FROM favicon_bitmaps b " +
                         "JOIN icon_mapping m ON b.icon_id = m.icon_id " +
                         $"WHERE m.page_url LIKE '%{domain.Replace("'", "''")}%' " +
                         "ORDER BY b.width DESC LIMIT 1;";

            rc = sqlite3_prepare_v2(db, sql, -1, out stmt, IntPtr.Zero);
            if (rc != SQLITE_OK || stmt == IntPtr.Zero) return null;

            if (sqlite3_step(stmt) == SQLITE_ROW)
            {
                IntPtr blob = sqlite3_column_blob(stmt, 0);
                int length = sqlite3_column_bytes(stmt, 0);

                if (blob != IntPtr.Zero && length > 32)
                {
                    byte[] data = new byte[length];
                    Marshal.Copy(blob, data, 0, length);
                    using var ms = new MemoryStream(data);
                    return new Bitmap(ms);
                }
            }
        }
        catch
        {
            // SQLite read safety against locked files
            return null;
        }
        finally
        {
            if (stmt != IntPtr.Zero) sqlite3_finalize(stmt);
            if (db != IntPtr.Zero) sqlite3_close(db);
        }

        return null;
    }
}
