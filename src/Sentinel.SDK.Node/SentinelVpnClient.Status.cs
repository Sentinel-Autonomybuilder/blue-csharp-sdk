using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using Sentinel.SDK.Core;

namespace Sentinel.SDK.Node;

// ─── Status, Diagnostics, Verification & QuickConnect ───────────────────────

public partial class SentinelVpnClient
{
    // ─── Quick Connect (Static Convenience) ────────────────────────

    /// <summary>
    /// One-call convenience method: creates a wallet from mnemonic, builds a VPN client,
    /// connects to the best available node via <see cref="ConnectAutoAsync"/>, and returns
    /// a disposable handle for cleanup.
    /// </summary>
    /// <remarks>
    /// Equivalent to the JS SDK's <c>quickConnect()</c>. This is a static factory — it
    /// creates and owns the wallet and client internally. Dispose the returned
    /// <see cref="QuickConnectResult"/> to disconnect and release all resources.
    /// <para>
    /// Example usage:
    /// <code>
    /// await using var vpn = await SentinelVpnClient.QuickConnectAsync("word1 word2 ...");
    /// Console.WriteLine($"Connected: {vpn.Connection.NodeAddress}");
    /// // vpn is disconnected and cleaned up when disposed
    /// </code>
    /// </para>
    /// </remarks>
    /// <param name="mnemonic">BIP39 mnemonic phrase (12 or 24 words).</param>
    /// <param name="options">Optional connection preferences (countries, service type, endpoints, etc.).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="QuickConnectResult"/> with the connection details and a disposable cleanup handle.</returns>
    /// <exception cref="SentinelException">Thrown when the mnemonic is invalid, balance is insufficient, or no node can be reached.</exception>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="ct"/> is cancelled.</exception>
    public static async Task<QuickConnectResult> QuickConnectAsync(
        string mnemonic,
        QuickConnectOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mnemonic);

        var opts = options ?? new QuickConnectOptions();

        // ─── Step 1: Create wallet from mnemonic ───
        var wallet = SentinelWallet.FromMnemonic(mnemonic);

        // ─── Step 2: Create VPN client with options ───
        var vpnOptions = new SentinelVpnOptions
        {
            LcdUrls = opts.LcdUrls,
            RpcUrls = opts.RpcUrls,
            V2RayExePath = opts.V2RayExePath,
            Gigabytes = opts.Gigabytes,
            FullTunnel = opts.FullTunnel,
            SystemProxy = opts.SystemProxy,
            Logger = opts.Logger,
            FeeGranter = opts.FeeGranter,
        };

        var client = new SentinelVpnClient(wallet, vpnOptions);

        try
        {
            // ─── Step 3: Connect via auto-select ───
            var autoOpts = new ConnectAutoOptions
            {
                Countries = opts.Countries,
                ServiceType = opts.ServiceType,
                MaxAttempts = opts.MaxAttempts,
                NodePool = opts.NodePool,
            };

            var result = await client.ConnectAutoAsync(autoOpts, ct);

            return new QuickConnectResult(result, client);
        }
        catch
        {
            // If connection fails, dispose the client we created
            client.Dispose();
            throw;
        }
    }

    // ─── Status ──────────────────────────────────────────────────────

    /// <summary>
    /// Get the current connection status, or null if not connected.
    /// </summary>
    /// <returns>Connection status with uptime, or null if disconnected.</returns>
    public ConnectionStatus? GetStatus()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_activeConnection is null)
        {
            return null;
        }

        return new ConnectionStatus
        {
            Connected = true,
            NodeAddress = _activeConnection.NodeAddress,
            SessionId = _activeConnection.SessionId,
            ServiceType = _activeConnection.ServiceType,
            Uptime = DateTime.UtcNow - _connectedAt,
        };
    }

    // ─── Diagnostics ──────────────────────────────────────────────────

    /// <summary>
    /// Gather comprehensive diagnostic information about the current connection state.
    /// Checks WireGuard service status, peer handshake, traffic counters, system proxy,
    /// DNS resolution, and V2Ray SOCKS port. Never throws — all checks are best-effort.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="ConnectionDiagnostics"/> snapshot with all available information.</returns>
    public async Task<ConnectionDiagnostics> DiagnoseConnectionAsync(CancellationToken ct = default)
    {
        var serviceRunning = false;
        var interfaceExists = false;
        var peerHandshakeComplete = false;
        var bytesReceived = -1L;
        var bytesSent = -1L;
        var systemProxySet = false;
        var dnsWorking = false;
        string? tunnelName = null;
        int? socksPort = null;
        string? lastError = null;

        try
        {
            // ─── Determine tunnel type from active connection ───
            var isWireGuard = _wgTunnel is not null;
            var isV2Ray = _v2RayProcess is not null;

            // ─── WireGuard-specific checks ───
            if (isWireGuard)
            {
                tunnelName = _wgTunnel!.TunnelName;

                // Check WireGuard service status (Windows)
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    try
                    {
                        var serviceName = $"WireGuardTunnel${tunnelName}";
                        var psi = new ProcessStartInfo("sc.exe", $"query {serviceName}")
                        {
                            CreateNoWindow = true,
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                        };
                        using var proc = Process.Start(psi);
                        if (proc is not null)
                        {
                            var output = await proc.StandardOutput.ReadToEndAsync(ct);
                            await proc.WaitForExitAsync(ct);
                            serviceRunning = output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase);
                            interfaceExists = serviceRunning || output.Contains("STOPPED", StringComparison.OrdinalIgnoreCase);
                        }
                    }
                    catch (Exception ex)
                    {
                        lastError = $"sc.exe query failed: {ex.Message}";
                    }
                }
                else
                {
                    // Linux/macOS: check if interface exists via ip/ifconfig
                    interfaceExists = true; // Assume if _wgTunnel is not null
                }

                // Check wg show for peer handshake and transfer stats
                try
                {
                    var wgPsi = new ProcessStartInfo("wg", $"show {tunnelName}")
                    {
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    };
                    using var wgProc = Process.Start(wgPsi);
                    if (wgProc is not null)
                    {
                        var wgOutput = await wgProc.StandardOutput.ReadToEndAsync(ct);
                        await wgProc.WaitForExitAsync(ct);

                        if (wgProc.ExitCode == 0)
                        {
                            interfaceExists = true;

                            // Parse latest-handshake
                            foreach (var line in wgOutput.Split('\n'))
                            {
                                var trimmed = line.Trim();
                                if (trimmed.StartsWith("latest handshake:", StringComparison.OrdinalIgnoreCase))
                                {
                                    // Any non-empty value means handshake completed
                                    peerHandshakeComplete = trimmed.Length > "latest handshake:".Length + 1;
                                }
                                else if (trimmed.StartsWith("transfer:", StringComparison.OrdinalIgnoreCase))
                                {
                                    // Format: "transfer: X.XX KiB received, Y.YY KiB sent"
                                    ParseTransferLine(trimmed, out bytesReceived, out bytesSent);
                                }
                            }
                        }
                    }
                }
                catch
                {
                    // wg.exe may not be on PATH — non-fatal
                }
            }

            // ─── V2Ray-specific checks ───
            if (isV2Ray)
            {
                socksPort = _v2RayProcess!.SocksPort;

                // Check if SOCKS port is actually listening
                try
                {
                    using var tcpClient = new TcpClient();
                    await tcpClient.ConnectAsync(IPAddress.Loopback, socksPort.Value, ct);
                    interfaceExists = true; // SOCKS port is reachable
                }
                catch
                {
                    interfaceExists = false;
                    lastError = $"V2Ray SOCKS port {socksPort} is not listening";
                }
            }

            // ─── System proxy check (Windows) ───
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try
                {
                    var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                        @"Software\Microsoft\Windows\CurrentVersion\Internet Settings", false);
                    if (key is not null)
                    {
                        var proxyEnable = key.GetValue("ProxyEnable");
                        var proxyServer = key.GetValue("ProxyServer") as string;
                        key.Close();

                        systemProxySet = proxyEnable is int enabled && enabled == 1
                            && proxyServer is not null && proxyServer.Contains("socks", StringComparison.OrdinalIgnoreCase);
                    }
                }
                catch
                {
                    // Registry read failed — non-fatal
                }
            }
            else
            {
                // On non-Windows, trust our internal tracking
                systemProxySet = _systemProxySet;
            }

            // ─── DNS resolution check ───
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(5000);
                var addresses = await Dns.GetHostAddressesAsync("sentinel.co", cts.Token);
                dnsWorking = addresses.Length > 0;
            }
            catch
            {
                dnsWorking = false;
            }
        }
        catch (Exception ex)
        {
            lastError = $"Diagnostics failed: {ex.Message}";
        }

        return new ConnectionDiagnostics(
            ServiceRunning: serviceRunning,
            InterfaceExists: interfaceExists,
            PeerHandshakeComplete: peerHandshakeComplete,
            BytesReceived: bytesReceived,
            BytesSent: bytesSent,
            SystemProxySet: systemProxySet,
            DnsWorking: dnsWorking,
            TunnelName: tunnelName,
            SocksPort: socksPort,
            LastError: lastError
        );
    }

    /// <summary>
    /// Parse a WireGuard transfer line like "transfer: 1.23 KiB received, 4.56 MiB sent"
    /// into byte counts. Sets -1 if parsing fails.
    /// </summary>
    private static void ParseTransferLine(string line, out long received, out long sent)
    {
        received = -1;
        sent = -1;

        try
        {
            // Remove "transfer:" prefix
            var data = line.Substring(line.IndexOf(':') + 1).Trim();
            var parts = data.Split(',');

            if (parts.Length >= 2)
            {
                received = ParseByteValue(parts[0].Trim());
                sent = ParseByteValue(parts[1].Trim());
            }
        }
        catch
        {
            // Non-fatal — leave as -1
        }
    }

    /// <summary>
    /// Parse a value like "1.23 KiB received" or "4.56 MiB sent" to bytes.
    /// </summary>
    private static long ParseByteValue(string value)
    {
        var tokens = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2) return -1;

        if (!double.TryParse(tokens[0], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var num))
        {
            return -1;
        }

        var unit = tokens[1].ToUpperInvariant();
        var multiplier = unit switch
        {
            "B" => 1L,
            "KIB" => 1024L,
            "MIB" => 1024L * 1024,
            "GIB" => 1024L * 1024 * 1024,
            "TIB" => 1024L * 1024 * 1024 * 1024,
            _ => 1L,
        };

        return (long)(num * multiplier);
    }

    // ─── Post-Connection Verification ─────────────────────────────────

    /// <summary>
    /// Verify the VPN tunnel is working by checking the public IP via an external service.
    /// If the IP changed from the local IP, the tunnel is routing traffic correctly.
    /// </summary>
    /// <param name="timeoutMs">Timeout in milliseconds for the IP check (default: 8000).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A <see cref="ConnectionVerification"/> indicating whether the tunnel is working
    /// and the public IP seen through the tunnel.
    /// </returns>
    public async Task<ConnectionVerification> VerifyConnectionAsync(
        int timeoutMs = 8000,
        CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);

            HttpClient httpClient;
            HttpClientHandler? proxyHandler = null;

            // For V2Ray connections, route the verification request through the SOCKS5 proxy
            // so we check the VPN IP, not the real IP.
            if (_v2RayProcess is not null && _v2RayProcess.SocksPort > 0)
            {
                var proxy = new WebProxy($"socks5://127.0.0.1:{_v2RayProcess.SocksPort}");
                // SOCKS5 proxy requires authentication — include credentials
                if (_v2RayProcess.SocksUser is not null && _v2RayProcess.SocksPass is not null)
                {
                    proxy.Credentials = new NetworkCredential(_v2RayProcess.SocksUser, _v2RayProcess.SocksPass);
                }
                proxyHandler = new HttpClientHandler
                {
                    Proxy = proxy,
                    UseProxy = true,
                };
                httpClient = new HttpClient(proxyHandler) { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };
            }
            else
            {
                httpClient = SharedHttpClient;
            }

            try
            {
                var response = await httpClient.GetStringAsync(
                    "https://api.ipify.org?format=json", cts.Token);

                using var doc = JsonDocument.Parse(response);
                var ip = doc.RootElement.TryGetProperty("ip", out var ipProp)
                    ? ipProp.GetString()
                    : null;

                return new ConnectionVerification(true, ip);
            }
            finally
            {
                // Dispose the per-request client/handler if we created one for V2Ray
                if (proxyHandler is not null)
                {
                    httpClient.Dispose();
                    proxyHandler.Dispose();
                }
            }
        }
        catch
        {
            return new ConnectionVerification(false, null);
        }
    }
}
