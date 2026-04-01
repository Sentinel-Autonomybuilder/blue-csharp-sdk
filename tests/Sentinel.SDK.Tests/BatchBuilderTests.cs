using Sentinel.SDK.Core;
using Xunit;

namespace Sentinel.SDK.Tests;

/// <summary>
/// Tests for BatchBuilder — convenience builders for batch TX messages
/// (StartSessions, SendBatch, LinkNodes).
/// </summary>
public class BatchBuilderTests
{
    private const string FromAddr = "sent1abc123";
    private const string ProvAddr = "sentprov1xyz789";
    private const string NodeA = "sentnode1aaa";
    private const string NodeB = "sentnode1bbb";
    private const string NodeC = "sentnode1ccc";
    private const string RecipientA = "sent1recipient_a";
    private const string RecipientB = "sent1recipient_b";

    private static readonly PriceEntry TestPrice = new("udvpn", "1000000", "1000000");

    // ─── StartSessions — Correct Count ───

    [Fact]
    public void StartSessions_ReturnsCorrectNumberOfMessages()
    {
        var nodes = new[]
        {
            (NodeA, 1, TestPrice),
            (NodeB, 2, TestPrice),
            (NodeC, 5, TestPrice),
        };

        var messages = BatchBuilder.StartSessions(FromAddr, nodes);

        Assert.Equal(3, messages.Length);
    }

    // ─── StartSessions — Correct TypeUrl ───

    [Fact]
    public void StartSessions_MessagesHaveCorrectTypeUrl()
    {
        var nodes = new[] { (NodeA, 1, TestPrice) };

        var messages = BatchBuilder.StartSessions(FromAddr, nodes);

        Assert.All(messages, m =>
            Assert.Equal("/sentinel.node.v3.MsgStartSessionRequest", m.TypeUrl));
    }

    // ─── StartSessions — Non-Empty Value ───

    [Fact]
    public void StartSessions_MessagesHaveNonEmptyValue()
    {
        var nodes = new[] { (NodeA, 1, TestPrice) };

        var messages = BatchBuilder.StartSessions(FromAddr, nodes);

        Assert.All(messages, m =>
        {
            Assert.NotNull(m.Value);
            Assert.NotEmpty(m.Value);
        });
    }

    // ─── StartSessions — Single Item ───

    [Fact]
    public void StartSessions_SingleItem_Works()
    {
        var nodes = new[] { (NodeA, 3, TestPrice) };

        var messages = BatchBuilder.StartSessions(FromAddr, nodes);

        Assert.Single(messages);
        Assert.Equal("/sentinel.node.v3.MsgStartSessionRequest", messages[0].TypeUrl);
        Assert.NotEmpty(messages[0].Value);
    }

    // ─── StartSessions — Empty Array Throws ───

    [Fact]
    public void StartSessions_EmptyArray_Throws()
    {
        var nodes = Array.Empty<(string, int, PriceEntry)>();

        var ex = Assert.Throws<SentinelException>(
            () => BatchBuilder.StartSessions(FromAddr, nodes));

        Assert.Equal("BATCH_EMPTY", ex.Code);
    }

    // ─── StartSessions — Null Args Throw ───

    [Fact]
    public void StartSessions_NullFrom_Throws()
    {
        var nodes = new[] { (NodeA, 1, TestPrice) };

        Assert.Throws<ArgumentNullException>(
            () => BatchBuilder.StartSessions(null!, nodes));
    }

    [Fact]
    public void StartSessions_NullNodes_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => BatchBuilder.StartSessions(FromAddr, null!));
    }

    // ─── StartSessions — Zero Gigabytes Clamped to 1 ───

    [Fact]
    public void StartSessions_ZeroGigabytes_ClampedTo1()
    {
        var nodes = new[] { (NodeA, 0, TestPrice) };

        var messages = BatchBuilder.StartSessions(FromAddr, nodes);

        // Should not throw — gigabytes clamped to 1 internally
        Assert.Single(messages);
        Assert.NotEmpty(messages[0].Value);
    }

    // ─── SendBatch — Correct Count ───

    [Fact]
    public void SendBatch_ReturnsCorrectNumberOfMessages()
    {
        var recipients = new[]
        {
            (RecipientA, 1000000L),
            (RecipientB, 2000000L),
        };

        var messages = BatchBuilder.SendBatch(FromAddr, recipients);

        Assert.Equal(2, messages.Length);
    }

    // ─── SendBatch — Correct TypeUrl ───

    [Fact]
    public void SendBatch_TypeUrl_IsCosmoBankMsgSend()
    {
        var recipients = new[] { (RecipientA, 1000000L) };

        var messages = BatchBuilder.SendBatch(FromAddr, recipients);

        Assert.All(messages, m =>
            Assert.Equal("/cosmos.bank.v1beta1.MsgSend", m.TypeUrl));
    }

    // ─── SendBatch — Non-Empty Value ───

    [Fact]
    public void SendBatch_MessagesHaveNonEmptyValue()
    {
        var recipients = new[] { (RecipientA, 5000000L) };

        var messages = BatchBuilder.SendBatch(FromAddr, recipients);

        Assert.All(messages, m =>
        {
            Assert.NotNull(m.Value);
            Assert.NotEmpty(m.Value);
        });
    }

    // ─── SendBatch — Single Item ───

    [Fact]
    public void SendBatch_SingleItem_Works()
    {
        var recipients = new[] { (RecipientA, 500000L) };

        var messages = BatchBuilder.SendBatch(FromAddr, recipients);

        Assert.Single(messages);
        Assert.Equal("/cosmos.bank.v1beta1.MsgSend", messages[0].TypeUrl);
    }

    // ─── SendBatch — Empty Array Throws ───

    [Fact]
    public void SendBatch_EmptyArray_Throws()
    {
        var recipients = Array.Empty<(string, long)>();

        var ex = Assert.Throws<SentinelException>(
            () => BatchBuilder.SendBatch(FromAddr, recipients));

        Assert.Equal("BATCH_EMPTY", ex.Code);
    }

    // ─── SendBatch — Null Args Throw ───

    [Fact]
    public void SendBatch_NullFrom_Throws()
    {
        var recipients = new[] { (RecipientA, 1000000L) };

        Assert.Throws<ArgumentNullException>(
            () => BatchBuilder.SendBatch(null!, recipients));
    }

    [Fact]
    public void SendBatch_NullRecipients_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => BatchBuilder.SendBatch(FromAddr, null!));
    }

    // ─── LinkNodes — Correct Count ───

    [Fact]
    public void LinkNodes_ReturnsCorrectNumberOfMessages()
    {
        var nodeAddresses = new[] { NodeA, NodeB, NodeC };

        var messages = BatchBuilder.LinkNodes(ProvAddr, 42, nodeAddresses);

        Assert.Equal(3, messages.Length);
    }

    // ─── LinkNodes — Correct TypeUrl ───

    [Fact]
    public void LinkNodes_TypeUrl_IsMsgLinkNodeRequest()
    {
        var nodeAddresses = new[] { NodeA };

        var messages = BatchBuilder.LinkNodes(ProvAddr, 1, nodeAddresses);

        Assert.All(messages, m =>
            Assert.Equal("/sentinel.plan.v3.MsgLinkNodeRequest", m.TypeUrl));
    }

    // ─── LinkNodes — Non-Empty Value ───

    [Fact]
    public void LinkNodes_MessagesHaveNonEmptyValue()
    {
        var nodeAddresses = new[] { NodeA, NodeB };

        var messages = BatchBuilder.LinkNodes(ProvAddr, 10, nodeAddresses);

        Assert.All(messages, m =>
        {
            Assert.NotNull(m.Value);
            Assert.NotEmpty(m.Value);
        });
    }

    // ─── LinkNodes — Single Item ───

    [Fact]
    public void LinkNodes_SingleItem_Works()
    {
        var nodeAddresses = new[] { NodeA };

        var messages = BatchBuilder.LinkNodes(ProvAddr, 99, nodeAddresses);

        Assert.Single(messages);
        Assert.Equal("/sentinel.plan.v3.MsgLinkNodeRequest", messages[0].TypeUrl);
        Assert.NotEmpty(messages[0].Value);
    }

    // ─── LinkNodes — Empty Array Throws ───

    [Fact]
    public void LinkNodes_EmptyArray_Throws()
    {
        var nodeAddresses = Array.Empty<string>();

        var ex = Assert.Throws<SentinelException>(
            () => BatchBuilder.LinkNodes(ProvAddr, 1, nodeAddresses));

        Assert.Equal("BATCH_EMPTY", ex.Code);
    }

    // ─── LinkNodes — Null Args Throw ───

    [Fact]
    public void LinkNodes_NullProvAddress_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => BatchBuilder.LinkNodes(null!, 1, new[] { NodeA }));
    }

    [Fact]
    public void LinkNodes_NullNodeAddresses_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => BatchBuilder.LinkNodes(ProvAddr, 1, null!));
    }

    // ─── Multiple Items Produce Distinct Messages ───

    [Fact]
    public void StartSessions_MultipleNodes_ProduceDistinctValues()
    {
        var nodes = new[]
        {
            (NodeA, 1, TestPrice),
            (NodeB, 2, TestPrice),
        };

        var messages = BatchBuilder.StartSessions(FromAddr, nodes);

        // Different nodes should produce different protobuf bytes
        Assert.NotEqual(messages[0].Value, messages[1].Value);
    }

    [Fact]
    public void SendBatch_MultipleRecipients_ProduceDistinctValues()
    {
        var recipients = new[]
        {
            (RecipientA, 1000000L),
            (RecipientB, 2000000L),
        };

        var messages = BatchBuilder.SendBatch(FromAddr, recipients);

        Assert.NotEqual(messages[0].Value, messages[1].Value);
    }

    [Fact]
    public void LinkNodes_MultipleNodes_ProduceDistinctValues()
    {
        var nodeAddresses = new[] { NodeA, NodeB };

        var messages = BatchBuilder.LinkNodes(ProvAddr, 1, nodeAddresses);

        Assert.NotEqual(messages[0].Value, messages[1].Value);
    }

    // ─── SentinelMessage Record ───

    [Fact]
    public void SentinelMessage_HasExpectedProperties()
    {
        var msg = new SentinelMessage("/test.TypeUrl", new byte[] { 0x01, 0x02 });

        Assert.Equal("/test.TypeUrl", msg.TypeUrl);
        Assert.Equal(new byte[] { 0x01, 0x02 }, msg.Value);
    }

    // ─── Large Batch ───

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(20)]
    public void StartSessions_VariousBatchSizes_ReturnCorrectCount(int count)
    {
        var nodes = Enumerable.Range(0, count)
            .Select(i => ($"sentnode1node{i:D3}", 1, TestPrice))
            .ToArray();

        var messages = BatchBuilder.StartSessions(FromAddr, nodes);

        Assert.Equal(count, messages.Length);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(20)]
    public void SendBatch_VariousBatchSizes_ReturnCorrectCount(int count)
    {
        var recipients = Enumerable.Range(0, count)
            .Select(i => ($"sent1recipient{i:D3}", (long)(i + 1) * 1000000))
            .ToArray();

        var messages = BatchBuilder.SendBatch(FromAddr, recipients);

        Assert.Equal(count, messages.Length);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(20)]
    public void LinkNodes_VariousBatchSizes_ReturnCorrectCount(int count)
    {
        var nodeAddresses = Enumerable.Range(0, count)
            .Select(i => $"sentnode1node{i:D3}")
            .ToArray();

        var messages = BatchBuilder.LinkNodes(ProvAddr, 1, nodeAddresses);

        Assert.Equal(count, messages.Length);
    }
}
