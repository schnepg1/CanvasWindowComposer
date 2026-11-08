using System;
using System.Collections.Generic;

namespace CanvasDesktop;

/// <summary>
/// Thread-safe, O(1) bounded Least-Recently-Used (LRU) memory cache enforcing both
/// maximum item count and maximum memory byte budget constraints.
/// </summary>
/// <typeparam name="TKey">Lookup key type.</typeparam>
/// <typeparam name="TValue">Stored value type.</typeparam>
internal sealed class LruMemoryCache<TKey, TValue> : IDisposable where TKey : notnull
{
    private sealed class LruNode
    {
        public readonly TKey Key;
        public readonly TValue Value;
        public readonly long Bytes;

        public LruNode(TKey key, TValue value, long bytes)
        {
            Key = key;
            Value = value;
            Bytes = bytes;
        }
    }

    private readonly object _sync = new();
    private readonly Dictionary<TKey, LinkedListNode<LruNode>> _map;
    private readonly LinkedList<LruNode> _list = new();
    private readonly Func<TValue, long> _getBytes;
    private readonly Action<TKey, TValue>? _onEvicted;

    private int _maxCapacity;
    private long _maxMemoryBytes;
    private long _totalBytes;
    private bool _disposed;

    public LruMemoryCache(
        int maxCapacity,
        long maxMemoryBytes,
        Func<TValue, long> getBytes,
        Action<TKey, TValue>? onEvicted = null)
    {
        if (maxCapacity < 0) throw new ArgumentOutOfRangeException(nameof(maxCapacity), "Capacity cannot be negative.");
        if (maxMemoryBytes < 0) throw new ArgumentOutOfRangeException(nameof(maxMemoryBytes), "Memory budget cannot be negative.");

        _maxCapacity = maxCapacity;
        _maxMemoryBytes = maxMemoryBytes;
        _getBytes = getBytes ?? throw new ArgumentNullException(nameof(getBytes));
        _onEvicted = onEvicted;
        _map = new Dictionary<TKey, LinkedListNode<LruNode>>();
    }

    public int Count
    {
        get { lock (_sync) return _map.Count; }
    }

    public long TotalBytes
    {
        get { lock (_sync) return _totalBytes; }
    }

    public int MaxCapacity
    {
        get { lock (_sync) return _maxCapacity; }
        set
        {
            List<(TKey Key, TValue Value)> evictions;
            lock (_sync)
            {
                if (value < 0) throw new ArgumentOutOfRangeException(nameof(value), "Capacity cannot be negative.");
                _maxCapacity = value;
                evictions = TrimUnderLock();
            }
            NotifyEvicted(evictions);
        }
    }

    public long MaxMemoryBytes
    {
        get { lock (_sync) return _maxMemoryBytes; }
        set
        {
            List<(TKey Key, TValue Value)> evictions;
            lock (_sync)
            {
                if (value < 0) throw new ArgumentOutOfRangeException(nameof(value), "Memory budget cannot be negative.");
                _maxMemoryBytes = value;
                evictions = TrimUnderLock();
            }
            NotifyEvicted(evictions);
        }
    }

    public bool ContainsKey(TKey key)
    {
        lock (_sync)
        {
            return !_disposed && _map.ContainsKey(key);
        }
    }

    public bool TryGet(TKey key, out TValue? value)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                value = default;
                return false;
            }

            if (_map.TryGetValue(key, out var node))
            {
                if (node != _list.First)
                {
                    _list.Remove(node);
                    _list.AddFirst(node);
                }
                value = node.Value.Value;
                return true;
            }

            value = default;
            return false;
        }
    }

    public bool Peek(TKey key, out TValue? value)
    {
        lock (_sync)
        {
            if (!_disposed && _map.TryGetValue(key, out var node))
            {
                value = node.Value.Value;
                return true;
            }

            value = default;
            return false;
        }
    }

    public void Set(TKey key, TValue value)
    {
        List<(TKey Key, TValue Value)> evictions;
        lock (_sync)
        {
            if (_disposed) return;

            long bytes = Math.Max(0L, _getBytes(value));
            evictions = new List<(TKey Key, TValue Value)>();

            if (_map.TryGetValue(key, out var existingNode))
            {
                _totalBytes -= existingNode.Value.Bytes;
                _list.Remove(existingNode);
                _map.Remove(key);
                evictions.Add((key, existingNode.Value.Value));
            }

            var newNode = new LinkedListNode<LruNode>(new LruNode(key, value, bytes));
            _list.AddFirst(newNode);
            _map[key] = newNode;
            _totalBytes += bytes;

            TrimUnderLock(evictions);
        }

        NotifyEvicted(evictions);
    }

    public TValue GetOrCreate(TKey key, Func<TKey, TValue> factory)
    {
        if (TryGet(key, out var existing) && existing is not null)
        {
            return existing;
        }

        var created = factory(key);
        Set(key, created);
        return created;
    }

    /// <summary>
    /// Moves an existing key to the tail (LRU / eviction head) of the cache so that it
    /// will be prioritized for eviction under memory or capacity pressure.
    /// </summary>
    /// <returns>True if the key was found and demoted; false otherwise.</returns>
    public bool Demote(TKey key)
    {
        lock (_sync)
        {
            if (_disposed) return false;
            if (_map.TryGetValue(key, out var node))
            {
                if (node != _list.Last)
                {
                    _list.Remove(node);
                    _list.AddLast(node);
                }
                return true;
            }
            return false;
        }
    }

    public bool Remove(TKey key)
    {
        TValue? evictedValue = default;
        bool found = false;
        lock (_sync)
        {
            if (_disposed) return false;
            if (_map.TryGetValue(key, out var node))
            {
                _totalBytes -= node.Value.Bytes;
                _list.Remove(node);
                _map.Remove(key);
                evictedValue = node.Value.Value;
                found = true;
            }
        }

        if (found)
        {
            NotifyEvicted(new List<(TKey Key, TValue Value)> { (key, evictedValue!) });
        }

        return found;
    }

    public void Clear()
    {
        List<(TKey Key, TValue Value)> evictions;
        lock (_sync)
        {
            evictions = new List<(TKey Key, TValue Value)>(_map.Count);
            foreach (var node in _list)
            {
                evictions.Add((node.Key, node.Value));
            }
            _map.Clear();
            _list.Clear();
            _totalBytes = 0;
        }
        NotifyEvicted(evictions);
    }

    public void Dispose()
    {
        List<(TKey Key, TValue Value)> evictions;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            evictions = new List<(TKey Key, TValue Value)>(_map.Count);
            foreach (var node in _list)
            {
                evictions.Add((node.Key, node.Value));
            }
            _map.Clear();
            _list.Clear();
            _totalBytes = 0;
        }
        NotifyEvicted(evictions);
    }

    private List<(TKey Key, TValue Value)> TrimUnderLock(List<(TKey Key, TValue Value)>? collector = null)
    {
        var evictions = collector ?? new List<(TKey Key, TValue Value)>();
        while (_list.Count > 0 &&
               (_map.Count > _maxCapacity || _totalBytes > _maxMemoryBytes))
        {
            var lru = _list.Last!;
            _list.RemoveLast();
            _map.Remove(lru.Value.Key);
            _totalBytes -= lru.Value.Bytes;
            evictions.Add((lru.Value.Key, lru.Value.Value));
        }
        return evictions;
    }

    private void NotifyEvicted(List<(TKey Key, TValue Value)> evictions)
    {
        if (evictions == null || evictions.Count == 0) return;
        foreach (var (k, v) in evictions)
        {
            try
            {
                _onEvicted?.Invoke(k, v);
            }
            catch
            {
                // Never allow caller callback exceptions to propagate from cache mutation
            }

            if (v is IDisposable disposable)
            {
                try
                {
                    disposable.Dispose();
                }
                catch
                {
                    // Ignore disposal exceptions from evicted items
                }
            }
        }
    }
}
