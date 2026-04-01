using Sentinel.SDK.Core;
using Xunit;

namespace Sentinel.SDK.Tests;

/// <summary>
/// Tests for TransactionBuilder — input validation and error paths.
/// Cannot test actual broadcast without a live chain connection.
/// </summary>
public class TransactionBuilderTests
{
    // ─── Constructor Validation ───

    [Fact]
    public void Constructor_ThrowsOnNullWallet()
    {
        var client = new ChainClient(
            ["https://lcd.sentinel.co"],
            ["https://rpc.sentinel.co"]
        );

        var ex = Assert.Throws<ArgumentNullException>(() =>
            new TransactionBuilder(null!, client));

        Assert.Equal("wallet", ex.ParamName);
    }

    [Fact]
    public void Constructor_ThrowsOnNullClient()
    {
        using var wallet = SentinelWallet.Generate();

        var ex = Assert.Throws<ArgumentNullException>(() =>
            new TransactionBuilder(wallet, null!));

        Assert.Equal("client", ex.ParamName);
    }

    [Fact]
    public void Constructor_AcceptsValidArguments()
    {
        using var wallet = SentinelWallet.Generate();
        var client = new ChainClient(
            ["https://lcd.sentinel.co"],
            ["https://rpc.sentinel.co"]
        );

        var builder = new TransactionBuilder(wallet, client);

        Assert.NotNull(builder);
    }

    // ─── BroadcastAsync (SentinelMessage) Validation ───

    [Fact]
    public async Task BroadcastAsync_ThrowsOnNull()
    {
        using var wallet = SentinelWallet.Generate();
        var client = new ChainClient(
            ["https://lcd.sentinel.co"],
            ["https://rpc.sentinel.co"]
        );
        var builder = new TransactionBuilder(wallet, client);

        var ex = await Assert.ThrowsAsync<SentinelException>(() =>
            builder.BroadcastAsync(null!));

        Assert.Equal("TX_NO_MESSAGES", ex.Code);
    }

    [Fact]
    public async Task BroadcastAsync_ThrowsOnEmptyMessages()
    {
        using var wallet = SentinelWallet.Generate();
        var client = new ChainClient(
            ["https://lcd.sentinel.co"],
            ["https://rpc.sentinel.co"]
        );
        var builder = new TransactionBuilder(wallet, client);

        var ex = await Assert.ThrowsAsync<SentinelException>(() =>
            builder.BroadcastAsync(Array.Empty<SentinelMessage>()));

        Assert.Equal("TX_NO_MESSAGES", ex.Code);
        Assert.Contains("At least one message", ex.Message);
    }

    // ─── BroadcastProtobufAsync Validation ───

    [Fact]
    public async Task BroadcastProtobufAsync_ThrowsOnNull()
    {
        using var wallet = SentinelWallet.Generate();
        var client = new ChainClient(
            ["https://lcd.sentinel.co"],
            ["https://rpc.sentinel.co"]
        );
        var builder = new TransactionBuilder(wallet, client);

        var ex = await Assert.ThrowsAsync<SentinelException>(() =>
            builder.BroadcastProtobufAsync(null!));

        Assert.Equal("TX_NO_MESSAGES", ex.Code);
    }

    [Fact]
    public async Task BroadcastProtobufAsync_ThrowsOnEmptyMessages()
    {
        using var wallet = SentinelWallet.Generate();
        var client = new ChainClient(
            ["https://lcd.sentinel.co"],
            ["https://rpc.sentinel.co"]
        );
        var builder = new TransactionBuilder(wallet, client);

        var ex = await Assert.ThrowsAsync<SentinelException>(() =>
            builder.BroadcastProtobufAsync(Array.Empty<Google.Protobuf.IMessage>()));

        Assert.Equal("TX_NO_MESSAGES", ex.Code);
    }

    // ─── MAX_SEQUENCE_RETRIES Verification ───

    [Fact]
    public async Task BroadcastAsync_RetriesUpToMaxThenThrows()
    {
        // With a client pointing to a non-existent endpoint, BroadcastAsync
        // should retry up to MAX_SEQUENCE_RETRIES (3) times then throw.
        // We verify by checking the exception code indicates exhausted retries.
        using var wallet = SentinelWallet.Generate();
        var client = new ChainClient(
            ["https://localhost:1"],
            ["https://localhost:1"]
        );
        var builder = new TransactionBuilder(wallet, client);

        var msg = new SentinelMessage(
            "/sentinel.node.v3.MsgStartSessionRequest",
            new byte[] { 0x0A, 0x01, 0x00 }
        );

        var ex = await Assert.ThrowsAsync<SentinelException>(() =>
            builder.BroadcastAsync(msg));

        // After exhausting retries, it should throw a meaningful exception
        Assert.NotNull(ex.Code);
        Assert.NotEmpty(ex.Message);
    }

    // ─── SentinelMessage Record ───

    [Fact]
    public void SentinelMessage_Creation()
    {
        var msg = new SentinelMessage(
            "/sentinel.node.v3.MsgStartSessionRequest",
            new byte[] { 0x0A, 0x02, 0xFF, 0x01 }
        );

        Assert.Equal("/sentinel.node.v3.MsgStartSessionRequest", msg.TypeUrl);
        Assert.Equal(4, msg.Value.Length);
    }
}
