using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CanvasDesktop;
using Xunit;

namespace CanvasDesktop.Tests;

public class LruMemoryCacheTests
{
    private sealed class TestResource : IDisposable
    {
        public string Name { get; }
        public long Bytes { get; }
        public bool IsDisposed { get; private set; }

        public TestResource(string name, long bytes)
        {
            Name = name;
            Bytes = bytes;
        }

        public void Dispose()
        {
            IsDisposed = true;
        }
    }

    [Fact]
    public void EvictsLeastRecentlyUsedWhenCapacityExceeded()
    {
        var evicted = new List<string>();
        using var cache = new LruMemoryCache<string, string>(
            maxCapacity: 3,
            maxMemoryBytes: 10_000,
            getBytes: _ => 10,
            onEvicted: (k, _) => evicted.Add(k));

        cache.Set("a", "1");
        cache.Set("b", "2");
        cache.Set("c", "3");

        // Touch "a" so it becomes MRU (order: a, c, b)
        Assert.True(cache.TryGet("a", out var valA));
        Assert.Equal("1", valA);

        // Add "d", which exceeds capacity 3 -> should evict LRU ("b")
        cache.Set("d", "4");

        Assert.Equal(3, cache.Count);
        Assert.Contains("b", evicted);
        Assert.False(cache.ContainsKey("b"));
        Assert.True(cache.ContainsKey("a"));
        Assert.True(cache.ContainsKey("c"));
        Assert.True(cache.ContainsKey("d"));
    }

    [Fact]
    public void EvictsLeastRecentlyUsedWhenByteBudgetExceeded()
    {
        var evicted = new List<string>();
        using var cache = new LruMemoryCache<string, string>(
            maxCapacity: 10,
            maxMemoryBytes: 100,
            getBytes: s => s.Length * 10, // each char is 10 bytes
            onEvicted: (k, _) => evicted.Add(k));

        cache.Set("k1", "abcd"); // 40 bytes
        cache.Set("k2", "efgh"); // 40 bytes
        Assert.Equal(80, cache.TotalBytes);

        // Adding k3 (40 bytes) makes 120 bytes > 100 max -> evict k1 (40 bytes)
        cache.Set("k3", "ijkl"); // 40 bytes

        Assert.Equal(80, cache.TotalBytes);
        Assert.Equal(2, cache.Count);
        Assert.Contains("k1", evicted);
        Assert.False(cache.ContainsKey("k1"));
        Assert.True(cache.ContainsKey("k2"));
        Assert.True(cache.ContainsKey("k3"));
    }

    [Fact]
    public void DemoteMovesEntryToEvictionHead()
    {
        var evicted = new List<string>();
        using var cache = new LruMemoryCache<string, string>(
            maxCapacity: 3,
            maxMemoryBytes: 10_000,
            getBytes: _ => 10,
            onEvicted: (k, _) => evicted.Add(k));

        cache.Set("a", "1");
        cache.Set("b", "2");
        cache.Set("c", "3");

        // Normal order from MRU to LRU is: c, b, a.
        // If we demote "c", "c" moves to the LRU tail (eviction head).
        Assert.True(cache.Demote("c"));

        // Inserting "d" should now evict "c" instead of "a"!
        cache.Set("d", "4");

        Assert.Single(evicted);
        Assert.Equal("c", evicted[0]);
        Assert.False(cache.ContainsKey("c"));
        Assert.True(cache.ContainsKey("a"));
        Assert.True(cache.ContainsKey("b"));
        Assert.True(cache.ContainsKey("d"));
    }

    [Fact]
    public void DisposesEvictedDisposableEntries()
    {
        var res1 = new TestResource("r1", 10);
        var res2 = new TestResource("r2", 10);
        var res3 = new TestResource("r3", 10);

        using (var cache = new LruMemoryCache<string, TestResource>(
            maxCapacity: 2,
            maxMemoryBytes: 1000,
            getBytes: r => r.Bytes))
        {
            cache.Set("1", res1);
            cache.Set("2", res2);
            Assert.False(res1.IsDisposed);
            Assert.False(res2.IsDisposed);

            // Adding 3rd should evict and dispose res1
            cache.Set("3", res3);
            Assert.True(res1.IsDisposed);
            Assert.False(res2.IsDisposed);
            Assert.False(res3.IsDisposed);

            // Remove res2 explicitly
            cache.Remove("2");
            Assert.True(res2.IsDisposed);
        }

        // Disposing cache disposes remaining items (res3)
        Assert.True(res3.IsDisposed);
    }

    [Fact]
    public async Task ConcurrentReadersAndWritersDoNotCorruptState()
    {
        using var cache = new LruMemoryCache<int, string>(
            maxCapacity: 50,
            maxMemoryBytes: 5_000,
            getBytes: s => s.Length);

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var tasks = new List<Task>();

        for (int t = 0; t < 8; t++)
        {
            int threadId = t;
            tasks.Add(Task.Run(() =>
            {
                var rand = new Random(threadId * 100);
                while (!cts.IsCancellationRequested)
                {
                    int key = rand.Next(0, 100);
                    int op = rand.Next(0, 5);
                    switch (op)
                    {
                        case 0:
                        case 1:
                            cache.Set(key, new string('x', rand.Next(10, 50)));
                            break;
                        case 2:
                        case 3:
                            cache.TryGet(key, out _);
                            break;
                        case 4:
                            cache.Demote(key);
                            break;
                    }
                }
            }));
        }

        await Task.WhenAll(tasks);

        Assert.True(cache.Count <= 50);
        Assert.True(cache.TotalBytes <= 5_000);
    }
}
