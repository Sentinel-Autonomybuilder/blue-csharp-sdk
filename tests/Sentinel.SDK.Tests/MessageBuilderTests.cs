using Sentinel.SDK.Core;
using Xunit;

namespace Sentinel.SDK.Tests;

/// <summary>
/// Tests for MessageBuilder — validates protobuf message construction
/// for Sentinel chain transactions.
/// </summary>
public class MessageBuilderTests
{
    // ─── StartSession ───

    [Fact]
    public void StartSession_ProducesNonEmptyBytes()
    {
        var msg = MessageBuilder.StartSession(
            from: "sent1xyz789",
            nodeAddress: "sentnode1abc123"
        );

        Assert.NotNull(msg);
        Assert.NotNull(msg.Value);
        Assert.NotEmpty(msg.Value);
    }

    [Fact]
    public void StartSession_HasCorrectTypeUrl()
    {
        var msg = MessageBuilder.StartSession(
            from: "sent1test",
            nodeAddress: "sentnode1test"
        );

        Assert.Equal("/sentinel.node.v3.MsgStartSessionRequest", msg.TypeUrl);
    }

    [Fact]
    public void StartSession_BytesStartWithFieldTag()
    {
        var msg = MessageBuilder.StartSession(
            from: "sent1abc",
            nodeAddress: "sentnode1abc",
            gigabytes: 100
        );

        // Protobuf field 1 wire type 2 (length-delimited string) = tag 0x0A
        Assert.True(msg.Value[0] == 0x0A,
            $"Expected protobuf field tag 0x0A, got 0x{msg.Value[0]:X2}");
    }

    [Fact]
    public void StartSession_DefaultGigabytesIsOne()
    {
        var msg1 = MessageBuilder.StartSession("sent1abc", "sentnode1abc");
        var msg2 = MessageBuilder.StartSession("sent1abc", "sentnode1abc", gigabytes: 1);

        Assert.Equal(msg1.Value, msg2.Value);
    }

    [Fact]
    public void StartSession_DifferentGigabytesProduceDifferentBytes()
    {
        var msg1 = MessageBuilder.StartSession("sent1abc", "sentnode1abc", gigabytes: 1);
        var msg2 = MessageBuilder.StartSession("sent1abc", "sentnode1abc", gigabytes: 10);

        Assert.NotEqual(msg1.Value, msg2.Value);
    }

    [Fact]
    public void StartSession_WithMaxPrice_ProducesDifferentBytes()
    {
        var msg1 = MessageBuilder.StartSession("sent1abc", "sentnode1abc");
        var msg2 = MessageBuilder.StartSession("sent1abc", "sentnode1abc",
            maxPrice: new PriceEntry("udvpn", "1000000", "1000000"));

        Assert.NotEqual(msg1.Value, msg2.Value);
    }

    // ─── EndSession ───

    [Fact]
    public void EndSession_ProducesNonEmptyBytes()
    {
        var msg = MessageBuilder.EndSession(
            from: "sent1xyz789",
            sessionId: 999
        );

        Assert.NotNull(msg);
        Assert.NotEmpty(msg.Value);
    }

    [Fact]
    public void EndSession_HasCorrectTypeUrl()
    {
        var msg = MessageBuilder.EndSession(
            from: "sent1test",
            sessionId: 1
        );

        Assert.Equal("/sentinel.session.v3.MsgCancelSessionRequest", msg.TypeUrl);
    }

    [Fact]
    public void EndSession_DifferentSessionIdsProduceDifferentBytes()
    {
        var msg1 = MessageBuilder.EndSession("sent1test", 1);
        var msg2 = MessageBuilder.EndSession("sent1test", 9999);

        Assert.NotEqual(msg1.Value, msg2.Value);
    }

    // ─── Send ───

    [Fact]
    public void Send_ProducesNonEmptyBytes()
    {
        var msg = MessageBuilder.Send(
            from: "sent1sender",
            to: "sent1receiver",
            amountUdvpn: 1000000
        );

        Assert.NotNull(msg);
        Assert.NotEmpty(msg.Value);
    }

    [Fact]
    public void Send_HasCorrectTypeUrl()
    {
        var msg = MessageBuilder.Send("sent1a", "sent1b", 100);

        Assert.Equal("/cosmos.bank.v1beta1.MsgSend", msg.TypeUrl);
    }

    [Fact]
    public void Send_DifferentAmountsProduceDifferentBytes()
    {
        var msg1 = MessageBuilder.Send("sent1a", "sent1b", 100);
        var msg2 = MessageBuilder.Send("sent1a", "sent1b", 999999);

        Assert.NotEqual(msg1.Value, msg2.Value);
    }

    [Fact]
    public void Send_DifferentRecipientsProduceDifferentBytes()
    {
        var msg1 = MessageBuilder.Send("sent1a", "sent1b", 100);
        var msg2 = MessageBuilder.Send("sent1a", "sent1c", 100);

        Assert.NotEqual(msg1.Value, msg2.Value);
    }

    // ─── StartSubscription ───

    [Fact]
    public void StartSubscription_ProducesNonEmptyBytes()
    {
        var msg = MessageBuilder.StartSubscription(
            from: "sent1xyz",
            planId: 10
        );

        Assert.NotNull(msg);
        Assert.NotEmpty(msg.Value);
    }

    [Fact]
    public void StartSubscription_HasCorrectTypeUrl()
    {
        var msg = MessageBuilder.StartSubscription("sent1a", 5);

        Assert.Equal("/sentinel.subscription.v3.MsgStartSubscriptionRequest", msg.TypeUrl);
    }

    [Fact]
    public void StartSubscription_DefaultDenomIsUdvpn()
    {
        var msg1 = MessageBuilder.StartSubscription("sent1a", 1);
        var msg2 = MessageBuilder.StartSubscription("sent1a", 1, "udvpn");

        Assert.Equal(msg1.Value, msg2.Value);
    }

    // ─── SubStartSession ───

    [Fact]
    public void SubStartSession_ProducesNonEmptyBytes()
    {
        var msg = MessageBuilder.SubStartSession(
            from: "sent1abc",
            subscriptionId: 42,
            nodeAddress: "sentnode1abc"
        );

        Assert.NotNull(msg);
        Assert.NotEmpty(msg.Value);
    }

    [Fact]
    public void SubStartSession_HasCorrectTypeUrl()
    {
        var msg = MessageBuilder.SubStartSession("sent1a", 1, "sentnode1a");

        Assert.Equal("/sentinel.subscription.v3.MsgStartSessionRequest", msg.TypeUrl);
    }

    // ─── PlanStartSession ───

    [Fact]
    public void PlanStartSession_ProducesNonEmptyBytes()
    {
        var msg = MessageBuilder.PlanStartSession(
            from: "sent1abc",
            planId: 7
        );

        Assert.NotNull(msg);
        Assert.NotEmpty(msg.Value);
    }

    [Fact]
    public void PlanStartSession_HasCorrectTypeUrl()
    {
        var msg = MessageBuilder.PlanStartSession("sent1a", 7);

        Assert.Equal("/sentinel.plan.v3.MsgStartSessionRequest", msg.TypeUrl);
    }

    [Fact]
    public void PlanStartSession_WithNodeAddress_ProducesDifferentBytes()
    {
        var msg1 = MessageBuilder.PlanStartSession("sent1a", 7);
        var msg2 = MessageBuilder.PlanStartSession("sent1a", 7, nodeAddress: "sentnode1abc");

        Assert.NotEqual(msg1.Value, msg2.Value);
    }

    // ─── CreatePlan ───

    [Fact]
    public void CreatePlan_ProducesNonEmptyBytes()
    {
        var prices = new[] { new PriceEntry("udvpn", "1000000", "1000000") };
        var msg = MessageBuilder.CreatePlan(
            from: "sentprov1abc",
            bytes: "10000000000",
            durationSeconds: 86400,
            prices: prices
        );

        Assert.NotNull(msg);
        Assert.NotEmpty(msg.Value);
    }

    [Fact]
    public void CreatePlan_HasCorrectTypeUrl()
    {
        var prices = new[] { new PriceEntry("udvpn", "100", "100") };
        var msg = MessageBuilder.CreatePlan("sentprov1a", "1000", 3600, prices);

        Assert.Equal("/sentinel.plan.v3.MsgCreatePlanRequest", msg.TypeUrl);
    }

    // ─── UpdatePlanStatus ───

    [Fact]
    public void UpdatePlanStatus_ProducesNonEmptyBytes()
    {
        var msg = MessageBuilder.UpdatePlanStatus("sentprov1abc", 1, 1);

        Assert.NotNull(msg);
        Assert.NotEmpty(msg.Value);
    }

    [Fact]
    public void UpdatePlanStatus_HasCorrectTypeUrl()
    {
        var msg = MessageBuilder.UpdatePlanStatus("sentprov1a", 1, 1);

        Assert.Equal("/sentinel.plan.v3.MsgUpdatePlanStatusRequest", msg.TypeUrl);
    }

    // ─── LinkNode / UnlinkNode ───

    [Fact]
    public void LinkNode_HasCorrectTypeUrl()
    {
        var msg = MessageBuilder.LinkNode("sentprov1a", 1, "sentnode1abc");

        Assert.Equal("/sentinel.plan.v3.MsgLinkNodeRequest", msg.TypeUrl);
    }

    [Fact]
    public void UnlinkNode_HasCorrectTypeUrl()
    {
        var msg = MessageBuilder.UnlinkNode("sentprov1a", 1, "sentnode1abc");

        Assert.Equal("/sentinel.plan.v3.MsgUnlinkNodeRequest", msg.TypeUrl);
    }

    // ─── RegisterProvider ───

    [Fact]
    public void RegisterProvider_ProducesNonEmptyBytes()
    {
        var msg = MessageBuilder.RegisterProvider("sent1abc", "MyProvider");

        Assert.NotNull(msg);
        Assert.NotEmpty(msg.Value);
    }

    [Fact]
    public void RegisterProvider_HasCorrectTypeUrl()
    {
        var msg = MessageBuilder.RegisterProvider("sent1abc", "MyProvider");

        Assert.Equal("/sentinel.provider.v3.MsgRegisterProviderRequest", msg.TypeUrl);
    }

    [Fact]
    public void RegisterProvider_WithOptionalFields_ProducesDifferentBytes()
    {
        var msg1 = MessageBuilder.RegisterProvider("sent1abc", "MyProvider");
        var msg2 = MessageBuilder.RegisterProvider("sent1abc", "MyProvider",
            identity: "keybase123", website: "https://example.com", description: "A provider");

        Assert.NotEqual(msg1.Value, msg2.Value);
    }

    // ─── UpdateProviderDetails ───

    [Fact]
    public void UpdateProviderDetails_HasCorrectTypeUrl()
    {
        var msg = MessageBuilder.UpdateProviderDetails("sentprov1abc");

        Assert.Equal("/sentinel.provider.v3.MsgUpdateProviderDetailsRequest", msg.TypeUrl);
    }

    // ─── UpdateProviderStatus ───

    [Fact]
    public void UpdateProviderStatus_HasCorrectTypeUrl()
    {
        var msg = MessageBuilder.UpdateProviderStatus("sent1abc", 1);

        Assert.Equal("/sentinel.provider.v3.MsgUpdateProviderStatusRequest", msg.TypeUrl);
    }

    // ─── Lease ───

    [Fact]
    public void StartLease_HasCorrectTypeUrl()
    {
        var msg = MessageBuilder.StartLease("sentprov1abc", "sentnode1abc", 24);

        Assert.Equal("/sentinel.lease.v1.MsgStartLeaseRequest", msg.TypeUrl);
    }

    [Fact]
    public void EndLease_HasCorrectTypeUrl()
    {
        var msg = MessageBuilder.EndLease("sentprov1abc", 1);

        Assert.Equal("/sentinel.lease.v1.MsgEndLeaseRequest", msg.TypeUrl);
    }

    // ─── Fee Grant ───

    [Fact]
    public void GrantFeeAllowance_HasCorrectTypeUrl()
    {
        var msg = MessageBuilder.GrantFeeAllowance("sent1granter", "sent1grantee");

        Assert.Equal("/cosmos.feegrant.v1beta1.MsgGrantAllowance", msg.TypeUrl);
    }

    [Fact]
    public void GrantFeeAllowance_WithSpendLimit_ProducesDifferentBytes()
    {
        var msg1 = MessageBuilder.GrantFeeAllowance("sent1granter", "sent1grantee");
        var msg2 = MessageBuilder.GrantFeeAllowance("sent1granter", "sent1grantee",
            spendLimitUdvpn: 1000000);

        Assert.NotEqual(msg1.Value, msg2.Value);
    }

    [Fact]
    public void RevokeFeeAllowance_HasCorrectTypeUrl()
    {
        var msg = MessageBuilder.RevokeFeeAllowance("sent1granter", "sent1grantee");

        Assert.Equal("/cosmos.feegrant.v1beta1.MsgRevokeAllowance", msg.TypeUrl);
    }

    // ─── SentinelMessage Record ───

    [Fact]
    public void SentinelMessage_RecordEquality()
    {
        var bytes = new byte[] { 0x0A, 0x01 };
        var msg1 = new SentinelMessage("/test.Type", bytes);
        var msg2 = new SentinelMessage("/test.Type", bytes);

        // Record equality compares by value for string, but reference for arrays
        Assert.Equal(msg1.TypeUrl, msg2.TypeUrl);
    }

    [Fact]
    public void SentinelMessage_PreservesTypeUrlAndValue()
    {
        var bytes = new byte[] { 0x08, 0x01, 0x12, 0x05 };
        var msg = new SentinelMessage("/sentinel.session.v3.MsgCancelSessionRequest", bytes);

        Assert.Equal("/sentinel.session.v3.MsgCancelSessionRequest", msg.TypeUrl);
        Assert.Equal(bytes, msg.Value);
    }

    // ─── TypeUrl Format ───

    [Fact]
    public void AllTypeUrls_StartWithSlash()
    {
        var messages = new[]
        {
            MessageBuilder.StartSession("sent1a", "sentnode1a"),
            MessageBuilder.EndSession("sent1a", 1),
            MessageBuilder.StartSubscription("sent1a", 1),
            MessageBuilder.SubStartSession("sent1a", 1, "sentnode1a"),
            MessageBuilder.PlanStartSession("sent1a", 1),
            MessageBuilder.Send("sent1a", "sent1b", 100),
            MessageBuilder.GrantFeeAllowance("sent1a", "sent1b"),
            MessageBuilder.RevokeFeeAllowance("sent1a", "sent1b"),
        };

        foreach (var msg in messages)
        {
            Assert.StartsWith("/", msg.TypeUrl);
        }
    }

    [Fact]
    public void SentinelMessages_ContainSentinelInTypeUrl()
    {
        var msg1 = MessageBuilder.StartSession("sent1a", "sentnode1a");
        var msg2 = MessageBuilder.EndSession("sent1a", 1);

        Assert.Contains("sentinel", msg1.TypeUrl, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sentinel", msg2.TypeUrl, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CosmosMessages_ContainCosmosInTypeUrl()
    {
        var send = MessageBuilder.Send("sent1a", "sent1b", 100);
        var grant = MessageBuilder.GrantFeeAllowance("sent1a", "sent1b");

        Assert.Contains("cosmos", send.TypeUrl, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cosmos", grant.TypeUrl, StringComparison.OrdinalIgnoreCase);
    }

    // ─── DecToScaledInt (sdk.Dec → big.Int scaling) ───

    [Theory]
    [InlineData("0.003000000000000000", "3000000000000000")]
    [InlineData("40152030", "40152030000000000000000000")]
    [InlineData("0", "0000000000000000000")]
    [InlineData("1", "1000000000000000000")]
    [InlineData("1.5", "1500000000000000000")]
    [InlineData("0.1", "100000000000000000")]
    [InlineData("", "0")]
    [InlineData(null, "0")]
    public void DecToScaledInt_MatchesJsSdk(string? input, string expected)
    {
        var result = MessageBuilder.DecToScaledInt(input!);
        // For "0", the JS SDK returns "0" but we return "0" + 18 zeros. Both are
        // valid sdk.Dec representations. Normalize by trimming leading zeros.
        var normalizedResult = result.TrimStart('0');
        var normalizedExpected = expected.TrimStart('0');
        if (normalizedResult == "") normalizedResult = "0";
        if (normalizedExpected == "") normalizedExpected = "0";
        Assert.Equal(normalizedExpected, normalizedResult);
    }

    [Fact]
    public void DecToScaledInt_DecimalPrecision()
    {
        // Exact JS SDK test case: "0.003000000000000000" → "3000000000000000"
        Assert.Equal("3000000000000000", MessageBuilder.DecToScaledInt("0.003000000000000000"));
    }

    [Fact]
    public void DecToScaledInt_IntegerScaling()
    {
        // Exact JS SDK test case: integer → integer + 18 zeros
        var result = MessageBuilder.DecToScaledInt("40152030");
        Assert.Equal("40152030000000000000000000", result);
    }
}
