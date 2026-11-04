using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CanvasDesktop;

internal sealed record BackgroundAsset(string Id, string Name, string Path, bool External);

/// <summary>Resolves stable asset identities without file I/O. Imported bytes are kept outside the install directory.</summary>
internal sealed class BackgroundAssets
{
    internal const long MaximumFileBytes = 64L * 1024 * 1024;
    internal const long MaximumSourcePixels = 32L * 1024 * 1024;
    private static readonly string[] BuiltIns = ["study", "bright", "nature", "abstract"];
    private readonly string _directory;

    public BackgroundAssets(string directory) => _directory = System.IO.Path.GetFullPath(directory);

    public BackgroundAsset Import(string sourcePath, bool external = false)
    {
        string path = System.IO.Path.GetFullPath(sourcePath);
        string extension = CanonicalExtension(path)
            ?? throw new InvalidDataException("Choose a PNG, JPEG or BMP background image.");
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Validate(input);
        if (external) return new("external:" + path, System.IO.Path.GetFileNameWithoutExtension(path), path, true);

        input.Position = 0;
        string id = Convert.ToHexStringLower(SHA256.HashData(input)) + extension;
        string destination = System.IO.Path.Combine(_directory, id);
        Directory.CreateDirectory(_directory);
        if (!MatchesHash(destination, id[..64]))
        {
            string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                input.Position = 0;
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { input.CopyTo(output); output.Flush(true); }
                // Importing the original again also repairs a damaged managed copy.
                File.Move(temporary, destination, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        return new(id, System.IO.Path.GetFileNameWithoutExtension(path), destination, false);
    }

    public string? Resolve(string? assetId)
    {
        if (string.IsNullOrEmpty(assetId)) return null;
        if (assetId.StartsWith("builtin:", StringComparison.Ordinal))
        {
            string name = assetId[8..];
            return BuiltIns.Contains(name, StringComparer.Ordinal)
                ? System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "Backgrounds", name + ".png") : null;
        }
        if (assetId.StartsWith("external:", StringComparison.Ordinal))
        {
            string path = assetId[9..];
            try
            {
                return System.IO.Path.IsPathFullyQualified(path) && CanonicalExtension(path) != null
                    ? System.IO.Path.GetFullPath(path) : null;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        }
        string? extension = assetId.EndsWith(".jpeg", StringComparison.Ordinal) ? ".jpeg" : CanonicalExtension(assetId);
        if (extension == null || assetId.Length != 64 + extension.Length || !assetId.EndsWith(extension, StringComparison.Ordinal)
            || !assetId.AsSpan(0, 64).ToString().All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')) return null;
        return System.IO.Path.Combine(_directory, assetId);
    }

    private static string? CanonicalExtension(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() switch
    { ".png" => ".png", ".jpg" or ".jpeg" => ".jpg", ".bmp" => ".bmp", _ => null };

    private static bool MatchesHash(string path, string expected)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return stream.Length <= MaximumFileBytes && Convert.ToHexStringLower(SHA256.HashData(stream)) == expected;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    internal static (int Width, int Height) Validate(Stream stream)
    {
        if (stream.Length == 0 || stream.Length > MaximumFileBytes)
            throw new InvalidDataException("Background images must be smaller than 64 MiB.");
        stream.Position = 0;
        try
        {
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            if (decoder is not (PngBitmapDecoder or JpegBitmapDecoder or BmpBitmapDecoder))
                throw new InvalidDataException("Choose a PNG, JPEG or BMP background image.");
            var frame = decoder.Frames[0];
            if (frame.PixelWidth < 1 || frame.PixelHeight < 1 || (long)frame.PixelWidth * frame.PixelHeight > MaximumSourcePixels)
                throw new InvalidDataException("Background images must contain at most 32 megapixels.");
            return (frame.PixelWidth, frame.PixelHeight);
        }
        catch (Exception ex) when (ex is NotSupportedException or System.Runtime.InteropServices.COMException or ArgumentException
            or FileFormatException or InvalidOperationException)
        { throw new InvalidDataException("The background image could not be decoded.", ex); }
    }
}

/// <summary>Shared immutable image cache. UI lookups never open files; decode and conversion run off-thread.</summary>
internal sealed class BackgroundImageCache : IDisposable
{
    private readonly record struct Key(string Id, bool LowQuality, bool Compact = false);
    private sealed class Entry
    {
        public BitmapSource? Image;
        public string? Error;
        public long Bytes;
        public long Used;
        public bool Complete;
        public (int Width, int Height)? Dimensions;
    }
    private readonly object _gate = new();
    private readonly Dictionary<Key, Entry> _entries = new();
    private readonly HashSet<string> _demoted = new(StringComparer.Ordinal);
    private readonly HashSet<string> _compact = new(StringComparer.Ordinal);
    private readonly BackgroundAssets _assets;
    private readonly SynchronizationContext _context;
    private readonly SemaphoreSlim _workers = new(2);
    private readonly CancellationTokenSource _stop = new();
    private readonly long _maximumBytes;
    private long _bytes, _clock;
    private bool _disposed;
    public event Action? Changed;

    public BackgroundImageCache(BackgroundAssets assets, SynchronizationContext context, long maximumBytes = 128L * 1024 * 1024)
    { _assets = assets; _context = context; _maximumBytes = Math.Max(1, maximumBytes); }

    public ImageSource? TryGet(string assetId, bool lowQuality)
    {
        lock (_gate)
        {
            if (_disposed) return null;
            if(!lowQuality && _demoted.Contains(assetId))lowQuality=true;
            bool compact = _compact.Contains(assetId);
            var key = new Key(assetId, lowQuality || compact, compact);
            if (_entries.TryGetValue(key, out var existing)) { existing.Used = ++_clock; return existing.Image; }
            // Bound remembered failures and queued work as well as decoded bitmap memory.
            if (_entries.Count >= 512)
            {
                var oldest = _entries.Where(pair => pair.Value.Complete).OrderBy(pair => pair.Value.Used).FirstOrDefault();
                if (oldest.Value == null) return null;
                _bytes -= oldest.Value.Bytes; _entries.Remove(oldest.Key);
            }
            var entry = new Entry { Used = ++_clock };
            _entries.Add(key, entry);
            _ = Task.Run(() => Load(key, entry));
            return null;
        }
    }

    public string? GetError(string assetId)
    {
        lock (_gate)
        {
            _entries.TryGetValue(new(assetId,false),out var normal);
            _entries.TryGetValue(new(assetId,true),out var low);
            _entries.TryGetValue(new(assetId,true,true),out var compact);
            return normal?.Error??low?.Error??compact?.Error;
        }
    }

    public ImageSource? GetResident(string assetId)
    {
        lock(_gate)
        {
            _entries.TryGetValue(new(assetId,false),out var normal);
            _entries.TryGetValue(new(assetId,true),out var low);
            _entries.TryGetValue(new(assetId,true,true),out var compact);
            return normal?.Image??low?.Image??compact?.Image;
        }
    }

    public (int Width, int Height)? GetDimensions(string assetId)
    {
        lock (_gate)
        {
            _entries.TryGetValue(new(assetId,false),out var normal);
            _entries.TryGetValue(new(assetId,true),out var low);
            _entries.TryGetValue(new(assetId,true,true),out var compact);
            return normal?.Dimensions??low?.Dimensions??compact?.Dimensions;
        }
    }

    public void Invalidate(string assetId)
    {
        lock (_gate)
        {
            _demoted.Remove(assetId);
            _compact.Remove(assetId);
            foreach (var key in _entries.Keys.Where(key => key.Id == assetId).ToArray())
            { _bytes -= _entries[key].Bytes; _entries.Remove(key); }
        }
    }

    private async Task Load(Key key, Entry entry)
    {
        BitmapSource? image = null;
        string? error = null;
        (int Width, int Height)? sourceDimensions = null;
        try
        {
            await _workers.WaitAsync(_stop.Token).ConfigureAwait(false);
            try
            {
                _stop.Token.ThrowIfCancellationRequested();
                string path = _assets.Resolve(key.Id) ?? throw new InvalidDataException("The background image reference is invalid.");
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var dimensions = BackgroundAssets.Validate(stream);
                sourceDimensions = dimensions;
                stream.Position = 0;
                // The compact tier can retain the entire bounded working set.
                // Demotion therefore converges without leaving visible images blank
                // or repeatedly decoding neighbours that evict each other.
                int edge = key.Compact ? Math.Clamp((int)Math.Sqrt(_maximumBytes / (512.0 * 4)), 1, 256)
                    : key.LowQuality ? 1024 : 4096;
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                // Stream imports are decoded directly and have no URI cache key
                // to invalidate with IgnoreImageCache.
                if (dimensions.Width >= dimensions.Height) bitmap.DecodePixelWidth = Math.Min(edge, dimensions.Width);
                else bitmap.DecodePixelHeight = Math.Min(edge, dimensions.Height);
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Pbgra32, null, 0);
                // Detach the cached pixels from WIC's decoder/conversion graph. Keeping
                // FormatConvertedBitmap itself would also retain its higher-bit-depth
                // source, so counting only four bytes per pixel would understate memory.
                int stride = checked(converted.PixelWidth * 4);
                var pixels = new byte[checked(stride * converted.PixelHeight)];
                converted.CopyPixels(pixels, stride, 0);
                image = BitmapSource.Create(converted.PixelWidth, converted.PixelHeight, 96, 96,
                    PixelFormats.Pbgra32, null, pixels, stride);
                image.Freeze();
            }
            finally { _workers.Release(); }
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException
            or System.Runtime.InteropServices.COMException or FileFormatException or InvalidOperationException)
        { error = "Background image unavailable. Locate or replace the image. " + ex.Message; }

        lock (_gate)
        {
            if (_disposed || !_entries.TryGetValue(key, out var current) || !ReferenceEquals(entry, current)) return;
            entry.Image = image;
            entry.Error = error;
            entry.Complete = true;
            entry.Dimensions = sourceDimensions;
            entry.Bytes = image == null ? 0 : (long)image.PixelWidth * image.PixelHeight * 4;
            _bytes += entry.Bytes;
            foreach (var candidate in _entries.Where(pair => pair.Key != key && pair.Value.Image != null)
                .OrderBy(pair => pair.Key.Compact ? 2 : pair.Key.LowQuality ? 1 : 0).ThenBy(pair => pair.Value.Used).ToArray())
            {
                if (_bytes <= _maximumBytes) break;
                _bytes -= candidate.Value.Bytes;
                if(!candidate.Key.LowQuality)
                {
                    // Remember pressure demotions. Repainting must not immediately
                    // reload the large image and evict its neighbour in an idle loop.
                    _demoted.Add(candidate.Key.Id);
                    _entries.Remove(candidate.Key);
                }
                else if (!candidate.Key.Compact)
                {
                    _compact.Add(candidate.Key.Id);
                    _entries.Remove(candidate.Key);
                }
                else
                {
                    candidate.Value.Image=null;candidate.Value.Bytes=0;
                    candidate.Value.Error="This background exceeds the image cache memory budget.";
                }
            }
            // One image can exceed a caller's smaller test/custom budget. Never retain it above that budget.
            if (_bytes > _maximumBytes)
            {
                _bytes -= entry.Bytes;
                entry.Image = null; entry.Bytes = 0;
                entry.Error = "This background exceeds the image cache memory budget.";
            }
        }
        _context.Post(_ => { lock (_gate) { if (_disposed) return; } Changed?.Invoke(); }, null);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _stop.Cancel();
            _entries.Clear();_demoted.Clear();_compact.Clear(); _bytes = 0; Changed = null;
        }
        // In-flight jobs own the semaphore/token until they finish; do not dispose them underneath a worker.
    }
}
