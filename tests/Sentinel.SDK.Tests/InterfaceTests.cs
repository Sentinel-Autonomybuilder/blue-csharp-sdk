using Sentinel.SDK.Core;
using Xunit;

namespace Sentinel.SDK.Tests;

public class InterfaceTests
{
    // ─── ISentinelWallet ───

    [Fact]
    public void SentinelWallet_Implements_ISentinelWallet()
    {
        var wallet = SentinelWallet.Generate();
        Assert.IsAssignableFrom<ISentinelWallet>(wallet);
    }

    [Fact]
    public void ISentinelWallet_HasAddress()
    {
        ISentinelWallet wallet = SentinelWallet.Generate();
        Assert.NotNull(wallet.Address);
        Assert.StartsWith("sent1", wallet.Address);
    }

    [Fact]
    public void ISentinelWallet_HasSign()
    {
        ISentinelWallet wallet = SentinelWallet.Generate();
        var hash = new byte[32];
        var sig = wallet.Sign(hash);
        Assert.NotNull(sig);
        Assert.Equal(64, sig.Length);
    }

    [Fact]
    public void ISentinelWallet_HasGetPublicKeyCompressed()
    {
        ISentinelWallet wallet = SentinelWallet.Generate();
        var pubKey = wallet.GetPublicKeyCompressed();
        Assert.Equal(33, pubKey.Length);
    }

    // ─── IChainClient ───

    [Fact]
    public void ChainClient_Implements_IChainClient()
    {
        using var client = new ChainClient();
        Assert.IsAssignableFrom<IChainClient>(client);
    }

    [Fact]
    public void ChainClient_Implements_IDisposable()
    {
        using var client = new ChainClient();
        Assert.IsAssignableFrom<IDisposable>(client);
    }

    // ─── IChainClient Method Signatures ───

    [Fact]
    public void IChainClient_GetBalanceAsync_Exists()
    {
        var method = typeof(IChainClient).GetMethod("GetBalanceAsync");
        Assert.NotNull(method);
        Assert.Equal(typeof(Task<Balance>), method.ReturnType);
    }

    [Fact]
    public void IChainClient_GetActiveNodesAsync_Exists()
    {
        var method = typeof(IChainClient).GetMethod("GetActiveNodesAsync");
        Assert.NotNull(method);
    }

    [Fact]
    public void IChainClient_GetNodeAsync_Exists()
    {
        var method = typeof(IChainClient).GetMethod("GetNodeAsync");
        Assert.NotNull(method);
    }

    [Fact]
    public void IChainClient_GetSubscriptionsAsync_Exists()
    {
        var method = typeof(IChainClient).GetMethod("GetSubscriptionsAsync");
        Assert.NotNull(method);
    }

    [Fact]
    public void IChainClient_GetSessionsAsync_Exists()
    {
        var method = typeof(IChainClient).GetMethod("GetSessionsAsync");
        Assert.NotNull(method);
    }

    [Fact]
    public void IChainClient_GetPlanNodesAsync_Exists()
    {
        var method = typeof(IChainClient).GetMethod("GetPlanNodesAsync");
        Assert.NotNull(method);
    }

    [Fact]
    public void IChainClient_DiscoverPlansAsync_Exists()
    {
        var method = typeof(IChainClient).GetMethod("DiscoverPlansAsync");
        Assert.NotNull(method);
    }

    [Fact]
    public void IChainClient_QueryFeeGrantsAsync_Exists()
    {
        var method = typeof(IChainClient).GetMethod("QueryFeeGrantsAsync");
        Assert.NotNull(method);
    }

    [Fact]
    public void IChainClient_QueryActiveSessionsForAddressAsync_Exists()
    {
        var method = typeof(IChainClient).GetMethod("QueryActiveSessionsForAddressAsync");
        Assert.NotNull(method);
    }

    [Fact]
    public void IChainClient_QuerySessionAllocationAsync_Exists()
    {
        var method = typeof(IChainClient).GetMethod("QuerySessionAllocationAsync");
        Assert.NotNull(method);
    }

    [Fact]
    public void IChainClient_GetAvailableNodesAsync_Exists()
    {
        var method = typeof(IChainClient).GetMethod("GetAvailableNodesAsync");
        Assert.NotNull(method);
    }

    [Fact]
    public void IChainClient_CheckEndpointHealthAsync_Exists()
    {
        var method = typeof(IChainClient).GetMethod("CheckEndpointHealthAsync");
        Assert.NotNull(method);
    }

    // ─── SentinelWallet IDisposable ───

    [Fact]
    public void SentinelWallet_Implements_IDisposable()
    {
        var wallet = SentinelWallet.Generate();
        Assert.IsAssignableFrom<IDisposable>(wallet);
        wallet.Dispose();
    }
}
