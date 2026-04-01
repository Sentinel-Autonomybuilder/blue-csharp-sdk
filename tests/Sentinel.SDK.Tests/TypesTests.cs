using Sentinel.SDK.Core;
using Xunit;

namespace Sentinel.SDK.Tests;

/// <summary>
/// Tests for Types/records defined in Types.cs — verifies record creation,
/// equality, and property access for all domain types.
/// </summary>
public class TypesTests
{
    // ─── PriceEntry ───

    [Fact]
    public void PriceEntry_Creation()
    {
        var price = new PriceEntry("udvpn", "1000000", "1000000");

        Assert.Equal("udvpn", price.Denom);
        Assert.Equal("1000000", price.BaseValue);
        Assert.Equal("1000000", price.QuoteValue);
    }

    [Fact]
    public void PriceEntry_RecordEquality()
    {
        var a = new PriceEntry("udvpn", "100", "100");
        var b = new PriceEntry("udvpn", "100", "100");

        Assert.Equal(a, b);
    }

    [Fact]
    public void PriceEntry_RecordInequality_DifferentDenom()
    {
        var a = new PriceEntry("udvpn", "100", "100");
        var b = new PriceEntry("uatom", "100", "100");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void PriceEntry_RecordInequality_DifferentValue()
    {
        var a = new PriceEntry("udvpn", "100", "100");
        var b = new PriceEntry("udvpn", "200", "200");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void PriceEntry_WithExpression()
    {
        var original = new PriceEntry("udvpn", "100", "100");
        var modified = original with { BaseValue = "200" };

        Assert.Equal("200", modified.BaseValue);
        Assert.Equal("udvpn", modified.Denom);
        Assert.Equal("100", modified.QuoteValue);
    }

    [Fact]
    public void PriceEntry_ToString_ContainsValues()
    {
        var price = new PriceEntry("udvpn", "500", "500");
        var str = price.ToString();

        Assert.Contains("udvpn", str);
        Assert.Contains("500", str);
    }

    // ─── ChainNode ───

    [Fact]
    public void ChainNode_Creation()
    {
        var prices = new[] { new PriceEntry("udvpn", "1000000", "1000000") };
        var node = new ChainNode(
            Address: "sentnode1abc123",
            RemoteAddrs: new[] { "https://1.2.3.4:8585" },
            RemoteUrl: "https://1.2.3.4:8585",
            GigabytePrices: prices,
            HourlyPrices: prices,
            Status: 1
        );

        Assert.Equal("sentnode1abc123", node.Address);
        Assert.Single(node.RemoteAddrs);
        Assert.Equal("https://1.2.3.4:8585", node.RemoteUrl);
        Assert.Single(node.GigabytePrices);
        Assert.Single(node.HourlyPrices);
        Assert.Equal(1, node.Status);
    }

    [Fact]
    public void ChainNode_ActiveStatus()
    {
        var node = new ChainNode("sentnode1x", Array.Empty<string>(), null,
            Array.Empty<PriceEntry>(), Array.Empty<PriceEntry>(), 1);

        Assert.Equal(1, node.Status);
    }

    [Fact]
    public void ChainNode_InactiveStatus()
    {
        var node = new ChainNode("sentnode1x", Array.Empty<string>(), null,
            Array.Empty<PriceEntry>(), Array.Empty<PriceEntry>(), 2);

        Assert.Equal(2, node.Status);
    }

    [Fact]
    public void ChainNode_NullRemoteUrl()
    {
        var node = new ChainNode("sentnode1x", Array.Empty<string>(), null,
            Array.Empty<PriceEntry>(), Array.Empty<PriceEntry>(), 1);

        Assert.Null(node.RemoteUrl);
    }

    [Fact]
    public void ChainNode_EmptyPrices()
    {
        var node = new ChainNode("sentnode1x", Array.Empty<string>(), null,
            Array.Empty<PriceEntry>(), Array.Empty<PriceEntry>(), 1);

        Assert.Empty(node.GigabytePrices);
        Assert.Empty(node.HourlyPrices);
    }

    [Fact]
    public void ChainNode_MultiplePrices()
    {
        var gbPrices = new[]
        {
            new PriceEntry("udvpn", "100000", "100000"),
            new PriceEntry("uatom", "500", "500"),
        };
        var node = new ChainNode("sentnode1x", Array.Empty<string>(), null,
            gbPrices, Array.Empty<PriceEntry>(), 1);

        Assert.Equal(2, node.GigabytePrices.Length);
        Assert.Equal("udvpn", node.GigabytePrices[0].Denom);
        Assert.Equal("uatom", node.GigabytePrices[1].Denom);
    }

    [Fact]
    public void ChainNode_MultipleRemoteAddrs()
    {
        var addrs = new[] { "https://1.2.3.4:8585", "https://5.6.7.8:8585" };
        var node = new ChainNode("sentnode1x", addrs, addrs[0],
            Array.Empty<PriceEntry>(), Array.Empty<PriceEntry>(), 1);

        Assert.Equal(2, node.RemoteAddrs.Length);
    }

    // ─── Subscription ───

    [Fact]
    public void Subscription_Creation()
    {
        var price = new PriceEntry("udvpn", "1000000", "1000000");
        var sub = new Subscription(
            Id: "42",
            AccAddress: "sent1abc",
            PlanId: "0",
            Price: price,
            Status: "STATUS_ACTIVE",
            StartAt: "2026-01-01T00:00:00Z",
            InactiveAt: "2026-12-31T23:59:59Z"
        );

        Assert.Equal("42", sub.Id);
        Assert.Equal("sent1abc", sub.AccAddress);
        Assert.Equal("0", sub.PlanId);
        Assert.NotNull(sub.Price);
        Assert.Equal("STATUS_ACTIVE", sub.Status);
        Assert.Equal("2026-01-01T00:00:00Z", sub.StartAt);
        Assert.Equal("2026-12-31T23:59:59Z", sub.InactiveAt);
    }

    [Fact]
    public void Subscription_NullPrice_ForPlanBased()
    {
        var sub = new Subscription("1", "sent1x", "7", null, "STATUS_ACTIVE",
            "2026-01-01T00:00:00Z", "2026-12-31T23:59:59Z");

        Assert.Null(sub.Price);
        Assert.NotEqual("0", sub.PlanId);
    }

    [Fact]
    public void Subscription_RecordEquality()
    {
        var price = new PriceEntry("udvpn", "100", "100");
        var a = new Subscription("1", "sent1a", "0", price, "active", "t1", "t2");
        var b = new Subscription("1", "sent1a", "0", price, "active", "t1", "t2");

        Assert.Equal(a, b);
    }

    [Fact]
    public void Subscription_RecordInequality()
    {
        var a = new Subscription("1", "sent1a", "0", null, "active", "t1", "t2");
        var b = new Subscription("2", "sent1a", "0", null, "active", "t1", "t2");

        Assert.NotEqual(a, b);
    }

    // ─── ChainSession ───

    [Fact]
    public void ChainSession_Creation()
    {
        var session = new ChainSession(
            Id: "999",
            AccAddress: "sent1abc",
            NodeAddress: "sentnode1xyz",
            DownloadBytes: "1073741824",
            UploadBytes: "524288000",
            MaxBytes: "5368709120",
            Duration: "44.728960452s",
            MaxDuration: "0s",
            Status: "STATUS_ACTIVE",
            InactiveAt: "2026-03-23T11:51:45Z",
            StartAt: "2026-03-23T06:53:03Z"
        );

        Assert.Equal("999", session.Id);
        Assert.Equal("sent1abc", session.AccAddress);
        Assert.Equal("sentnode1xyz", session.NodeAddress);
        Assert.Equal("1073741824", session.DownloadBytes);
        Assert.Equal("524288000", session.UploadBytes);
        Assert.Equal("5368709120", session.MaxBytes);
        Assert.Equal("44.728960452s", session.Duration);
        Assert.Equal("0s", session.MaxDuration);
        Assert.Equal("STATUS_ACTIVE", session.Status);
        Assert.Equal("2026-03-23T11:51:45Z", session.InactiveAt);
        Assert.Equal("2026-03-23T06:53:03Z", session.StartAt);
    }

    [Fact]
    public void ChainSession_ZeroBandwidth()
    {
        var session = new ChainSession("1", "sent1a", "sentnode1b",
            "0", "0", "1073741824", null, null, "STATUS_ACTIVE", null, null);

        Assert.Equal("0", session.DownloadBytes);
        Assert.Equal("0", session.UploadBytes);
    }

    [Fact]
    public void ChainSession_RecordEquality()
    {
        var a = new ChainSession("1", "sent1a", "sentnode1b", "100", "50", "1000", null, null, "active", null, null);
        var b = new ChainSession("1", "sent1a", "sentnode1b", "100", "50", "1000", null, null, "active", null, null);

        Assert.Equal(a, b);
    }

    // ─── TxResult ───

    [Fact]
    public void TxResult_SuccessfulTransaction()
    {
        var result = new TxResult(
            TxHash: "ABCDEF1234567890ABCDEF1234567890ABCDEF1234567890ABCDEF1234567890",
            Code: 0,
            RawLog: "[]",
            Success: true
        );

        Assert.Equal(0, result.Code);
        Assert.True(result.Success);
        Assert.Equal(64, result.TxHash.Length);
    }

    [Fact]
    public void TxResult_FailedTransaction()
    {
        var result = new TxResult(
            TxHash: "1234567890ABCDEF1234567890ABCDEF1234567890ABCDEF1234567890ABCDEF",
            Code: 5,
            RawLog: "insufficient funds",
            Success: false
        );

        Assert.Equal(5, result.Code);
        Assert.False(result.Success);
        Assert.Contains("insufficient", result.RawLog);
    }

    [Fact]
    public void TxResult_SequenceMismatch()
    {
        var result = new TxResult("HASH", 32, "account sequence mismatch", false);

        Assert.Equal(32, result.Code);
        Assert.False(result.Success);
    }

    [Fact]
    public void TxResult_RecordEquality()
    {
        var a = new TxResult("HASH", 0, "ok", true);
        var b = new TxResult("HASH", 0, "ok", true);

        Assert.Equal(a, b);
    }

    [Fact]
    public void TxResult_RecordInequality_DifferentHash()
    {
        var a = new TxResult("HASH1", 0, "ok", true);
        var b = new TxResult("HASH2", 0, "ok", true);

        Assert.NotEqual(a, b);
    }

    // ─── Balance ───

    [Fact]
    public void Balance_Creation()
    {
        var balance = new Balance(1_000_000, 1.0m, "1.00 P2P");

        Assert.Equal(1_000_000, balance.Udvpn);
        Assert.Equal(1.0m, balance.P2P);
        Assert.Equal("1.00 P2P", balance.Display);
    }

    [Fact]
    public void Balance_ZeroBalance()
    {
        var balance = new Balance(0, 0m, "0.00 P2P");

        Assert.Equal(0, balance.Udvpn);
        Assert.Equal(0m, balance.P2P);
    }

    [Fact]
    public void Balance_LargeBalance()
    {
        var balance = new Balance(47_690_000_000, 47690.0m, "47690.00 P2P");

        Assert.Equal(47_690_000_000, balance.Udvpn);
        Assert.Equal(47690.0m, balance.P2P);
        Assert.Contains("P2P", balance.Display);
    }

    [Fact]
    public void Balance_RecordEquality()
    {
        var a = new Balance(100, 0.0001m, "0.00 P2P");
        var b = new Balance(100, 0.0001m, "0.00 P2P");

        Assert.Equal(a, b);
    }

    // ─── DiscoveredPlan ───

    [Fact]
    public void DiscoveredPlan_Creation()
    {
        var price = new PriceEntry("udvpn", "50000000", "50000000");
        var plan = new DiscoveredPlan(7, 150, 45, price);

        Assert.Equal(7, plan.Id);
        Assert.Equal(150, plan.Subscribers);
        Assert.Equal(45, plan.NodeCount);
        Assert.NotNull(plan.Price);
        Assert.Equal("50000000", plan.Price!.BaseValue);
    }

    [Fact]
    public void DiscoveredPlan_NullPrice()
    {
        var plan = new DiscoveredPlan(1, 0, 10, null);

        Assert.Null(plan.Price);
    }

    [Fact]
    public void DiscoveredPlan_RecordEquality()
    {
        var price = new PriceEntry("udvpn", "100", "100");
        var a = new DiscoveredPlan(1, 10, 5, price);
        var b = new DiscoveredPlan(1, 10, 5, price);

        Assert.Equal(a, b);
    }

    // ─── FeeGrant ───

    [Fact]
    public void FeeGrant_Creation()
    {
        var grant = new FeeGrant(
            Granter: "sent1granter",
            Grantee: "sent1grantee",
            Allowance: new object()
        );

        Assert.Equal("sent1granter", grant.Granter);
        Assert.Equal("sent1grantee", grant.Grantee);
        Assert.NotNull(grant.Allowance);
    }

    [Fact]
    public void FeeGrant_GranterAndGranteeDiffer()
    {
        var grant = new FeeGrant("sent1a", "sent1b", "allowance_data");

        Assert.NotEqual(grant.Granter, grant.Grantee);
    }

    // ─── SentinelException ───

    [Fact]
    public void SentinelException_IsException()
    {
        var ex = new SentinelException("CODE", "message");

        Assert.IsAssignableFrom<Exception>(ex);
    }

    [Fact]
    public void SentinelException_CodeAndMessage()
    {
        var ex = new SentinelException("WALLET_ERROR", "Something went wrong");

        Assert.Equal("WALLET_ERROR", ex.Code);
        Assert.Equal("Something went wrong", ex.Message);
    }

    [Fact]
    public void SentinelException_WithInnerException()
    {
        var inner = new InvalidOperationException("inner");
        var ex = new SentinelException("CODE", "outer", inner);

        Assert.Same(inner, ex.InnerException);
    }

    // ─── IChainClient Types (from IChainClient.cs) ───

    [Fact]
    public void ActiveSession_Creation()
    {
        var session = new ActiveSession(42UL, "sentnode1abc", SessionStatus.Active);

        Assert.Equal(42UL, session.Id);
        Assert.Equal("sentnode1abc", session.NodeAddress);
        Assert.Equal(SessionStatus.Active, session.Status);
    }

    [Fact]
    public void ActiveSession_InactiveStatus()
    {
        var session = new ActiveSession(1UL, "sentnode1x", SessionStatus.Inactive);

        Assert.Equal(SessionStatus.Inactive, session.Status);
    }

    [Fact]
    public void RawSessionAllocation_Creation()
    {
        var alloc = new RawSessionAllocation(10_000_000_000, 5_000_000_000);

        Assert.Equal(10_000_000_000, alloc.MaxBytes);
        Assert.Equal(5_000_000_000, alloc.UsedBytes);
    }

    [Fact]
    public void RawSessionAllocation_ZeroUsed()
    {
        var alloc = new RawSessionAllocation(1_000_000, 0);

        Assert.Equal(0, alloc.UsedBytes);
    }

    [Fact]
    public void SessionStatus_EnumValues()
    {
        Assert.Equal(0, (int)SessionStatus.Active);
        Assert.Equal(1, (int)SessionStatus.Inactive);
    }

    // ─── Record Deconstruction ───

    [Fact]
    public void PriceEntry_Deconstruction()
    {
        var price = new PriceEntry("udvpn", "100", "200");
        var (denom, baseVal, quoteVal) = price;

        Assert.Equal("udvpn", denom);
        Assert.Equal("100", baseVal);
        Assert.Equal("200", quoteVal);
    }

    [Fact]
    public void TxResult_Deconstruction()
    {
        var result = new TxResult("HASH", 0, "log", true);
        var (hash, code, log, success) = result;

        Assert.Equal("HASH", hash);
        Assert.Equal(0, code);
        Assert.Equal("log", log);
        Assert.True(success);
    }

    [Fact]
    public void Balance_Deconstruction()
    {
        var balance = new Balance(1_000_000, 1.0m, "1.00 P2P");
        var (udvpn, p2p, display) = balance;

        Assert.Equal(1_000_000, udvpn);
        Assert.Equal(1.0m, p2p);
        Assert.Equal("1.00 P2P", display);
    }
}
