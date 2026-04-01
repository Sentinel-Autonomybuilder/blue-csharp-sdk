using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sentinel.SDK.Core;

namespace Sentinel.SDK.Tunnel.V2Ray;

// ─── V2Ray Config ───

/// <summary>
/// Configuration for a V2Ray tunnel derived from a Sentinel node handshake.
/// </summary>
/// <param name="ServerHost">Node IP address or hostname.</param>
/// <param name="Port">Port from the handshake result.</param>
/// <param name="Protocol">"vless" or "vmess".</param>
/// <param name="Transport">Transport type: "tcp", "websocket", "grpc", "gun", "http", "mkcp", "quic", "ds".</param>
/// <param name="Tls">Whether TLS is enabled.</param>
/// <param name="Uuid">V2Ray UUID string.</param>
/// <param name="LocalSocksPort">Local SOCKS5 proxy listen port.</param>
public record V2RayConfig(
    string ServerHost,
    int Port,
    string Protocol,
    string Transport,
    bool Tls,
    string Uuid,
    int LocalSocksPort = 10808
);

/// <summary>
/// Result of building a V2Ray config, including the JSON and SOCKS5 authentication credentials.
/// </summary>
/// <param name="ConfigJson">Complete V2Ray JSON configuration string.</param>
/// <param name="SocksUser">SOCKS5 proxy username (null when systemProxy=true, uses noauth).</param>
/// <param name="SocksPass">SOCKS5 proxy password (null when systemProxy=true, uses noauth).</param>
public record V2RayConfigResult(
    string ConfigJson,
    string? SocksUser,
    string? SocksPass
);

// ─── V2Ray Config Builder ───

/// <summary>
/// Builds the JSON configuration for V2Ray, matching the Sentinel JS SDK format.
/// </summary>
/// <remarks>
/// Must match sentinel-go-sdk client.json.tmpl structure:
/// <list type="bullet">
///   <item>API inbound (dokodemo-door) for StatsService</item>
///   <item>SOCKS inbound with sniffing and password auth</item>
///   <item>VLess: encryption = "none", NO flow field</item>
///   <item>VMess: alterId = 0, NO security in user object</item>
///   <item>UUID field name must be "id" in V2Ray config (not "uuid")</item>
///   <item>TLS: allowInsecure = true, serverName = host</item>
///   <item>Routing: API tag to api, proxy tag to outbound</item>
///   <item>Policy with uplinkOnly/downlinkOnly = 0</item>
///   <item>Global transport section with quicSettings</item>
/// </list>
/// Transport mapping: 1=ds, 2=gun, 3=grpc, 4=http, 5=mkcp, 6=quic, 7=tcp, 8=websocket.
/// CRITICAL: gun (2) and grpc (3) are DIFFERENT protocols.
/// </remarks>
public static class V2RayConfigBuilder
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
    };

    /// <summary>
    /// Build a complete V2Ray JSON configuration string from the given parameters.
    /// Returns raw JSON string for backward compatibility. Uses password-authenticated SOCKS5.
    /// </summary>
    /// <param name="config">V2Ray configuration from a Sentinel node handshake.</param>
    /// <returns>JSON string ready to write to a config file.</returns>
    public static string BuildConfig(V2RayConfig config)
    {
        return BuildConfigWithAuth(config).ConfigJson;
    }

    /// <summary>
    /// Build a complete V2Ray JSON configuration with SOCKS5 authentication credentials.
    /// Matches the JS SDK's buildV2RayClientConfig() output exactly.
    /// </summary>
    /// <param name="config">V2Ray configuration from a Sentinel node handshake.</param>
    /// <returns>Config result including JSON string and SOCKS5 credentials.</returns>
    public static V2RayConfigResult BuildConfigWithAuth(V2RayConfig config, string? dnsOption = null, bool systemProxy = false)
    {
        // When systemProxy=true, OS proxy can't send SOCKS5 credentials → use noauth
        var socksUser = systemProxy ? null : Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        var socksPass = systemProxy ? null : Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

        var network = MapTransportToNetwork(config.Transport);
        var security = config.Tls ? "tls" : "none";
        var outboundTag = $"{config.ServerHost}_{config.Port}_{config.Protocol}_{network}_{security}";

        // Random API port — avoids Windows TIME_WAIT collisions
        var apiPort = 10000 + RandomNumberGenerator.GetInt32(50000);

        var root = new JsonObject
        {
            ["api"] = new JsonObject
            {
                ["services"] = new JsonArray { "StatsService" },
                ["tag"] = "api",
            },
            ["dns"] = BuildDnsSection(dnsOption),
            ["inbounds"] = BuildInbounds(config.LocalSocksPort, apiPort, socksUser, socksPass, systemProxy),
            ["log"] = new JsonObject { ["loglevel"] = "info" },
            ["outbounds"] = BuildOutbounds(config, outboundTag),
            ["routing"] = new JsonObject
            {
                ["domainStrategy"] = "IPIfNonMatch",
                ["rules"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["inboundTag"] = new JsonArray { "api" },
                        ["outboundTag"] = "api",
                        ["type"] = "field",
                    },
                    new JsonObject
                    {
                        ["inboundTag"] = new JsonArray { "proxy" },
                        ["outboundTag"] = outboundTag,
                        ["type"] = "field",
                    },
                },
            },
            ["policy"] = new JsonObject
            {
                ["levels"] = new JsonObject
                {
                    ["0"] = new JsonObject
                    {
                        ["downlinkOnly"] = 0,
                        ["uplinkOnly"] = 0,
                    },
                },
                ["system"] = new JsonObject
                {
                    ["statsOutboundDownlink"] = true,
                    ["statsOutboundUplink"] = true,
                },
            },
            ["stats"] = new JsonObject(),
            ["transport"] = new JsonObject
            {
                ["dsSettings"] = new JsonObject(),
                ["grpcSettings"] = new JsonObject(),
                ["gunSettings"] = new JsonObject(),
                ["httpSettings"] = new JsonObject(),
                ["kcpSettings"] = new JsonObject(),
                ["quicSettings"] = new JsonObject
                {
                    ["security"] = "none",
                    ["key"] = "",
                    ["header"] = new JsonObject { ["type"] = "none" },
                },
                ["tcpSettings"] = new JsonObject(),
                ["wsSettings"] = new JsonObject(),
            },
        };

        return new V2RayConfigResult(
            ConfigJson: root.ToJsonString(SerializerOptions),
            SocksUser: socksUser,
            SocksPass: socksPass
        );
    }

    /// <summary>
    /// Build a V2Ray JSON configuration with MULTIPLE outbounds — one per transport entry.
    /// Matches the JS SDK's buildV2RayClientConfig() which creates ALL outbounds from metadata
    /// and routes to the first (most reliable) by default. If that transport fails, V2Ray
    /// automatically tries subsequent outbounds via routing rules.
    /// </summary>
    /// <param name="configs">
    /// List of V2RayConfig entries (one per metadata entry), sorted by transport reliability (best first).
    /// All entries MUST share the same ServerHost and Uuid.
    /// </param>
    /// <returns>Config result including JSON string and SOCKS5 credentials.</returns>
    /// <exception cref="ArgumentException">Thrown when configs list is empty.</exception>
    public static V2RayConfigResult BuildMultiOutboundConfig(IReadOnlyList<V2RayConfig> configs, string? dnsOption = null, bool systemProxy = false)
    {
        if (configs.Count == 0)
        {
            throw new ArgumentException("At least one V2RayConfig entry is required", nameof(configs));
        }

        // Single entry — delegate to existing method
        if (configs.Count == 1)
        {
            return BuildConfigWithAuth(configs[0], dnsOption, systemProxy);
        }

        var socksUser = systemProxy ? null : Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        var socksPass = systemProxy ? null : Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var apiPort = 10000 + RandomNumberGenerator.GetInt32(50000);

        // Build one outbound per config entry, each with a unique tag
        var outboundsArray = new JsonArray();
        string? primaryTag = null;

        foreach (var config in configs)
        {
            var network = MapTransportToNetwork(config.Transport);
            var security = config.Tls ? "tls" : "none";
            var tag = $"{config.ServerHost}_{config.Port}_{config.Protocol}_{network}_{security}";

            var outbound = BuildSingleOutbound(config, tag);
            outboundsArray.Add(outbound);

            // First outbound is the primary (most reliable transport)
            primaryTag ??= tag;
        }

        var root = new JsonObject
        {
            ["api"] = new JsonObject
            {
                ["services"] = new JsonArray { "StatsService" },
                ["tag"] = "api",
            },
            ["dns"] = BuildDnsSection(dnsOption),
            ["inbounds"] = BuildInbounds(configs[0].LocalSocksPort, apiPort, socksUser, socksPass, systemProxy),
            ["log"] = new JsonObject { ["loglevel"] = "info" },
            ["outbounds"] = outboundsArray,
            ["routing"] = new JsonObject
            {
                ["domainStrategy"] = "IPIfNonMatch",
                ["rules"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["inboundTag"] = new JsonArray { "api" },
                        ["outboundTag"] = "api",
                        ["type"] = "field",
                    },
                    new JsonObject
                    {
                        ["inboundTag"] = new JsonArray { "proxy" },
                        ["outboundTag"] = primaryTag!,
                        ["type"] = "field",
                    },
                },
            },
            ["policy"] = new JsonObject
            {
                ["levels"] = new JsonObject
                {
                    ["0"] = new JsonObject
                    {
                        ["downlinkOnly"] = 0,
                        ["uplinkOnly"] = 0,
                    },
                },
                ["system"] = new JsonObject
                {
                    ["statsOutboundDownlink"] = true,
                    ["statsOutboundUplink"] = true,
                },
            },
            ["stats"] = new JsonObject(),
            ["transport"] = new JsonObject
            {
                ["dsSettings"] = new JsonObject(),
                ["grpcSettings"] = new JsonObject(),
                ["gunSettings"] = new JsonObject(),
                ["httpSettings"] = new JsonObject(),
                ["kcpSettings"] = new JsonObject(),
                ["quicSettings"] = new JsonObject
                {
                    ["security"] = "none",
                    ["key"] = "",
                    ["header"] = new JsonObject { ["type"] = "none" },
                },
                ["tcpSettings"] = new JsonObject(),
                ["wsSettings"] = new JsonObject(),
            },
        };

        return new V2RayConfigResult(
            ConfigJson: root.ToJsonString(SerializerOptions),
            SocksUser: socksUser,
            SocksPass: socksPass
        );
    }

    // ─── DNS ───

    /// <summary>
    /// Build the V2Ray DNS section with resolved DNS servers and fallbacks.
    /// </summary>
    private static JsonObject BuildDnsSection(string? dnsOption)
    {
        var resolved = Constants.DnsPresets.Resolve(dnsOption);
        var servers = new JsonArray();
        foreach (var server in resolved.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            servers.Add(server);
        }
        return new JsonObject { ["servers"] = servers };
    }

    // ─── Inbounds ───

    /// <summary>
    /// Build the inbounds section: dokodemo-door for API + SOCKS5 with sniffing.
    /// Matches sentinel-go-sdk client.json.tmpl structure.
    /// </summary>
    private static JsonArray BuildInbounds(int socksPort, int apiPort, string? socksUser, string? socksPass, bool useNoAuth = false)
    {
        return new JsonArray
        {
            // API inbound (dokodemo-door for StatsService)
            new JsonObject
            {
                ["listen"] = "127.0.0.1",
                ["port"] = apiPort,
                ["protocol"] = "dokodemo-door",
                ["settings"] = new JsonObject { ["address"] = "127.0.0.1" },
                ["tag"] = "api",
            },
            // SOCKS5 inbound with sniffing
            // When systemProxy (useNoAuth), OS proxy can't send SOCKS5 credentials → use noauth.
            // Otherwise password auth prevents other local processes from hijacking the tunnel.
            new JsonObject
            {
                ["listen"] = "127.0.0.1",
                ["port"] = socksPort,
                ["protocol"] = "socks",
                ["settings"] = useNoAuth
                    ? new JsonObject { ["auth"] = "noauth", ["ip"] = "127.0.0.1", ["udp"] = true }
                    : new JsonObject
                    {
                        ["auth"] = "password",
                        ["accounts"] = new JsonArray
                        {
                            new JsonObject { ["user"] = socksUser!, ["pass"] = socksPass! },
                        },
                        ["ip"] = "127.0.0.1",
                        ["udp"] = true,
                    },
                ["sniffing"] = new JsonObject
                {
                    ["enabled"] = true,
                    ["destOverride"] = new JsonArray { "http", "tls" },
                },
                ["tag"] = "proxy",
            },
        };
    }

    // ─── Outbounds ───

    /// <summary>
    /// Build the outbounds section with the proxy outbound.
    /// VLess: encryption = "none", NO flow.
    /// VMess: alterId = 0, NO security in user.
    /// </summary>
    private static JsonArray BuildOutbounds(V2RayConfig config, string tag)
    {
        var network = MapTransportToNetwork(config.Transport);

        var user = new JsonObject();

        if (config.Protocol == "vless")
        {
            user["id"] = config.Uuid;
            user["encryption"] = "none";
            // NO flow field — this is non-negotiable
        }
        else
        {
            // vmess
            user["id"] = config.Uuid;
            user["alterId"] = 0;
            // NO security field — this is non-negotiable
        }

        var settings = new JsonObject
        {
            ["vnext"] = new JsonArray
            {
                new JsonObject
                {
                    ["address"] = config.ServerHost,
                    ["port"] = config.Port,
                    ["users"] = new JsonArray { user },
                },
            },
        };

        var streamSettings = BuildStreamSettings(config, network);

        var proxyOutbound = new JsonObject
        {
            ["tag"] = tag,
            ["protocol"] = config.Protocol,
            ["settings"] = settings,
            ["streamSettings"] = streamSettings,
        };

        return new JsonArray { proxyOutbound };
    }

    /// <summary>
    /// Build a single outbound JsonObject for use in multi-outbound configs.
    /// Same logic as <see cref="BuildOutbounds"/> but returns one outbound object
    /// instead of a JsonArray wrapping it.
    /// </summary>
    private static JsonObject BuildSingleOutbound(V2RayConfig config, string tag)
    {
        var network = MapTransportToNetwork(config.Transport);

        var user = new JsonObject();

        if (config.Protocol == "vless")
        {
            user["id"] = config.Uuid;
            user["encryption"] = "none";
        }
        else
        {
            user["id"] = config.Uuid;
            user["alterId"] = 0;
        }

        var settings = new JsonObject
        {
            ["vnext"] = new JsonArray
            {
                new JsonObject
                {
                    ["address"] = config.ServerHost,
                    ["port"] = config.Port,
                    ["users"] = new JsonArray { user },
                },
            },
        };

        var streamSettings = BuildStreamSettings(config, network);

        return new JsonObject
        {
            ["tag"] = tag,
            ["protocol"] = config.Protocol,
            ["settings"] = settings,
            ["streamSettings"] = streamSettings,
        };
    }

    // ─── Stream Settings ───

    /// <summary>
    /// Build stream settings for the given transport and TLS configuration.
    /// TLS: allowInsecure = true, serverName = host (fixes TLS SNI for grpc/tls nodes).
    /// </summary>
    private static JsonObject BuildStreamSettings(V2RayConfig config, string network)
    {
        var stream = new JsonObject
        {
            ["network"] = network,
        };

        // ─── TLS settings ───
        if (config.Tls)
        {
            stream["security"] = "tls";
            stream["tlsSettings"] = new JsonObject
            {
                ["allowInsecure"] = true,
                ["serverName"] = config.ServerHost,
            };
        }
        else
        {
            stream["security"] = "none";
        }

        // ─── Transport-specific settings (per-outbound) ───
        // Only add the minimal required settings for grpc/gun (serviceName).
        // Other transports: no per-outbound settings needed (global transport section handles them).
        if (network == "grpc" || network == "gun")
        {
            stream["grpcSettings"] = new JsonObject { ["serviceName"] = "" };
        }

        if (network == "quic")
        {
            stream["quicSettings"] = new JsonObject
            {
                ["security"] = "none",
                ["key"] = "",
                ["header"] = new JsonObject { ["type"] = "none" },
            };
        }

        return stream;
    }

    // ─── Transport Mapping ───

    /// <summary>
    /// Map a Sentinel transport name to the V2Ray network name.
    /// Transport mapping: 1=ds, 2=gun, 3=grpc, 4=http, 5=mkcp, 6=quic, 7=tcp, 8=websocket.
    /// CRITICAL: gun (2) and grpc (3) are DIFFERENT protocols.
    /// </summary>
    /// <param name="transport">Transport name from the handshake (e.g. "tcp", "ws", "grpc", "gun").</param>
    /// <returns>V2Ray network identifier string.</returns>
    private static string MapTransportToNetwork(string transport)
    {
        return transport.ToLowerInvariant() switch
        {
            "ds" or "domainsocket" => "ds",
            "gun" => "gun",
            "grpc" => "grpc",
            "http" => "http",
            "kcp" or "mkcp" => "mkcp",
            "quic" => "quic",
            "tcp" => "tcp",
            "ws" or "websocket" => "websocket",
            _ => transport.ToLowerInvariant(),
        };
    }
}
