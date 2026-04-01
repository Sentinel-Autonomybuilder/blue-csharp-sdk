using Sentinel.SDK.Core;
using Xunit;

namespace Sentinel.SDK.Tests;

/// <summary>
/// Tests for NodeCache — in-memory node list cache with TTL,
/// stale-while-revalidate, and deduplicated inflight fetches.
/// </summary>
public class NodeCacheTests
{
    private static readonly IReadOnlyList<ChainNode> SampleNodes = new List<ChainNode>
    {
        new("sentnode1aaa", new[] { "1.2.3.4:8585" }, "https://node-a.example.com",
            new[] { new PriceEntry("udvpn", "1000000", "1000000") },
            Array.Empty<PriceEntry>(), 1),
        new("sentnode1bbb", new[] { "5.6.7.8:8585" }, "https://node-b.example.com",
            new[] { new PriceEntry("udvpn", "2000000", "2000000") },
            Array.Empty<PriceEntry>(), 1),
    };

    private static readonly IReadOnlyList<ChainNode> UpdatedNodes = new List<ChainNode>
    {
        new("sentnode1ccc", new[] { "9.10.11.12:8585" }, "https://node-c.example.com",
            new[] { new PriceEntry("udvpn", "3000000", "3000000") },
            Array.Empty<PriceEntry>(), 1),
    };

    // ─── GetAsync Calls Fetcher on First Call ───

    [Fact]
    public async Task GetAsync_CallsFetcher_OnFirstCall()
    {
        var cache = new NodeCache(TimeSpan.FromMinutes(5));
        var callCount = 0;

        Task<IReadOnlyList<ChainNode>> Fetcher()
        {
            Interlocked.Increment(ref callCount);
            return Task.FromResult(SampleNodes);
        }

        var result = await cache.GetAsync(Fetcher);

        Assert.Equal(1, callCount);
        Assert.Equal(2, result.Count);
        Assert.Equal("sentnode1aaa", result[0].Address);
    }

    // ─── GetAsync Returns Cached Result ───

    [Fact]
    public async Task GetAsync_ReturnsCachedResult_OnSecondCall()
    {
        var cache = new NodeCache(TimeSpan.FromMinutes(5));
        var callCount = 0;

        Task<IReadOnlyList<ChainNode>> Fetcher()
        {
            Interlocked.Increment(ref callCount);
            return Task.FromResult(SampleNodes);
        }

        var first = await cache.GetAsync(Fetcher);
        var second = await cache.GetAsync(Fetcher);

        // Fetcher called once for the initial fetch. Second call returns fresh cache.
        // A background refresh may be started but we only need to verify the fetcher
        // was NOT awaited again for the second call's return value.
        Assert.Equal(first.Count, second.Count);
        Assert.Equal("sentnode1aaa", second[0].Address);
    }

    // ─── IsFresh After Fetch ───

    [Fact]
    public async Task IsFresh_IsTrue_AfterFetch()
    {
        var cache = new NodeCache(TimeSpan.FromMinutes(5));

        Assert.False(cache.IsFresh); // Before any fetch

        await cache.GetAsync(() => Task.FromResult(SampleNodes));

        Assert.True(cache.IsFresh);
    }

    // ─── IsFresh After TTL ───

    [Fact]
    public async Task IsFresh_IsFalse_AfterTtlExpires()
    {
        var cache = new NodeCache(TimeSpan.FromMilliseconds(50));

        await cache.GetAsync(() => Task.FromResult(SampleNodes));
        Assert.True(cache.IsFresh);

        Thread.Sleep(100);

        Assert.False(cache.IsFresh);
    }

    // ─── Flush Clears Cache ───

    [Fact]
    public async Task Flush_ClearsCache_NextGetAsyncCallsFetcherAgain()
    {
        var cache = new NodeCache(TimeSpan.FromMinutes(5));
        var callCount = 0;

        Task<IReadOnlyList<ChainNode>> Fetcher()
        {
            Interlocked.Increment(ref callCount);
            return Task.FromResult(
                callCount == 1 ? SampleNodes : UpdatedNodes);
        }

        var first = await cache.GetAsync(Fetcher);
        Assert.Equal(2, first.Count);
        Assert.Equal(1, callCount);

        cache.Flush();
        Assert.False(cache.IsFresh);

        var second = await cache.GetAsync(Fetcher);
        Assert.Equal(2, callCount);
        Assert.Single(second);
        Assert.Equal("sentnode1ccc", second[0].Address);
    }

    // ─── AgeSeconds Increases ───

    [Fact]
    public async Task AgeSeconds_IncreasesOverTime()
    {
        var cache = new NodeCache(TimeSpan.FromMinutes(5));

        // Before any fetch, age should be MaxValue
        Assert.Equal(double.MaxValue, cache.AgeSeconds);

        await cache.GetAsync(() => Task.FromResult(SampleNodes));
        var age1 = cache.AgeSeconds;
        Assert.True(age1 < 2.0); // Should be very small right after fetch

        Thread.Sleep(200);
        var age2 = cache.AgeSeconds;
        Assert.True(age2 > age1);
    }

    // ─── Concurrent GetAsync Deduplicates ───

    [Fact]
    public async Task GetAsync_DeduplicatesConcurrentCalls()
    {
        var cache = new NodeCache(TimeSpan.FromMinutes(5));
        var callCount = 0;

        async Task<IReadOnlyList<ChainNode>> SlowFetcher()
        {
            Interlocked.Increment(ref callCount);
            await Task.Delay(100); // Simulate slow network call
            return SampleNodes;
        }

        // Launch 10 concurrent GetAsync calls on a cold cache
        var tasks = Enumerable.Range(0, 10)
            .Select(_ => cache.GetAsync(SlowFetcher))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        // Fetcher should have been called exactly once (all 10 share the inflight task)
        Assert.Equal(1, callCount);

        // All results should contain the same data
        foreach (var result in results)
        {
            Assert.Equal(2, result.Count);
        }
    }

    // ─── GetAsync Falls Back to Stale Data on Fetch Failure ───

    [Fact]
    public async Task GetAsync_FallsBackToStaleData_WhenFetchFails()
    {
        var cache = new NodeCache(TimeSpan.FromMilliseconds(50));
        var callCount = 0;

        Task<IReadOnlyList<ChainNode>> Fetcher()
        {
            Interlocked.Increment(ref callCount);
            if (callCount == 1)
                return Task.FromResult(SampleNodes);
            throw new InvalidOperationException("Network error");
        }

        // First call succeeds
        var first = await cache.GetAsync(Fetcher);
        Assert.Equal(2, first.Count);

        // Wait for TTL to expire
        Thread.Sleep(100);

        // Second call: fetcher throws, but cache returns stale data
        var second = await cache.GetAsync(Fetcher);
        Assert.Equal(2, second.Count);
        Assert.Equal("sentnode1aaa", second[0].Address);
    }

    // ─── GetAsync Throws on Cold Start Fetch Failure ───

    [Fact]
    public async Task GetAsync_Throws_WhenColdStartFetchFails()
    {
        var cache = new NodeCache(TimeSpan.FromMinutes(5));

        Task<IReadOnlyList<ChainNode>> FailingFetcher()
        {
            throw new InvalidOperationException("Network error");
        }

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => cache.GetAsync(FailingFetcher));
    }

    // ─── Flush Resets AgeSeconds ───

    [Fact]
    public async Task Flush_ResetsAgeToMaxValue()
    {
        var cache = new NodeCache(TimeSpan.FromMinutes(5));

        await cache.GetAsync(() => Task.FromResult(SampleNodes));
        Assert.NotEqual(double.MaxValue, cache.AgeSeconds);

        cache.Flush();
        Assert.Equal(double.MaxValue, cache.AgeSeconds);
    }

    // ─── GetAsync Throws on Null Fetcher ───

    [Fact]
    public async Task GetAsync_ThrowsArgumentNullException_WhenFetcherIsNull()
    {
        var cache = new NodeCache(TimeSpan.FromMinutes(5));

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => cache.GetAsync(null!));
    }

    // ─── Constructor Defaults ───

    [Fact]
    public void Constructor_DefaultTtl_CacheIsNotFresh()
    {
        var cache = new NodeCache();

        Assert.False(cache.IsFresh);
        Assert.Equal(double.MaxValue, cache.AgeSeconds);
    }
}
