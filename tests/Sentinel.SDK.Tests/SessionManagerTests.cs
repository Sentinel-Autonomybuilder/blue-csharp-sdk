using Sentinel.SDK.Core;
using Sentinel.SDK.Node;
using Xunit;

namespace Sentinel.SDK.Tests;

/// <summary>
/// Tests for SessionManager types and exception classes.
/// Cannot test FindExistingSessionAsync or GetSessionAllocationAsync
/// without a live chain connection — focuses on records and error classes.
/// </summary>
public class SessionManagerTests
{
    // ─── SessionAllocation Record ───

    [Fact]
    public void SessionAllocation_Creation_AllFields()
    {
        var allocation = new SessionAllocation(
            MaxBytes: 1_000_000_000L,
            UsedBytes: 250_000_000L,
            RemainingBytes: 750_000_000L,
            PercentUsed: 25
        );

        Assert.Equal(1_000_000_000L, allocation.MaxBytes);
        Assert.Equal(250_000_000L, allocation.UsedBytes);
        Assert.Equal(750_000_000L, allocation.RemainingBytes);
        Assert.Equal(25, allocation.PercentUsed);
    }

    [Fact]
    public void SessionAllocation_PercentUsed_ZeroWhenNoUsage()
    {
        var allocation = new SessionAllocation(
            MaxBytes: 1_000_000_000L,
            UsedBytes: 0L,
            RemainingBytes: 1_000_000_000L,
            PercentUsed: 0
        );

        Assert.Equal(0, allocation.PercentUsed);
        Assert.Equal(allocation.MaxBytes, allocation.RemainingBytes);
    }

    [Fact]
    public void SessionAllocation_PercentUsed_100WhenFullyUsed()
    {
        var allocation = new SessionAllocation(
            MaxBytes: 500_000_000L,
            UsedBytes: 500_000_000L,
            RemainingBytes: 0L,
            PercentUsed: 100
        );

        Assert.Equal(100, allocation.PercentUsed);
        Assert.Equal(0L, allocation.RemainingBytes);
    }

    [Fact]
    public void SessionAllocation_PercentUsed_Halfway()
    {
        var allocation = new SessionAllocation(
            MaxBytes: 2_000_000_000L,
            UsedBytes: 1_000_000_000L,
            RemainingBytes: 1_000_000_000L,
            PercentUsed: 50
        );

        Assert.Equal(50, allocation.PercentUsed);
    }

    [Theory]
    [InlineData(1_000_000L, 100_000L, 900_000L, 10)]
    [InlineData(1_000_000L, 750_000L, 250_000L, 75)]
    [InlineData(1_000_000L, 999_000L, 1_000L, 99)]
    [InlineData(10_000_000L, 0L, 10_000_000L, 0)]
    public void SessionAllocation_VariousUsageLevels(
        long maxBytes, long usedBytes, long remainingBytes, int percentUsed)
    {
        var allocation = new SessionAllocation(
            maxBytes, usedBytes, remainingBytes, percentUsed);

        Assert.Equal(maxBytes, allocation.MaxBytes);
        Assert.Equal(usedBytes, allocation.UsedBytes);
        Assert.Equal(remainingBytes, allocation.RemainingBytes);
        Assert.Equal(percentUsed, allocation.PercentUsed);
    }

    // ─── SessionAllocation Record Equality ───

    [Fact]
    public void SessionAllocation_RecordEquality()
    {
        var a = new SessionAllocation(1000L, 500L, 500L, 50);
        var b = new SessionAllocation(1000L, 500L, 500L, 50);

        Assert.Equal(a, b);
    }

    [Fact]
    public void SessionAllocation_RecordInequality()
    {
        var a = new SessionAllocation(1000L, 500L, 500L, 50);
        var b = new SessionAllocation(1000L, 600L, 400L, 60);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void SessionAllocation_WithExpression()
    {
        var original = new SessionAllocation(1000L, 0L, 1000L, 0);
        var updated = original with { UsedBytes = 200L, RemainingBytes = 800L, PercentUsed = 20 };

        Assert.Equal(0L, original.UsedBytes);
        Assert.Equal(200L, updated.UsedBytes);
        Assert.Equal(800L, updated.RemainingBytes);
        Assert.Equal(20, updated.PercentUsed);
    }

    // ─── SentinelSessionException ───

    [Fact]
    public void SentinelSessionException_HasCodeProperty()
    {
        var ex = new SentinelSessionException("session failed");

        Assert.Equal(ErrorCodes.BroadcastFailed, ex.Code);
        Assert.Equal("session failed", ex.Message);
    }

    [Fact]
    public void SentinelSessionException_InheritsFromSentinelException()
    {
        var ex = new SentinelSessionException("test");

        Assert.IsAssignableFrom<SentinelException>(ex);
    }

    [Fact]
    public void SentinelSessionException_InheritsFromChainException()
    {
        var ex = new SentinelSessionException("test");

        Assert.IsAssignableFrom<ChainException>(ex);
    }

    [Fact]
    public void SentinelSessionException_WithInnerException()
    {
        var inner = new HttpRequestException("network error");
        var ex = new SentinelSessionException("query failed", inner);

        Assert.Equal(ErrorCodes.BroadcastFailed, ex.Code);
        Assert.Same(inner, ex.InnerException);
        Assert.Contains("query failed", ex.Message);
    }

    [Fact]
    public void SentinelSessionException_WithCustomCode()
    {
        var ex = new SentinelSessionException("CUSTOM_SESSION_ERROR", "custom error message");

        Assert.Equal("CUSTOM_SESSION_ERROR", ex.Code);
        Assert.Equal("custom error message", ex.Message);
    }

    [Fact]
    public void SentinelSessionException_DefaultCode_IsBroadcastFailed()
    {
        var ex = new SentinelSessionException("any message");

        Assert.Equal("BROADCAST_FAILED", ex.Code);
    }

    // ─── ChainException Base (used by SentinelSessionException) ───

    [Fact]
    public void ChainException_HasCodeProperty()
    {
        var ex = new ChainException("CHAIN_ERROR", "chain failed");

        Assert.Equal("CHAIN_ERROR", ex.Code);
        Assert.Equal("chain failed", ex.Message);
    }

    [Fact]
    public void ChainException_InheritsFromSentinelException()
    {
        var ex = new ChainException("CODE", "msg");

        Assert.IsAssignableFrom<SentinelException>(ex);
    }

    // ─── ActiveSession Record (used by SessionManager internally) ───

    [Fact]
    public void ActiveSession_Creation()
    {
        var session = new ActiveSession(
            Id: 12345UL,
            NodeAddress: "sentnode1abcdefghijklmnopqrstuvwxyz012345678",
            Status: SessionStatus.Active
        );

        Assert.Equal(12345UL, session.Id);
        Assert.Equal("sentnode1abcdefghijklmnopqrstuvwxyz012345678", session.NodeAddress);
        Assert.Equal(SessionStatus.Active, session.Status);
    }

    [Fact]
    public void SessionStatus_HasActiveAndInactive()
    {
        Assert.True(Enum.IsDefined(SessionStatus.Active));
        Assert.True(Enum.IsDefined(SessionStatus.Inactive));
        Assert.NotEqual(SessionStatus.Active, SessionStatus.Inactive);
    }

    // ─── RawSessionAllocation Record (chain query return type) ───

    [Fact]
    public void RawSessionAllocation_Creation()
    {
        var raw = new RawSessionAllocation(
            MaxBytes: 5_000_000_000L,
            UsedBytes: 1_234_567_890L
        );

        Assert.Equal(5_000_000_000L, raw.MaxBytes);
        Assert.Equal(1_234_567_890L, raw.UsedBytes);
    }
}
