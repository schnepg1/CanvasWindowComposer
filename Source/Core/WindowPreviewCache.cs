using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;

namespace CanvasDesktop;

/// <summary>
/// UI-thread-owned LRU cache. One background capture maximum, no queued jobs.
/// Image bytes include the in-flight reservation; all surfaces/bitmaps have one owner.
/// </summary>
internal sealed class WindowPreviewCache : IDisposable
{
    internal const int MaxWidth = 1024, MaxHeight = 576, MaxEntries = 64;
    internal const long DefaultBudgetBytes = 32 * 1024 * 1024;
    private sealed class Entry
    {
        public required AutomaticParkingToken Key;
        public required Bitmap Image;
        public ICachedPreviewSurface? Surface;
        public long Used;
        public long Bytes => (long)Image.Width * Image.Height * 4;
    }
    private readonly Dictionary<IntPtr, AutomaticParkingToken> _identities = new();
    private readonly Dictionary<IntPtr, Entry> _entries = new();
    private readonly IWindowPreviewCapture _capture;
    private readonly Func<Bitmap, ICachedPreviewSurface> _createSurface;
    private readonly long _budget;
    private long _used, _sequence;
    private readonly object _workerGate = new();
    private bool _disposed, _busy;
    private long _reserved;
    private (AutomaticParkingToken key, Bitmap? image)? _completed;
    private Bitmap? _fallback;
    private ICachedPreviewSurface? _fallbackSurface;
    public event Action? Changed;
    public long ImageBytes { get { lock (_workerGate) return _used + _reserved; } }
    public int Count => _entries.Count;
    public bool CapturePending { get { lock (_workerGate) return _busy; } }

    public WindowPreviewCache(IWindowPreviewCapture? capture = null,
        Func<Bitmap, ICachedPreviewSurface>? createSurface = null, long budgetBytes = DefaultBudgetBytes)
    {
        if (budgetBytes <= 0) throw new ArgumentOutOfRangeException(nameof(budgetBytes));
        _capture = capture ?? new WindowPreviewCapture();
        _createSurface = createSurface ?? (image => new CachedPreviewWindow(image));
        _budget = budgetBytes;
    }

    public static Size Fit(Size size)
    {
        if (size.Width <= 0 || size.Height <= 0) return Size.Empty;
        double scale = Math.Min(1, Math.Min((double)MaxWidth / size.Width, (double)MaxHeight / size.Height));
        return new(Math.Max(1, (int)(size.Width * scale)), Math.Max(1, (int)(size.Height * scale)));
    }

    public void Observe(IntPtr handle, AutomaticParkingToken? key)
    {
        if (_disposed) return;
        if (key.HasValue)
        {
            if (_identities.TryGetValue(handle, out var old) && old == key.Value) return;
            _identities[handle] = key.Value;
        }
        else _identities.Remove(handle);
        RemoveImage(handle);
    }

    public bool HasImage(AutomaticParkingToken key)
        => !_disposed && _entries.TryGetValue(key.Identity.Handle, out var e) && e.Key == key;

    public bool RequestCapture(AutomaticParkingToken key, Size sourceSize)
    {
        var size = Fit(sourceSize);
        long bytes = _capture.ReservationBytes(sourceSize, size);
        lock (_workerGate)
        {
            if (_disposed || _busy || bytes == 0 || bytes > _budget || !IsObserved(key)) return false;
            // Remove an older frame before reserving; no unaccounted duplicate images.
            RemoveImage(key.Identity.Handle);
            while (_used + bytes > _budget || _entries.Count >= MaxEntries) EvictOldest();
            _reserved = bytes;
            _busy = true;
        }
        var thread = new Thread(() =>
        {
            Bitmap? image = null;
            try { image = _capture.Capture(key.Identity.Handle, sourceSize, size); }
            catch { /* Failure leaves live preview/placeholder; never authorize parking from it. */ }
            lock (_workerGate)
            {
                if (_disposed) { image?.Dispose(); _reserved = 0; _busy = false; }
                else _completed = (key, image);
            }
        }) { IsBackground = true, Name = "Canvas preview capture" };
        try { thread.Start(); }
        catch { lock (_workerGate) { _reserved = 0; _busy = false; } throw; }
        return true;
    }

    public void DrainCompleted()
    {
        (AutomaticParkingToken key, Bitmap? image)? completed;
        lock (_workerGate)
        {
            completed = _completed;
            if (completed == null) return;
            _completed = null;
            _reserved = 0;
            _busy = false;
        }
        var (key, image) = completed.Value;
        if (image == null) return;
        if (_disposed || !IsObserved(key) || image.Width > MaxWidth || image.Height > MaxHeight
            || (long)image.Width * image.Height * 4 + _used > _budget)
        { image.Dispose(); return; }
        var entry = new Entry { Key = key, Image = image, Used = ++_sequence };
        _entries[key.Identity.Handle] = entry;
        _used += entry.Bytes;
        Changed?.Invoke();
    }

    public IntPtr GetSource(AutomaticParkingToken key)
    {
        if (!HasImage(key)) return IsObserved(key) && !_disposed ? GetFallbackSource() : IntPtr.Zero;
        var e = _entries[key.Identity.Handle];
        e.Used = ++_sequence;
        try { e.Surface ??= _createSurface(e.Image); return e.Surface.Handle; }
        catch { RemoveImage(key.Identity.Handle); return IntPtr.Zero; }
    }

    private IntPtr GetFallbackSource()
    {
        try
        {
            if (_fallback == null)
            {
                int w = Math.Min(160, (int)Math.Sqrt(_budget / 4));
                int h = Math.Min(90, w);
                if (w < 1) return IntPtr.Zero;
                long bytes = (long)w * h * 4;
                // Do not displace an in-flight capture to create a placeholder.
                if (_reserved + bytes > _budget) return IntPtr.Zero;
                while (_used + _reserved + bytes > _budget) EvictOldest();
                _fallback = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
                _used += bytes;
                using var g = Graphics.FromImage(_fallback);
                g.Clear(Color.FromArgb(38, 42, 48));
                if (w >= 100) g.DrawString("Preview unavailable", SystemFonts.MessageBoxFont!, Brushes.LightGray,
                    new RectangleF(8, 8, w - 16, h - 16));
            }
            _fallbackSurface ??= _createSurface(_fallback);
            return _fallbackSurface.Handle;
        }
        catch { ClearFallback(); return IntPtr.Zero; }
    }

    // Overview close releases native surfaces but keeps bounded CPU images for reopening.
    public void ReleaseSurfaces()
    {
        foreach (var e in _entries.Values) { e.Surface?.Dispose(); e.Surface = null; }
        _fallbackSurface?.Dispose();
        _fallbackSurface = null;
    }
    public void Remove(IntPtr handle) { _identities.Remove(handle); RemoveImage(handle); }
    public void InvalidateImage(IntPtr handle) => RemoveImage(handle);
    public void Clear()
    {
        _identities.Clear();
        foreach (var handle in new List<IntPtr>(_entries.Keys)) RemoveImage(handle);
        ClearFallback();
    }
    private bool IsObserved(AutomaticParkingToken key)
        => _identities.TryGetValue(key.Identity.Handle, out var current) && current == key;
    private void EvictOldest()
    {
        Entry? oldest = null;
        foreach (var e in _entries.Values) if (oldest == null || e.Used < oldest.Used) oldest = e;
        if (oldest != null) RemoveImage(oldest.Key.Identity.Handle);
        else ClearFallback();
    }
    private void ClearFallback()
    {
        _fallbackSurface?.Dispose(); _fallbackSurface = null;
        if (_fallback != null)
        {
            _used -= (long)_fallback.Width * _fallback.Height * 4;
            _fallback.Dispose(); _fallback = null;
            Changed?.Invoke();
        }
    }
    private void RemoveImage(IntPtr handle)
    {
        if (!_entries.Remove(handle, out var e)) return;
        _used -= e.Bytes;
        try { e.Surface?.Dispose(); }
        finally { e.Image.Dispose(); }
        Changed?.Invoke();
    }
    public void Dispose()
    {
        lock (_workerGate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_completed.HasValue) { _reserved = 0; _busy = false; }
            _completed?.image?.Dispose();
            _completed = null;
            // An uninterruptible PrintWindow owns its bitmap until it returns.
            // Never join it or create another worker after disposal.
            if (!_busy) _reserved = 0;
        }
        Clear();
    }
}
