using System.Text.Json;
using Sentinel.SDK.Tunnel.V2Ray;
using Xunit;

namespace Sentinel.SDK.Tests;

/// <summary>
/// Tests for V2RayConfigBuilder — validates JSON config generation
/// matching the Sentinel JS SDK format and non-negotiable rules.
/// </summary>
public class V2RayConfigTests
{
    // ─── Helpers ───

    private static V2RayConfig MakeConfig(
        string protocol = "vless",
        string transport = "tcp",
        bool tls = false,
        int port = 443,
        int socksPort = 10808)
    {
        return new V2RayConfig(
            ServerHost: "1.2.3.4",
            Port: port,
            Protocol: protocol,
            Transport: transport,
            Tls: tls,
            Uuid: "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
            LocalSocksPort: socksPort
        );
    }

    private static JsonElement ParseConfig(string json)
    {
        return JsonDocument.Parse(json).RootElement;
    }

    /// <summary>Find the SOCKS5 inbound in the inbounds array (tagged "proxy").</summary>
    private static JsonElement FindSocksInbound(JsonElement root)
    {
        var inbounds = root.GetProperty("inbounds");
        for (var i = 0; i < inbounds.GetArrayLength(); i++)
        {
            if (inbounds[i].GetProperty("protocol").GetString() == "socks")
                return inbounds[i];
        }
        throw new System.Exception("No SOCKS inbound found");
    }

    /// <summary>Get the first (proxy) outbound.</summary>
    private static JsonElement GetProxyOutbound(JsonElement root)
    {
        return root.GetProperty("outbounds")[0];
    }

    // ─── Valid JSON Output ───

    [Fact]
    public void BuildConfig_ProducesValidJson()
    {
        var config = MakeConfig();
        var json = V2RayConfigBuilder.BuildConfig(config);

        Assert.NotNull(json);
        Assert.NotEmpty(json);

        // Should parse without throwing
        var doc = JsonDocument.Parse(json);
        Assert.NotNull(doc);
    }

    [Fact]
    public void BuildConfig_HasRequiredTopLevelKeys()
    {
        var json = V2RayConfigBuilder.BuildConfig(MakeConfig());
        var root = ParseConfig(json);

        Assert.True(root.TryGetProperty("api", out _));
        Assert.True(root.TryGetProperty("log", out _));
        Assert.True(root.TryGetProperty("inbounds", out _));
        Assert.True(root.TryGetProperty("outbounds", out _));
        Assert.True(root.TryGetProperty("routing", out _));
        Assert.True(root.TryGetProperty("policy", out _));
        Assert.True(root.TryGetProperty("stats", out _));
        Assert.True(root.TryGetProperty("transport", out _));
    }

    [Fact]
    public void BuildConfig_LogLevelIsInfo()
    {
        var json = V2RayConfigBuilder.BuildConfig(MakeConfig());
        var root = ParseConfig(json);

        var logLevel = root.GetProperty("log").GetProperty("loglevel").GetString();
        Assert.Equal("info", logLevel);
    }

    // ─── API Section ───

    [Fact]
    public void BuildConfig_HasApiSection()
    {
        var json = V2RayConfigBuilder.BuildConfig(MakeConfig());
        var root = ParseConfig(json);

        var api = root.GetProperty("api");
        Assert.Equal("api", api.GetProperty("tag").GetString());
        var services = api.GetProperty("services");
        Assert.Equal(1, services.GetArrayLength());
        Assert.Equal("StatsService", services[0].GetString());
    }

    // ─── Inbounds ───

    [Fact]
    public void BuildConfig_HasTwoInbounds()
    {
        var json = V2RayConfigBuilder.BuildConfig(MakeConfig());
        var root = ParseConfig(json);
        var inbounds = root.GetProperty("inbounds");

        Assert.Equal(2, inbounds.GetArrayLength());

        // First: dokodemo-door for API
        Assert.Equal("dokodemo-door", inbounds[0].GetProperty("protocol").GetString());
        Assert.Equal("api", inbounds[0].GetProperty("tag").GetString());

        // Second: SOCKS5 proxy with sniffing
        Assert.Equal("socks", inbounds[1].GetProperty("protocol").GetString());
        Assert.Equal("proxy", inbounds[1].GetProperty("tag").GetString());
    }

    [Fact]
    public void BuildConfig_SocksInboundHasSniffing()
    {
        var json = V2RayConfigBuilder.BuildConfig(MakeConfig());
        var root = ParseConfig(json);
        var socks = FindSocksInbound(root);

        Assert.True(socks.TryGetProperty("sniffing", out var sniffing));
        Assert.True(sniffing.GetProperty("enabled").GetBoolean());
        var destOverride = sniffing.GetProperty("destOverride");
        Assert.Equal(2, destOverride.GetArrayLength());
    }

    [Fact]
    public void BuildConfig_SocksInboundUsesCorrectPort()
    {
        var config = MakeConfig(socksPort: 12345);
        var json = V2RayConfigBuilder.BuildConfig(config);
        var root = ParseConfig(json);

        var port = FindSocksInbound(root).GetProperty("port").GetInt32();
        Assert.Equal(12345, port);
    }

    [Fact]
    public void BuildConfig_SocksInboundDefaultPort()
    {
        var config = new V2RayConfig("1.2.3.4", 443, "vless", "tcp", false,
            "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var json = V2RayConfigBuilder.BuildConfig(config);
        var root = ParseConfig(json);

        var port = FindSocksInbound(root).GetProperty("port").GetInt32();
        Assert.Equal(10808, port);
    }

    [Fact]
    public void BuildConfig_SocksPasswordAuth()
    {
        var json = V2RayConfigBuilder.BuildConfig(MakeConfig());
        var root = ParseConfig(json);
        var settings = FindSocksInbound(root).GetProperty("settings");

        // SOCKS5 must use password auth to prevent open-proxy exploitation
        Assert.Equal("password", settings.GetProperty("auth").GetString());
        Assert.True(settings.GetProperty("udp").GetBoolean());

        // Must have an accounts array with user/pass
        var accounts = settings.GetProperty("accounts");
        Assert.Equal(JsonValueKind.Array, accounts.ValueKind);
        Assert.True(accounts.GetArrayLength() >= 1);

        var account = accounts[0];
        Assert.True(account.TryGetProperty("user", out var user));
        Assert.True(account.TryGetProperty("pass", out var pass));
        Assert.False(string.IsNullOrEmpty(user.GetString()));
        Assert.False(string.IsNullOrEmpty(pass.GetString()));
    }

    [Fact]
    public void BuildConfigWithAuth_ReturnsCredentials()
    {
        var config = MakeConfig();
        var result = V2RayConfigBuilder.BuildConfigWithAuth(config);

        Assert.NotNull(result.ConfigJson);
        Assert.NotEmpty(result.ConfigJson);
        Assert.NotNull(result.SocksUser);
        Assert.NotNull(result.SocksPass);
        // Hex-encoded random bytes: 8 bytes = 16 hex chars, 16 bytes = 32 hex chars
        Assert.Equal(16, result.SocksUser.Length);
        Assert.Equal(32, result.SocksPass.Length);

        // Credentials should match what's in the config JSON
        var root = ParseConfig(result.ConfigJson);
        var account = FindSocksInbound(root)
            .GetProperty("settings")
            .GetProperty("accounts")[0];

        Assert.Equal(result.SocksUser, account.GetProperty("user").GetString());
        Assert.Equal(result.SocksPass, account.GetProperty("pass").GetString());
    }

    // ─── VLess Protocol (Non-Negotiable Rules) ───

    [Fact]
    public void BuildConfig_VLess_EncryptionIsNone()
    {
        var json = V2RayConfigBuilder.BuildConfig(MakeConfig(protocol: "vless"));
        var root = ParseConfig(json);

        var user = GetProxyOutbound(root)
            .GetProperty("settings")
            .GetProperty("vnext")[0]
            .GetProperty("users")[0];

        Assert.Equal("none", user.GetProperty("encryption").GetString());
    }

    [Fact]
    public void BuildConfig_VLess_NoFlowField()
    {
        var json = V2RayConfigBuilder.BuildConfig(MakeConfig(protocol: "vless"));
        var root = ParseConfig(json);

        var user = GetProxyOutbound(root)
            .GetProperty("settings")
            .GetProperty("vnext")[0]
            .GetProperty("users")[0];

        // flow field MUST NOT exist — this is non-negotiable
        Assert.False(user.TryGetProperty("flow", out _),
            "VLess config must NOT have a 'flow' field");
    }

    [Fact]
    public void BuildConfig_VLess_IdFieldContainsUuid()
    {
        var json = V2RayConfigBuilder.BuildConfig(MakeConfig(protocol: "vless"));
        var root = ParseConfig(json);

        var user = GetProxyOutbound(root)
            .GetProperty("settings")
            .GetProperty("vnext")[0]
            .GetProperty("users")[0];

        // V2Ray uses "id" field name for the UUID in protocol settings
        Assert.True(user.TryGetProperty("id", out var idEl));
        Assert.Equal("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", idEl.GetString());
    }

    // ─── VMess Protocol (Non-Negotiable Rules) ───

    [Fact]
    public void BuildConfig_VMess_AlterIdIsZero()
    {
        var json = V2RayConfigBuilder.BuildConfig(MakeConfig(protocol: "vmess"));
        var root = ParseConfig(json);

        var user = GetProxyOutbound(root)
            .GetProperty("settings")
            .GetProperty("vnext")[0]
            .GetProperty("users")[0];

        Assert.Equal(0, user.GetProperty("alterId").GetInt32());
    }

    [Fact]
    public void BuildConfig_VMess_NoSecurityInUser()
    {
        var json = V2RayConfigBuilder.BuildConfig(MakeConfig(protocol: "vmess"));
        var root = ParseConfig(json);

        var user = GetProxyOutbound(root)
            .GetProperty("settings")
            .GetProperty("vnext")[0]
            .GetProperty("users")[0];

        // security field MUST NOT exist in user object — this is non-negotiable
        Assert.False(user.TryGetProperty("security", out _),
            "VMess user must NOT have a 'security' field");
    }

    [Fact]
    public void BuildConfig_VMess_HasIdField()
    {
        var json = V2RayConfigBuilder.BuildConfig(MakeConfig(protocol: "vmess"));
        var root = ParseConfig(json);

        var user = GetProxyOutbound(root)
            .GetProperty("settings")
            .GetProperty("vnext")[0]
            .GetProperty("users")[0];

        Assert.True(user.TryGetProperty("id", out _));
    }

    // ─── Server Configuration ───

    [Fact]
    public void BuildConfig_VnextHasCorrectServerAddress()
    {
        var config = new V2RayConfig("5.6.7.8", 8443, "vless", "tcp", false,
            "test-uuid-1234");
        var json = V2RayConfigBuilder.BuildConfig(config);
        var root = ParseConfig(json);

        var vnext = GetProxyOutbound(root)
            .GetProperty("settings")
            .GetProperty("vnext")[0];

        Assert.Equal("5.6.7.8", vnext.GetProperty("address").GetString());
        Assert.Equal(8443, vnext.GetProperty("port").GetInt32());
    }

    // ─── Transport Mapping ───

    [Theory]
    [InlineData("tcp", "tcp")]
    [InlineData("ws", "websocket")]
    [InlineData("websocket", "websocket")]
    [InlineData("grpc", "grpc")]
    [InlineData("gun", "gun")]
    [InlineData("http", "http")]
    [InlineData("kcp", "mkcp")]
    [InlineData("mkcp", "mkcp")]
    [InlineData("quic", "quic")]
    [InlineData("ds", "ds")]
    [InlineData("domainsocket", "ds")]
    public void BuildConfig_TransportMapping(string input, string expectedNetwork)
    {
        var config = MakeConfig(transport: input);
        var json = V2RayConfigBuilder.BuildConfig(config);
        var root = ParseConfig(json);

        var stream = GetProxyOutbound(root).GetProperty("streamSettings");
        var network = stream.GetProperty("network").GetString();
        Assert.Equal(expectedNetwork, network);
    }

    [Fact]
    public void BuildConfig_GunAndGrpcAreDifferent()
    {
        // CRITICAL: gun (2) and grpc (3) are DIFFERENT protocols
        var gunJson = V2RayConfigBuilder.BuildConfig(MakeConfig(transport: "gun"));
        var grpcJson = V2RayConfigBuilder.BuildConfig(MakeConfig(transport: "grpc"));

        var gunRoot = ParseConfig(gunJson);
        var grpcRoot = ParseConfig(grpcJson);

        var gunNetwork = GetProxyOutbound(gunRoot)
            .GetProperty("streamSettings").GetProperty("network").GetString();
        var grpcNetwork = GetProxyOutbound(grpcRoot)
            .GetProperty("streamSettings").GetProperty("network").GetString();

        Assert.Equal("gun", gunNetwork);
        Assert.Equal("grpc", grpcNetwork);
        Assert.NotEqual(gunNetwork, grpcNetwork);
    }

    // ─── TLS Settings ───

    [Fact]
    public void BuildConfig_TlsEnabled_HasTlsSettings()
    {
        var json = V2RayConfigBuilder.BuildConfig(MakeConfig(tls: true));
        var root = ParseConfig(json);

        var stream = GetProxyOutbound(root).GetProperty("streamSettings");
        Assert.Equal("tls", stream.GetProperty("security").GetString());
        Assert.True(stream.TryGetProperty("tlsSettings", out var tlsSettings));
        Assert.True(tlsSettings.GetProperty("allowInsecure").GetBoolean());
    }

    [Fact]
    public void BuildConfig_TlsEnabled_HasServerName()
    {
        // serverName in tlsSettings is required for TLS SNI (fixes grpc/tls nodes)
        var json = V2RayConfigBuilder.BuildConfig(MakeConfig(tls: true));
        var root = ParseConfig(json);

        var tlsSettings = GetProxyOutbound(root)
            .GetProperty("streamSettings")
            .GetProperty("tlsSettings");

        Assert.True(tlsSettings.TryGetProperty("serverName", out var sn));
        Assert.Equal("1.2.3.4", sn.GetString());
    }

    [Fact]
    public void BuildConfig_TlsDisabled_SecurityIsNone()
    {
        var json = V2RayConfigBuilder.BuildConfig(MakeConfig(tls: false));
        var root = ParseConfig(json);

        var stream = GetProxyOutbound(root).GetProperty("streamSettings");
        Assert.Equal("none", stream.GetProperty("security").GetString());
    }

    [Fact]
    public void BuildConfig_TlsDisabled_NoTlsSettingsObject()
    {
        var json = V2RayConfigBuilder.BuildConfig(MakeConfig(tls: false));
        var root = ParseConfig(json);

        var stream = GetProxyOutbound(root).GetProperty("streamSettings");
        Assert.False(stream.TryGetProperty("tlsSettings", out _));
    }

    // ─── Transport-Specific Settings ───

    [Fact]
    public void BuildConfig_Grpc_HasGrpcSettings()
    {
        var json = V2RayConfigBuilder.BuildConfig(MakeConfig(transport: "grpc"));
        var root = ParseConfig(json);

        var stream = GetProxyOutbound(root).GetProperty("streamSettings");
        Assert.True(stream.TryGetProperty("grpcSettings", out var grpc));
        Assert.True(grpc.TryGetProperty("serviceName", out _));
    }

    [Fact]
    public void BuildConfig_Gun_HasGrpcSettings()
    {
        // gun also uses grpcSettings
        var json = V2RayConfigBuilder.BuildConfig(MakeConfig(transport: "gun"));
        var root = ParseConfig(json);

        var stream = GetProxyOutbound(root).GetProperty("streamSettings");
        Assert.True(stream.TryGetProperty("grpcSettings", out var grpc));
        Assert.True(grpc.TryGetProperty("serviceName", out _));
    }

    [Fact]
    public void BuildConfig_Tcp_NoExtraTransportSettings()
    {
        var json = V2RayConfigBuilder.BuildConfig(MakeConfig(transport: "tcp"));
        var root = ParseConfig(json);

        var stream = GetProxyOutbound(root).GetProperty("streamSettings");

        // TCP should NOT have any transport-specific settings on the outbound
        Assert.False(stream.TryGetProperty("tcpSettings", out _));
        Assert.False(stream.TryGetProperty("wsSettings", out _));
        Assert.False(stream.TryGetProperty("grpcSettings", out _));
        Assert.False(stream.TryGetProperty("gunSettings", out _));
    }

    [Fact]
    public void BuildConfig_Quic_HasQuicSettings()
    {
        var json = V2RayConfigBuilder.BuildConfig(MakeConfig(transport: "quic"));
        var root = ParseConfig(json);

        var stream = GetProxyOutbound(root).GetProperty("streamSettings");
        Assert.True(stream.TryGetProperty("quicSettings", out var quic));
        Assert.Equal("none", quic.GetProperty("security").GetString());
    }

    // ─── Global Transport Section ───

    [Fact]
    public void BuildConfig_HasGlobalTransport()
    {
        var json = V2RayConfigBuilder.BuildConfig(MakeConfig());
        var root = ParseConfig(json);

        var transport = root.GetProperty("transport");
        Assert.True(transport.TryGetProperty("quicSettings", out var quic));
        Assert.Equal("none", quic.GetProperty("security").GetString());
    }

    // ─── Routing ───

    [Fact]
    public void BuildConfig_HasRoutingRules()
    {
        var json = V2RayConfigBuilder.BuildConfig(MakeConfig());
        var root = ParseConfig(json);

        var routing = root.GetProperty("routing");
        Assert.Equal("IPIfNonMatch", routing.GetProperty("domainStrategy").GetString());
        var rules = routing.GetProperty("rules");
        Assert.Equal(2, rules.GetArrayLength());

        // First rule: api inbound -> api tag
        Assert.Equal("api", rules[0].GetProperty("outboundTag").GetString());
        // Second rule: proxy inbound -> outbound tag
        Assert.Equal("field", rules[1].GetProperty("type").GetString());
    }

    // ─── Policy ───

    [Fact]
    public void BuildConfig_HasPolicy()
    {
        var json = V2RayConfigBuilder.BuildConfig(MakeConfig());
        var root = ParseConfig(json);

        var policy = root.GetProperty("policy");
        var level0 = policy.GetProperty("levels").GetProperty("0");
        Assert.Equal(0, level0.GetProperty("downlinkOnly").GetInt32());
        Assert.Equal(0, level0.GetProperty("uplinkOnly").GetInt32());

        var system = policy.GetProperty("system");
        Assert.True(system.GetProperty("statsOutboundDownlink").GetBoolean());
        Assert.True(system.GetProperty("statsOutboundUplink").GetBoolean());
    }

    // ─── Outbound Structure ───

    [Fact]
    public void BuildConfig_HasOneOutbound()
    {
        var json = V2RayConfigBuilder.BuildConfig(MakeConfig());
        var root = ParseConfig(json);

        var outbounds = root.GetProperty("outbounds");
        Assert.Equal(1, outbounds.GetArrayLength());
    }

    [Fact]
    public void BuildConfig_ProxyOutbound_ProtocolMatchesInput()
    {
        var vlessJson = V2RayConfigBuilder.BuildConfig(MakeConfig(protocol: "vless"));
        var vmessJson = V2RayConfigBuilder.BuildConfig(MakeConfig(protocol: "vmess"));

        var vlessRoot = ParseConfig(vlessJson);
        var vmessRoot = ParseConfig(vmessJson);

        Assert.Equal("vless", GetProxyOutbound(vlessRoot).GetProperty("protocol").GetString());
        Assert.Equal("vmess", GetProxyOutbound(vmessRoot).GetProperty("protocol").GetString());
    }

    // ─── V2RayConfig Record ───

    [Fact]
    public void V2RayConfig_Record_DefaultSocksPort()
    {
        var config = new V2RayConfig("host", 443, "vless", "tcp", false, "uuid");
        Assert.Equal(10808, config.LocalSocksPort);
    }

    [Fact]
    public void V2RayConfig_Record_CustomSocksPort()
    {
        var config = new V2RayConfig("host", 443, "vless", "tcp", false, "uuid", 9090);
        Assert.Equal(9090, config.LocalSocksPort);
    }

    [Fact]
    public void V2RayConfig_Record_PropertiesPreserved()
    {
        var config = new V2RayConfig("10.0.0.1", 8443, "vmess", "grpc", true, "my-uuid", 5555);

        Assert.Equal("10.0.0.1", config.ServerHost);
        Assert.Equal(8443, config.Port);
        Assert.Equal("vmess", config.Protocol);
        Assert.Equal("grpc", config.Transport);
        Assert.True(config.Tls);
        Assert.Equal("my-uuid", config.Uuid);
        Assert.Equal(5555, config.LocalSocksPort);
    }

    // ─── Combination Tests ───

    [Fact]
    public void BuildConfig_VLess_Grpc_Tls_FullConfig()
    {
        var config = new V2RayConfig("1.2.3.4", 443, "vless", "grpc", true,
            "12345678-1234-1234-1234-123456789012");
        var json = V2RayConfigBuilder.BuildConfig(config);
        var root = ParseConfig(json);

        // Verify the complete config is valid
        var outbound = GetProxyOutbound(root);
        Assert.Equal("vless", outbound.GetProperty("protocol").GetString());

        var stream = outbound.GetProperty("streamSettings");
        Assert.Equal("grpc", stream.GetProperty("network").GetString());
        Assert.Equal("tls", stream.GetProperty("security").GetString());

        var user = outbound.GetProperty("settings").GetProperty("vnext")[0]
            .GetProperty("users")[0];
        Assert.Equal("none", user.GetProperty("encryption").GetString());
    }

    [Fact]
    public void BuildConfig_VMess_Ws_NoTls_FullConfig()
    {
        var config = new V2RayConfig("5.6.7.8", 80, "vmess", "ws", false,
            "abcdefab-abcd-abcd-abcd-abcdefabcdef");
        var json = V2RayConfigBuilder.BuildConfig(config);
        var root = ParseConfig(json);

        var outbound = GetProxyOutbound(root);
        Assert.Equal("vmess", outbound.GetProperty("protocol").GetString());

        var stream = outbound.GetProperty("streamSettings");
        Assert.Equal("websocket", stream.GetProperty("network").GetString());
        Assert.Equal("none", stream.GetProperty("security").GetString());

        var user = outbound.GetProperty("settings").GetProperty("vnext")[0]
            .GetProperty("users")[0];
        Assert.Equal(0, user.GetProperty("alterId").GetInt32());
    }
}
