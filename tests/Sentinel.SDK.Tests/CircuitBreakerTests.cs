using Sentinel.SDK.Core;
using Xunit;

namespace Sentinel.SDK.Tests;

/// <summary>
/// Tests for the CircuitBreaker — per-node failure tracking with
/// configurable threshold, TTL-based auto-purge, and thread safety.
/// </summary>
public class CircuitBreakerTests
{
    private const string NodeA = "sentnode1aaa";
    private const string NodeB = "sentnode1bbb";

    // ─── IsOpen — Unknown Node ───

    [Fact]
    public void IsOpen_ReturnsFalse_ForUnknownNode()
    {
        var cb = new CircuitBreaker();

        Assert.False(cb.IsOpen(NodeA));
    }

    // ─── RecordFailure Increments Count ───

    [Fact]
    public void RecordFailure_IncrementsFailureCount()
    {
        var cb = new CircuitBreaker();

        cb.RecordFailure(NodeA);
        var status = cb.GetStatus(NodeA);

        Assert.NotNull(status);
        Assert.Equal(1, status!.FailureCount);

        cb.RecordFailure(NodeA);
        status = cb.GetStatus(NodeA);

        Assert.Equal(2, status!.FailureCount);
    }

    // ─── IsOpen After Threshold ───

    [Fact]
    public void IsOpen_ReturnsTrue_AfterThresholdFailures()
    {
        var cb = new CircuitBreaker();

        cb.RecordFailure(NodeA);
        cb.RecordFailure(NodeA);
        Assert.False(cb.IsOpen(NodeA)); // 2 < 3 (default threshold)

        cb.RecordFailure(NodeA);
        Assert.True(cb.IsOpen(NodeA)); // 3 >= 3
    }

    // ─── IsOpen Returns False After TTL Expires ───

    [Fact]
    public void IsOpen_ReturnsFalse_AfterTtlExpires()
    {
        var cb = new CircuitBreaker();
        // Minimum TTL is 1 second (enforced by Configure)
        cb.Configure(threshold: 1, ttl: TimeSpan.FromSeconds(1));

        cb.RecordFailure(NodeA);
        Assert.True(cb.IsOpen(NodeA));

        Thread.Sleep(1200);

        Assert.False(cb.IsOpen(NodeA));
    }

    // ─── Reset Specific Node ───

    [Fact]
    public void Reset_ClearsSpecificNode()
    {
        var cb = new CircuitBreaker();

        cb.RecordFailure(NodeA);
        cb.RecordFailure(NodeA);
        cb.RecordFailure(NodeA);
        cb.RecordFailure(NodeB);

        Assert.True(cb.IsOpen(NodeA));

        cb.Reset(NodeA);

        Assert.False(cb.IsOpen(NodeA));
        Assert.Null(cb.GetStatus(NodeA));

        // NodeB should be unaffected
        var statusB = cb.GetStatus(NodeB);
        Assert.NotNull(statusB);
        Assert.Equal(1, statusB!.FailureCount);
    }

    // ─── Reset All ───

    [Fact]
    public void Reset_WithNull_ClearsAllNodes()
    {
        var cb = new CircuitBreaker();

        cb.RecordFailure(NodeA);
        cb.RecordFailure(NodeA);
        cb.RecordFailure(NodeA);
        cb.RecordFailure(NodeB);
        cb.RecordFailure(NodeB);
        cb.RecordFailure(NodeB);

        Assert.True(cb.IsOpen(NodeA));
        Assert.True(cb.IsOpen(NodeB));

        cb.Reset(null);

        Assert.False(cb.IsOpen(NodeA));
        Assert.False(cb.IsOpen(NodeB));
        Assert.Empty(cb.GetStatus());
    }

    // ─── Configure Changes Threshold ───

    [Fact]
    public void Configure_ChangesThreshold()
    {
        var cb = new CircuitBreaker();
        cb.Configure(threshold: 5);

        for (var i = 0; i < 3; i++)
            cb.RecordFailure(NodeA);

        Assert.False(cb.IsOpen(NodeA)); // 3 < 5

        cb.RecordFailure(NodeA);
        cb.RecordFailure(NodeA);
        Assert.True(cb.IsOpen(NodeA)); // 5 >= 5
    }

    // ─── Configure Changes TTL ───

    [Fact]
    public void Configure_ChangesTtl()
    {
        var cb = new CircuitBreaker();
        // Minimum TTL is 1 second (enforced by Configure)
        cb.Configure(threshold: 1, ttl: TimeSpan.FromSeconds(1));

        cb.RecordFailure(NodeA);
        Assert.True(cb.IsOpen(NodeA));

        Thread.Sleep(1200);
        Assert.False(cb.IsOpen(NodeA));
    }

    // ─── Configure Enforces Minimums ───

    [Fact]
    public void Configure_EnforcesMinimumThresholdOf1()
    {
        var cb = new CircuitBreaker();
        cb.Configure(threshold: 0);

        cb.RecordFailure(NodeA);
        Assert.True(cb.IsOpen(NodeA)); // threshold clamped to 1
    }

    [Fact]
    public void Configure_EnforcesMinimumTtlOf1Second()
    {
        var cb = new CircuitBreaker();
        cb.Configure(threshold: 1, ttl: TimeSpan.FromMilliseconds(1));

        cb.RecordFailure(NodeA);
        Assert.True(cb.IsOpen(NodeA));

        // TTL should be clamped to 1 second, so still open after 100ms
        Thread.Sleep(100);
        Assert.True(cb.IsOpen(NodeA));
    }

    // ─── GetStatus — Empty ───

    [Fact]
    public void GetStatus_ReturnsEmptyDict_Initially()
    {
        var cb = new CircuitBreaker();

        var status = cb.GetStatus();

        Assert.NotNull(status);
        Assert.Empty(status);
    }

    // ─── GetStatus — After Failures ───

    [Fact]
    public void GetStatus_ReturnsCorrectData_AfterFailures()
    {
        var cb = new CircuitBreaker();

        cb.RecordFailure(NodeA);
        cb.RecordFailure(NodeA);
        cb.RecordFailure(NodeA);
        cb.RecordFailure(NodeB);

        var status = cb.GetStatus();

        Assert.Equal(2, status.Count);
        Assert.True(status.ContainsKey(NodeA));
        Assert.True(status.ContainsKey(NodeB));

        Assert.Equal(3, status[NodeA].FailureCount);
        Assert.True(status[NodeA].IsOpen);

        Assert.Equal(1, status[NodeB].FailureCount);
        Assert.False(status[NodeB].IsOpen);
    }

    // ─── GetStatus Single Node — Null for Unknown ───

    [Fact]
    public void GetStatus_SingleNode_ReturnsNull_ForUnknownNode()
    {
        var cb = new CircuitBreaker();

        Assert.Null(cb.GetStatus(NodeA));
    }

    // ─── GetStatus Single Node — Returns Data ───

    [Fact]
    public void GetStatus_SingleNode_ReturnsStatus_AfterFailure()
    {
        var cb = new CircuitBreaker();

        cb.RecordFailure(NodeA);
        var status = cb.GetStatus(NodeA);

        Assert.NotNull(status);
        Assert.Equal(1, status!.FailureCount);
        Assert.False(status.IsOpen); // 1 < 3 threshold
        Assert.True(status.LastFailure > DateTime.MinValue);
    }

    // ─── Thread Safety ───

    [Fact]
    public void RecordFailure_IsThreadSafe_ConcurrentCalls()
    {
        var cb = new CircuitBreaker();
        cb.Configure(threshold: 1000); // High threshold so we can count accurately

        const int threadCount = 10;
        const int failuresPerThread = 100;
        var barrier = new Barrier(threadCount);

        var threads = Enumerable.Range(0, threadCount).Select(_ => new Thread(() =>
        {
            barrier.SignalAndWait();
            for (var i = 0; i < failuresPerThread; i++)
            {
                cb.RecordFailure(NodeA);
            }
        })).ToArray();

        foreach (var t in threads) t.Start();
        foreach (var t in threads) t.Join();

        var status = cb.GetStatus(NodeA);
        Assert.NotNull(status);
        Assert.Equal(threadCount * failuresPerThread, status!.FailureCount);
    }

    // ─── Argument Validation ───

    [Fact]
    public void RecordFailure_ThrowsOnNullOrEmpty()
    {
        var cb = new CircuitBreaker();

        Assert.Throws<ArgumentException>(() => cb.RecordFailure(""));
        Assert.Throws<ArgumentNullException>(() => cb.RecordFailure(null!));
    }

    [Fact]
    public void IsOpen_ThrowsOnNullOrEmpty()
    {
        var cb = new CircuitBreaker();

        Assert.Throws<ArgumentException>(() => cb.IsOpen(""));
        Assert.Throws<ArgumentNullException>(() => cb.IsOpen(null!));
    }

    [Fact]
    public void GetStatus_SingleNode_ThrowsOnNullOrEmpty()
    {
        var cb = new CircuitBreaker();

        Assert.Throws<ArgumentException>(() => cb.GetStatus(""));
        Assert.Throws<ArgumentNullException>(() => cb.GetStatus(null!));
    }

    // ─── TTL Auto-Purge ───

    [Fact]
    public void IsOpen_AutoPurgesEntry_WhenTtlExpires()
    {
        var cb = new CircuitBreaker();
        cb.Configure(threshold: 1, ttl: TimeSpan.FromSeconds(1));

        cb.RecordFailure(NodeA);
        Assert.True(cb.IsOpen(NodeA));
        Assert.NotNull(cb.GetStatus(NodeA));

        Thread.Sleep(1200);

        // IsOpen should auto-purge the expired entry
        Assert.False(cb.IsOpen(NodeA));

        // After auto-purge, the entry should be gone from the dictionary
        var allStatus = cb.GetStatus();
        Assert.DoesNotContain(NodeA, (IDictionary<string, CircuitBreakerStatus>)allStatus);
    }

    // ─── CircuitBreakerStatus Record ───

    [Fact]
    public void CircuitBreakerStatus_IsRecord_WithExpectedProperties()
    {
        var status = new CircuitBreakerStatus(5, DateTime.UtcNow, true);

        Assert.Equal(5, status.FailureCount);
        Assert.True(status.IsOpen);
    }

    // ─── Below Threshold Not Open ───

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void IsOpen_ReturnsFalse_WhenBelowThreshold(int failureCount)
    {
        var cb = new CircuitBreaker();

        for (var i = 0; i < failureCount; i++)
            cb.RecordFailure(NodeA);

        Assert.False(cb.IsOpen(NodeA));
    }
}
