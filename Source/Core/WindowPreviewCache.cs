using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;

namespace CanvasDesktop;

/// <summary>
/// UI-thread-owned LRU cache. One background capture maximum, no queued jobs.
/// Image bytes include the in-flight reservation; all surfaces/bitmaps have one owner.
/// Eviction is O(1) via an internal doubly-linked list.
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
    private readonly Dictionary<IntPtr, LinkedListNode<Entry>> _entries = new();
    private readonly LinkedList<Entry> _lruList = new();
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
        => !_disposed && _entries.TryGetValue(key.Identity.Handle, out var node) && node.Value.Key == key;

    public bool RequestCapture(AutomaticParkingToken key, Size sourceSize)
    {
        var size = Fit(sourceSize);
        long bytes = _capture.ReservationBytes(sourceSize, size);
        var surfacesToDispose = new List<ICachedPreviewSurface>();
        var imagesToDispose = new List<Bitmap>();
        lock (_workerGate)
        {
            if (_disposed || _busy || bytes == 0 || bytes > _budget || !IsObserved(key)) return false;
            // Remove an older frame before reserving; no unaccounted duplicate images.
            var older = RemoveImageUnderLock(key.Identity.Handle, surfacesToDispose);
            if (older != null) imagesToDispose.Add(older.Image);
            while (_used + bytes > _budget || _entries.Count >= MaxEntries)
            {
                EvictOldestUnderLock(surfacesToDispose, imagesToDispose);
            }
            _reserved = bytes;
            _busy = true;
        }
        foreach (var s in surfacesToDispose) try { s.Dispose(); } catch { }
        foreach (var img in imagesToDispose) try { img.Dispose(); } catch { }

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
        if (_disposed || !IsObserved(key) || image.Width > MaxWidth || image.Height > MaxHeight)
        {
            image.Dispose();
            return;
        }
        long imageBytes = (long)image.Width * image.Height * 4;
        var entry = new Entry { Key = key, Image = image, Used = ++_sequence };
        bool accepted = false;
        lock (_workerGate)
        {
            if (!_disposed && _used + imageBytes <= _budget)
            {
                var node = new LinkedListNode<Entry>(entry);
                _lruList.AddFirst(node);
                _entries[key.Identity.Handle] = node;
                _used += imageBytes;
                accepted = true;
            }
        }
        if (accepted)
        {
            Changed?.Invoke();
        }
        else
        {
            image.Dispose();
        }
    }

    public IntPtr GetSource(AutomaticParkingToken key)
    {
        if (!HasImage(key)) return IsObserved(key) && !_disposed ? GetFallbackSource() : IntPtr.Zero;
        var node = _entries[key.Identity.Handle];
        node.Value.Used = ++_sequence;
        if (node != _lruList.First)
        {
            _lruList.Remove(node);
            _lruList.AddFirst(node);
        }
        try { node.Value.Surface ??= _createSurface(node.Value.Image); return node.Value.Surface.Handle; }
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
                var surfacesToDispose = new List<ICachedPreviewSurface>();
                var imagesToDispose = new List<Bitmap>();
                lock (_workerGate)
                {
                    // Do not displace an in-flight capture to create a placeholder.
                    if (_reserved + bytes > _budget) return IntPtr.Zero;
                    while (_used + _reserved + bytes > _budget)
                    {
                        if (_lruList.Last != null)
                        {
                            var evicted = RemoveImageUnderLock(_lruList.Last.Value.Key.Identity.Handle, surfacesToDispose);
                            if (evicted != null) imagesToDispose.Add(evicted.Image);
                        }
                        else
                        {
                            ClearFallbackUnderLock(surfacesToDispose);
                            break;
                        }
                    }
                    _used += bytes;
                }
                foreach (var s in surfacesToDispose) try { s.Dispose(); } catch { }
                foreach (var img in imagesToDispose) try { img.Dispose(); } catch { }

                _fallback = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
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
        foreach (var node in _entries.Values) { node.Value.Surface?.Dispose(); node.Value.Surface = null; }
        _fallbackSurface?.Dispose();
        _fallbackSurface = null;
    }
    public void Remove(IntPtr handle) { _identities.Remove(handle); RemoveImage(handle); }
    public void InvalidateImage(IntPtr handle) => RemoveImage(handle);
    public void Clear()
    {
        _identities.Clear();
        List<IntPtr> handles;
        lock (_workerGate)
        {
            handles = new List<IntPtr>(_entries.Keys);
        }
        foreach (var handle in handles) RemoveImage(handle);
        ClearFallback();
    }
    private bool IsObserved(AutomaticParkingToken key)
        => _identities.TryGetValue(key.Identity.Handle, out var current) && current == key;

    private void EvictOldestUnderLock(List<ICachedPreviewSurface> surfacesToDispose, List<Bitmap> imagesToDispose)
    {
        if (_lruList.Last != null)
        {
            var evicted = RemoveImageUnderLock(_lruList.Last.Value.Key.Identity.Handle, surfacesToDispose);
            if (evicted != null) imagesToDispose.Add(evicted.Image);
        }
        else
        {
            ClearFallbackUnderLock(surfacesToDispose);
        }
    }

    private void ClearFallbackUnderLock(List<ICachedPreviewSurface>? surfacesToDispose = null)
    {
        if (_fallbackSurface != null)
        {
            if (surfacesToDispose != null) surfacesToDispose.Add(_fallbackSurface);
            else try { _fallbackSurface.Dispose(); } catch { }
            _fallbackSurface = null;
        }
        if (_fallback != null)
        {
            _used -= (long)_fallback.Width * _fallback.Height * 4;
            try { _fallback.Dispose(); } catch { }
            _fallback = null;
            Changed?.Invoke();
        }
    }

    private void ClearFallback()
    {
        ICachedPreviewSurface? surface = null;
        Bitmap? fallback = null;
        lock (_workerGate)
        {
            surface = _fallbackSurface;
            _fallbackSurface = null;
            if (_fallback != null)
            {
                fallback = _fallback;
                _used -= (long)_fallback.Width * _fallback.Height * 4;
                _fallback = null;
            }
        }
        try { surface?.Dispose(); } catch { }
        if (fallback != null)
        {
            try { fallback.Dispose(); } catch { }
            Changed?.Invoke();
        }
    }

    private Entry? RemoveImageUnderLock(IntPtr handle, List<ICachedPreviewSurface>? surfacesToDispose = null)
    {
        if (!_entries.Remove(handle, out var node)) return null;
        _lruList.Remove(node);
        var e = node.Value;
        _used -= e.Bytes;
        if (e.Surface != null)
        {
            if (surfacesToDispose != null) surfacesToDispose.Add(e.Surface);
            else try { e.Surface.Dispose(); } catch { }
            e.Surface = null;
        }
        return e;
    }

    private void RemoveImage(IntPtr handle)
    {
        Entry? e;
        lock (_workerGate)
        {
            e = RemoveImageUnderLock(handle);
        }
        if (e != null)
        {
            try { e.Surface?.Dispose(); } catch { }
            try { e.Image.Dispose(); } catch { }
            Changed?.Invoke();
        }
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
