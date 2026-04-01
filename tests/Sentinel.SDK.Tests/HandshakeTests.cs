using Sentinel.SDK.Core;
using Sentinel.SDK.Node;
using Xunit;

namespace Sentinel.SDK.Tests;

/// <summary>
/// Tests for Handshake types and exception classes.
/// Cannot test actual handshake without a live node —
/// focuses on enum values, result records, and error classes.
/// </summary>
public class HandshakeTests
{
    // ─── HandshakeType Enum ───

    [Fact]
    public void HandshakeType_HasWireGuardValue()
    {
        var type = HandshakeType.WireGuard;

        Assert.Equal(HandshakeType.WireGuard, type);
    }

    [Fact]
    public void HandshakeType_HasV2RayValue()
    {
        var type = HandshakeType.V2Ray;

        Assert.Equal(HandshakeType.V2Ray, type);
    }

    [Fact]
    public void HandshakeType_WireGuardAndV2Ray_AreDifferent()
    {
        Assert.NotEqual(HandshakeType.WireGuard, HandshakeType.V2Ray);
    }

    [Theory]
    [InlineData(HandshakeType.WireGuard)]
    [InlineData(HandshakeType.V2Ray)]
    public void HandshakeType_IsDefined(HandshakeType type)
    {
        Assert.True(Enum.IsDefined(type));
    }

    // ─── WireGuardHandshakeResult ───

    [Fact]
    public void WireGuardHandshakeResult_Creation()
    {
        var privateKey = new byte[32];
        Array.Fill(privateKey, (byte)0xCC);

        var result = new WireGuardHandshakeResult(
            ServerPublicKey: "dGVzdHB1YmtleQ==",
            AssignedAddresses: ["10.8.0.2/24", "fd1d::2/128"],
            ServerEndpoint: "203.0.113.50:51820",
            ClientPrivateKey: privateKey
        );

        Assert.Equal("dGVzdHB1YmtleQ==", result.ServerPublicKey);
        Assert.Equal(2, result.AssignedAddresses.Length);
        Assert.Equal("10.8.0.2/24", result.AssignedAddresses[0]);
        Assert.Equal("fd1d::2/128", result.AssignedAddresses[1]);
        Assert.Equal("203.0.113.50:51820", result.ServerEndpoint);
        Assert.Equal(32, result.ClientPrivateKey.Length);
    }

    [Fact]
    public void WireGuardHandshakeResult_SingleAddress()
    {
        var result = new WireGuardHandshakeResult(
            ServerPublicKey: "a2V5",
            AssignedAddresses: ["10.8.0.5/32"],
            ServerEndpoint: "1.2.3.4:51820",
            ClientPrivateKey: new byte[32]
        );

        Assert.Single(result.AssignedAddresses);
    }

    // ─── V2RayHandshakeResult ───

    [Fact]
    public void V2RayHandshakeResult_Creation()
    {
        var result = new V2RayHandshakeResult(
            Uuid: "550e8400-e29b-41d4-a716-446655440000",
            ProxyProtocol: 1,
            Transport: 3,
            Tls: 0,
            Port: 443
        );

        Assert.Equal("550e8400-e29b-41d4-a716-446655440000", result.Uuid);
        Assert.Equal(1, result.ProxyProtocol);
        Assert.Equal(3, result.Transport);
        Assert.Equal(0, result.Tls);
        Assert.Equal(443, result.Port);
    }

    [Fact]
    public void V2RayHandshakeResult_VMess_WithTls()
    {
        var result = new V2RayHandshakeResult(
            Uuid: "test-uuid",
            ProxyProtocol: 2,  // VMess
            Transport: 7,      // TCP
            Tls: 1,            // TLS enabled
            Port: 8443
        );

        Assert.Equal(2, result.ProxyProtocol);
        Assert.Equal(7, result.Transport);
        Assert.Equal(1, result.Tls);
    }

    [Theory]
    [InlineData(1, "VLess")]
    [InlineData(2, "VMess")]
    public void V2RayHandshakeResult_ProxyProtocolValues(int protocol, string _)
    {
        var result = new V2RayHandshakeResult("uuid", protocol, 7, 0, 443);

        Assert.Equal(protocol, result.ProxyProtocol);
    }

    [Theory]
    [InlineData(1, "domainsocket")]
    [InlineData(2, "gun")]
    [InlineData(3, "grpc")]
    [InlineData(4, "http")]
    [InlineData(5, "mkcp")]
    [InlineData(6, "quic")]
    [InlineData(7, "tcp")]
    [InlineData(8, "websocket")]
    public void V2RayHandshakeResult_TransportValues(int transport, string _)
    {
        var result = new V2RayHandshakeResult("uuid", 1, transport, 0, 443);

        Assert.Equal(transport, result.Transport);
    }

    // ─── SentinelHandshakeException ───

    [Fact]
    public void SentinelHandshakeException_HasCodeProperty()
    {
        var ex = new SentinelHandshakeException("handshake failed");

        Assert.Equal(ErrorCodes.HandshakeFailed, ex.Code);
        Assert.Equal("handshake failed", ex.Message);
    }

    [Fact]
    public void SentinelHandshakeException_InheritsFromHandshakeException()
    {
        var ex = new SentinelHandshakeException("test");

        Assert.IsAssignableFrom<HandshakeException>(ex);
    }

    [Fact]
    public void SentinelHandshakeException_InheritsFromSentinelException()
    {
        var ex = new SentinelHandshakeException("test");

        Assert.IsAssignableFrom<SentinelException>(ex);
    }

    [Fact]
    public void SentinelHandshakeException_WithInnerException()
    {
        var inner = new TimeoutException("connection timed out");
        var ex = new SentinelHandshakeException("handshake failed", inner);

        Assert.Equal(ErrorCodes.HandshakeFailed, ex.Code);
        Assert.Same(inner, ex.InnerException);
    }

    [Fact]
    public void SentinelHandshakeException_WithCustomCode()
    {
        var ex = new SentinelHandshakeException(
            ErrorCodes.SessionAlreadyExists,
            "session already exists on node");

        Assert.Equal(ErrorCodes.SessionAlreadyExists, ex.Code);
        Assert.Contains("session already exists", ex.Message);
    }

    [Fact]
    public void SentinelHandshakeException_DefaultCode_IsHandshakeFailed()
    {
        var ex = new SentinelHandshakeException("any message");

        Assert.Equal("HANDSHAKE_FAILED", ex.Code);
    }

    // ─── HandshakeException Base ───

    [Fact]
    public void HandshakeException_HasCodeProperty()
    {
        var ex = new HandshakeException("CUSTOM_CODE", "custom message");

        Assert.Equal("CUSTOM_CODE", ex.Code);
        Assert.Equal("custom message", ex.Message);
    }

    [Fact]
    public void HandshakeException_InheritsFromSentinelException()
    {
        var ex = new HandshakeException("CODE", "msg");

        Assert.IsAssignableFrom<SentinelException>(ex);
    }

    [Fact]
    public void HandshakeException_WithInnerException()
    {
        var inner = new Exception("inner");
        var ex = new HandshakeException("CODE", "msg", inner);

        Assert.Same(inner, ex.InnerException);
    }
}
