using Sentinel.SDK.Core;
using Xunit;

namespace Sentinel.SDK.Tests;

/// <summary>
/// Tests for the error hierarchy — SentinelException, typed subclasses,
/// error codes, severity classification, and retry logic.
/// </summary>
public class ErrorTests
{
    // ─── SentinelException Base ───

    [Fact]
    public void SentinelException_HasCodeProperty()
    {
        var ex = new SentinelException("TEST_CODE", "test message");

        Assert.Equal("TEST_CODE", ex.Code);
        Assert.Equal("test message", ex.Message);
    }

    [Fact]
    public void SentinelException_PreservesInnerException()
    {
        var inner = new InvalidOperationException("inner error");
        var ex = new SentinelException("OUTER_CODE", "outer message", inner);

        Assert.Equal("OUTER_CODE", ex.Code);
        Assert.Equal("outer message", ex.Message);
        Assert.Same(inner, ex.InnerException);
    }

    [Fact]
    public void SentinelException_SupportsDetails()
    {
        var details = new { Node = "sentnode1abc", Port = 443 };
        var ex = new SentinelException("TEST", "test", details);

        Assert.NotNull(ex.Details);
    }

    [Fact]
    public void SentinelException_InheritsFromException()
    {
        var ex = new SentinelException("CODE", "msg");

        Assert.IsAssignableFrom<Exception>(ex);
    }

    [Fact]
    public void SentinelException_CanBeCaughtAsException()
    {
        Exception? caught = null;

        try
        {
            throw new SentinelException("TEST", "test");
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        Assert.NotNull(caught);
        Assert.IsType<SentinelException>(caught);
        Assert.Equal("TEST", ((SentinelException)caught).Code);
    }

    // ─── Typed Subclasses ───

    [Fact]
    public void WalletException_InheritsFromSentinelException()
    {
        var ex = new WalletException("WALLET_ERROR", "wallet broke");

        Assert.IsAssignableFrom<SentinelException>(ex);
        Assert.Equal("WALLET_ERROR", ex.Code);
        Assert.Equal("wallet broke", ex.Message);
    }

    [Fact]
    public void ChainException_InheritsFromSentinelException()
    {
        var ex = new ChainException("CHAIN_ERROR", "chain broke");

        Assert.IsAssignableFrom<SentinelException>(ex);
        Assert.Equal("CHAIN_ERROR", ex.Code);
    }

    [Fact]
    public void NodeException_InheritsFromSentinelException()
    {
        var ex = new NodeException("NODE_ERROR", "node broke");

        Assert.IsAssignableFrom<SentinelException>(ex);
        Assert.Equal("NODE_ERROR", ex.Code);
    }

    [Fact]
    public void TunnelException_InheritsFromSentinelException()
    {
        var ex = new TunnelException("TUNNEL_ERROR", "tunnel broke");

        Assert.IsAssignableFrom<SentinelException>(ex);
        Assert.Equal("TUNNEL_ERROR", ex.Code);
    }

    [Fact]
    public void HandshakeException_InheritsFromSentinelException()
    {
        var ex = new HandshakeException("HANDSHAKE_ERROR", "handshake broke");

        Assert.IsAssignableFrom<SentinelException>(ex);
        Assert.Equal("HANDSHAKE_ERROR", ex.Code);
    }

    [Fact]
    public void AllSubclasses_PreserveInnerException()
    {
        var inner = new TimeoutException("timed out");

        var wallet = new WalletException("W", "w", inner);
        var chain = new ChainException("C", "c", inner);
        var node = new NodeException("N", "n", inner);
        var tunnel = new TunnelException("T", "t", inner);
        var handshake = new HandshakeException("H", "h", inner);

        Assert.Same(inner, wallet.InnerException);
        Assert.Same(inner, chain.InnerException);
        Assert.Same(inner, node.InnerException);
        Assert.Same(inner, tunnel.InnerException);
        Assert.Same(inner, handshake.InnerException);
    }

    [Fact]
    public void AllSubclasses_SupportDetails()
    {
        var details = new { Reason = "test" };

        var wallet = new WalletException("W", "w", details);
        var chain = new ChainException("C", "c", details);
        var node = new NodeException("N", "n", details);
        var tunnel = new TunnelException("T", "t", details);
        var handshake = new HandshakeException("H", "h", details);

        Assert.Same(details, wallet.Details);
        Assert.Same(details, chain.Details);
        Assert.Same(details, node.Details);
        Assert.Same(details, tunnel.Details);
        Assert.Same(details, handshake.Details);
    }

    // ─── ErrorCodes Constants ───

    [Fact]
    public void ErrorCodes_WalletCodesAreNonNull()
    {
        Assert.False(string.IsNullOrEmpty(ErrorCodes.InvalidMnemonic));
        Assert.False(string.IsNullOrEmpty(ErrorCodes.InsufficientBalance));
    }

    [Fact]
    public void ErrorCodes_NodeCodesAreNonNull()
    {
        Assert.False(string.IsNullOrEmpty(ErrorCodes.NodeOffline));
        Assert.False(string.IsNullOrEmpty(ErrorCodes.NodeNotFound));
    }

    [Fact]
    public void ErrorCodes_ChainCodesAreNonNull()
    {
        Assert.False(string.IsNullOrEmpty(ErrorCodes.BroadcastFailed));
        Assert.False(string.IsNullOrEmpty(ErrorCodes.TxFailed));
        Assert.False(string.IsNullOrEmpty(ErrorCodes.SequenceMismatch));
    }

    [Fact]
    public void ErrorCodes_TunnelCodesAreNonNull()
    {
        Assert.False(string.IsNullOrEmpty(ErrorCodes.WireGuardNotAvailable));
        Assert.False(string.IsNullOrEmpty(ErrorCodes.V2RayNotFound));
        Assert.False(string.IsNullOrEmpty(ErrorCodes.TunnelSetupFailed));
    }

    [Fact]
    public void ErrorCodes_ConnectionCodesAreNonNull()
    {
        Assert.False(string.IsNullOrEmpty(ErrorCodes.AllNodesFailed));
        Assert.False(string.IsNullOrEmpty(ErrorCodes.AlreadyConnected));
        Assert.False(string.IsNullOrEmpty(ErrorCodes.NotConnected));
        Assert.False(string.IsNullOrEmpty(ErrorCodes.HandshakeFailed));
    }

    [Fact]
    public void ErrorCodes_AreDistinctStrings()
    {
        var codes = new[]
        {
            ErrorCodes.InvalidMnemonic,
            ErrorCodes.InsufficientBalance,
            ErrorCodes.NodeOffline,
            ErrorCodes.NodeNotFound,
            ErrorCodes.BroadcastFailed,
            ErrorCodes.TxFailed,
            ErrorCodes.SequenceMismatch,
            ErrorCodes.WireGuardNotAvailable,
            ErrorCodes.V2RayNotFound,
            ErrorCodes.TunnelSetupFailed,
            ErrorCodes.AllNodesFailed,
            ErrorCodes.AlreadyConnected,
            ErrorCodes.NotConnected,
            ErrorCodes.HandshakeFailed,
        };

        var unique = new HashSet<string>(codes);
        Assert.Equal(codes.Length, unique.Count);
    }

    [Fact]
    public void ErrorCodes_HaveExpectedValues()
    {
        Assert.Equal("INVALID_MNEMONIC", ErrorCodes.InvalidMnemonic);
        Assert.Equal("INSUFFICIENT_BALANCE", ErrorCodes.InsufficientBalance);
        Assert.Equal("NODE_OFFLINE", ErrorCodes.NodeOffline);
        Assert.Equal("NODE_NOT_FOUND", ErrorCodes.NodeNotFound);
        Assert.Equal("BROADCAST_FAILED", ErrorCodes.BroadcastFailed);
        Assert.Equal("TX_FAILED", ErrorCodes.TxFailed);
        Assert.Equal("SEQUENCE_MISMATCH", ErrorCodes.SequenceMismatch);
        Assert.Equal("WG_NOT_AVAILABLE", ErrorCodes.WireGuardNotAvailable);
        Assert.Equal("V2RAY_NOT_FOUND", ErrorCodes.V2RayNotFound);
        Assert.Equal("TUNNEL_SETUP_FAILED", ErrorCodes.TunnelSetupFailed);
        Assert.Equal("ALL_NODES_FAILED", ErrorCodes.AllNodesFailed);
        Assert.Equal("ALREADY_CONNECTED", ErrorCodes.AlreadyConnected);
        Assert.Equal("NOT_CONNECTED", ErrorCodes.NotConnected);
        Assert.Equal("HANDSHAKE_FAILED", ErrorCodes.HandshakeFailed);
    }

    // ─── ErrorSeverity ───

    [Fact]
    public void ErrorSeverity_Get_ReturnsValidCategory()
    {
        var validCategories = new[] { "fatal", "retryable", "recoverable", "unknown" };

        var severity = ErrorSeverity.Get(ErrorCodes.InvalidMnemonic);
        Assert.Contains(severity, validCategories);
    }

    [Fact]
    public void ErrorSeverity_Get_WalletErrorsAreFatal()
    {
        Assert.Equal("fatal", ErrorSeverity.Get(ErrorCodes.InvalidMnemonic));
        Assert.Equal("fatal", ErrorSeverity.Get(ErrorCodes.InsufficientBalance));
    }

    [Fact]
    public void ErrorSeverity_Get_NodeOfflineIsRetryable()
    {
        Assert.Equal("retryable", ErrorSeverity.Get(ErrorCodes.NodeOffline));
    }

    [Fact]
    public void ErrorSeverity_Get_BroadcastFailedIsRetryable()
    {
        Assert.Equal("retryable", ErrorSeverity.Get(ErrorCodes.BroadcastFailed));
    }

    [Fact]
    public void ErrorSeverity_Get_AllNodesFailedIsRetryable()
    {
        Assert.Equal("retryable", ErrorSeverity.Get(ErrorCodes.AllNodesFailed));
    }

    [Fact]
    public void ErrorSeverity_Get_HandshakeFailedIsRecoverable()
    {
        Assert.Equal("recoverable", ErrorSeverity.Get(ErrorCodes.HandshakeFailed));
    }

    [Fact]
    public void ErrorSeverity_Get_UnknownCodeReturnsUnknown()
    {
        var severity = ErrorSeverity.Get("UNKNOWN_CODE_12345");
        Assert.Equal("unknown", severity);
    }

    [Fact]
    public void ErrorSeverity_IsRetryable_RetryableCodesReturnTrue()
    {
        Assert.True(ErrorSeverity.IsRetryable(ErrorCodes.NodeOffline));
        Assert.True(ErrorSeverity.IsRetryable(ErrorCodes.BroadcastFailed));
        Assert.True(ErrorSeverity.IsRetryable(ErrorCodes.AllNodesFailed));
    }

    [Fact]
    public void ErrorSeverity_IsRetryable_FatalCodesReturnFalse()
    {
        Assert.False(ErrorSeverity.IsRetryable(ErrorCodes.InvalidMnemonic));
        Assert.False(ErrorSeverity.IsRetryable(ErrorCodes.InsufficientBalance));
    }

    [Fact]
    public void ErrorSeverity_IsRetryable_UnknownCodeReturnsFalse()
    {
        Assert.False(ErrorSeverity.IsRetryable("SOME_UNKNOWN_CODE"));
    }

    [Fact]
    public void ErrorSeverity_UserMessage_ReturnsNonEmptyString()
    {
        var msg = ErrorSeverity.UserMessage(ErrorCodes.InsufficientBalance);
        Assert.False(string.IsNullOrWhiteSpace(msg));
    }

    [Fact]
    public void ErrorSeverity_UserMessage_DifferentCodesGiveDifferentMessages()
    {
        var msg1 = ErrorSeverity.UserMessage(ErrorCodes.InsufficientBalance);
        var msg2 = ErrorSeverity.UserMessage(ErrorCodes.NodeOffline);
        Assert.NotEqual(msg1, msg2);
    }

    [Fact]
    public void ErrorSeverity_UserMessage_UnknownCodeReturnsGenericMessage()
    {
        var msg = ErrorSeverity.UserMessage("TOTALLY_UNKNOWN_ERROR");
        Assert.False(string.IsNullOrWhiteSpace(msg));
    }

    [Fact]
    public void ErrorSeverity_UserMessage_ContainsNoInternalDetails()
    {
        var msg = ErrorSeverity.UserMessage(ErrorCodes.BroadcastFailed);
        Assert.DoesNotContain("Exception", msg);
        Assert.DoesNotContain("StackTrace", msg);
        Assert.DoesNotContain("NullReference", msg);
    }

    [Fact]
    public void ErrorSeverity_UserMessage_InsufficientBalanceMentionsP2P()
    {
        var msg = ErrorSeverity.UserMessage(ErrorCodes.InsufficientBalance);
        Assert.Contains("P2P", msg);
    }

    // ─── Existing Exception Pattern Compatibility ───

    [Fact]
    public void SentinelException_EmptyMnemonicThrows()
    {
        var ex = Assert.Throws<SentinelException>(
            () => SentinelWallet.FromMnemonic("")
        );

        Assert.Equal("WALLET_EMPTY_MNEMONIC", ex.Code);
    }

    [Fact]
    public void SentinelException_InvalidMnemonicThrows()
    {
        var ex = Assert.Throws<SentinelException>(
            () => SentinelWallet.FromMnemonic("not a valid mnemonic phrase at all")
        );

        Assert.Equal("WALLET_INVALID_MNEMONIC", ex.Code);
    }

    [Fact]
    public void SentinelException_InvalidStrengthThrows()
    {
        var ex = Assert.Throws<SentinelException>(
            () => SentinelWallet.Generate(999)
        );

        Assert.Equal("WALLET_INVALID_STRENGTH", ex.Code);
    }
}
