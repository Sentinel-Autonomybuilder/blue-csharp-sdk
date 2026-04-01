using Sentinel.SDK.Core;
using Sentinel.SDK.Tunnel.WireGuard;
using Xunit;

namespace Sentinel.SDK.Tests;

/// <summary>
/// Tests for WireGuardTunnel and WireGuardConfig.
/// Cannot test InstallAsync/UninstallAsync without admin privileges —
/// focuses on config records, defaults, and state properties.
/// </summary>
public class WireGuardTunnelTests
{
    // ─── WireGuardConfig Record ───

    [Fact]
    public void WireGuardConfig_Creation_AllFields()
    {
        var privateKey = new byte[32];
        Array.Fill(privateKey, (byte)0xAB);
        var addresses = new[] { "10.8.0.2/24", "fd1d::2/128" };
        var splitIPs = new[] { "192.168.1.0/24", "10.0.0.0/8" };

        var config = new WireGuardConfig(
            ClientPrivateKey: privateKey,
            AssignedAddresses: addresses,
            ServerPublicKey: "dGVzdC1wdWJsaWMta2V5LWJhc2U2NA==",
            ServerEndpoint: "1.2.3.4:51820",
            FullTunnel: false,
            SplitIPs: splitIPs
        );

        Assert.Equal(privateKey, config.ClientPrivateKey);
        Assert.Equal(2, config.AssignedAddresses.Length);
        Assert.Equal("10.8.0.2/24", config.AssignedAddresses[0]);
        Assert.Equal("dGVzdC1wdWJsaWMta2V5LWJhc2U2NA==", config.ServerPublicKey);
        Assert.Equal("1.2.3.4:51820", config.ServerEndpoint);
        Assert.False(config.FullTunnel);
        Assert.NotNull(config.SplitIPs);
        Assert.Equal(2, config.SplitIPs!.Length);
    }

    [Fact]
    public void WireGuardConfig_Defaults_FullTunnelTrue_SplitIPsNull()
    {
        var config = new WireGuardConfig(
            ClientPrivateKey: new byte[32],
            AssignedAddresses: ["10.8.0.2/24"],
            ServerPublicKey: "c29tZS1rZXk=",
            ServerEndpoint: "5.6.7.8:51820"
        );

        Assert.True(config.FullTunnel);
        Assert.Null(config.SplitIPs);
    }

    [Fact]
    public void WireGuardConfig_FullTunnel_ExplicitTrue()
    {
        var config = new WireGuardConfig(
            ClientPrivateKey: new byte[32],
            AssignedAddresses: ["10.8.0.2/24"],
            ServerPublicKey: "c29tZS1rZXk=",
            ServerEndpoint: "5.6.7.8:51820",
            FullTunnel: true
        );

        Assert.True(config.FullTunnel);
    }

    [Fact]
    public void WireGuardConfig_SplitIPs_OverridesFullTunnel()
    {
        var splitIPs = new[] { "192.168.0.0/16" };

        var config = new WireGuardConfig(
            ClientPrivateKey: new byte[32],
            AssignedAddresses: ["10.8.0.2/24"],
            ServerPublicKey: "c29tZS1rZXk=",
            ServerEndpoint: "5.6.7.8:51820",
            FullTunnel: false,
            SplitIPs: splitIPs
        );

        Assert.False(config.FullTunnel);
        Assert.NotNull(config.SplitIPs);
        Assert.Single(config.SplitIPs!);
        Assert.Equal("192.168.0.0/16", config.SplitIPs![0]);
    }

    // ─── WireGuardTunnel ───

    [Fact]
    public void TunnelName_Default_IsWgsent0()
    {
        using var tunnel = new WireGuardTunnel();

        Assert.Equal("wgsent0", tunnel.TunnelName);
    }

    [Fact]
    public void TunnelName_CustomName()
    {
        using var tunnel = new WireGuardTunnel("my-custom-tunnel");

        Assert.Equal("my-custom-tunnel", tunnel.TunnelName);
    }

    [Fact]
    public void IsActive_ReturnsFalse_Initially()
    {
        using var tunnel = new WireGuardTunnel();

        // No tunnel installed, so service query should return false
        Assert.False(tunnel.IsActive);
    }

    [Fact]
    public void IsActive_ReturnsFalse_ForNonexistentService()
    {
        using var tunnel = new WireGuardTunnel("nonexistent_tunnel_name_xyz");

        Assert.False(tunnel.IsActive);
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes()
    {
        var tunnel = new WireGuardTunnel();

        tunnel.Dispose();
        tunnel.Dispose(); // Should not throw
    }

    [Fact]
    public void WireGuardTunnel_ImplementsIDisposable()
    {
        using var tunnel = new WireGuardTunnel();

        Assert.IsAssignableFrom<IDisposable>(tunnel);
    }

    // ─── WireGuardConfig Equality ───

    [Fact]
    public void WireGuardConfig_RecordEquality()
    {
        var key = new byte[32];
        var a = new WireGuardConfig(key, ["10.8.0.2/24"], "pk", "1.2.3.4:51820");
        var b = new WireGuardConfig(key, ["10.8.0.2/24"], "pk", "1.2.3.4:51820");

        // Records use value equality for simple fields,
        // but arrays use reference equality by default
        Assert.Equal(a.ServerPublicKey, b.ServerPublicKey);
        Assert.Equal(a.ServerEndpoint, b.ServerEndpoint);
        Assert.Equal(a.FullTunnel, b.FullTunnel);
    }

    [Fact]
    public void WireGuardConfig_WithExpression()
    {
        var original = new WireGuardConfig(
            new byte[32], ["10.8.0.2/24"], "pk", "1.2.3.4:51820");

        var modified = original with { FullTunnel = false, SplitIPs = ["10.0.0.0/8"] };

        Assert.True(original.FullTunnel);
        Assert.False(modified.FullTunnel);
        Assert.NotNull(modified.SplitIPs);
    }
}
