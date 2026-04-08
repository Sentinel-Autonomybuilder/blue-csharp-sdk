using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Sentinel.SDK.Core;

// ─── Tunnel Detection (WireGuard + V2Ray binary) ───

public static partial class DependencyCheck
{
    // ─── WireGuard Installation Check ───

    /// <summary>
    /// Check if WireGuard is installed on the system.
    /// Ported from js-sdk/wireguard.js (WG_AVAILABLE constant).
    /// </summary>
    internal static bool CheckWireGuardInstalled()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return File.Exists(@"C:\Program Files\WireGuard\wireguard.exe")
                || File.Exists(@"C:\Program Files (x86)\WireGuard\wireguard.exe");
        }

        // Linux/macOS: check for wg-quick
        try
        {
            var psi = new ProcessStartInfo("which", "wg-quick")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            var proc = Process.Start(psi);
            proc?.WaitForExit(3000);
            return proc?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    // ─── V2Ray Binary Detection ───

    /// <summary>
    /// Find V2Ray binary on disk or in PATH.
    /// Ported from js-sdk/preflight.js line 224-236 (findV2Ray closure).
    /// </summary>
    /// <param name="customPath">Explicit path from options, checked first.</param>
    /// <returns>Full path to v2ray binary, or null if not found.</returns>
    internal static string? FindV2Ray(string? customPath)
    {
        // Ported from js-sdk/preflight.js line 224-236
        // 1. Check explicit path first
        if (customPath is not null && File.Exists(customPath))
            return customPath;

        // 2. Check V2RAY_PATH environment variable
        // Ported from js-sdk/preflight.js line 228: process.env.V2RAY_PATH
        var envPath = Environment.GetEnvironmentVariable("V2RAY_PATH");
        if (envPath is not null && File.Exists(envPath))
            return envPath;

        // 3. Check common relative and absolute paths
        // Ported from js-sdk/preflight.js line 229-230 + original C# candidates
        string[] candidates;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            candidates =
            [
                Path.Combine("bin", "v2ray.exe"),
                Path.Combine("..", "bin", "v2ray.exe"),
                "v2ray.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Programs", "v2ray", "v2ray.exe"),
            ];
        }
        else
        {
            candidates =
            [
                Path.Combine("bin", "v2ray"),
                Path.Combine("..", "bin", "v2ray"),
                "/usr/local/bin/v2ray",
                "/usr/bin/v2ray",
                "/snap/bin/v2ray",
            ];
        }

        var found = candidates.FirstOrDefault(File.Exists);
        if (found is not null) return Path.GetFullPath(found);

        // 4. Check PATH (where/which)
        // Ported from js-sdk/preflight.js line 232-235
        try
        {
            var cmd = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? "where" : "which";
            var arg = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? "v2ray.exe" : "v2ray";
            var output = RunProcess(cmd, [arg], 3000);
            var firstLine = output.Trim().Split('\n')[0].Trim();
            if (!string.IsNullOrEmpty(firstLine) && File.Exists(firstLine))
                return firstLine;
        }
        catch
        {
            // where/which may fail — ported from js-sdk/preflight.js line 235
        }

        return null;
    }
}
