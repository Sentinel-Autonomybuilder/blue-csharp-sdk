using Sentinel.SDK.Core;
using Xunit;

namespace Sentinel.SDK.Tests;

/// <summary>
/// Extended constants tests. Basic tests live in WalletTests.cs.
/// These cover the remaining constant values and edge cases.
/// </summary>
public class ExtendedConstantsTests
{
    [Fact]
    public void ChainId_IsSentinelhub2()
    {
        Assert.Equal("sentinelhub-2", Constants.ChainId);
    }

    [Fact]
    public void Denom_IsUdvpn()
    {
        Assert.Equal("udvpn", Constants.Denom);
    }

    [Fact]
    public void GasPrice_IsPointTwo()
    {
        Assert.Equal("0.2", Constants.GasPrice);
    }

    [Fact]
    public void BechPrefix_IsSent()
    {
        Assert.Equal("sent", Constants.BechPrefix);
    }

    [Fact]
    public void NodePrefix_IsSentnode()
    {
        Assert.Equal("sentnode", Constants.NodePrefix);
    }

    [Fact]
    public void ProviderPrefix_IsSentprov()
    {
        Assert.Equal("sentprov", Constants.ProviderPrefix);
    }

    [Fact]
    public void DefaultLcdUrls_HasFourEntries()
    {
        Assert.Equal(4, Constants.DefaultLcdUrls.Length);
    }

    [Fact]
    public void DefaultLcdUrls_AllStartWithHttps()
    {
        Assert.All(Constants.DefaultLcdUrls, url => Assert.StartsWith("https://", url));
    }

    [Fact]
    public void DefaultRpcUrls_HasAtLeastThreeEntries()
    {
        Assert.True(Constants.DefaultRpcUrls.Length >= 3);
    }

    [Fact]
    public void DefaultRpcUrls_AllStartWithHttps()
    {
        Assert.All(Constants.DefaultRpcUrls, url => Assert.StartsWith("https://", url));
    }

    [Fact]
    public void DefaultLcdUrls_ContainsSentinelCo()
    {
        Assert.Contains(Constants.DefaultLcdUrls, url => url.Contains("sentinel.co"));
    }

    [Fact]
    public void DefaultRpcUrls_ContainsSentinelCo()
    {
        Assert.Contains(Constants.DefaultRpcUrls, url => url.Contains("sentinel.co"));
    }

    [Fact]
    public void DefaultLcdUrls_NoDuplicates()
    {
        var distinct = Constants.DefaultLcdUrls.Distinct().Count();
        Assert.Equal(Constants.DefaultLcdUrls.Length, distinct);
    }

    [Fact]
    public void DefaultRpcUrls_NoDuplicates()
    {
        var distinct = Constants.DefaultRpcUrls.Distinct().Count();
        Assert.Equal(Constants.DefaultRpcUrls.Length, distinct);
    }
}
