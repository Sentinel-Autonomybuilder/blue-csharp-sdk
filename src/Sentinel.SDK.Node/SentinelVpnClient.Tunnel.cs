using System.Net;
using Sentinel.SDK.Core;
using Sentinel.SDK.Tunnel.V2Ray;
using Sentinel.SDK.Tunnel.WireGuard;

namespace Sentinel.SDK.Node;

// ─── Tunnel Installation & Fast Reconnect ───────────────────────────────────

public partial class SentinelVpnClient
{
    /// <summary>
    /// Install a WireGuard tunnel from the handshake result.
    /// </summary>
    private async Task<ConnectionResult> InstallWireGuardTunnelAsync(
        WireGuardHandshakeResult wgResult,
        ulong sessionId,
        string nodeAddress,
        CancellationToken ct)
    {
        // Wait for node to register our peer before installing tunnel
        // The node needs 1-5s to add the peer to its WireGuard interface after handshake
        EmitProgress("tunnel", "Waiting for node to register peer...");
        await Task.Delay(5000, ct);

        EmitProgress("tunnel", "Installing WireGuard tunnel...");

        var config = new WireGuardConfig(
            ClientPrivateKey: wgResult.ClientPrivateKey,
            AssignedAddresses: wgResult.AssignedAddresses,
            ServerPublicKey: wgResult.ServerPublicKey,
            ServerEndpoint: wgResult.ServerEndpoint,
            FullTunnel: _options.FullTunnel
        ) { Dns = Constants.DnsPresets.Resolve(_options.Dns) };

        _wgTunnel = new WireGuardTunnel();
        await _wgTunnel.InstallAsync(config, ct);
        ct.ThrowIfCancellationRequested();

        // Extract the first assigned IPv4 address (strip CIDR)
        string? vpnIp = null;
        foreach (var addr in wgResult.AssignedAddresses)
        {
            if (addr.Contains('.'))
            {
                vpnIp = addr.Split('/')[0];
                break;
            }
        }

        EmitProgress("tunnel", $"WireGuard tunnel active, VPN IP: {vpnIp}");

        return new ConnectionResult
        {
            SessionId = sessionId.ToString(),
            NodeAddress = nodeAddress,
            ServiceType = "wireguard",
            VpnIp = vpnIp,
        };
    }

    /// <summary>
    /// Install a V2Ray tunnel from the handshake result.
    /// Matches JS SDK behavior: builds one V2Ray config per transport entry and tests
    /// each outbound sequentially until one connects. This is critical because V2Ray
    /// does not auto-fallback between outbounds via routing rules alone — the application
    /// layer must restart V2Ray with each individual outbound config.
    /// </summary>
    /// <remarks>
    /// When <paramref name="extremeDrift"/> is true:
    /// <list type="bullet">
    ///   <item>If the node has VLess transports (proxy_protocol=1), strips all VMess
    ///         outbounds and reorders VLess first. VLess is immune to clock drift.</item>
    ///   <item>If VMess-only (no VLess available), throws <c>CLOCK_DRIFT_TOO_HIGH</c>
    ///         because VMess AEAD rejects packets with &gt;120s drift.</item>
    /// </list>
    /// </remarks>
    private async Task<ConnectionResult> InstallV2RayTunnelAsync(
        V2RayHandshakeResult v2Result,
        ulong sessionId,
        string nodeAddress,
        string nodeUrl,
        bool extremeDrift,
        double? clockDriftSec,
        CancellationToken ct)
    {
        EmitProgress("tunnel", "Starting V2Ray process...");

        // Extract host from the node URL
        var uri = new Uri(nodeUrl);
        var serverHost = uri.Host;

        // ─── Build V2RayConfig list from all transport entries ───
        // Use incrementing SOCKS ports to avoid TIME_WAIT collisions on Windows.
        // After killing V2Ray, the previous port stays in TIME_WAIT (~120s on Windows).
        // Each outbound attempt gets its own port so fallback works immediately.
        var baseSocksPort = DEFAULT_SOCKS_PORT + Random.Shared.Next(0, 100);
        var configs = new List<V2RayConfig>(v2Result.AllEntries.Count);
        for (var idx = 0; idx < v2Result.AllEntries.Count; idx++)
        {
            var entry = v2Result.AllEntries[idx];
            var entryProtocol = entry.ProxyProtocol == 1 ? "vless" : "vmess";
            var entryTransport = MapTransportNumber(entry.Transport);
            configs.Add(new V2RayConfig(
                ServerHost: serverHost,
                Port: entry.Port,
                Protocol: entryProtocol,
                Transport: entryTransport,
                Tls: entry.Tls == 1,
                Uuid: v2Result.Uuid,
                LocalSocksPort: baseSocksPort + idx
            ));
        }

        // ─── Clock drift handling: strip VMess when drift >120s ───
        // VLess (proxy_protocol=1) is immune to clock drift; VMess (2) uses AEAD timestamps.
        if (extremeDrift)
        {
            var hasVless = configs.Any(c => c.Protocol == "vless");
            if (!hasVless)
            {
                throw new SentinelNodeException(
                    ErrorCodes.NodeClockDrift,
                    $"VMess-only node with clock drift {clockDriftSec:F0}s " +
                    "(AEAD tolerance +/-120s, no VLess available). " +
                    "Choose a different node."
                );
            }

            // Strip VMess outbounds and reorder VLess first
            var vlessCount = configs.Count(c => c.Protocol == "vless");
            configs = configs.Where(c => c.Protocol == "vless").ToList();
            _logger.Info($"Clock drift {clockDriftSec:F0}s: stripped VMess outbounds, {vlessCount} VLess remaining");
        }

        _logger.Info($"Testing {configs.Count} V2Ray transport(s) sequentially");

        // ─── Sequential transport fallback (matches JS SDK) ───
        // Try each outbound individually: write single-outbound config, start V2Ray,
        // test SOCKS5 connectivity. If it fails, kill V2Ray and try the next outbound.
        // NEVER use balancer/observatory — causes session poisoning.
        V2RayProcess? workingProcess = null;

        for (var i = 0; i < configs.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var config = configs[i];
            var transportLabel = $"{config.Protocol}/{config.Transport}/{(config.Tls ? "tls" : "none")}";
            EmitProgress("tunnel", $"  Trying outbound {i + 1}/{configs.Count}: {transportLabel} (port {config.Port})");

            // Kill previous V2Ray process if any
            if (_v2RayProcess is not null && _v2RayProcess.IsRunning)
            {
                await _v2RayProcess.StopAsync(ct);
                _v2RayProcess.Dispose();
                _v2RayProcess = null;
                // Brief pause for port release
                await Task.Delay(1000, ct);
            }

            _v2RayProcess = new V2RayProcess(_options.V2RayExePath!);

            try
            {
                await _v2RayProcess.StartAsync(config, ct, _options.Dns, _options.SystemProxy);
            }
            catch (SentinelException ex)
            {
                _logger.Warn($"  {transportLabel}: V2Ray start failed — {ex.Message}");
                EmitProgress("tunnel", $"  {transportLabel}: failed ({ex.Code})");
                continue;
            }

            // V2Ray started and SOCKS5 port is accepting connections.
            // Test actual connectivity through the proxy.
            var connected = await TestSocksConnectivityAsync(
                _v2RayProcess.SocksPort,
                _v2RayProcess.SocksUser,
                _v2RayProcess.SocksPass,
                ct
            );

            if (connected)
            {
                _logger.Info($"  {transportLabel}: connected!");
                EmitProgress("tunnel", $"  {transportLabel}: connected!");
                workingProcess = _v2RayProcess;
                break;
            }

            _logger.Warn($"  {transportLabel}: SOCKS5 port open but no connectivity");
            EmitProgress("tunnel", $"  {transportLabel}: failed (no connectivity)");
        }

        if (workingProcess is null)
        {
            // All outbounds failed — clean up
            if (_v2RayProcess is not null)
            {
                await _v2RayProcess.StopAsync(ct);
                _v2RayProcess.Dispose();
                _v2RayProcess = null;
            }

            throw new TunnelException(
                "V2RAY_ALL_FAILED",
                $"All {configs.Count} V2Ray transport/protocol combinations failed on {nodeAddress}"
            );
        }

        ct.ThrowIfCancellationRequested();

        // Set system SOCKS5 proxy so all traffic routes through V2Ray
        if (_options.SystemProxy)
        {
            SystemProxy.Set(_v2RayProcess!.SocksPort);
            _systemProxySet = true;
            EmitProgress("tunnel", $"System proxy set to SOCKS5 127.0.0.1:{_v2RayProcess.SocksPort}");
        }

        EmitProgress("tunnel", $"V2Ray active on SOCKS5 port {_v2RayProcess!.SocksPort}");

        return new ConnectionResult
        {
            SessionId = sessionId.ToString(),
            NodeAddress = nodeAddress,
            ServiceType = "v2ray",
            SocksPort = _v2RayProcess.SocksPort,
            SocksUser = _v2RayProcess.SocksUser,
            SocksPass = _v2RayProcess.SocksPass,
        };
    }

    /// <summary>
    /// Test connectivity through a local SOCKS5 proxy by making an HTTP request
    /// to a reliable target. Matches JS SDK's per-outbound connectivity test.
    /// </summary>
    private static async Task<bool> TestSocksConnectivityAsync(
        int socksPort, string? socksUser, string? socksPass, CancellationToken ct)
    {
        var targets = new[] { "https://www.google.com", "https://www.cloudflare.com" };

        foreach (var target in targets)
        {
            try
            {
                var proxy = new WebProxy($"socks5://127.0.0.1:{socksPort}");
                if (!string.IsNullOrEmpty(socksUser) && !string.IsNullOrEmpty(socksPass))
                {
                    proxy.Credentials = new NetworkCredential(socksUser, socksPass);
                }

                using var handler = new HttpClientHandler { Proxy = proxy, UseProxy = true };
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
                var response = await client.GetAsync(target, ct);
                // Any HTTP response (even 403, 301) means the tunnel is working
                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Try next target
            }
        }

        return false;
    }

    /// <summary>
    /// Fast reconnect using saved credentials — skips payment AND handshake entirely.
    /// Builds the tunnel configuration from saved keys/metadata and installs the tunnel directly.
    /// </summary>
    /// <param name="saved">Previously saved handshake credentials.</param>
    /// <param name="nodeAddress">Node address being reconnected to.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Connection details of the fast-reconnected session.</returns>
    private async Task<ConnectionResult> FastReconnectAsync(
        SavedCredentials saved, string nodeAddress, CancellationToken ct)
    {
        ConnectionResult result;

        if (saved.ServiceType == "wireguard")
        {
            EmitProgress("tunnel", "Fast reconnect: installing WireGuard tunnel from saved credentials...");

            var config = new WireGuardConfig(
                ClientPrivateKey: Convert.FromBase64String(saved.WgPrivateKey!),
                AssignedAddresses: saved.WgAssignedAddrs!,
                ServerPublicKey: saved.WgServerPubKey!,
                ServerEndpoint: saved.WgServerEndpoint!,
                FullTunnel: _options.FullTunnel
            ) { Dns = Constants.DnsPresets.Resolve(_options.Dns) };

            _wgTunnel = new WireGuardTunnel();
            await _wgTunnel.InstallAsync(config, ct);
            ct.ThrowIfCancellationRequested();

            // Extract the first assigned IPv4 address (strip CIDR)
            string? vpnIp = null;
            foreach (var addr in saved.WgAssignedAddrs!)
            {
                if (addr.Contains('.'))
                {
                    vpnIp = addr.Split('/')[0];
                    break;
                }
            }

            EmitProgress("tunnel", $"WireGuard tunnel active (fast reconnect), VPN IP: {vpnIp}");

            // Verify tunnel
            EmitProgress("verify", "Verifying VPN tunnel...");
            var verification = await VerifyConnectionAsync(ct: ct);

            result = new ConnectionResult
            {
                SessionId = saved.SessionId,
                NodeAddress = nodeAddress,
                ServiceType = "wireguard",
                VpnIp = verification.Working ? verification.VpnIp : vpnIp,
                Verification = verification,
            };
        }
        else
        {
            // V2Ray fast reconnect
            EmitProgress("tunnel", "Fast reconnect: starting V2Ray from saved credentials...");

            if (string.IsNullOrWhiteSpace(_options.V2RayExePath))
            {
                throw new SentinelException(
                    "V2RAY_PATH_REQUIRED",
                    "V2Ray node selected but V2RayExePath is not configured in SentinelVpnOptions"
                );
            }

            var protocol = saved.V2RayProtocol == 1 ? "vless" : "vmess";
            var transport = MapTransportNumber(saved.V2RayTransport ?? 7);

            var v2Config = new V2RayConfig(
                ServerHost: saved.V2RayServerHost!,
                Port: saved.V2RayPort ?? 443,
                Protocol: protocol,
                Transport: transport,
                Tls: saved.V2RayTls == 1,
                Uuid: saved.V2RayUuid!,
                LocalSocksPort: DEFAULT_SOCKS_PORT
            );

            _v2RayProcess = new V2RayProcess(_options.V2RayExePath!);
            await _v2RayProcess.StartAsync(v2Config, ct, _options.Dns, _options.SystemProxy);
            ct.ThrowIfCancellationRequested();

            EmitProgress("tunnel", $"V2Ray active (fast reconnect) on SOCKS5 port {_v2RayProcess.SocksPort}");

            // Verify tunnel
            EmitProgress("verify", "Verifying VPN tunnel...");
            var verification = await VerifyConnectionAsync(ct: ct);

            result = new ConnectionResult
            {
                SessionId = saved.SessionId,
                NodeAddress = nodeAddress,
                ServiceType = "v2ray",
                SocksPort = _v2RayProcess.SocksPort,
                SocksUser = _v2RayProcess.SocksUser,
                SocksPass = _v2RayProcess.SocksPass,
                Verification = verification,
            };
        }

        if (result.Verification?.Working == true)
        {
            EmitProgress("verify", $"Tunnel verified (fast reconnect), external IP: {result.Verification.VpnIp}");
        }
        else
        {
            EmitProgress("verify", "Tunnel verification failed — IP check did not succeed");
        }

        _activeConnection = result;
        _connectedAt = DateTime.UtcNow;

        EmitConnected(result);
        return result;
    }
}
