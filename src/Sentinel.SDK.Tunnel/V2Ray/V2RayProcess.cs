using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using Sentinel.SDK.Core;

namespace Sentinel.SDK.Tunnel.V2Ray;

// ─── V2Ray Process ───

/// <summary>
/// Manages a V2Ray process lifecycle on Windows.
/// Starts V2Ray with a generated config, provides a local SOCKS5 proxy,
/// and handles clean shutdown and temp file cleanup.
/// </summary>
public class V2RayProcess : IDisposable
{
    private const int SOCKS_READY_TIMEOUT_MS = 10_000;
    private const int SOCKS_POLL_INTERVAL_MS = 250;
    private const int PROCESS_KILL_TIMEOUT_MS = 5_000;

    private readonly string _v2RayExePath;
    private readonly StringBuilder _stderrBuffer = new();
    private Process? _process;
    private string? _tempConfigPath;
    private bool _disposed;

    /// <summary>
    /// Whether the V2Ray process is currently running.
    /// </summary>
    public bool IsRunning => _process is not null && !_process.HasExited;

    /// <summary>
    /// Local SOCKS5 proxy port (set after <see cref="StartAsync"/>).
    /// </summary>
    public int SocksPort { get; private set; }

    /// <summary>
    /// SOCKS5 proxy username for authentication (set after <see cref="StartAsync"/>).
    /// </summary>
    public string? SocksUser { get; private set; }

    /// <summary>
    /// SOCKS5 proxy password for authentication (set after <see cref="StartAsync"/>).
    /// </summary>
    public string? SocksPass { get; private set; }

    /// <summary>
    /// Creates a new V2Ray process manager.
    /// </summary>
    /// <param name="v2rayExePath">Full path to v2ray.exe.</param>
    /// <exception cref="SentinelException">Thrown when the v2ray.exe path is invalid.</exception>
    public V2RayProcess(string v2rayExePath)
    {
        if (!File.Exists(v2rayExePath))
        {
            throw new SentinelException(
                "V2RAY_NOT_FOUND",
                $"v2ray.exe not found at: {v2rayExePath}"
            );
        }

        _v2RayExePath = v2rayExePath;
    }

    /// <summary>
    /// Get captured stderr output from the V2Ray process.
    /// </summary>
    public string GetStderr() => _stderrBuffer.ToString();

    /// <summary>
    /// Start V2Ray with the given configuration.
    /// Writes a temp config file, launches v2ray.exe, and waits for the SOCKS5 port to accept connections.
    /// </summary>
    /// <param name="config">V2Ray configuration from a Sentinel node handshake.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="SentinelException">Thrown when V2Ray fails to start or SOCKS5 port is not ready in time.</exception>
    public async Task StartAsync(V2RayConfig config, CancellationToken ct = default, string? dnsOption = null, bool systemProxy = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (IsRunning)
        {
            throw new SentinelException("V2RAY_RUNNING", "V2Ray is already running; call StopAsync() first");
        }

        // ─── Build and write config ───
        // When systemProxy=true, SOCKS5 uses noauth (OS proxy can't send credentials)
        var configResult = V2RayConfigBuilder.BuildConfigWithAuth(config, dnsOption, systemProxy);
        SocksUser = configResult.SocksUser;
        SocksPass = configResult.SocksPass;
        _tempConfigPath = Path.Combine(Path.GetTempPath(), $"sentinel-v2ray-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(_tempConfigPath, configResult.ConfigJson, ct);

        // ─── Start V2Ray process ───
        var psi = new ProcessStartInfo
        {
            FileName = _v2RayExePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // Use ArgumentList for safe argument passing (no shell injection via string interpolation)
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("-config");
        psi.ArgumentList.Add(_tempConfigPath);

        _process = Process.Start(psi)
            ?? throw new SentinelException("V2RAY_START", "Failed to start v2ray.exe process");

        // ─── Capture stderr asynchronously ───
        _process.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) _stderrBuffer.AppendLine(e.Data); };
        _process.BeginErrorReadLine();

        SocksPort = config.LocalSocksPort;

        // ─── Wait for SOCKS5 port to become available ───
        var deadline = Environment.TickCount64 + SOCKS_READY_TIMEOUT_MS;
        while (Environment.TickCount64 < deadline)
        {
            ct.ThrowIfCancellationRequested();

            if (_process.HasExited)
            {
                throw new SentinelException(
                    "V2RAY_EXITED",
                    $"V2Ray exited prematurely with code {_process.ExitCode}: {_stderrBuffer.ToString().Trim()}"
                );
            }

            if (await IsSocksPortReady(config.LocalSocksPort))
            {
                return;
            }

            await Task.Delay(SOCKS_POLL_INTERVAL_MS, ct);
        }

        // Timeout — kill the process and throw
        await StopAsync();
        throw new SentinelException(
            "V2RAY_TIMEOUT",
            $"V2Ray SOCKS5 port {config.LocalSocksPort} not ready within {SOCKS_READY_TIMEOUT_MS / 1000}s"
        );
    }

    /// <summary>
    /// Start V2Ray with a pre-built configuration result (multi-outbound support).
    /// Use this when building the config externally via
    /// <see cref="V2RayConfigBuilder.BuildMultiOutboundConfig"/>.
    /// </summary>
    /// <param name="configResult">Pre-built V2Ray config result containing JSON and SOCKS5 credentials.</param>
    /// <param name="localSocksPort">Local SOCKS5 port that the config listens on.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task StartAsync(V2RayConfigResult configResult, int localSocksPort, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (IsRunning)
        {
            throw new SentinelException("V2RAY_RUNNING", "V2Ray is already running; call StopAsync() first");
        }

        SocksUser = configResult.SocksUser;
        SocksPass = configResult.SocksPass;
        _tempConfigPath = Path.Combine(Path.GetTempPath(), $"sentinel-v2ray-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(_tempConfigPath, configResult.ConfigJson, ct);

        var psi = new ProcessStartInfo
        {
            FileName = _v2RayExePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("-config");
        psi.ArgumentList.Add(_tempConfigPath);

        _process = Process.Start(psi)
            ?? throw new SentinelException("V2RAY_START", "Failed to start v2ray.exe process");

        _process.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) _stderrBuffer.AppendLine(e.Data); };
        _process.BeginErrorReadLine();

        SocksPort = localSocksPort;

        var deadline = Environment.TickCount64 + SOCKS_READY_TIMEOUT_MS;
        while (Environment.TickCount64 < deadline)
        {
            ct.ThrowIfCancellationRequested();

            if (_process.HasExited)
            {
                throw new SentinelException(
                    "V2RAY_EXITED",
                    $"V2Ray exited prematurely with code {_process.ExitCode}: {_stderrBuffer.ToString().Trim()}"
                );
            }

            if (await IsSocksPortReady(localSocksPort))
            {
                return;
            }

            await Task.Delay(SOCKS_POLL_INTERVAL_MS, ct);
        }

        await StopAsync();
        throw new SentinelException(
            "V2RAY_TIMEOUT",
            $"V2Ray SOCKS5 port {localSocksPort} not ready within {SOCKS_READY_TIMEOUT_MS / 1000}s"
        );
    }

    /// <summary>
    /// Stop the V2Ray process and delete the temp config file.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task StopAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_process is not null && !_process.HasExited)
        {
            try
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromMilliseconds(PROCESS_KILL_TIMEOUT_MS), ct);
            }
            catch (Exception ex) when (ex is not ObjectDisposedException and not OperationCanceledException)
            {
                // Process may have already exited between check and kill
            }
        }

        CleanupProcess();
        CleanupTempConfig();

        SocksPort = 0;
        SocksUser = null;
        SocksPass = null;
    }

    // ─── Helpers ───

    /// <summary>
    /// Test if the local SOCKS5 port is accepting TCP connections.
    /// </summary>
    private static async Task<bool> IsSocksPortReady(int port)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", port);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>
    /// Dispose the process object.
    /// </summary>
    private void CleanupProcess()
    {
        _process?.Dispose();
        _process = null;
    }

    /// <summary>
    /// Delete the temporary V2Ray config file.
    /// </summary>
    private void CleanupTempConfig()
    {
        if (_tempConfigPath is not null && File.Exists(_tempConfigPath))
        {
            try
            {
                File.Delete(_tempConfigPath);
            }
            catch
            {
                // Best effort — temp dir will clean up eventually
            }
            _tempConfigPath = null;
        }
    }

    // ─── IDisposable ───

    /// <summary>
    /// Dispose the V2Ray process manager, stopping the process if running.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (IsRunning)
        {
            try
            {
                _process!.Kill(entireProcessTree: true);
                _process.WaitForExit(PROCESS_KILL_TIMEOUT_MS);
            }
            catch
            {
                // Suppress — disposal must not throw
            }
        }

        CleanupProcess();
        CleanupTempConfig();

        GC.SuppressFinalize(this);
    }
}
