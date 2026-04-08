using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Sentinel.SDK.Core;

// ─── Orphan Recovery and Tunnel Cleanup ───

public static partial class StateManager
{
    // ─── Orphan Recovery ───

    /// <summary>
    /// Detect and clean up orphaned tunnels, processes, and proxy settings from a previous crash.
    /// <para>
    /// Call this at application startup. It loads the saved state file, checks if the process
    /// that created it is still alive, and if not, cleans up:
    /// <list type="bullet">
    ///   <item>Orphaned V2Ray process (killed by PID)</item>
    ///   <item>Orphaned WireGuard tunnel service (uninstalled)</item>
    ///   <item>Stuck system proxy settings (reset to direct)</item>
    ///   <item>Stale config files (deleted)</item>
    /// </list>
    /// </para>
    /// </summary>
    /// <returns>
    /// A <see cref="RecoverResult"/> describing what was found and cleaned,
    /// or a result with <c>HadState = false</c> if no saved state existed.
    /// </returns>
    public static RecoverResult RecoverOrphans()
    {
        var state = LoadState();
        if (state is null)
        {
            return new RecoverResult(false, []);
        }

        // Validate state values before using them in shell commands
        if (!ValidateStateValues(state))
        {
            ClearState();
            return new RecoverResult(true, ["Corrupted state file removed"]);
        }

        var cleaned = new List<string>();

        // Check if the process that saved the state is still running
        var processAlive = false;
        if (state.Pid > 0)
        {
            try
            {
                var proc = Process.GetProcessById(state.Pid);
                processAlive = !proc.HasExited;
            }
            catch
            {
                // Process not found — it has exited
                processAlive = false;
            }
        }

        // If the original process is still running, don't touch anything
        if (processAlive)
        {
            return new RecoverResult(true, []);
        }

        // Clean up orphaned V2Ray
        if (state.ServiceType == "v2ray" && state.V2RayPid is > 0)
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    RunProcess("taskkill", ["/F", "/PID", state.V2RayPid.Value.ToString()],
                        ProcessTimeoutMs);
                }
                else
                {
                    try
                    {
                        var v2proc = Process.GetProcessById(state.V2RayPid.Value);
                        v2proc.Kill(true);
                    }
                    catch
                    {
                        // Already dead — expected
                    }
                }

                cleaned.Add($"v2ray PID {state.V2RayPid}");
            }
            catch
            {
                // Already dead — expected if process exited naturally
            }
        }

        // Clean up orphaned WireGuard tunnel
        if (state.ServiceType == "wireguard" && state.WgTunnelName is not null)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try
                {
                    var output = RunProcess("sc",
                        ["query", $"WireGuardTunnel${state.WgTunnelName}"],
                        ProcessTimeoutMs);

                    if (output.Contains("RUNNING") || output.Contains("STOPPED"))
                    {
                        // Find wireguard.exe
                        string? wgExe = new[]
                            {
                                @"C:\Program Files\WireGuard\wireguard.exe",
                                @"C:\Program Files (x86)\WireGuard\wireguard.exe",
                            }
                            .FirstOrDefault(File.Exists);

                        if (wgExe is not null)
                        {
                            RunProcess(wgExe,
                                ["/uninstalltunnelservice", state.WgTunnelName],
                                WireGuardTimeoutMs);
                            cleaned.Add($"WireGuard tunnel {state.WgTunnelName}");
                        }
                    }
                }
                catch (Exception e)
                {
                    _logger?.Warn($"WG orphan cleanup warning: {e.Message}");
                }
            }
            else
            {
                // Linux/macOS: use wg-quick to remove stale tunnel
                try
                {
                    RunProcess("wg-quick", ["down", state.WgTunnelName], 10000);
                    cleaned.Add($"WireGuard tunnel {state.WgTunnelName} (wg-quick down)");
                }
                catch (Exception e)
                {
                    _logger?.Warn($"wg-quick down warning: {e.Message}");
                }
            }
        }

        // Clean up orphaned system proxy
        if (state.SystemProxySet)
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    const string regKey =
                        @"HKCU\Software\Microsoft\Windows\CurrentVersion\Internet Settings";
                    RunProcess("reg",
                        ["add", regKey, "/v", "ProxyEnable", "/t", "REG_DWORD", "/d", "0", "/f"],
                        ProcessTimeoutMs);
                    RunProcess("reg",
                        ["delete", regKey, "/v", "ProxyServer", "/f"],
                        ProcessTimeoutMs);
                    cleaned.Add("Windows system proxy");
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    var servicesOutput = RunProcess("networksetup",
                        ["-listallnetworkservices"], ProcessTimeoutMs);
                    var services = servicesOutput
                        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        .Where(s => !s.StartsWith('*') && !s.StartsWith("An asterisk"));

                    foreach (var svc in services)
                    {
                        try
                        {
                            RunProcess("networksetup",
                                ["-setsocksfirewallproxystate", svc.Trim(), "off"],
                                ProcessTimeoutMs);
                        }
                        catch
                        {
                            // Service may not have proxy enabled
                        }
                    }

                    cleaned.Add("macOS system proxy");
                }
                else
                {
                    // Linux (GNOME)
                    RunProcess("gsettings",
                        ["set", "org.gnome.system.proxy", "mode", "none"],
                        ProcessTimeoutMs);
                    cleaned.Add("Linux system proxy (GNOME)");
                }
            }
            catch (Exception e)
            {
                _logger?.Warn($"proxy orphan cleanup warning: {e.Message}");
            }
        }

        // Clean up stale config file
        if (state.ConfPath is not null && File.Exists(state.ConfPath))
        {
            try
            {
                File.Delete(state.ConfPath);
            }
            catch (Exception e)
            {
                _logger?.Warn($"conf cleanup warning: {e.Message}");
            }
        }

        ClearState();
        return new RecoverResult(true, [.. cleaned]);
    }

    // ─── PID File ───

    /// <summary>
    /// Write a PID file for the current process.
    /// Use at server/application startup to enable detection of stale instances.
    /// The PID file is stored at <c>{StateDir}/{name}.pid</c>.
    /// </summary>
    /// <param name="name">Application name (defaults to "app"). Creates <c>{name}.pid</c>.</param>
    /// <returns>Full path to the written PID file.</returns>
    public static string WritePidFile(string name = "app")
    {
        try
        {
            EnsureStateDir();
            var pidFile = Path.Combine(StateDir, $"{name}.pid");
            var data = new
            {
                pid = Environment.ProcessId,
                startedAt = DateTime.UtcNow.ToString("o"),
            };
            var json = System.Text.Json.JsonSerializer.Serialize(data, JsonOptions);
            AtomicWrite(pidFile, json);
            return pidFile;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Check if a previous instance is running from a PID file.
    /// If the PID file exists but the process is dead, the stale file is removed.
    /// </summary>
    /// <param name="name">Application name (defaults to "app").</param>
    /// <returns>
    /// A <see cref="PidCheck"/> indicating whether the process is running,
    /// and if so, its PID and start time.
    /// </returns>
    public static PidCheck CheckPidFile(string name = "app")
    {
        try
        {
            var pidFile = Path.Combine(StateDir, $"{name}.pid");
            if (!File.Exists(pidFile))
            {
                return new PidCheck(false, null, null);
            }

            var json = File.ReadAllText(pidFile);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            var pid = root.GetProperty("pid").GetInt32();
            var startedAt = root.TryGetProperty("startedAt", out var sa) ? sa.GetString() : null;

            try
            {
                var proc = Process.GetProcessById(pid);
                if (!proc.HasExited)
                {
                    return new PidCheck(true, pid, startedAt);
                }
            }
            catch
            {
                // Process not found — stale PID file
            }

            // Process is dead — clean up stale PID file
            try { File.Delete(pidFile); } catch { /* best-effort */ }
            return new PidCheck(false, pid, null);
        }
        catch
        {
            return new PidCheck(false, null, null);
        }
    }

    /// <summary>
    /// Remove the PID file for the given application name.
    /// Call this on clean shutdown to prevent stale PID detection on next startup.
    /// </summary>
    /// <param name="name">Application name (defaults to "app").</param>
    public static void ClearPidFile(string name = "app")
    {
        try
        {
            var pidFile = Path.Combine(StateDir, $"{name}.pid");
            if (File.Exists(pidFile)) File.Delete(pidFile);
        }
        catch
        {
            // Best-effort cleanup — non-fatal
        }
    }
}
