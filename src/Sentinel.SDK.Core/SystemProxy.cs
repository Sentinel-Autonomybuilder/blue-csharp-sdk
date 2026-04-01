using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Sentinel.SDK.Core;

/// <summary>
/// System-wide proxy configuration for SOCKS5 tunnels (V2Ray).
/// </summary>
public static class SystemProxy
{
    /// <summary>Set system proxy to SOCKS5 on specified port.</summary>
    public static void Set(int socksPort)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Windows: use registry
            var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Internet Settings", true);
            if (key != null)
            {
                key.SetValue("ProxyEnable", 1);
                key.SetValue("ProxyServer", $"socks=127.0.0.1:{socksPort}");
                key.Close();
            }
            // Notify system of change
            InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0); // INTERNET_OPTION_SETTINGS_CHANGED
            InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0); // INTERNET_OPTION_REFRESH
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            RunCommand("networksetup", $"-setsocksfirewallproxy Wi-Fi 127.0.0.1 {socksPort}");
            RunCommand("networksetup", "-setsocksfirewallproxystate Wi-Fi on");
        }
        else // Linux
        {
            RunCommand("gsettings", $"set org.gnome.system.proxy.socks host 127.0.0.1");
            RunCommand("gsettings", $"set org.gnome.system.proxy.socks port {socksPort}");
            RunCommand("gsettings", "set org.gnome.system.proxy mode manual");
        }
    }

    /// <summary>Clear system proxy settings.</summary>
    public static void Clear()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Internet Settings", true);
            if (key != null)
            {
                key.SetValue("ProxyEnable", 0);
                key.DeleteValue("ProxyServer", false);
                key.Close();
            }
            InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0);
            InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            RunCommand("networksetup", "-setsocksfirewallproxystate Wi-Fi off");
        }
        else
        {
            RunCommand("gsettings", "set org.gnome.system.proxy mode none");
        }
    }

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

    private static void RunCommand(string exe, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args) { CreateNoWindow = true, UseShellExecute = false };
            Process.Start(psi)?.WaitForExit(5000);
        }
        catch { /* best-effort */ }
    }
}
