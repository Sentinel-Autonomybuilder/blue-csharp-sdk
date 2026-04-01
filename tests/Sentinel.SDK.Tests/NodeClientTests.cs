using Sentinel.SDK.Core;
using Sentinel.SDK.Node;
using Xunit;

namespace Sentinel.SDK.Tests;

public class NodeClientTests
{
    // ─── NodeStatus Record ───

    [Fact]
    public void NodeStatus_CreatesWithAllFields()
    {
        var location = new Location("Frankfurt", "Germany", "DE", 50.1109, 8.6821);
        var bandwidth = new Bandwidth(1_000_000, 2_000_000);

        var status = new NodeStatus(
            Address: "sentnode1test",
            Type: "wireguard",
            Moniker: "TestNode",
            Peers: 5,
            Location: location,
            Bandwidth: bandwidth,
            ClockDriftSec: 1.5
        );

        Assert.Equal("sentnode1test", status.Address);
        Assert.Equal("wireguard", status.Type);
        Assert.Equal("TestNode", status.Moniker);
        Assert.Equal(5, status.Peers);
        Assert.Equal("Frankfurt", status.Location.City);
        Assert.Equal("Germany", status.Location.Country);
        Assert.Equal("DE", status.Location.CountryCode);
        Assert.Equal(50.1109, status.Location.Latitude);
        Assert.Equal(8.6821, status.Location.Longitude);
        Assert.Equal(1_000_000, status.Bandwidth.Upload);
        Assert.Equal(2_000_000, status.Bandwidth.Download);
        Assert.Equal(1.5, status.ClockDriftSec);
    }

    [Fact]
    public void NodeStatus_ClockDrift_CanBeNull()
    {
        var status = new NodeStatus(
            null, "v2ray", "Node2", 0,
            new Location("", "", "", 0, 0),
            new Bandwidth(0, 0),
            null
        );

        Assert.Null(status.ClockDriftSec);
    }

    // ─── Location Record ───

    [Fact]
    public void Location_CreatesWithAllFields()
    {
        var loc = new Location("Berlin", "Germany", "DE", 52.52, 13.405);

        Assert.Equal("Berlin", loc.City);
        Assert.Equal("Germany", loc.Country);
        Assert.Equal("DE", loc.CountryCode);
        Assert.Equal(52.52, loc.Latitude);
        Assert.Equal(13.405, loc.Longitude);
    }

    [Fact]
    public void Location_SupportsValueEquality()
    {
        var loc1 = new Location("Berlin", "Germany", "DE", 52.52, 13.405);
        var loc2 = new Location("Berlin", "Germany", "DE", 52.52, 13.405);

        Assert.Equal(loc1, loc2);
    }

    // ─── Bandwidth Record ───

    [Fact]
    public void Bandwidth_CreatesWithAllFields()
    {
        var bw = new Bandwidth(Upload: 50_000_000, Download: 100_000_000);

        Assert.Equal(50_000_000, bw.Upload);
        Assert.Equal(100_000_000, bw.Download);
    }

    [Fact]
    public void Bandwidth_SupportsValueEquality()
    {
        var bw1 = new Bandwidth(100, 200);
        var bw2 = new Bandwidth(100, 200);

        Assert.Equal(bw1, bw2);
    }

    // ─── SentinelNodeException ───

    [Fact]
    public void SentinelNodeException_HasCodeProperty()
    {
        var ex = new SentinelNodeException("Test error");

        Assert.NotNull(ex.Code);
        Assert.Equal(ErrorCodes.NodeOffline, ex.Code);
    }

    [Fact]
    public void SentinelNodeException_InheritsFromNodeException()
    {
        var ex = new SentinelNodeException("Test error");

        Assert.IsAssignableFrom<NodeException>(ex);
    }

    [Fact]
    public void SentinelNodeException_InheritsFromSentinelException()
    {
        var ex = new SentinelNodeException("Test error");

        Assert.IsAssignableFrom<SentinelException>(ex);
    }

    [Fact]
    public void SentinelNodeException_InheritsFromException()
    {
        var ex = new SentinelNodeException("Test error");

        Assert.IsAssignableFrom<Exception>(ex);
    }

    [Fact]
    public void SentinelNodeException_MessageConstructor_SetsMessage()
    {
        var ex = new SentinelNodeException("Node unreachable");

        Assert.Equal("Node unreachable", ex.Message);
    }

    [Fact]
    public void SentinelNodeException_WithInnerException_Chains()
    {
        var inner = new TimeoutException("timeout");
        var ex = new SentinelNodeException("Failed to connect", inner);

        Assert.Equal("Failed to connect", ex.Message);
        Assert.Same(inner, ex.InnerException);
        Assert.Equal(ErrorCodes.NodeOffline, ex.Code);
    }

    [Fact]
    public void SentinelNodeException_CodeConstructor_SetsCustomCode()
    {
        var ex = new SentinelNodeException("CUSTOM_CODE", "custom message");

        Assert.Equal("CUSTOM_CODE", ex.Code);
        Assert.Equal("custom message", ex.Message);
    }

    // ─── NodeClient Static Class ───

    [Fact]
    public void NodeClient_IsStaticClass()
    {
        Assert.True(typeof(NodeClient).IsAbstract && typeof(NodeClient).IsSealed);
    }

    [Fact]
    public async Task GetStatusAsync_ThrowsOnNullUrl()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => NodeClient.GetStatusAsync(null!)
        );
    }
}
