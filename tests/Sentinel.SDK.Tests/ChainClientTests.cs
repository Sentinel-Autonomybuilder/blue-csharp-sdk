using Sentinel.SDK.Core;
using Xunit;

namespace Sentinel.SDK.Tests;

public class ChainClientTests
{
    // ─── Constructor ───

    [Fact]
    public void Constructor_DefaultUrls_DoesNotThrow()
    {
        using var client = new ChainClient();
        Assert.NotNull(client);
    }

    [Fact]
    public void Constructor_CustomUrls_Accepted()
    {
        using var client = new ChainClient(
            lcdUrls: new[] { "https://lcd.example.com" },
            rpcUrls: new[] { "https://rpc.example.com" }
        );
        Assert.NotNull(client);
    }

    [Fact]
    public void Constructor_NullUrls_UsesDefaults()
    {
        // null arrays should fall back to Constants defaults without throwing
        using var client = new ChainClient(null, null);
        Assert.NotNull(client);
    }

    [Fact]
    public void Constructor_EmptyLcdUrls_Throws()
    {
        var ex = Assert.Throws<SentinelException>(
            () => new ChainClient(lcdUrls: Array.Empty<string>())
        );
        Assert.Equal("CLIENT_NO_LCD", ex.Code);
    }

    [Fact]
    public void Constructor_EmptyRpcUrls_Throws()
    {
        var ex = Assert.Throws<SentinelException>(
            () => new ChainClient(
                lcdUrls: new[] { "https://lcd.example.com" },
                rpcUrls: Array.Empty<string>()
            )
        );
        Assert.Equal("CLIENT_NO_RPC", ex.Code);
    }

    // ─── Interface Compliance ───

    [Fact]
    public void Implements_IChainClient()
    {
        using var client = new ChainClient();
        Assert.IsAssignableFrom<IChainClient>(client);
    }

    [Fact]
    public void Implements_IDisposable()
    {
        using var client = new ChainClient();
        Assert.IsAssignableFrom<IDisposable>(client);
    }

    // ─── EstimateSessionCost (static) ───

    [Fact]
    public void EstimateSessionCost_WithUdvpnPrice_ReturnsCorrectBreakdown()
    {
        var node = new ChainNode(
            Address: "sentnode1abc",
            RemoteAddrs: new[] { "https://1.2.3.4:8585" },
            RemoteUrl: "https://1.2.3.4:8585",
            GigabytePrices: new[] { new PriceEntry("udvpn", "1000000", "1000000") },
            HourlyPrices: Array.Empty<PriceEntry>(),
            Status: 1
        );

        var cost = ChainClient.EstimateSessionCost(node, gigabytes: 2);

        Assert.Equal(2_000_000L, cost.Udvpn);
        Assert.Equal(2.0m, cost.P2P);
        Assert.Equal(200_000L, cost.GasUdvpn);
        Assert.Equal(2_200_000L, cost.TotalUdvpn);
    }

    [Fact]
    public void EstimateSessionCost_NoPriceEntry_ReturnsZeroCost()
    {
        var node = new ChainNode(
            Address: "sentnode1abc",
            RemoteAddrs: Array.Empty<string>(),
            RemoteUrl: null,
            GigabytePrices: Array.Empty<PriceEntry>(),
            HourlyPrices: Array.Empty<PriceEntry>(),
            Status: 1
        );

        var cost = ChainClient.EstimateSessionCost(node);

        Assert.Equal(0L, cost.Udvpn);
        Assert.Equal(0m, cost.P2P);
        Assert.Equal(200_000L, cost.GasUdvpn);
        Assert.Equal(200_000L, cost.TotalUdvpn);
    }

    [Fact]
    public void EstimateSessionCost_NullNode_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() => ChainClient.EstimateSessionCost(null!));
    }

    [Fact]
    public void EstimateSessionCost_DefaultGigabytes_IsOne()
    {
        var node = new ChainNode(
            Address: "sentnode1abc",
            RemoteAddrs: Array.Empty<string>(),
            RemoteUrl: null,
            GigabytePrices: new[] { new PriceEntry("udvpn", "500000", "500000") },
            HourlyPrices: Array.Empty<PriceEntry>(),
            Status: 1
        );

        var cost = ChainClient.EstimateSessionCost(node);

        Assert.Equal(500_000L, cost.Udvpn);
    }

    [Fact]
    public void EstimateSessionCost_IgnoresNonUdvpnPrices()
    {
        var node = new ChainNode(
            Address: "sentnode1abc",
            RemoteAddrs: Array.Empty<string>(),
            RemoteUrl: null,
            GigabytePrices: new[] { new PriceEntry("uatom", "999999", "999999") },
            HourlyPrices: Array.Empty<PriceEntry>(),
            Status: 1
        );

        var cost = ChainClient.EstimateSessionCost(node);

        Assert.Equal(0L, cost.Udvpn);
    }

    [Fact]
    public void EstimateSessionCost_UsesQuoteValueOverBaseValue()
    {
        var node = new ChainNode(
            Address: "sentnode1abc",
            RemoteAddrs: Array.Empty<string>(),
            RemoteUrl: null,
            GigabytePrices: new[] { new PriceEntry("udvpn", "100", "200") },
            HourlyPrices: Array.Empty<PriceEntry>(),
            Status: 1
        );

        var cost = ChainClient.EstimateSessionCost(node, gigabytes: 1);

        // QuoteValue (200) should be preferred over BaseValue (100)
        Assert.Equal(200L, cost.Udvpn);
    }

    // ─── EstimateBatchFee (static) ───

    [Fact]
    public void EstimateBatchFee_StartSession_200kGasPerMsg()
    {
        var fee = ChainClient.EstimateBatchFee(1, "startSession");

        Assert.Equal(200_000L, fee.Gas);
        Assert.Equal(40_000L, fee.Amount); // 200_000 * 0.2
    }

    [Fact]
    public void EstimateBatchFee_FeeGrant_150kGasPerMsg()
    {
        var fee = ChainClient.EstimateBatchFee(1, "feeGrant");

        Assert.Equal(150_000L, fee.Gas);
        Assert.Equal(30_000L, fee.Amount); // 150_000 * 0.2
    }

    [Fact]
    public void EstimateBatchFee_Send_80kGasPerMsg()
    {
        var fee = ChainClient.EstimateBatchFee(1, "send");

        Assert.Equal(80_000L, fee.Gas);
        Assert.Equal(16_000L, fee.Amount); // 80_000 * 0.2
    }

    [Fact]
    public void EstimateBatchFee_Link_150kGasPerMsg()
    {
        var fee = ChainClient.EstimateBatchFee(1, "link");

        Assert.Equal(150_000L, fee.Gas);
        Assert.Equal(30_000L, fee.Amount); // 150_000 * 0.2
    }

    [Fact]
    public void EstimateBatchFee_DefaultMsgType_IsStartSession()
    {
        var fee = ChainClient.EstimateBatchFee(1);

        Assert.Equal(200_000L, fee.Gas);
    }

    [Fact]
    public void EstimateBatchFee_UnknownMsgType_DefaultsTo200k()
    {
        var fee = ChainClient.EstimateBatchFee(1, "unknown");

        Assert.Equal(200_000L, fee.Gas);
    }

    [Fact]
    public void EstimateBatchFee_MultipleMsgs_ScalesLinearly()
    {
        var fee = ChainClient.EstimateBatchFee(5, "startSession");

        Assert.Equal(1_000_000L, fee.Gas);       // 5 * 200_000
        Assert.Equal(200_000L, fee.Amount);       // 1_000_000 * 0.2
    }

    [Fact]
    public void EstimateBatchFee_Amount_IsGasTimesPointTwo()
    {
        var fee = ChainClient.EstimateBatchFee(3, "send");

        Assert.Equal(240_000L, fee.Gas);       // 3 * 80_000
        Assert.Equal(48_000L, fee.Amount);     // 240_000 * 0.2
    }

    [Fact]
    public void EstimateBatchFee_StringFields_MatchNumericFields()
    {
        var fee = ChainClient.EstimateBatchFee(2, "feeGrant");

        Assert.Equal(fee.Gas.ToString(), fee.GasString);
        Assert.Equal(fee.Amount.ToString(), fee.AmountString);
    }

    // ─── Dispose ───

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        var client = new ChainClient();
        client.Dispose();
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes()
    {
        var client = new ChainClient();
        client.Dispose();
        client.Dispose(); // Second dispose should not throw
    }

    // ─── Async Method Existence (interface compliance) ───

    [Fact]
    public void GetBalanceAsync_IsDefined()
    {
        using var client = new ChainClient();
        var method = typeof(ChainClient).GetMethod("GetBalanceAsync");
        Assert.NotNull(method);
    }

    [Fact]
    public void GetActiveNodesAsync_IsDefined()
    {
        using var client = new ChainClient();
        var method = typeof(ChainClient).GetMethod("GetActiveNodesAsync");
        Assert.NotNull(method);
    }

    [Fact]
    public void GetSubscriptionsAsync_IsDefined()
    {
        using var client = new ChainClient();
        var method = typeof(ChainClient).GetMethod("GetSubscriptionsAsync");
        Assert.NotNull(method);
    }

    [Fact]
    public void GetSessionsAsync_IsDefined()
    {
        using var client = new ChainClient();
        var method = typeof(ChainClient).GetMethod("GetSessionsAsync");
        Assert.NotNull(method);
    }

    [Fact]
    public void GetNodeAsync_IsDefined()
    {
        using var client = new ChainClient();
        var method = typeof(ChainClient).GetMethod("GetNodeAsync");
        Assert.NotNull(method);
    }

    [Fact]
    public void CheckEndpointHealthAsync_IsDefined()
    {
        using var client = new ChainClient();
        var method = typeof(ChainClient).GetMethod("CheckEndpointHealthAsync");
        Assert.NotNull(method);
    }
}
